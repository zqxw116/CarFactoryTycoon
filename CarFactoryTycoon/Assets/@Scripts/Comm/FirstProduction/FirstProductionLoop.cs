using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>Main-scene-only growth loop, reusing the existing spawner, slots, assembly and dolly.</summary>
[DefaultExecutionOrder(180)]
public sealed partial class FirstProductionLoop:MonoBehaviour,ILineGate,ITargetedLineGate
{
    public static FirstProductionLoop Current {get;private set;}
    public FirstProductionSettings settings;
    public ProductionProgression progression;
    public FactoryPlayerController player;
    public CarSpawner spawner;
    public int OpenedProcesses {get;private set;}=1;
    public int OutletPrefix {get;private set;}=1;
    public int AcceptedVehicles {get;private set;}
    public int SuccessfulDeliveries {get;private set;}
    public int FailedDeliveries {get;private set;}
    public int DeliveryIncome {get;private set;}
    public int LastDeliveryAmount {get;private set;}
    public string LastResult {get;private set;}="";
    public bool FirstWorkerHired=>progression!=null&&progression.processes[0].hired;
    public float WashProgress=>Mathf.Max(.008f,progression.processes[0].gateProgress-settings.washDistanceBeforeDashboard/Mathf.Max(.1f,length));
    public CarController FirstCar {get;private set;}
    public CarController LatestCar {get;private set;}
    public float GateProgress
    {
        get
        {
            var wash=firstEntry!=null?firstEntry.wash:null;
            return wash==null||!wash.IsClean?WashProgress:Mathf.Max(progression.processes[0].gateProgress,DeliveryEnd(1)-.35f/progression.mainSpline.CalculateLength());
        }
    }
    public bool GateEnabled=>tutorialGate&&isActiveAndEnabled&&FirstCar!=null&&FirstCar.gameObject.activeInHierarchy&&firstEntry!=null&&!firstEntry.returned&&FirstCar.LifeVersion==firstEntry.version&&FirstCar.CanRepair&&!FirstWorkComplete();
    public bool Blocks(CarController car)=>car==FirstCar&&GateEnabled;
    sealed class Entry
    {
        public CarController car;public int version,sequence,prefix;public ProductionContract contract;
        public bool returned;public string failure;public VehicleWashSurface wash;public AssemblyPart[] parts;
    }
    readonly List<Entry> entries=new List<Entry>();
    readonly Dictionary<int,ProductionDispatch> outlets=new Dictionary<int,ProductionDispatch>();
    readonly HashSet<PartStack> stacks=new HashSet<PartStack>();
    Entry firstEntry;
    bool ready,ownsSettings;
    public bool AutomaticInputPurchased { get; private set; }
    float nextAutomaticRequest;
    LineTrafficManager traffic;
    FinalInspectionLane inspection;
    bool FullLine => OpenedProcesses == progression.processes.Length;
    float length;
    FactoryExpansion expansion;
    PrototypeServicesInteraction services;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name!="Car_Factory 3")return;
#if UNITY_EDITOR
        if(UnityEditor.SessionState.GetBool("FirstProduction.LegacyChecks",false))return;
