using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>Isolated authoring session using the factory's actual crew, stock and traffic APIs.</summary>
[DefaultExecutionOrder(200)]
public sealed class ProcessAuthoringPreview : MonoBehaviour
{
    public ProductionProgression progression;
    public GameObject[] processRoots;
    public FuelStation fuelStation;
    public bool FuelMode => fuelStation!=null && selectedProcess==progression.processes.Length;
    public CarController[] vehicleInstances;
    public int selectedProcess, selectedVehicle;
    public bool robotMode, autoRun;
    public float speed = 1f, vehicleSpeed = 1.2f;
    [SerializeField] bool idleAtStation;
    public bool Idle=>idleAtStation;
    public bool Running { get; private set; }
    public bool Paused { get; private set; }
    public int RunCount { get; private set; }
    public int CompletedRuns { get; private set; }
    public int FailedRuns { get; private set; }
    int fuelBeforeRun;
    public string Status { get; private set; } = "준비";
    public CarController Car => vehicleInstances != null && selectedVehicle >= 0 && selectedVehicle < vehicleInstances.Length ? vehicleInstances[selectedVehicle] : null;
    readonly Dictionary<PartStack,int> initialStock = new Dictionary<PartStack,int>();
    readonly Dictionary<PartStack,int> runStock = new Dictionary<PartStack,int>();
    float oldTimeScale, oldFixedStep;
    bool timeOwned, retryPending, retryPrefetch;
    float preparationTime;
    PrototypeProcessCrew Crew => PrototypeProcessCrew.Ensure(progression,selectedProcess);
    void Awake(){if(GetComponent<ProcessAuthoringCamera>()==null)gameObject.AddComponent<ProcessAuthoringCamera>();}
#if UNITY_EDITOR
    public static event Action<ProcessAuthoringPreview> BeforeSessionStart;
#endif
    void Start()
    {
#if UNITY_EDITOR
        BeforeSessionStart?.Invoke(this);
#endif
        oldTimeScale=Time.timeScale;oldFixedStep=Time.fixedDeltaTime;timeOwned=true;
        if(UnityEngine.SceneManagement.SceneManager.sceneCount!=1)
        {Status="편집 씬만 단독으로 열어 주세요";enabled=false;RestoreClock();return;}
        EconomyManager.Instance.currentMoney=100000000;
        foreach(var vehicle in vehicleInstances)if(vehicle!=null)CarBodyCollision.EnsureCreated(vehicle.gameObject);
        Select(selectedProcess,robotMode,selectedVehicle);
    }
    public void Select(int process, bool robot, int vehicle)
    {
        Stop();runStock.Clear();idleAtStation=false;
        foreach(var c in progression.GetComponents<PrototypeProcessCrew>())c.SetPreviewIdle(false);
        selectedProcess=Mathf.Clamp(process,0,fuelStation!=null?progression.processes.Length:progression.processes.Length-1);robotMode=robot;
        selectedVehicle=Mathf.Clamp(vehicle,0,vehicleInstances.Length-1);
        for(int i=0;i<processRoots.Length;i++)
        {
            processRoots[i].SetActive(i==selectedProcess);
            var arms=processRoots[i].transform.Find("RobotArms");if(arms!=null)arms.gameObject.SetActive(robot);
        }
        if(fuelStation!=null)fuelStation.gameObject.SetActive(FuelMode);
        for(int i=0;i<vehicleInstances.Length;i++)if(vehicleInstances[i]!=null)vehicleInstances[i].gameObject.SetActive(i==selectedVehicle);
        for(int i=0;i<progression.processes.Length;i++)
        {
            var entry=progression.processes[i];entry.hired=i==selectedProcess;entry.automated=robot&&i==selectedProcess;
            progression.RefreshProcess(i);
        }
        ProcessAuthoringLayout.Apply(progression,progression.authoringProfile);
        initialStock.Clear();
        if(!FuelMode)foreach(var stock in processRoots[selectedProcess].GetComponentsInChildren<PartStack>(true))initialStock[stock]=stock.Count;
        else {fuelStation.RestoreStaffing(true,robotMode);fuelStation.SetDutyStatus(true);}
        ResetVehicle();Status="준비 · 실행하거나 배치를 편집하세요";
    }
    void ResetVehicle(ProcessAuthoringProfile profile=null)
    {
        profile=profile??progression.authoringProfile;
        if(Car==null){Status="차종 프리팹이 없습니다";return;}
        var model=profile.vehicles[selectedVehicle];
        ProcessAuthoringLayout.ApplyVehicle(Car,profile,model.id);
        Car.SetPath(progression.mainSpline,vehicleSpeed,0f);
        if(FuelMode){foreach(var part in Car.GetComponentsInChildren<AssemblyPart>(true))part.SetAssembled();Car.isMoving=false;return;}
        var entry=progression.processes[selectedProcess];
        int earliest=int.MaxValue;PartType first=PartType.None;
        foreach(var part in entry.parts){int index=Constants.GetPartTypeIndex(part);if(index>=0&&index<earliest){earliest=index;first=part;}}
        if(first!=PartType.None)Car.SetCurretParts(first);
        Car.isMoving=false;
    }
    public bool ActiveAttempt => Running || retryPending;
    public void PlayOnce()
    {
        if(Car==null)return;
        idleAtStation=false;if(!FuelMode)Crew.SetPreviewIdle(false);else fuelStation.SetDutyStatus(true);
        if(!FuelMode)
        {
            var available=Car.GetComponentsInChildren<AssemblyPart>(true);
            foreach(var type in progression.processes[selectedProcess].parts)
            {
                bool found=false;foreach(var part in available)if(part.myType==type){found=true;break;}
                if(!found){Stop();Status="이 차종에 부품 매핑이 없습니다: "+type;return;}
            }
        }
        Stop();
        runStock.Clear();foreach(var s in initialStock.Keys)runStock[s]=s.Count;
        ProcessAuthoringLayout.Apply(progression,progression.authoringProfile);
        ResetVehicle();if(!FuelMode){Crew.ReloadLayout();Crew.SetDutyStatus(true,false);}retryPending=true;preparationTime=0f;Paused=false;
        SetSpeed(speed);Status="대기 위치 준비 중";
    }
    public void Retry()
    {
        idleAtStation=false;if(!FuelMode)Crew.SetPreviewIdle(false);else fuelStation.SetDutyStatus(true);
        Stop();RestoreStock(runStock.Count>0?runStock:initialStock);
        ProcessAuthoringLayout.Apply(progression,progression.authoringProfile,false);
        ResetVehicle();
        if(!FuelMode){Crew.ReloadLayout();Crew.SetDutyStatus(true,false);Crew.BeginPreviewPickup(Car);}
        retryPending=true;retryPrefetch=!FuelMode;preparationTime=0f;Status=FuelMode?"주유 재시도":"부품 먼저 집기 · 차량 진입 대기";Paused=false;SetSpeed(speed);
    }
    public void IdleAtStation()=>IdleAtStationWithProfile(progression.authoringProfile);
    public void IdleAtStationWithProfile(ProcessAuthoringProfile profile)
    {
        Stop();autoRun=false;idleAtStation=true;
        ProcessAuthoringLayout.Apply(progression,profile,false);
        if(Application.isPlaying)ResetVehicle(profile);
        else if(Car!=null)ProcessAuthoringLayout.ApplyVehicle(Car,profile,profile.vehicles[selectedVehicle].id);
        float stop=progression.authoringFuelAnchorProgress;
        if(!FuelMode)
        {
            var entry=progression.processes[selectedProcess];
            var record=profile.Find(entry.authoringId);var frame=ProcessAuthoringLayout.Frame(progression,entry);
            if(Application.isPlaying){Crew.SetPreviewIdle(true);stop=Crew.PreviewStopProgress(Car);}
            else stop=Mathf.Clamp01(entry.gateProgress+record.stopOffsetMeters/Mathf.Max(.01f,progression.mainSpline.CalculateLength()));
            if(entry.workers!=null)foreach(var worker in entry.workers)
            {
                if(worker==null)continue;var actor=record.FindActor(ProcessAuthoringLayout.Key(worker));if(actor==null)continue;
                worker.PrototypeRelease();
                ProcessAuthoringLayout.WithDistance(actor.home,actor.side,profile.actorDistance).Apply(worker.transform,frame);
                if(Application.isPlaying)worker.PrototypeMoveTo(worker.transform.position,Worker.WorkerState.Idle,null);
                else worker.currentState=Worker.WorkerState.Idle;
            }
        }
        else fuelStation.SetDutyStatus(false);
        if(Car!=null)
        {
            if(Application.isPlaying)Car.SetProgress(stop);
            else {var at=ProcessAuthoringLayout.FrameAt(progression.mainSpline,stop);Car.transform.SetPositionAndRotation(at.position,at.rotation);Car.pathProgress=stop;}
            Car.isMoving=false;
        }
        Status="Idle · 차량 정차 / 작업자 일반 대기 / 작업 안 함";
    }
    public void RestoreStock()=>RestoreStock(initialStock);
    void RestoreStock(Dictionary<PartStack,int> snapshot)
    {
        foreach(var pair in snapshot)
        {
            int delta=pair.Value-pair.Key.Count;if(delta>0)pair.Key.Add(delta);
            while(pair.Key.Count>pair.Value)pair.Key.TryConsume();
            pair.Key.ResetReadySlot();
        }
    }
    public void Pause(bool value)
    {Paused=value;if(timeOwned)Time.timeScale=value?0f:speed;Status=value?"일시정지 · 실행/정지 모두 편집 가능":"실행 중";}
    public void SetSpeed(float value)
    {speed=Mathf.Clamp(value,.1f,20f);if(timeOwned&&!Paused)Time.timeScale=speed;}
    public void Stop()
    {
        if(progression!=null)foreach(var crew in progression.GetComponents<PrototypeProcessCrew>())crew.CancelCycle();
        if(fuelStation!=null)fuelStation.CancelCycle();
        if(Car!=null)Car.isMoving=false;
        Running=false;retryPending=false;retryPrefetch=false;Paused=false;Status="정지";
        if(timeOwned)Time.timeScale=oldTimeScale;
    }
    void Update()
    {
        if(retryPending && FuelMode)
        {retryPending=false;Running=true;Car.isMoving=true;RunCount++;fuelBeforeRun=fuelStation.CompletedFuelCount;Status="주유 실행 중";}
        if(retryPending&&retryPrefetch)
        {
            preparationTime+=Time.deltaTime;
            if(Crew.PreviewPickupPending)
            {
                if(preparationTime>15f){Stop();Status="집기 위치 도달 불가 · 슬롯/잡기 위치를 확인하세요";}
                return;
            }
            retryPending=false;retryPrefetch=false;Running=true;Car.isMoving=true;RunCount++;Status="부품 집기 완료 · 차량 진입 중";
        }
        if(retryPending)
        {
            bool home=true;
            var entry=progression.processes[selectedProcess];
            var record=ProcessAuthoringLayout.Record(progression,entry);
            var frame=ProcessAuthoringLayout.Frame(progression,entry);
            if(entry.workers!=null)foreach(var w in entry.workers)
            {
                var data=record?.FindActor(ProcessAuthoringLayout.Key(w));if(w==null||data==null)continue;
                var goal=ProcessAuthoringLayout.WithDistance(data.home,data.side,progression.authoringProfile.actorDistance).WorldPosition(frame);
                if(w.gameObject.activeInHierarchy)home&=Vector3.Distance(w.transform.position,goal)<=w.arriveRadius+.01f;
            }
            if(robotMode && entry.robots!=null)foreach(var robot in entry.robots)
            {
                if(robot==null||robot.robotArm==null)continue;
                var goal=robot.endPos!=null?robot.endPos:robot.stationPilePos;
                if(goal!=null && robot.robotArm.endEffector!=null)
                    home&=Vector3.Distance(robot.robotArm.endEffector.position,goal.position)<=.1f;
            }
            preparationTime+=Time.deltaTime;
            if(preparationTime>15f && !home){Stop();Status="대기 위치 도달 불가 · 목표/관절 범위를 확인하세요";return;}
            if(home)
            {retryPending=false;Running=true;Crew.SetDutyStatus(true,false);Car.isMoving=true;RunCount++;Status="실행 중";}
        }
        if(!Running||Paused||Car==null)return;
        if(Car.pathProgress>=.94f)
        {
            bool success=FuelMode?fuelStation.CompletedFuelCount>fuelBeforeRun:true;
            if(!FuelMode)foreach(var type in progression.processes[selectedProcess].parts)
            {
                bool complete=false;foreach(var part in Car.GetComponentsInChildren<AssemblyPart>(true))if(part.myType==type&&part.IsAssembled){complete=true;break;}
                success&=complete;
            }
            string detail=!FuelMode?Crew.LastFailure:null;
            Stop();if(success){CompletedRuns++;Status="1회 완료";}else{FailedRuns++;Status="통과 종료 · 미완료"+(string.IsNullOrEmpty(detail)?"":" · "+detail);}
            if(autoRun)PlayOnce();
        }
    }
    public void CloseTool(){Stop();if(timeOwned)Time.fixedDeltaTime=oldFixedStep;}
    void RestoreClock()
    {if(!timeOwned)return;Time.timeScale=oldTimeScale;Time.fixedDeltaTime=oldFixedStep;timeOwned=false;}
    void OnDisable(){Stop();RestoreClock();}
    void OnDestroy(){RestoreClock();}
}
