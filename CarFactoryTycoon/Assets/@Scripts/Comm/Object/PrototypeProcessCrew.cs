using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>Prototype timed assembly. Legacy stopping/continuous-work controllers remain available to old scenes.</summary>
public sealed class PrototypeProcessCrew : MonoBehaviour, ILineGate, ITargetedLineGate
{
    public ProductionProgression progression;
    public int processIndex;
    public bool WorkersOnDuty = true, Overtime;
    public int CompletedPartCount { get; private set; }
    public int StrikeCount { get; private set; }
    public int MissedPartCount { get; private set; }
    public CarController CurrentCar => car;
    public AssemblyPart PreviewPartFor(Worker worker)
    {foreach(var s in slots)if(s.worker==worker)return s.part;return null;}
    public bool IsBusy { get { foreach(var s in slots) if(s.part!=null)return true;return false; } }
    public float LowestCondition { get { float c=100;foreach(var s in slots)if(s.worker!=null)c=Mathf.Min(c,s.worker.condition);return c; } }
    public string StatusText => !Entry.hired ? "미고용" : Entry.automated ? (IsBusy?"로봇 체결 중":"로봇 대기") :
        !WorkersOnDuty?"근무 종료":IsBusy?"체결 중 · 컨디션 "+Mathf.RoundToInt(LowestCondition)+"%":"작업자 대기 · 컨디션 "+Mathf.RoundToInt(LowestCondition)+"%";
    enum Phase { Fetch, Approach, WaitDock, Attach, Hits }
    sealed class Slot
    {
        public Worker worker;
        public WorkerReachEnvelope reach;
        public StationController robot;
        public Transform tracker;
        public Vector3 home, source, gripOffset, pathFrom, carryFrom;
        public Quaternion carryRotation;
        public ProcessGripSettings grip;
        public Vector3 sourceScale,robotGripLocal;
        public Quaternion pickupRotation;
        public ProcessActorLayout layout;
        public ProcessVehicleOverride vehicleLayout;
        public Phase pathPhase;
        public bool pathStarted;
        public float pathProgress, pathLength, robotPathProgress;
        public AssemblyPart part;
        public PartStack stock;
        public Phase phase;
        public float elapsed, nextHit;
        public bool consumed, attached;
        public UnityEngine.Object pickupOwner;
        public PrototypeAssemblyEffects effects;
        public Renderer[] renderers;
        public Vector3 carLocalWorkContact;
        public float pickupHandHeight;
    }
    readonly List<Slot> slots=new List<Slot>();
    readonly HashSet<PartType> attempted=new HashSet<PartType>();
    readonly Dictionary<PartType,PartStack> stocks=new Dictionary<PartType,PartStack>();
    CarController car;
    AssemblyPart[] parts;
    bool configured, robotMode, accepted, ownsStop, previewAwaitingEntry;
    int carVersion;
    float cycleStarted;
    LineTrafficManager registeredTraffic;
    CarController skippedCar;
    int skippedVersion;
    public string LastFailure { get; private set; }
    public float GateProgress => StopProgress(car);
    float StopProgress(CarController candidate)
    {
        if(progression==null || progression.mainSpline==null)return 1f;
        float length=progression.mainSpline.CalculateLength();
        float meters=ProcessAuthoringLayout.Record(progression,Entry)?.stopOffsetMeters??2f;
        var model=candidate!=null?progression.authoringProfile?.FindVehicle(candidate.authoringVehicleId):null;
        if(model!=null)foreach(var data in model.overrides)
            if(data.processId==Entry.authoringId&&data.overrideStop){meters=data.stopOffsetMeters;break;}
        return Mathf.Clamp01(Entry.gateProgress+Mathf.Clamp(meters,.1f,Mathf.Max(.1f,Settings.workZoneMeters-.1f))/Mathf.Max(.01f,length));
    }
    public bool GateEnabled => isActiveAndEnabled && accepted && !robotMode && WorkersOnDuty &&
        car!=null && car.gameObject.activeInHierarchy && car.LifeVersion==carVersion;
    public bool Blocks(CarController candidate) => GateEnabled && candidate==car && candidate.LifeVersion==carVersion;
    public bool PreviewPickupPending
    {get{if(!previewAwaitingEntry)return false;foreach(var slot in slots)if(slot.part!=null&&slot.phase==Phase.Fetch)return true;return false;}}
    // Explicit authoring-only entry point. The main game still starts from FindDetected().
    public bool BeginPreviewPickup(CarController candidate)
    {
        CancelCycle();Configure();
        if(candidate==null||!candidate.CanRepair||!candidate.gameObject.activeInHierarchy)return false;
        parts=candidate.GetComponentsInChildren<AssemblyPart>(true);
        if(!CanAccept(candidate)){parts=null;return false;}
        car=candidate;carVersion=car.LifeVersion;cycleStarted=Time.time;previewAwaitingEntry=true;accepted=false;
        foreach(var slot in slots)Assign(slot);
        return IsBusy;
    }
    public bool PreviewIdle {get;private set;}
    public float PreviewStopProgress(CarController candidate)=>StopProgress(candidate);
    public void SetPreviewIdle(bool value){PreviewIdle=value;if(value)CancelCycle();}
    public void ReloadLayout(){CancelCycle();LastFailure=null;configured=false;Configure();}
    // Rebind authoring data without cancelling a moving vehicle or consuming stock again.
    public void RefreshAuthoredLayout()
    {
        var record=ProcessAuthoringLayout.Record(progression,Entry);if(record==null)return;
        var frame=ProcessAuthoringLayout.Frame(progression,Entry);
        foreach(var slot in slots)
        {
            slot.layout=record.FindActor(ProcessAuthoringLayout.Key(slot.worker!=null?(Component)slot.worker:slot.robot));
            if(slot.layout==null)continue;
            slot.home=ProcessAuthoringLayout.WithDistance(slot.layout.home,slot.layout.side,progression.authoringProfile.actorDistance).WorldPosition(frame);
            if(slot.part!=null){slot.grip=ProcessPartGrip.Find(progression,slot.part.myType);
                if(slot.stock?.ReadyDisplay!=null)slot.sourceScale=slot.stock.ReadyDisplay.transform.lossyScale;
                ProcessPartGrip.Apply(slot.part,slot.grip,slot.sourceScale);
                slot.robotGripLocal=slot.grip!=null?Vector3.Scale(slot.grip.robot,ProcessPartGrip.ScaleRatio(slot.sourceScale,slot.part.transform.lossyScale)):Vector3.zero;slot.gripOffset=slot.part.AttachmentWorldPosition-AssembledContact(slot);}
            if(slot.phase==Phase.Fetch&&slot.stock!=null){slot.source=slot.stock.ReadyPosition;slot.pickupRotation=slot.stock.ReadyRotation;}
            if(slot.worker!=null)
            {
                if(slot.worker.handIK!=null){slot.worker.handIK.bodySupport=slot.layout.bodySupport;var rig=slot.worker.handIK.GetComponent<WorkerInteractionRig>();if(rig!=null)rig.enabled=slot.layout.useAnimationRigging;}
                if(slot.part!=null&&car!=null)CacheWorkerHandTargets(slot);
                if(slot.part!=null&&slot.consumed&&!slot.attached)slot.worker.PrototypeGrip(slot.part,false);
            }
        }
    }
    public void CancelCycle()
    {
        CancelAll(); car=null; parts=null; attempted.Clear();
    }
    void ReleaseStop()
    {
        if(ownsStop && car!=null && car.gameObject.activeInHierarchy && car.LifeVersion==carVersion)
        { car.SetProgress(Mathf.Max(car.pathProgress,GateProgress)); car.isMoving=true; }
        ownsStop=false;accepted=false;previewAwaitingEntry=false;
    }
    bool CanAccept(CarController candidate)
    {
        if(robotMode)return true;
        if(candidate.pathProgress>=StopProgress(candidate))return false;
        int available=0;
        foreach(var slot in slots)if(slot.worker!=null && slot.worker.isActiveAndEnabled &&
            slot.worker.condition>0f && !slot.worker.IsResting)available++;
        if(available==0)return false;
        bool needed=false;
        foreach(var type in Entry.parts)
        {
            AssemblyPart part=null;
            foreach(var item in parts)if(item!=null&&item.myType==type){part=item;break;}
            if(part==null||part.IsAssembled)continue;
            if(part.IsPlayerAttaching)return false;
            needed=true;
            if(!part.IsPlayerAttached && part.currentWork<=0 &&
                (!stocks.TryGetValue(type,out var stock)||!stock.HasReadyPart))return false;
        }
        return needed;
    }
    void UpdateStop()
    {
        if(!GateEnabled || ownsStop)return;
        if((GateProgress-car.pathProgress)*car.SplineLength<=.04f)
        {car.isMoving=false;ownsStop=true;}
    }
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
        if(registeredTraffic==null && LineTrafficManager.Instance!=null)
        {registeredTraffic=LineTrafficManager.Instance;registeredTraffic.RegisterGate(this);}
        if(configured && robotMode==e.automated)return;
        CancelAll(); slots.Clear(); stocks.Clear(); configured=true;robotMode=e.automated;
        if(e.humanStation!=null && e.humanStation.partStacks!=null)
            foreach(var stack in e.humanStation.partStacks)if(stack!=null)stocks[stack.partType]=stack;
        if(e.wheel!=null && e.wheel.humanPartStacks!=null)
            foreach(var stack in e.wheel.humanPartStacks)if(stack!=null)stocks[stack.partType]=stack;
        if(!robotMode && e.workers!=null)foreach(var w in e.workers)
        {
            if(w==null)continue;
            var layout=ProcessAuthoringLayout.Record(progression,e)?.FindActor(ProcessAuthoringLayout.Key(w));
            var home=layout!=null?ProcessAuthoringLayout.WithDistance(layout.home,layout.side,progression.authoringProfile.actorDistance).WorldPosition(ProcessAuthoringLayout.Frame(progression,e)):w.transform.position;
            if(w.handIK!=null)w.handIK.bodySupport=layout!=null&&layout.bodySupport;
            var rig=w.handIK!=null?w.handIK.GetComponent<WorkerInteractionRig>():null;
            if(layout!=null&&layout.useAnimationRigging&&rig==null)Debug.LogError("Prepare the worker interaction rig in edit mode: "+w.name,w);
            if(rig!=null)rig.enabled=layout!=null&&layout.useAnimationRigging;
            slots.Add(new Slot{worker=w,home=home,layout=layout,reach=WorkerReachEnvelope.Ensure(w)});
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
            slots.Add(new Slot{robot=r,tracker=r.trackingTarget,layout=ProcessAuthoringLayout.Record(progression,e)?.FindActor(ProcessAuthoringLayout.Key(r))});
        }
        car=null;parts=null;attempted.Clear();
    }
    public void SetDutyStatus(bool onDuty,bool overtime)
    { if(WorkersOnDuty==onDuty&&Overtime==overtime)return;WorkersOnDuty=onDuty;Overtime=overtime;if(!onDuty&&!robotMode)CancelCycle(); }
    public float HammerInterval(Worker worker) => Mathf.Max(WorkerHammerAnimation.Duration,Settings.npcHitInterval)*(2f-Mathf.Clamp01(worker.condition/100f));

    public bool Detects(CarController candidate)
    {
        if(candidate==null || !candidate.gameObject.activeInHierarchy || !candidate.CanRepair || candidate.targetSpline!=progression.mainSpline)return false;
        var contract = candidate.GetComponent<ProductionContract>();
        if (contract != null && contract.requireWholeWash && (processIndex >= contract.processCount || !candidate.TryGetComponent<VehicleWashSurface>(out var wash) || !wash.IsClean))
            return false;
        bool hasBox=false;
        if(Entry.robots!=null)foreach(var robot in Entry.robots)
        {
            if(robot==null || !robot.TryGetComponent<BoxCollider>(out var box) || box.size.sqrMagnitude < .0001f)continue;
            hasBox=true;
            var scale=box.transform.lossyScale;
            var half=Vector3.Scale(box.size*.5f,new Vector3(Mathf.Abs(scale.x),Mathf.Abs(scale.y),Mathf.Abs(scale.z)));
            var center=candidate.transform.position;Vector3 radius=Vector3.zero;
            if(candidate.TryGetComponent<BoxCollider>(out var body))
            {
                center=body.transform.TransformPoint(body.center);var size=Vector3.Scale(body.size*.5f,body.transform.lossyScale);
                var x=body.transform.right*Mathf.Abs(size.x);var y=body.transform.up*Mathf.Abs(size.y);var z=body.transform.forward*Mathf.Abs(size.z);
                radius=new Vector3(Mathf.Abs(Vector3.Dot(x,box.transform.right))+Mathf.Abs(Vector3.Dot(y,box.transform.right))+Mathf.Abs(Vector3.Dot(z,box.transform.right)),
                    Mathf.Abs(Vector3.Dot(x,box.transform.up))+Mathf.Abs(Vector3.Dot(y,box.transform.up))+Mathf.Abs(Vector3.Dot(z,box.transform.up)),
                    Mathf.Abs(Vector3.Dot(x,box.transform.forward))+Mathf.Abs(Vector3.Dot(y,box.transform.forward))+Mathf.Abs(Vector3.Dot(z,box.transform.forward)));
            }
            var delta=center-box.transform.TransformPoint(box.center);
            if(Mathf.Abs(Vector3.Dot(delta,box.transform.right))<=half.x+radius.x &&
                Mathf.Abs(Vector3.Dot(delta,box.transform.up))<=half.y+radius.y &&
                Mathf.Abs(Vector3.Dot(delta,box.transform.forward))<=half.z+radius.z)return true;
        }
        return !hasBox && StationWorkZone.Contains(candidate,progression.mainSpline,Entry.gateProgress,Settings.workZoneMeters);
    }
    CarController FindDetected()
    {
        if(LineTrafficManager.Instance==null)return null;
        foreach(var candidate in LineTrafficManager.Instance.ActiveCars)
            if(Detects(candidate) && (candidate!=skippedCar||candidate.LifeVersion!=skippedVersion))return candidate;
        return null;
    }
    void Update()
    {
        if(progression==null||!progression.prototypeMode||Settings==null)return;
        if(!configured)Configure();
        if(PreviewIdle){ReturnArms();return;}
        bool zoneOccupied=FindDetected()!=null;
        if(!robotMode)foreach(var s in slots)if(s.worker!=null && s.worker.isActiveAndEnabled)
        {
            if(!zoneOccupied)s.worker.condition=Mathf.Min(100,s.worker.condition+Settings.idleRecoveryPerSecond*Time.deltaTime);
            if(s.part==null)s.worker.PrototypeMoveTo(s.home,Worker.WorkerState.Idle,null);
        }
        if(!Entry.hired || (!robotMode&&!WorkersOnDuty)){CancelCycle();ReturnArms();return;}
        if(car==null && parts!=null){CancelAll();parts=null;attempted.Clear();}
        if(car!=null&&previewAwaitingEntry&&Detects(car))
        {previewAwaitingEntry=false;accepted=!robotMode;cycleStarted=Time.time;}
        if(car!=null && (car.LifeVersion!=carVersion || !car.gameObject.activeInHierarchy || !car.CanRepair || (!previewAwaitingEntry&&!Detects(car))))
        {CancelAll();car=null;parts=null;attempted.Clear();}
        if(car==null)
        {
            attempted.Clear();
            var candidate=FindDetected();
            if(candidate!=null && (candidate!=skippedCar||candidate.LifeVersion!=skippedVersion))
            {
                parts=candidate.GetComponentsInChildren<AssemblyPart>(true);
                if(CanAccept(candidate))
                {car=candidate;carVersion=car.LifeVersion;accepted=!robotMode;cycleStarted=Time.time;LastFailure=null;}
                else {skippedCar=candidate;skippedVersion=candidate.LifeVersion;parts=null;}
            }
        }
        if(car!=null)
        {
            UpdateStop();
            // Arrival is controlled by the line lever and traffic ahead of this car.
            // Start the work timeout at the dock so a stopped line cannot consume more stock.
            if(!ownsStop)cycleStarted=Time.time;
            if(!robotMode && ownsStop && Time.time-cycleStarted>Mathf.Max(45f,Entry.parts.Length*15f))
            {LastFailure="Worker cycle timed out; check reach and authored route.";Debug.LogWarning(LastFailure,this);CancelCycle();return;}
            foreach(var s in slots)
            {
                if(s.part==null){if(s.stock!=null)Clear(s);Assign(s);}
                if(s.part!=null)Tick(s);
            }
            if(!robotMode && !IsBusy)foreach(var s in slots)Assign(s);
            if(!robotMode && !IsBusy)
            {skippedCar=car;skippedVersion=carVersion;ReleaseStop();car=null;parts=null;}
        }
        ReturnArms();
    }
    void Assign(Slot slot)
    {
        if(slot.worker!=null&&(!slot.worker.isActiveAndEnabled||slot.worker.IsResting||slot.worker.condition<=0f))return;
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
            var owner=slot.worker!=null?(UnityEngine.Object)slot.worker:slot.robot.robotArm;
            if(!alreadyAttached && !stock.TryReserveReady(owner))continue;
            Vector3 source=stock.ReadyPosition;
            slot.grip=ProcessPartGrip.Find(progression,type);slot.sourceScale=stock.ReadyDisplay!=null?stock.ReadyDisplay.transform.lossyScale:part.transform.lossyScale;
            ProcessPartGrip.Apply(part,slot.grip,slot.sourceScale);slot.robotGripLocal=slot.grip!=null?Vector3.Scale(slot.grip.robot,ProcessPartGrip.ScaleRatio(slot.sourceScale,part.transform.lossyScale)):Vector3.zero;slot.pickupRotation=stock.ReadyRotation;
            slot.vehicleLayout=null;
            var model=progression.authoringProfile?.FindVehicle(car.authoringVehicleId);
            if(model!=null && slot.layout!=null)foreach(var data in model.overrides)
                if(data.processId==Entry.authoringId && data.actorId==slot.layout.id && (data.part==PartType.None||data.part==type))
                {slot.vehicleLayout=data;if(data.part==type)break;}
            attempted.Add(type);slot.part=part;slot.renderers=car.GetPartRenderers(part);slot.stock=stock;slot.source=source;slot.consumed=false;
            if(slot.worker!=null)CacheWorkerHandTargets(slot);
            slot.pickupOwner=owner;slot.attached=alreadyAttached;slot.elapsed=0;
            slot.phase=alreadyAttached?Phase.Hits:Phase.Fetch;
            if(slot.robot!=null)slot.gripOffset=part.AttachmentWorldPosition-AssembledContact(slot);
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
    void CacheWorkerHandTargets(Slot s)
    {
        var w = s.worker;
        if (!WorkerHandIK.TryComputeGrip(s.part.transform, w.maxGripHalfSpan, w.gripEndInset, w.gripFaceInset,
            out var left, out var right, out _))
        {
            s.carLocalWorkContact = car.transform.InverseTransformPoint(AssembledContact(s));
            s.pickupHandHeight = s.source.y;
            return;
        }
        var center = (left + right) * .5f;
        var scale = s.part.transform.lossyScale;
        var contact = ProcessPartGrip.Contact(s.part.AttachmentWorldPosition, s.part.AttachmentWorldRotation, scale, center);
        s.carLocalWorkContact = car.transform.InverseTransformPoint(contact);
        s.pickupHandHeight = ProcessPartGrip.Contact(s.source, s.pickupRotation, scale, center).y;
    }

    Vector3 Contact(Slot s)
    {
        if(s.robot!=null&&s.grip!=null)return s.part.transform.TransformPoint(s.robotGripLocal);
        Vector3 origin=s.robot!=null?s.robot.robotArm.joints[0].bone.position:s.worker.transform.position+Vector3.up*1.2f;
        Vector3 best=s.part.GetArmLookWorldPos();float distance=float.MaxValue;
        if(s.renderers!=null)foreach(var renderer in s.renderers)
        {
            if(renderer==null)continue;Vector3 point=renderer.bounds.ClosestPoint(origin);float d=(point-origin).sqrMagnitude;
            if(d<distance){distance=d;best=point;}
        }
        return best;
    }
    Vector3 AssembledContact(Slot s)
    {
        if(s.robot!=null&&s.grip!=null)return ProcessPartGrip.Contact(s.part.AttachmentWorldPosition,s.part.AttachmentWorldRotation,s.part.transform.lossyScale,s.robotGripLocal);
        var origin=s.robot!=null?s.robot.robotArm.joints[0].bone.position:s.worker.transform.position+Vector3.up*1.2f;
        var goal=s.part.AttachmentWorldPosition;
        var rotation=s.part.AttachmentWorldRotation*Quaternion.Inverse(s.part.transform.rotation);
        var best=goal;float distance=float.MaxValue;
        foreach(var renderer in s.renderers)
        {
            if(renderer==null)continue;
            var local=renderer.localBounds;var bounds=new Bounds();bool first=true;
            for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
            {
                var world=renderer.transform.TransformPoint(local.center+Vector3.Scale(local.extents,new Vector3(x,y,z)));
                var point=goal+rotation*(world-s.part.transform.position);
                if(first){bounds=new Bounds(point,Vector3.zero);first=false;}else bounds.Encapsulate(point);
            }
            var contact=bounds.ClosestPoint(origin);float d=(contact-origin).sqrMagnitude;
            if(d<distance){distance=d;best=contact;}
        }
        return best;
    }
    public static bool WorkerCanReach(Worker worker,Vector3 point,float allowance=.55f)
    {
        var animator=worker!=null?worker.GetComponentInChildren<Animator>():null;
        if(animator==null||!animator.isHuman)return false;
        for(int side=0;side<2;side++)
        {
            bool left=side==0;var shoulder=animator.GetBoneTransform(left?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm);
            var elbow=animator.GetBoneTransform(left?HumanBodyBones.LeftLowerArm:HumanBodyBones.RightLowerArm);
            var hand=animator.GetBoneTransform(left?HumanBodyBones.LeftHand:HumanBodyBones.RightHand);
            if(shoulder!=null&&elbow!=null&&hand!=null&&Vector3.Distance(shoulder.position,point)<=Vector3.Distance(shoulder.position,elbow.position)+Vector3.Distance(elbow.position,hand.position)+allowance)return true;
        }
        return false;
    }
    bool RobotReached(Slot s)
    {
        if(s.robot==null)return WorkerCanReach(s.worker,Contact(s),.8f);
        var end=s.robot.robotArm.endEffector;
        return end!=null&&Vector3.Distance(end.position,Contact(s))<=StationConfig.Instance.assembleReachThreshold+StationConfig.Instance.reachReleaseMargin;
    }
    Vector3 Stand(Slot s)
    {
        var point=s.part.AttachmentWorldPosition;
        if(!robotMode)
        {
            var data=s.layout;
            if(data!=null && s.vehicleLayout!=null && s.vehicleLayout.overrideWait)
                return ProcessAuthoringLayout.WithDistance(s.vehicleLayout.assemblyWait,data.side,progression.authoringProfile.actorDistance).WorldPosition(ProcessAuthoringLayout.Frame(progression,Entry));
            if(data!=null)return ProcessAuthoringLayout.WithDistance(data.assemblyWait,data.side,progression.authoringProfile.actorDistance).WorldPosition(ProcessAuthoringLayout.Frame(progression,Entry));
        }
        return DefaultWorkStand(s);
    }
    Vector3 DefaultWorkStand(Slot s)=>DefaultWorkStandFor(car,s.part.AttachmentWorldPosition,s.home);
    public static Vector3 DefaultWorkStandFor(CarController candidate,Vector3 attachment,Vector3 home)
    {
        var point=attachment;
        float side=Vector3.Dot(home-candidate.transform.position,candidate.transform.right)<0?-1:1;
        var local=candidate.transform.InverseTransformPoint(point);
        if(Mathf.Abs(local.x)<.5f && Mathf.Abs(local.z)>1.2f)
        {
            float half=candidate.TryGetComponent<BoxCollider>(out var body)?body.size.z*.5f:2.3f;
            point=candidate.transform.TransformPoint(new Vector3(side*.35f,0,Mathf.Sign(local.z)*(half+.5f)));point.y=home.y;return point;
        }
        float lateral=Vector3.Dot(point-candidate.transform.position,candidate.transform.right);
        point+=candidate.transform.right*(side*Mathf.Max(1.45f,Mathf.Abs(lateral)+.55f)-lateral);
        point.y=home.y;return point;
    }
    Vector3 WorkStand(Slot s)
    {
        if(s.layout!=null && s.vehicleLayout!=null && s.vehicleLayout.overrideWork)
            return ProcessAuthoringLayout.WithDistance(s.vehicleLayout.workPose,s.layout.side,progression.authoringProfile.actorDistance).WorldPosition(ProcessAuthoringLayout.Frame(progression,Entry));
        if(s.layout!=null && s.layout.hasWorkPose)
            return ProcessAuthoringLayout.WithDistance(s.layout.workPose,s.layout.side,progression.authoringProfile.actorDistance).WorldPosition(ProcessAuthoringLayout.Frame(progression,Entry));
        return DefaultWorkStand(s);
    }
    AuthoringCurve Curve(Slot s,int kind)
    {
        if(s.vehicleLayout!=null && s.vehicleLayout.overridePaths)
        {var v=s.vehicleLayout;return kind==0?v.fetchPath:kind==1?v.approachPath:kind==2?v.walkPath:v.carryPath;}
        var a=s.layout;return a==null?null:kind==0?a.fetchPath:kind==1?a.approachPath:kind==2?a.walkPath:a.carryPath;
    }
    bool MoveWorker(Slot s,Vector3 goal,Worker.WorkerState state,AuthoringCurve curve)
    {
        if(curve==null || !curve.enabled)
        {
            if(state==Worker.WorkerState.Working && s.layout!=null && !s.layout.hasWorkPose && !(s.vehicleLayout!=null&&s.vehicleLayout.overrideWork))
            {
                var localGoal=car.transform.InverseTransformPoint(goal);var localWorker=car.transform.InverseTransformPoint(s.worker.transform.position);
                if(Mathf.Abs(localGoal.x)<.75f && Mathf.Abs(localWorker.z-localGoal.z)>.15f)
                {
                    float side=Vector3.Dot(s.home-car.transform.position,car.transform.right)<0?-1:1;
                    var corner=car.transform.TransformPoint(new Vector3(side*1.5f,0,localGoal.z));corner.y=s.home.y;
                    s.worker.PrototypeMoveTo(corner,state,s.part);return false;
                }
            }
            return s.worker.PrototypeMoveTo(goal,state,s.part);
        }
        var frame=ProcessAuthoringLayout.Frame(progression,Entry);
        goal.y=s.home.y;
        if(!s.pathStarted || s.pathPhase!=s.phase)
        {
            s.pathStarted=true;s.pathPhase=s.phase;s.pathProgress=0;s.pathFrom=s.worker.transform.position;
            s.pathLength=ProcessAuthoringPath.Length(curve,frame.rotation,s.pathFrom,goal);
            if(!ProcessAuthoringPath.Walkable(curve,frame.rotation,s.pathFrom,goal,out var obstruction))
            {LastFailure="Worker path blocked at "+obstruction;Cancel(s);return false;}
        }
        s.pathProgress=Mathf.Clamp01(s.pathProgress+s.worker.moveSpeed*Time.deltaTime/s.pathLength);
        var point=ProcessAuthoringPath.Evaluate(curve,frame.rotation,s.pathFrom,goal,s.pathProgress);
        return s.worker.PrototypeMoveTo(point,state,s.part)&&s.pathProgress>=1f;
    }
    Vector3 ReadyGripCenter(Slot s)=>ProcessPartGrip.Contact(s.source,s.pickupRotation,s.sourceScale,(s.grip.left+s.grip.right)*.5f);
    Vector3 FetchStand(Slot s)
    {
        if(s.grip==null||s.worker==null)return s.source;
        var center=ReadyGripCenter(s);var away=Vector3.ProjectOnPlane(s.home-center,Vector3.up);
        if(away.sqrMagnitude<.001f)away=-s.worker.transform.forward;
        ReadyWristPoints(s,out var a,out var b);
        return s.reach!=null&&s.reach.Ready?(s.grip.anatomicalWrist?s.reach.BalancedStandFor(a,b,away,s.home.y,s.grip.reachFraction):s.reach.StandFor(a,b,away,s.home.y,s.grip.reachFraction)):center;
    }
    void ReadyWristPoints(Slot s,out Vector3 a,out Vector3 b)
    {
        a=ProcessPartGrip.Contact(s.source,s.pickupRotation,s.sourceScale,s.grip.left);
        b=ProcessPartGrip.Contact(s.source,s.pickupRotation,s.sourceScale,s.grip.right);
        var across=b-a;var finger=Vector3.ProjectOnPlane(s.pickupRotation*Vector3.Scale(s.sourceScale,s.grip.finger),across);
        if(finger.sqrMagnitude<.000001f)finger=s.worker.transform.forward;finger.Normalize();
        a-=finger*s.worker.handIK.palmOffset;b-=finger*s.worker.handIK.palmOffset;
    }
    bool ReadyHandsTouch(Slot s)
    {
        if(s.worker.handIK==null||!s.worker.handIK.TryGetWristTargets(out var left,out var right,out var lr,out var rr))return false;
        var animator=s.worker.handIK.GetComponent<Animator>();if(animator==null||!animator.isHuman)return false;
        return (s.reach==null||s.reach.Contains(left,right))&&Vector3.Distance(animator.GetBoneTransform(HumanBodyBones.LeftHand).position,left)<.12f&&
            Vector3.Distance(animator.GetBoneTransform(HumanBodyBones.RightHand).position,right)<.12f;
    }
    Vector3 RobotAssemblyGoal(Slot s)=>s.grip!=null?AssembledContact(s):s.part.AttachmentWorldPosition-s.gripOffset;
    Vector3 PickupContact(Slot s)=>s.grip!=null?ProcessPartGrip.Contact(s.source,s.pickupRotation,s.sourceScale,s.grip.robot):s.source-s.gripOffset;
    void FollowRobotContact(Slot s)
    {
        if(s.robot==null||s.part==null||s.phase==Phase.Fetch||s.phase==Phase.Hits||!s.consumed)return;
        var rotation=s.grip!=null?Quaternion.Slerp(s.pickupRotation,s.part.AttachmentWorldRotation,s.robotPathProgress):s.part.AttachmentWorldRotation;
        var position=s.grip!=null?ProcessPartGrip.RootAtContact(s.robot.robotArm.endEffector.position,rotation,s.part.transform.lossyScale,s.robotGripLocal):s.robot.robotArm.endEffector.position+s.gripOffset;
        s.part.UpdateAttachmentCarryPose(this,position,rotation);
    }
    void Tick(Slot s)
    {
        // A retried preview may fetch early, but cannot chase/fasten onto a vehicle outside the work zone.
        if(previewAwaitingEntry&&s.phase!=Phase.Fetch)
        {
            if(s.robot!=null){s.robot.robotArm.TrackTargetNow();FollowRobotContact(s);return;}
            if(s.phase==Phase.Attach||s.phase==Phase.Hits)return;
        }
        var carryCurve=Curve(s,3);
        if(s.robot!=null&&s.grip!=null&&s.consumed&&s.phase!=Phase.Hits)s.robotPathProgress=Mathf.Clamp01(s.robotPathProgress+Time.deltaTime/Mathf.Max(.1f,Settings.robotAttachSeconds));
        // Recheck at every write, so component Update order cannot grant a late hit.
        if(!previewAwaitingEntry&&!Detects(car))return;
        if(s.part.IsAssembled){Finish(s);return;}
        if(s.tracker!=null)
        {
            if(s.phase!=Phase.Fetch && s.phase!=Phase.Hits && carryCurve!=null && carryCurve.enabled)
            {
                if(s.grip==null)s.robotPathProgress=Mathf.Clamp01(s.robotPathProgress+Time.deltaTime/Mathf.Max(.1f,Settings.robotAttachSeconds));
                var goal=ProcessAuthoringPath.Evaluate(carryCurve,ProcessAuthoringLayout.Frame(progression,Entry).rotation,PickupContact(s),RobotAssemblyGoal(s),s.robotPathProgress);
                s.tracker.position=Vector3.MoveTowards(s.tracker.position,goal,StationConfig.Instance.trackingTargetSpeed*Time.deltaTime);
            }
            else s.tracker.position=Vector3.MoveTowards(s.tracker.position,
                s.robot!=null && (s.phase==Phase.Approach || s.phase==Phase.Attach)
                    ? RobotAssemblyGoal(s) : s.phase==Phase.Fetch ? PickupContact(s) : Contact(s),
                StationConfig.Instance.trackingTargetSpeed*Time.deltaTime);
        }
        if(s.robot!=null){s.robot.robotArm.TrackTargetNow();FollowRobotContact(s);}
        switch(s.phase)
        {
            case Phase.Fetch:
                bool fetched;
                if (s.worker != null)
                {
                    if (s.grip != null && s.reach != null) s.reach.SamplePickupShoulders(s.worker);
                    var pickupStand = FetchStand(s);
                    s.worker.ApproachWorkHeight(pickupStand, s.pickupHandHeight);
                    fetched = MoveWorker(s, pickupStand, Worker.WorkerState.Fetching, Curve(s,0));
                }
                else fetched = s.robot.robotArm.endEffector!=null && Vector3.Distance(s.robot.robotArm.endEffector.position,PickupContact(s))<=(s.grip!=null?.04f:StationConfig.Instance.assembleReachThreshold);
                if(!fetched){s.elapsed=0;break;}
                if(s.worker!=null&&s.grip!=null)
                {
                    s.worker.PrototypeFacePoint(ReadyGripCenter(s));
                    ReadyWristPoints(s,out var readyLeft,out var readyRight);
                    if(s.reach!=null&&!s.reach.Contains(readyLeft,readyRight))
                    {s.elapsed+=Time.deltaTime;if(s.elapsed>2f){LastFailure="Pickup outside measured arm reach; edit slot or contacts.";Cancel(s);}break;}
                    s.worker.PrototypeReachReady(s.stock.ReadyDisplay!=null?s.stock.ReadyDisplay.transform:null,s.grip);
                    s.elapsed+=Time.deltaTime;
                    if(s.elapsed<.3f||!ReadyHandsTouch(s))
                    {if(s.elapsed>2f){LastFailure="Pickup hands out of reach; edit the slot or grip contacts.";Cancel(s);}break;}
                }
                if(!s.stock.TryTakeReady(s.pickupOwner,s.worker!=null?(s.worker.handPos!=null?s.worker.handPos:s.worker.transform):s.robot.robotArm.endEffector,out var pickupPosition,out var pickupRotation)){Cancel(s);break;}
                s.consumed=true;
                if(s.reach!=null)s.reach.ClearPickupShoulders();
                if(!s.part.TryBeginPlayerAttachment(this,pickupPosition,pickupRotation)){Cancel(s);break;}
                if(s.worker!=null){s.carryFrom=pickupPosition;s.carryRotation=pickupRotation;s.worker.PrototypeGrip(s.part,s.grip==null);}
                s.phase=Phase.Approach;s.elapsed=0;FollowRobotContact(s);
                break;
            case Phase.Approach:
                bool arrived=s.worker==null||MoveWorker(s,Stand(s),Worker.WorkerState.MovingToPart,Curve(s,1));
                if(s.part==null)break;
                if(s.worker!=null)
                {
                    s.elapsed+=Time.deltaTime;float lift=s.grip!=null?Mathf.SmoothStep(0,1,s.elapsed/.35f):1;
                    s.part.UpdateAttachmentCarryPose(this,Vector3.Lerp(s.carryFrom,s.worker.PrototypeCarryPoint,lift),Quaternion.Slerp(s.carryRotation,s.worker.PrototypeCarryRotation,lift));
                    arrived&=lift>=1;
                }
                if(arrived){s.phase=s.worker!=null?Phase.WaitDock:Phase.Attach;s.elapsed=0;}
                break;
            case Phase.WaitDock:
                s.worker.PrototypeMoveTo(Stand(s),Worker.WorkerState.MovingToPart,s.part);
                s.part.UpdateAttachmentCarryPose(this,s.worker.PrototypeCarryPoint,s.worker.PrototypeCarryRotation);
                if(ownsStop){s.phase=Phase.Attach;s.elapsed=0;s.carryFrom=s.part.transform.position;s.carryRotation=s.part.transform.rotation;}
                break;
            case Phase.Attach:
                if (s.worker != null) s.worker.ApproachWorkHeight(WorkStand(s), car.transform.TransformPoint(s.carLocalWorkContact).y);
                if(s.worker!=null && !MoveWorker(s,WorkStand(s),Worker.WorkerState.Working,Curve(s,2)))
                {if(s.part!=null)s.part.UpdateAttachmentCarryPose(this,s.worker.PrototypeCarryPoint,s.worker.PrototypeCarryRotation);break;}
                if(s.part==null)break;
                if(s.worker!=null && !WorkerCanReach(s.worker,AssembledContact(s)))
                {LastFailure="Worker target out of reach; edit the work position or attachment.";Cancel(s);break;}
                if(s.worker!=null && s.elapsed<=0f){s.carryFrom=s.part.transform.position;s.carryRotation=s.part.transform.rotation;}
                if(s.robot!=null)
                {
                    FollowRobotContact(s);
                    var carried=s.part.transform.position;
                    if(Vector3.Distance(carried,s.part.AttachmentWorldPosition)>StationConfig.Instance.assembleReachThreshold ||
                        (s.grip!=null&&Quaternion.Angle(s.part.transform.rotation,s.part.AttachmentWorldRotation)>1f))break;
                    if(!s.part.CompletePlayerAttachment(this)){Cancel(s);break;}
                    s.attached=true;s.phase=Phase.Hits;s.nextHit=Time.time+Settings.robotSparkInterval;
                    break;
                }
                s.elapsed+=Time.deltaTime;
                float duration=s.worker!=null?Settings.npcAttachSeconds:Settings.robotAttachSeconds;
                if(carryCurve!=null && carryCurve.enabled)
                {
                    var t=Mathf.Clamp01(s.elapsed/Mathf.Max(.01f,duration));
                    var point=ProcessAuthoringPath.Evaluate(carryCurve,ProcessAuthoringLayout.Frame(progression,Entry).rotation,s.carryFrom,s.part.AttachmentWorldPosition,t);
                    if(!s.part.UpdateAttachmentCarryPose(this,point,Quaternion.Slerp(s.carryRotation,s.part.AttachmentWorldRotation,t))){Cancel(s);break;}
                }
                else if(!s.part.AdvancePlayerAttachment(this,s.elapsed/Mathf.Max(.01f,duration))){Cancel(s);break;}
                if(s.elapsed<duration)break;
                if(!s.part.CompletePlayerAttachment(this)){Cancel(s);break;}
                s.attached=true;s.phase=Phase.Hits;
                if(s.worker!=null)s.worker.PrototypeRelease();
                s.nextHit=Time.time+(s.worker!=null?HammerInterval(s.worker):Settings.robotSparkInterval);
                break;
            case Phase.Hits:
                if(s.worker!=null)
                {
                    var stand = WorkStand(s);
                    s.worker.ApproachWorkHeight(stand, car.transform.TransformPoint(s.carLocalWorkContact).y);
                    s.worker.PrototypeMoveTo(stand,Worker.WorkerState.Working,s.part);
                }
                s.effects.WorkingPart=s.part;
                if(s.worker!=null && !s.effects.HammerRunning && Time.time>=s.nextHit-WorkerHammerAnimation.Duration)
                    s.effects.BeginHammerSwing();
                if(!RobotReached(s)){if(s.worker!=null){LastFailure="Worker hammer target out of reach.";Cancel(s);}else s.nextHit=Mathf.Max(s.nextHit,Time.time);break;}
                if(Time.time<s.nextHit)break;
                // Automatic crew keeps the established timed work rule; physical contact gates player input.
                if(s.worker!=null)s.effects.EndHammerSwing();

                int due=s.robot!=null?Mathf.Clamp(Mathf.FloorToInt((Time.time-s.nextHit)/Mathf.Max(.01f,Settings.robotSparkInterval))+1,1,3):1;
                for(int hit=0;hit<due && s.part!=null;hit++)
                {
                    if(!s.part.TryPlayerHammer(3))break;
                    StrikeCount++;s.effects.Pulse(Contact(s),s.part.IsAssembled);
                    if(s.worker!=null)s.nextHit=Time.time+HammerInterval(s.worker);
                    else s.nextHit+=Mathf.Max(.01f,Settings.robotSparkInterval);
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
        if(s.part==null){Clear(s);return;}
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
        if(s.reach!=null)s.reach.ClearPickupShoulders();
        if(s.stock!=null && s.pickupOwner!=null)s.stock.ReleaseReadyReservation(s.pickupOwner);
        if(s.effects!=null)s.effects.WorkingPart=null;
        s.part=null;s.stock=null;s.pickupOwner=null;s.consumed=false;s.attached=false;s.pathStarted=false;s.robotPathProgress=0;
        if(s.tracker!=null)
        {
            var home=s.robot.endPos!=null?s.robot.endPos:s.robot.stationPilePos;
            if(home!=null)s.tracker.position=home.position;
        }
    }
    void CancelAll(){foreach(var s in slots)Cancel(s);ReleaseStop();}
    void ReturnArms()
    {
        foreach(var s in slots)if(s.part==null&&s.robot!=null&&s.tracker!=null)
        {
            var home=s.robot.endPos!=null?s.robot.endPos:s.robot.stationPilePos;
            if(home!=null)s.tracker.position=Vector3.MoveTowards(s.tracker.position,home.position,StationConfig.Instance.trackingTargetSpeed*Time.deltaTime);
        }
    }
    void OnDisable(){CancelCycle();if(registeredTraffic!=null)registeredTraffic.UnregisterGate(this);registeredTraffic=null;}
}