#endif
        var p=FindFirstObjectByType<ProductionProgression>();if(p==null||!p.prototypeMode)return;
        var config=Resources.Load<FirstProductionSettings>("FirstProductionSettings");
        if(config!=null&&!config.enabledInMain)return;
        var root=new GameObject("FirstProductionLoop · Runtime"); // Ordinary Play-scene lifetime; DontSave leaves orphan geometry after stopping.
        var loop=root.AddComponent<FirstProductionLoop>();loop.progression=p;loop.settings=config;
    }
    void Awake(){Current=this;}
    void Start()
    {
        if(settings==null){settings=ScriptableObject.CreateInstance<FirstProductionSettings>();ownsSettings=true;}
        if(progression==null)progression=FindFirstObjectByType<ProductionProgression>();
        player=FindFirstObjectByType<FactoryPlayerController>();spawner=FindFirstObjectByType<CarSpawner>();
        if(progression==null||player==null||spawner==null){Debug.LogError("First production requires the existing main player, spawner and progression.");enabled=false;return;}
        length=progression.mainSpline.CalculateLength();progression.enabled=false;
        foreach(var p in progression.processes){p.hired=false;p.automated=false;}
        expansion = progression.GetComponent<FactoryExpansion>();
        services = progression.GetComponent<PrototypeServicesInteraction>();
        DisableLegacyPanels();
        if (services != null) services.EnableWarehouseOnly();
        foreach(var e in progression.processes)
        {
            var parts=e.humanStation!=null?e.humanStation.partStacks:e.wheel!=null?e.wheel.humanPartStacks:null;
            if(parts!=null)foreach(var stack in parts)if(stack!=null)stacks.Add(stack);
        }
        bool resumed = RestoreFactory();
        tutorialGate = OpenedProcesses == 1 && SuccessfulDeliveries == 0;
        ApplyOpenProcesses();
        if (FullLine && services != null && services.fuel != null && resumed)
            services.fuel.RestoreStaffing(savedFuelHired, savedFuelAutomated);
        if (!FullLine) EnsureOutlet(OutletPrefix);
        saveWallet.OnMoneyChanged += FlagWalletChange;
        traffic=LineTrafficManager.Instance;traffic.RegisterGate(this);
        InitializePlayerTool();
        ready = true;
        spawner.manualInput = true;
        if (tutorialGate) spawner.RequestVehicle();
        LastResult=resumed ? "공장 운영 기록을 불러왔습니다." : "첫 차는 전체 세척과 대시보드 체결을 마칠 때까지 기다립니다.";
    }
    void DisableLegacyPanels()
    {
        foreach(var x in FindObjectsByType<PaintBooth>(FindObjectsInactive.Include,FindObjectsSortMode.None))x.gameObject.SetActive(false);
        foreach(var x in FindObjectsByType<FactoryHiringUI>(FindObjectsInactive.Include,FindObjectsSortMode.None))x.enabled=false;
        foreach(var x in FindObjectsByType<PrototypeUpgradePurchases>(FindObjectsInactive.Include,FindObjectsSortMode.None))x.enabled=false;
    }
    public bool Owns(CarSpawner source)=>ready&&source==spawner;
    public void Stamp(CarController car)
    {
        entries.RemoveAll(e=>e.returned);
        int prefix=OpenedProcesses;
        var required=new List<PartType>();for(int i=0;i<prefix;i++)required.AddRange(progression.processes[i].parts);
        var c=car.GetComponent<ProductionContract>();if(c==null)c=car.gameObject.AddComponent<ProductionContract>();
        bool final = FullLine;
        c.ResetContract(required.ToArray(),final ? 1f : DeliveryEnd(prefix),settings.DeliveryPrice(prefix),final,prefix,final ? null : EnsureOutlet(prefix));
        car.SetFinalInspection(final ? inspection : null, c);
        var wait=car.GetComponent<PendingProductionOutletGate>();
        if(wait==null)wait=car.gameObject.AddComponent<PendingProductionOutletGate>();
        wait.Initialize(this,car,c);
        c.requireWholeWash=true;c.SettlementCompleted-=Settled;c.SettlementCompleted+=Settled;
        var paint=car.GetComponent<CarPaintController>();if(paint!=null){paint.enabled=false;paint.FinishReveal();}
        var wash=car.GetComponent<VehicleWashSurface>();if(wash==null)wash=car.gameObject.AddComponent<VehicleWashSurface>();
        wash.Initialize(car);wash.ResetWash();
        if (AdminWashVehicles) wash.CompleteForAdmin();
        int sequence=++AcceptedVehicles;
        var entry=new Entry{car=car,version=car.LifeVersion,sequence=sequence,prefix=prefix,contract=c,wash=wash,parts=car.GetComponentsInChildren<AssemblyPart>(true)};entries.Add(entry);
        if(sequence==1){FirstCar=car;firstEntry=entry;}LatestCar=car;
    }
    bool FirstWorkComplete()
    {
        if(FirstCar==null)return true;
        if(firstEntry==null||firstEntry.returned||FirstCar.LifeVersion!=firstEntry.version)return true;
        if(firstEntry.wash==null||!firstEntry.wash.IsClean)return false;
        foreach(var type in firstEntry.contract.requiredParts)
        {
            bool found=false;foreach(var part in firstEntry.parts)if(part.myType==type){found=true;if(!part.IsAssembled)return false;}
            if(!found)return false;
        }
        return true;
    }
    void Update()
    {
        CheckpointIfDue();
        if(!ready || PrototypeDayCycle.SettlementOpen)return;
        TickPlayerTool();
        if (AutomaticInputPurchased && Time.time >= nextAutomaticRequest)
        {
            spawner.RequestAutomaticVehicle();
            nextAutomaticRequest = Time.time + Mathf.Max(.1f, spawner.spawnInterval);
        }
        for(int i=0;i<entries.Count;i++)
        {
            var e=entries[i];if(e.returned)continue;
            if(e.car==null||e.car.LifeVersion!=e.version||!e.car.gameObject.activeInHierarchy)
            {e.returned=true;continue;}
            if(e.contract.isCompleteVehicle || e.prefix!=OutletPrefix)continue;
            if(e.car.pathProgress+0.0001f<e.contract.endProgress)continue;
            var failure=e.contract.FailureReason;
            if(e.contract.dispatch.TryCapture(e.car))e.failure=failure;
        }
        AdvanceOutlet();
    }
    void Settled(ProductionContract contract,bool accepted)
    {
        foreach(var e in entries)
        {
            if(e.contract!=contract||e.returned||e.version!=e.car.LifeVersion)continue;
            e.returned=true;LastDeliveryAmount=accepted?contract.payout:0;
            if (contract.isCompleteVehicle) e.failure = contract.inspectionFailure;
            if(accepted){SuccessfulDeliveries++;DeliveryIncome+=contract.payout;LastResult=(contract.isCompleteVehicle ? "검사 통과 · 완성차 출고 +" : "인수 완료 · 납품금 +")+contract.payout+"원";}
            else {FailedDeliveries++;PrototypeDayCycle.Current?.RecordRejectedDelivery();LastResult=(string.IsNullOrEmpty(e.failure)?"작업 미완료":e.failure)+" · 납품금 0원";}
            savePending = true;
            break;
        }
    }
    void AdvanceOutlet()
    {
        if(OutletPrefix>=OpenedProcesses)return;
        foreach(var e in entries)if(!e.returned&&e.prefix<=OutletPrefix)return;
        foreach(var pair in outlets)if(pair.Key<=OutletPrefix&&pair.Value.IsBusy)return;
        int next = OpenedProcesses;
        foreach (var e in entries)
            if (!e.returned && e.prefix > OutletPrefix) next = Mathf.Min(next, e.prefix);
        OutletPrefix=next;
        foreach(var pair in outlets)pair.Value.gameObject.SetActive(pair.Key==OutletPrefix);
        if (OutletPrefix < progression.processes.Length) EnsureOutlet(OutletPrefix).gameObject.SetActive(true);
    }
    float DeliveryEnd(int prefix)
    {
        var entry = progression.processes[prefix - 1];
        float stop = ProcessAuthoringLayout.Record(progression, entry)?.stopOffsetMeters ?? 2f;
        var profile = progression.authoringProfile;
        if (profile != null)
            foreach (var vehicle in profile.vehicles)
                foreach (var item in vehicle.overrides)
                    if (item.overrideStop && item.processId == entry.authoringId)
                        stop = Mathf.Max(stop, item.stopOffsetMeters);
        float dock = Mathf.Clamp(stop, .1f, Mathf.Max(.1f, progression.prototypeSettings.workZoneMeters - .1f));
        // Leave room for the worker gate before the dolly can capture this vehicle.
        return Mathf.Clamp01(Mathf.Max(progression.EndFor(prefix), entry.gateProgress + (dock + .35f) / length));
    }
    ProductionDispatch EnsureOutlet(int prefix)
    {
        if(outlets.TryGetValue(prefix,out var result))return result;
        var root=new GameObject("중간 납품 대차 · "+prefix);root.transform.SetParent(transform,false);
        result=root.AddComponent<ProductionDispatch>();result.simpleHandover=true;
        result.Initialize(progression.mainSpline,DeliveryEnd(prefix));outlets[prefix]=result;
        root.SetActive(prefix==OutletPrefix);
        return result;
    }
    public bool AdminOpenStaffedLine()
    {
        if (!ready || PrototypeDayCycle.SettlementOpen || player.InteractionUIOpen) return false;
        BeginAdminSession();
        OpenedProcesses = progression.processes.Length;
        for (int index = 0; index < OpenedProcesses; index++)
        {
            var process = progression.processes[index];
            if (process.hired && !process.automated) continue;
            process.hired = true;
            process.automated = false;
            progression.RefreshProcess(index);
        }
        ApplyOpenProcesses();
        AdvanceOutlet();
        LastResult = "관리자 · 조립 라인 전체 개방 및 작업자 배치";
        return true;
    }

    public bool AdminWashVehicles { get; private set; }

    public void SetAdminWashVehicles(bool enabled)
    {
        if (!ready || PrototypeDayCycle.SettlementOpen || player.InteractionUIOpen) return;
        BeginAdminSession();
        AdminWashVehicles = enabled;
        if (enabled)
            foreach (var entry in entries)
                if (!entry.returned && entry.car != null && entry.car.LifeVersion == entry.version)
                    entry.wash.CompleteForAdmin();
    }

    public bool CanInvestAt(int index)
    {
        return ready && !PrototypeDayCycle.SettlementOpen && InvestmentOpen && investmentProcess == index && index >= 0 && index < OpenedProcesses &&
            Vector3.Distance(player.transform.position, PostPosition(index)) <= InvestmentRange;
    }

    public bool TryHireFirstWorker() => investmentProcess == 0 && TryStaffProcess(false);

    public bool TryStaffProcess(bool robot)
    {
        if (!CanInvestAt(investmentProcess)) return false;
        bool purchased = robot ? progression.TryPurchaseRobot(investmentProcess) : progression.TryHireProcess(investmentProcess);
        if (!purchased) return false;
        ApplyProcessVisibility(investmentProcess);
        LastResult = ProcessName(investmentProcess) + (robot ? " 로봇팔 가동" : " 작업자 고용 완료");
        return true;
    }

    public bool TryPurchaseAutomaticInput()
    {
        if (!CanInvestAt(investmentProcess) || AutomaticInputPurchased ||
            !EconomyManager.Instance.TrySpend(settings.automaticInputCost)) return false;
        AutomaticInputPurchased = true;
        nextAutomaticRequest = Time.time + Mathf.Max(.1f, spawner.spawnInterval);
        LastResult = "자동 투입 가동 · 수동 투입도 가능";
        return true;
    }

    public bool TryUnlockNextProcess()
    {
        if(!CanInvestAt(investmentProcess)||OpenedProcesses>=progression.processes.Length||!EconomyManager.Instance.TrySpend(settings.UnlockPrice(OpenedProcesses)))return false;
        OpenedProcesses++;if (!FullLine) EnsureOutlet(OpenedProcesses);ApplyOpenProcesses();AdvanceOutlet();
        LastResult=progression.processes[OpenedProcesses-1].displayName+" 공정 해금 · 새 차량부터 작업량/납품금 증가";return true;
    }
    void ApplyOpenProcesses()
    {
        float assemblyEnd = DeliveryEnd(OpenedProcesses);
        float end = FullLine ? 1f : assemblyEnd;
        if (expansion != null && services != null && services.warehousePost != null && services.supply != null)
            expansion.ApplyProductionArea(OpenedProcesses, progression.mainSpline.EvaluatePosition(end), services);
        for (int i = 0; i < progression.processes.Length; i++) ApplyProcessVisibility(i);
        if(progression.finishingRoots!=null)foreach(var root in progression.finishingRoots)if(root!=null && !(FullLine && services != null && services.fuel != null && root == services.fuel.gameObject))root.SetActive(false);
        if(progression.constructionRows!=null)foreach(var root in progression.constructionRows)if(root!=null)root.SetActive(false);
        if (FullLine && inspection == null)
        {
            var lane = new GameObject("완성차 검사 · 수밀 / 주행 / 체결");
            lane.transform.SetParent(transform, false);
            inspection = lane.AddComponent<FinalInspectionLane>();
            inspection.Initialize(progression.mainSpline, assemblyEnd);
            if (services != null) services.OpenFinalFuelStation(inspection);
            var departure = FindFirstObjectByType<DepartureStation>(FindObjectsInactive.Include);
            if (departure == null) departure = lane.AddComponent<DepartureStation>();
            departure.gameObject.SetActive(true);
            departure.enabled = true;
            if (expansion != null) departure.SetFactoryExit(expansion.OpenOutboundDoor());
        }
        if(progression.conveyor!=null)progression.conveyor.SetProductionEnd(end, FullLine ? assemblyEnd : 1f);
    }
    void ApplyProcessVisibility(int i)
    {
        var e=progression.processes[i];bool open=i<OpenedProcesses;
        if(e.humanStation!=null){e.humanStation.gameObject.SetActive(open);if(!e.hired)e.humanStation.enabled=false;}
        if(e.humanRoot!=null)e.humanRoot.SetActive(open);
        if(e.workers!=null)foreach(var w in e.workers)if(w!=null)w.gameObject.SetActive(open&&e.hired&&!e.automated);
        if(e.robots!=null)foreach(var robot in e.robots)if(robot!=null)robot.gameObject.SetActive(open&&e.automated);
        if(e.robotDecor!=null)foreach(var x in e.robotDecor)if(x!=null)x.SetActive(open&&e.automated);
        if(e.wheel!=null)e.wheel.gameObject.SetActive(open);
        if (e.wheelPlatform != null) e.wheelPlatform.gameObject.SetActive(open);
        if(e.hiringPost!=null)e.hiringPost.gameObject.SetActive(open);
        var stock=e.humanStation!=null?e.humanStation.partStacks:e.wheel!=null?e.wheel.humanPartStacks:null;
        if(stock!=null)foreach(var x in stock)if(x!=null)x.gameObject.SetActive(open);
    }
    public bool CanAttach(CarController car)
    {
        var c=car!=null?car.GetComponent<ProductionContract>():null;
        return c==null||!c.requireWholeWash||car.GetComponent<VehicleWashSurface>()!=null&&car.GetComponent<VehicleWashSurface>().IsClean;
    }
    void OnDestroy()
    {
        if (saveWallet != null) saveWallet.OnMoneyChanged -= FlagWalletChange;
        if(traffic!=null)traffic.UnregisterGate(this);
        ReleasePlayerTool();
        if(ownsSettings&&settings!=null)Destroy(settings);
        if(Current==this)Current=null;
    }
}
