using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Prototype timed assembly. Legacy stopping/continuous-work controllers remain available to old scenes.</summary>
public sealed class PrototypeProcessCrew : MonoBehaviour
{
    public ProductionProgression progression;
    public int processIndex;
    public bool WorkersOnDuty = true, Overtime;
    public int CompletedPartCount { get; private set; }
    public int StrikeCount { get; private set; }
    public int MissedPartCount { get; private set; }
    public CarController CurrentCar => car;
    public bool IsBusy { get { foreach(var s in slots) if(s.part!=null)return true;return false; } }
    public float LowestCondition { get { float c=100;foreach(var s in slots)if(s.worker!=null)c=Mathf.Min(c,s.worker.condition);return c; } }
    public string StatusText => !Entry.hired ? "미고용" : Entry.automated ? (IsBusy?"로봇 체결 중":"로봇 대기") :
        !WorkersOnDuty?"근무 종료":IsBusy?"체결 중 · 컨디션 "+Mathf.RoundToInt(LowestCondition)+"%":"작업자 대기 · 컨디션 "+Mathf.RoundToInt(LowestCondition)+"%";
    enum Phase { Fetch, Approach, Attach, Hits }
    sealed class Slot
    {
        public Worker worker;
        public StationController robot;
        public Transform tracker;
        public Vector3 home, source;
        public AssemblyPart part;
        public PartStack stock;
        public Phase phase;
        public float elapsed, nextHit;
        public bool consumed, attached;
        public PrototypeAssemblyEffects effects;
        public Renderer[] renderers;
    }
    readonly List<Slot> slots=new List<Slot>();
    readonly HashSet<PartType> attempted=new HashSet<PartType>();
    readonly Dictionary<PartType,PartStack> stocks=new Dictionary<PartType,PartStack>();
    CarController car;
    AssemblyPart[] parts;
    bool configured, robotMode;
    ProductionProgression.ProcessEntry Entry => progression.processes[processIndex];
    PrototypeBalanceSettings Settings => progression.prototypeSettings;

    public static PrototypeProcessCrew Ensure(ProductionProgression p,int index)
    {
        PrototypeProcessCrew found=null;
        foreach(var c in p.GetComponents<PrototypeProcessCrew>())if(c.processIndex==index){found=c;break;}
        if(found==null){found=p.gameObject.AddComponent<PrototypeProcessCrew>();found.progression=p;found.processIndex=index;}
        found.Configure();
        var day=PrototypeDayCycle.Current;
        if(day!=null)found.SetDutyStatus(!day.IsSettlement&&(day.GameHour<20f||day.OvertimeEnabled),day.GameHour>=20f&&day.GameHour<22f&&day.OvertimeEnabled);
        return found;
    }
    void Configure()
    {
        var e=Entry;
        if(e.humanStation!=null)e.humanStation.enabled=false;
        if(e.wheel!=null)e.wheel.enabled=false; // Keep the lift hardware, never stop a flowing vehicle.
        if(e.robots!=null)foreach(var r in e.robots)if(r!=null){r.prototypeMode=true;r.enabled=false;}
        if(e.workers!=null)foreach(var w in e.workers)if(w!=null)w.prototypeMode=true;
        if(configured && robotMode==e.automated)return;
        CancelAll(); slots.Clear(); stocks.Clear(); configured=true;robotMode=e.automated;
        if(e.humanStation!=null && e.humanStation.partStacks!=null)
            foreach(var stack in e.humanStation.partStacks)if(stack!=null)stocks[stack.partType]=stack;
        if(e.wheel!=null && e.wheel.humanPartStacks!=null)
            foreach(var stack in e.wheel.humanPartStacks)if(stack!=null)stocks[stack.partType]=stack;
        if(!robotMode && e.workers!=null)foreach(var w in e.workers)
        {
            if(w==null)continue;
            slots.Add(new Slot{worker=w,home=w.transform.position});
        }
        if(robotMode && e.robots!=null)foreach(var r in e.robots)
        {
            if(r==null||r.robotArm==null)continue;
            if(r.trackingTarget==null)
            {
                r.trackingTarget=new GameObject("PrototypeTrackingTarget").transform;
                r.trackingTarget.SetParent(r.transform,false);
                r.trackingTarget.position=(r.endPos!=null?r.endPos:r.stationPilePos).position;
            }
            r.robotArm.targetPartType=r.targetPartType;
            r.robotArm.SetTarget(r.trackingTarget);
            slots.Add(new Slot{robot=r,tracker=r.trackingTarget});
        }
        car=null;parts=null;attempted.Clear();
    }
    public void SetDutyStatus(bool onDuty,bool overtime)
    { if(WorkersOnDuty==onDuty&&Overtime==overtime)return;WorkersOnDuty=onDuty;Overtime=overtime;if(!onDuty&&!robotMode)CancelAll(); }
    public float HammerInterval(Worker worker) => Settings.npcHitInterval*(2f-Mathf.Clamp01(worker.condition/100f));

