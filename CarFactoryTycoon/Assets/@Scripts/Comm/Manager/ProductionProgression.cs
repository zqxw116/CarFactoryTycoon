using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

/// <summary>Ordered hiring, row unlocks and per-vehicle processing contracts. The authored spline never changes.</summary>
[DefaultExecutionOrder(-120)]
public sealed class ProductionProgression : MonoBehaviour
{
    [Serializable] public class ProcessEntry
    {
        public string displayName;
        public int row;
        public PartType[] parts;
        public WorkerStation humanStation;
        public Worker[] workers;
        public StationController[] robots;
        public GameObject humanRoot;
        public GameObject[] robotDecor;
        public WheelStation wheel;
        public float gateProgress;
        public bool hired;
        public bool automated;
        public int workerLevel;
        [NonSerialized] public bool automationApplied;
        [NonSerialized] public bool experienced;
    }
    [Serializable] class SaveData
    {
        public int version=1, prefix=1, money;
        public bool[] automated, experienced;
        public int[] levels;
    }
    public static ProductionProgression Current {get;private set;}
    public SplineContainer mainSpline;
    public SplineConveyorDeformer conveyor;
    public ProcessEntry[] processes;
    public GameObject[] finishingRoots;
    public GameObject[] constructionRows;
    public bool persistProgress=true;
    public int SelectedProcess {get;set;}
    public int CurrentRow {get {return processes[Mathf.Max(0,HiredPrefix-1)].row;}}
    public int HiredPrefix {get;private set;}=1;
    public int HiredCountInRow {get {int n=0; foreach(var p in processes)if(p.row==CurrentRow&&p.hired)n++;return n;}}
    public int ProcessCountInRow {get {int n=0; foreach(var p in processes)if(p.row==CurrentRow)n++;return n;}}
    public bool IsTransitioning => Time.unscaledTime<buildUntil;
    public bool CanHireNext => !IsTransitioning&&HiredPrefix<processes.Length&&processes[HiredPrefix].row==CurrentRow;
    public int NextHireCost => 45+HiredPrefix*20;
    public string NextHireName => HiredPrefix<processes.Length?processes[HiredPrefix].displayName:"고용 완료";
    public bool CanUnlockNextRow => !IsTransitioning&&HiredPrefix<processes.Length&&processes[HiredPrefix].row==CurrentRow+1;
    public int NextRowCost => 250*(CurrentRow+1)*(CurrentRow+1);
    public bool CompleteVehicleProduction => HiredPrefix==processes.Length;
    public string StatusText => IsTransitioning?"생산 구간 확장 중":CompleteVehicleProduction?"완성차 생산 / 전체 조립 공정 개방":string.Format("{0}줄 작업장 {1}/{2} / 차체 가공 납품",CurrentRow+1,HiredCountInRow,ProcessCountInRow);
    readonly Dictionary<int,ProductionDispatch> outlets=new Dictionary<int,ProductionDispatch>();
    readonly Dictionary<int,int> inFlight=new Dictionary<int,int>();
    readonly List<int> obsolete=new List<int>();
    float buildUntil;
    bool started, changedMoney;
    float saveTimer;
    EconomyManager economy;
    const string SaveKey="CarFactoryTycoon.Production.v1";