    void Update()
    {
        if(progression==null||!progression.prototypeMode||Settings==null)return;
        if(!configured)Configure();
        bool zoneOccupied=StationWorkZone.Find(progression.mainSpline,Entry.gateProgress,Settings.workZoneMeters)!=null;
        if(!robotMode)foreach(var s in slots)if(s.worker!=null && s.worker.isActiveAndEnabled)
        {
            if(!zoneOccupied)s.worker.condition=Mathf.Min(100,s.worker.condition+Settings.idleRecoveryPerSecond*Time.deltaTime);
            if(s.part==null)s.worker.PrototypeMoveTo(s.home,Worker.WorkerState.Idle,null);
        }
        if(!Entry.hired || (!robotMode&&!WorkersOnDuty)){ReturnArms();return;}
        if(car!=null && !StationWorkZone.Contains(car,progression.mainSpline,Entry.gateProgress,Settings.workZoneMeters))
        {CancelAll();car=null;parts=null;attempted.Clear();}
        if(car==null)
        {
            car=StationWorkZone.Find(progression.mainSpline,Entry.gateProgress,Settings.workZoneMeters);
            if(car!=null)parts=car.GetComponentsInChildren<AssemblyPart>(true);
        }
        if(car!=null)
        {
            foreach(var s in slots)
            {
                if(s.part==null)Assign(s);
                if(s.part!=null)Tick(s);
            }
        }
        ReturnArms();
    }
    void Assign(Slot slot)
    {
        if(slot.worker!=null&&!slot.worker.isActiveAndEnabled)return;
        if(slot.robot!=null&&!slot.robot.gameObject.activeInHierarchy)return;
        foreach(var type in Entry.parts)
        {
            if(attempted.Contains(type))continue;
            if(slot.robot!=null && slot.robot.targetPartType!=type)continue;
            AssemblyPart part=null;
            foreach(var candidate in parts)if(candidate!=null&&candidate.myType==type){part=candidate;break;}
            if(part==null||part.IsAssembled){attempted.Add(type);continue;}
            if(part.IsPlayerAttaching)continue;
            // A wheel pair stays on its own side of the moving car.
            if(slot.worker!=null && slots.Count==2 && Entry.wheel!=null)
            {
                float homeSide=car.transform.InverseTransformPoint(slot.home).x;
                float partSide=car.transform.InverseTransformPoint(part.AttachmentWorldPosition).x;
                if((homeSide<0)!=(partSide<0))continue;
            }
            if(!stocks.TryGetValue(type,out var stock))continue;
            bool alreadyAttached=part.IsPlayerAttached||part.currentWork>0;
            Vector3 source=slot.robot!=null&&slot.robot.stationPilePos!=null?slot.robot.stationPilePos.position:stock.GetTopSlotWorldPos();
            if(!alreadyAttached && !stock.TryConsume())continue;
            if(!alreadyAttached && !part.TryBeginPlayerAttachment(this,source,stock.GetTopSlotWorldRot())){stock.Add(1);continue;}
            attempted.Add(type);slot.part=part;slot.renderers=part.GetComponentsInChildren<Renderer>(true);slot.stock=stock;slot.source=source;slot.consumed=!alreadyAttached;
            slot.attached=alreadyAttached;slot.elapsed=0;
            slot.phase=alreadyAttached?Phase.Hits:Phase.Fetch;
            slot.nextHit=Time.time+(slot.worker!=null?HammerInterval(slot.worker):Settings.robotSparkInterval);
            if(slot.effects==null)
            {
                var actor=slot.worker!=null?slot.worker.gameObject:slot.robot.gameObject;
                slot.effects=actor.GetComponent<PrototypeAssemblyEffects>();
                if(slot.effects==null)slot.effects=actor.AddComponent<PrototypeAssemblyEffects>();
                slot.effects.Configure(slot.robot!=null);
            }
            return;
        }
    }
    Vector3 Contact(Slot s)
    {
        Vector3 origin=s.robot!=null?s.robot.robotArm.joints[0].bone.position:s.worker.transform.position+Vector3.up*1.2f;
        Vector3 best=s.part.GetArmLookWorldPos();float distance=float.MaxValue;
        if(s.renderers!=null)foreach(var renderer in s.renderers)
        {
            if(renderer==null)continue;Vector3 point=renderer.bounds.ClosestPoint(origin);float d=(point-origin).sqrMagnitude;
            if(d<distance){distance=d;best=point;}
        }
        return best;
    }
    bool RobotReached(Slot s)
    {
        if(s.robot==null)return true;
        var end=s.robot.robotArm.endEffector;
        return end!=null&&Vector3.Distance(end.position,Contact(s))<=StationConfig.Instance.assembleReachThreshold+StationConfig.Instance.reachReleaseMargin;
    }
    Vector3 Stand(Slot s)
    {
        var point=s.part.AttachmentWorldPosition;
        float side=Vector3.Dot(s.home-car.transform.position,car.transform.right)<0?-1:1;
        float lateral=Vector3.Dot(point-car.transform.position,car.transform.right);
        point+=car.transform.right*(side*Mathf.Max(1.45f,Mathf.Abs(lateral)+.55f)-lateral);
        point.y=s.home.y;return point;
    }
    void Tick(Slot s)
    {
        // Recheck at every write, so component Update order cannot grant a late hit.
        if(!StationWorkZone.Contains(car,progression.mainSpline,Entry.gateProgress,Settings.workZoneMeters))return;
        if(s.part.IsAssembled){Finish(s);return;}
        if(s.tracker!=null)
            s.tracker.position=Vector3.MoveTowards(s.tracker.position,Contact(s),StationConfig.Instance.trackingTargetSpeed*Time.deltaTime);
        switch(s.phase)
        {
            case Phase.Fetch:
                bool fetched=s.worker!=null?s.worker.PrototypeMoveTo(s.source,Worker.WorkerState.Fetching,s.part):RobotReached(s);
                if(fetched){if(s.worker!=null)s.worker.PrototypeGrip(s.part);s.phase=Phase.Approach;}
                break;
            case Phase.Approach:
                bool arrived=s.worker==null||s.worker.PrototypeMoveTo(Stand(s),Worker.WorkerState.MovingToPart,s.part);
                if(s.worker!=null)s.part.UpdateAttachmentCarryPose(this,s.worker.PrototypeCarryPoint,s.worker.transform.rotation);
                if(arrived){s.phase=Phase.Attach;s.elapsed=0;}
                break;
            case Phase.Attach:
                if(s.worker!=null)s.worker.PrototypeMoveTo(Stand(s),Worker.WorkerState.Working,s.part);
                // Transit may cross a joint singularity. After the initial pickup, keep the
                // attachment moving; each spark still requires real tip contact at the car.
                s.elapsed+=Time.deltaTime;
                float duration=s.worker!=null?Settings.npcAttachSeconds:Settings.robotAttachSeconds;
                if(!s.part.AdvancePlayerAttachment(this,s.elapsed/Mathf.Max(.01f,duration))){Cancel(s);break;}
                if(s.elapsed<duration)break;
                if(!s.part.CompletePlayerAttachment(this)){Cancel(s);break;}
                s.attached=true;s.phase=Phase.Hits;
                if(s.worker!=null)s.worker.PrototypeRelease();
                s.nextHit=Time.time+(s.worker!=null?HammerInterval(s.worker):Settings.robotSparkInterval);
                break;
            case Phase.Hits:
                if(s.worker!=null)s.worker.PrototypeMoveTo(Stand(s),Worker.WorkerState.Working,s.part);
                s.effects.WorkingPart=s.part;
                if(s.worker!=null && !s.effects.HammerRunning && Time.time>=s.nextHit-WorkerHammerAnimation.Duration)
                    s.effects.BeginHammerSwing();
                if(Time.time<s.nextHit||!RobotReached(s))break;
                // Automatic crew keeps the established timed work rule; physical contact gates player input.
                if(s.worker!=null)s.effects.EndHammerSwing();

                if(s.part.TryPlayerHammer(3))
                {
                    StrikeCount++;s.effects.Pulse(Contact(s),s.part.IsAssembled);
                    s.nextHit=Time.time+(s.worker!=null?HammerInterval(s.worker):Settings.robotSparkInterval);
                    if(s.part.IsAssembled)Finish(s);
                }
                break;
        }
    }
    void Finish(Slot s)
    {
        CompletedPartCount++;
        if(s.worker!=null)s.worker.condition=Mathf.Max(0,s.worker.condition-Settings.conditionCost*(Overtime?Settings.overtimeFatigueMultiplier:1f));
        Clear(s);
    }
    void Cancel(Slot s)
    {
        if(s.part==null)return;
        if(!s.attached)
        {
            s.part.CancelPlayerAttachment(this);
            if(s.consumed&&s.stock!=null)s.stock.Add(1);
        }
        else s.part.StopWorldAssembly();
        if(!s.part.IsAssembled)MissedPartCount++;
        Clear(s);
    }
    void Clear(Slot s)
    {
        if(s.worker!=null)s.worker.PrototypeRelease();
        if(s.effects!=null)s.effects.WorkingPart=null;
        s.part=null;s.stock=null;s.consumed=false;s.attached=false;
    }
    void CancelAll(){foreach(var s in slots)Cancel(s);}
    void ReturnArms()
    {
        foreach(var s in slots)if(s.part==null&&s.robot!=null&&s.tracker!=null)
        {
            var home=s.robot.endPos!=null?s.robot.endPos:s.robot.stationPilePos;
            if(home!=null)s.tracker.position=Vector3.MoveTowards(s.tracker.position,home.position,StationConfig.Instance.trackingTargetSpeed*Time.deltaTime);
        }
    }
    void OnDisable(){CancelAll();}
}