    void Awake()
    {
        Current=this;
        if(processes==null||processes.Length==0){enabled=false;Current=null;return;}
        economy=EconomyManager.Instance;
        if(persistProgress) Load();
        HiredPrefix=Mathf.Clamp(HiredPrefix,1,processes.Length);
        for(int i=0;i<processes.Length;i++) processes[i].hired=i<HiredPrefix;
        ApplyAll();
    }
    void Start()
    {
        started=true;
        economy.OnMoneyChanged+=OnMoney;
    }
    void OnMoney(int amount,int delta,Vector3? pos){changedMoney=true;}
    void Update()
    {
        for(int i=0;i<HiredPrefix;i++)
        {
            var p=processes[i];
            if(p.automated&&!p.automationApplied&&CanSwitch(p)) ApplyProcess(p);
        }
        var cars=LineTrafficManager.Instance.ActiveCars;
        // Capture leaves the car in traffic until its dolly clears the belt.
        for(int i=cars.Count-1;i>=0;i--)
        {
            var car=cars[i];
            if(car==null||!car.TryGetComponent<ProductionContract>(out var contract)||contract.isCompleteVehicle||contract.settled||contract.dispatch==null)continue;
            if((contract.endProgress-car.pathProgress)*car.SplineLength<=.09f) contract.dispatch.TryCapture(car);
        }
        if(changedMoney&&Time.unscaledTime>=saveTimer){Save();changedMoney=false;saveTimer=Time.unscaledTime+2f;}
        if(outlets.Count>0)CleanupOutlets(); // Also retire externally cancelled old contracts once their outlet becomes idle.
    }
    public bool TryHireNext()
    {
        if(!CanHireNext||!EconomyManager.Instance.TrySpend(NextHireCost))return false;
        return AddProcess();
    }
    public bool TryUnlockNextRow()
    {
        if(!CanUnlockNextRow||!EconomyManager.Instance.TrySpend(NextRowCost))return false;
        return AddProcess(); // Exactly ONE first process is included, never the whole row.
    }
    bool AddProcess()
    {
        processes[HiredPrefix].hired=true;
        SelectedProcess=HiredPrefix++;
        buildUntil=Time.unscaledTime+.75f;
        ApplyAll();Save();return true;
    }
    public int WorkerUpgradeCost(int index)=>35+index*15+processes[index].workerLevel*45;
    public int AutomationCost(int index)=>400+index*90;
    bool Valid(int index)=>index>=0&&index<HiredPrefix;
    public bool CanUpgradeWorker(int index)=>Valid(index)&&!processes[index].automated&&processes[index].workerLevel<3;
    public bool CanAutomate(int index)=>Valid(index)&&!processes[index].automated&&processes[index].experienced;
    public bool CanRest(int index)
    {
        if(!Valid(index)||processes[index].automated)return false;
        foreach(var w in processes[index].workers)if(w!=null&&w.CanRestNow)return true;
        return false;
    }
    public bool TryUpgradeWorker(int index)
    {
        if(!CanUpgradeWorker(index)||!EconomyManager.Instance.TrySpend(WorkerUpgradeCost(index)))return false;
        processes[index].workerLevel++;ApplySpeeds(processes[index]);Save();return true;
    }
    public bool TryAutomate(int index)
    {
        if(!CanAutomate(index)||!EconomyManager.Instance.TrySpend(AutomationCost(index)))return false;
        processes[index].automated=true;
        if(CanSwitch(processes[index]))ApplyProcess(processes[index]);
        Save();return true;
    }
    public bool TryRest(int index)
    {
        if(!CanRest(index))return false;
        bool result=false;foreach(var w in processes[index].workers)if(w!=null)result|=w.TryPreemptiveRest();return result;
    }
    public void MarkExperienced(PartType part)
    {
        for(int i=0;i<HiredPrefix;i++)if(Array.IndexOf(processes[i].parts,part)>=0)processes[i].experienced=true;
        changedMoney=true;
    }
    bool CanSwitch(ProcessEntry p)
    {
        if(p.wheel!=null)return p.wheel.currentState==WheelStation.LiftState.Idle;
        if(p.humanStation!=null&&p.humanStation.currentState!=WorkerStation.WorkState.Idle)return false;
        if(p.workers!=null)foreach(var w in p.workers)if(w!=null&&w.IsBusy)return false;
        return true;
    }
    void ApplySpeeds(ProcessEntry p)
    {
        if(p.workers!=null)foreach(var w in p.workers)if(w!=null)w.assembleSpeed=4f*(1f+.5f*p.workerLevel);
    }
    void ApplyProcess(ProcessEntry p)
    {
        // Hiring must not force a previously purchased conversion through an active manual cycle.
        if(p.hired&&p.automated&&!p.automationApplied&&!CanSwitch(p)){ApplySpeeds(p);return;}
        bool auto=p.hired&&p.automated;
        if(p.wheel!=null)
        {
            p.wheel.gameObject.SetActive(p.hired);
            p.wheel.SetHumanMode(!auto);
        }
        if(p.humanRoot!=null)p.humanRoot.SetActive(p.hired&&!auto);
        if(p.humanStation!=null)p.humanStation.enabled=p.hired&&!auto;
        if(p.robots!=null)foreach(var r in p.robots)if(r!=null)r.gameObject.SetActive(auto);
        if(p.robotDecor!=null)foreach(var decor in p.robotDecor)if(decor!=null)decor.SetActive(auto);
        p.automationApplied=auto;
        ApplySpeeds(p);
    }
    void ApplyAll()
    {
        foreach(var p in processes)ApplyProcess(p);
        bool complete=CompleteVehicleProduction;
        if(finishingRoots!=null)foreach(var root in finishingRoots)if(root!=null)root.SetActive(complete);
        if(constructionRows!=null)foreach(var cover in constructionRows)if(cover!=null)cover.SetActive(!complete);
        if(!complete)EnsureOutlet(HiredPrefix);
        float visibleEnd=complete?1f:EndFor(HiredPrefix);
        // Old contract outlets remain operational until their vehicles have been delivered.
        if(conveyor!=null)conveyor.SetProductionEnd(visibleEnd);
        CleanupOutlets();
    }
    public float EndFor(int prefix)
    {
        int i=Mathf.Clamp(prefix-1,0,processes.Length-1);
        float next=i+1<processes.Length?processes[i+1].gateProgress:1f;
        float length=mainSpline.CalculateLength();
        return Mathf.Min(processes[i].gateProgress+3.0f/length,Mathf.Max(processes[i].gateProgress+.7f/length,next-.7f/length));
    }
    ProductionDispatch EnsureOutlet(int prefix)
    {
        if(outlets.TryGetValue(prefix,out var outlet)&&outlet!=null)return outlet;
        var go=new GameObject("ContractDispatch_"+prefix);go.transform.SetParent(transform,false);
        outlet=go.AddComponent<ProductionDispatch>();outlet.Initialize(mainSpline,EndFor(prefix));outlets[prefix]=outlet;
        return outlet;
    }
    public void Stamp(CarController car)
    {
        var required=new List<PartType>();for(int i=0;i<HiredPrefix;i++)required.AddRange(processes[i].parts);
        var c=car.GetComponent<ProductionContract>();if(c==null)c=car.gameObject.AddComponent<ProductionContract>();
        bool complete=CompleteVehicleProduction;
        c.ResetContract(required.ToArray(),complete?1f:EndFor(HiredPrefix),15+HiredPrefix*12,complete,HiredPrefix,complete?null:EnsureOutlet(HiredPrefix));
        if(!inFlight.ContainsKey(HiredPrefix))inFlight[HiredPrefix]=0;
        inFlight[HiredPrefix]++;
    }
    public void ContractReturned(ProductionContract contract)
    {
        int key=contract.processCount;
        if(inFlight.ContainsKey(key))inFlight[key]=Mathf.Max(0,inFlight[key]-1);
        CleanupOutlets();
    }
    void CleanupOutlets()
    {
        obsolete.Clear();foreach(var pair in outlets)
            if(pair.Key!=HiredPrefix&&(!inFlight.ContainsKey(pair.Key)||inFlight[pair.Key]==0)&&(pair.Value==null||pair.Value.IsIdle))obsolete.Add(pair.Key);
        foreach(int key in obsolete){if(outlets[key]!=null)Destroy(outlets[key].gameObject);outlets.Remove(key);}
    }
    public void Save()
    {
        if(!persistProgress||!started||economy==null)return;
        var data=new SaveData{prefix=HiredPrefix,money=economy.currentMoney,automated=new bool[processes.Length],experienced=new bool[processes.Length],levels=new int[processes.Length]};
        for(int i=0;i<processes.Length;i++){data.automated[i]=processes[i].automated;data.experienced[i]=processes[i].experienced;data.levels[i]=processes[i].workerLevel;}
        PlayerPrefs.SetString(SaveKey,JsonUtility.ToJson(data));PlayerPrefs.Save();
    }
    void Load()
    {
        if(!PlayerPrefs.HasKey(SaveKey))return;
        try
        {
            var d=JsonUtility.FromJson<SaveData>(PlayerPrefs.GetString(SaveKey));
            if(d==null||d.version!=1||d.automated==null||d.levels==null||d.experienced==null||d.automated.Length!=processes.Length||d.levels.Length!=processes.Length||d.experienced.Length!=processes.Length)return;
            HiredPrefix=Mathf.Clamp(d.prefix,1,processes.Length);EconomyManager.Instance.currentMoney=Mathf.Max(0,d.money);
            for(int i=0;i<processes.Length;i++){processes[i].automated=d.automated[i];processes[i].workerLevel=Mathf.Clamp(d.levels[i],0,3);processes[i].experienced=d.experienced[i];}
        }
        catch(Exception e){Debug.LogWarning("[Production] Save could not be read: "+e.Message);}
    }
    void OnApplicationPause(bool pause){if(pause)Save();}
    void OnApplicationQuit(){Save();}
    void OnDisable()
    {
        if(started){Save();if(economy!=null)economy.OnMoneyChanged-=OnMoney;}
        if(Current==this)Current=null;
    }
}
