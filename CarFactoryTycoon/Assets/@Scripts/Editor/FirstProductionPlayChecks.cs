#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

/// <summary>Live input driver. Never writes money, work progress, wash masks or vehicle progress.</summary>
public static class FirstProductionPlayChecks
{
    public static string Result="idle";
    public static readonly List<string> Evidence=new List<string>();
    static FirstProductionLoop L=>FirstProductionLoop.Current;
    static FactoryPlayerController P=>L.player;
    static readonly FieldInfo Pitch=typeof(FactoryPlayerController).GetField("pitch",BindingFlags.Instance|BindingFlags.NonPublic);
    sealed class AimNode{public Vector3 local,normal,approach;public int face,sample;public bool jump;}
    public static void StartWash()
    {Begin(Wash(L.LatestCar));}
    public static void StartAssembly(){Begin(Assemble(L.LatestCar));}
    public static void StartManualSecond(){Begin(ManualSecond());}
    public static void StartInvestment(){Begin(Invest());}
    public static void Start(){Begin(All());}
    static void Begin(IEnumerator routine)
    {
        if(!Application.isPlaying||L==null)throw new InvalidOperationException("Play main scene first.");
        if(Result.StartsWith("running"))throw new InvalidOperationException("Input check already active.");
        EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
        Result="running";L.StartCoroutine(Guard(routine));
    }
    static IEnumerator Guard(IEnumerator routine)
    {
        var stack=new Stack<IEnumerator>();stack.Push(routine);Exception failure=null;
        while(stack.Count>0)
        {
            object value=null;bool more=false;
            try{more=stack.Peek().MoveNext();if(more)value=stack.Peek().Current;}
            catch(Exception e){failure=e;}
            if(failure!=null)break;
            if(!more){(stack.Pop() as IDisposable)?.Dispose();continue;}
            if(value is IEnumerator child){stack.Push(child);continue;}
            yield return value;
        }
        Inputs();Result=failure==null?"PASS\n"+string.Join("\n",Evidence):"FAIL "+failure+"\n"+string.Join("\n",Evidence);
        SessionState.SetString("FirstProductionPlayChecks",Result);Debug.Log(Result);
    }
    static void Need(bool ok,string text){if(!ok)throw new InvalidOperationException(text);Evidence.Add(text);}
    static void Inputs(bool walk=false,bool sprint=false,bool jump=false,bool fire=false,bool use=false,Key key=Key.None)
    {
        var keys=new List<Key>();if(walk)keys.Add(Key.W);if(sprint)keys.Add(Key.LeftShift);if(jump)keys.Add(Key.Space);if(use)keys.Add(Key.E);if(key!=Key.None)keys.Add(key);
        InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState(keys.ToArray()));
        var mouse=new MouseState{position=new Vector2(Screen.width*.5f,Screen.height*.5f)}.WithButton(MouseButton.Right);
        if(fire)mouse=mouse.WithButton(MouseButton.Left);
        InputSystem.QueueStateEvent(Mouse.current,mouse);
    }
    static void Aim(Vector3 point)
    {
        var direction=(point-P.viewCamera.transform.position).normalized;if(direction.sqrMagnitude<.001f)return;
        float yaw=Mathf.Atan2(direction.x,direction.z)*Mathf.Rad2Deg;
        float pitch=-Mathf.Asin(Mathf.Clamp(direction.y,-1,1))*Mathf.Rad2Deg;
        P.RotateView(new Vector2(Mathf.DeltaAngle(P.ViewYaw,yaw),(float)Pitch.GetValue(P)-pitch));
    }
    static IEnumerator Hold(Func<Vector3> aim,float seconds,bool fire=false,bool use=false,bool jump=false,Key key=Key.None)
    {
        float end=Time.time+seconds;
        while(Time.time<end){if(aim!=null)Aim(aim());Inputs(fire:fire,use:use,jump:jump,key:key);yield return null;}
        Inputs();yield return null;
    }
    static IEnumerator Walk(Vector3 target,float tolerance=.28f)
    {
        float deadline=Time.time+24,lastTime=Time.time;var previous=P.transform.position;
        while(Vector3.ProjectOnPlane(target-P.transform.position,Vector3.up).magnitude>tolerance)
        {
            var heading=target;heading.y=P.viewCamera.transform.position.y;Aim(heading);
            float distance=Vector3.ProjectOnPlane(target-P.transform.position,Vector3.up).magnitude;
            Inputs(walk:true,sprint:distance>3);
            if(Time.time>deadline)throw new InvalidOperationException("Walk timed out "+P.transform.position+" -> "+target);
            if(Time.time-lastTime>3){if(Vector3.Distance(previous,P.transform.position)<.15f)throw new InvalidOperationException("Walk blocked "+P.transform.position+" -> "+target);previous=P.transform.position;lastTime=Time.time;}
            yield return null;
        }
        Inputs();yield return null;
    }
    static bool Crosses(Vector3 a,Vector3 b,Bounds box)
    {
        a.y=box.center.y;b.y=box.center.y;var d=b-a;float length=d.magnitude;if(length<.001f)return false;
        return box.IntersectRay(new Ray(a,d/length),out float hit)&&hit<length-.01f;
    }
    static IEnumerator Around(CarController car,Vector3 target)
    {
        var a=car.transform.InverseTransformPoint(P.transform.position);
        if(Mathf.Abs(a.x)<1.8f&&Mathf.Abs(a.z)<3.25f)
        {
            var escape=a;
            if(1.8f-Mathf.Abs(a.x)<3.25f-Mathf.Abs(a.z))escape.x=a.x>=0?2.35f:-2.35f;
            else escape.z=a.z>=0?3.95f:-3.95f;
            var outward=car.transform.TransformPoint(escape);outward.y=P.transform.position.y;
            yield return Walk(outward);a=car.transform.InverseTransformPoint(P.transform.position);
        }
        var destination=target;var b=car.transform.InverseTransformPoint(target);bool finalApproach=false;
        if(Mathf.Abs(b.x)<1.8f&&Mathf.Abs(b.z)<3.25f)
        {
            if(1.8f-Mathf.Abs(b.x)<3.25f-Mathf.Abs(b.z))b.x=b.x>=0?2.35f:-2.35f;
            else b.z=b.z>=0?3.95f:-3.95f;
            target=car.transform.TransformPoint(b);target.y=P.transform.position.y;finalApproach=true;
        }
        var box=new Bounds(new Vector3(0,1,0),new Vector3(3.5f,10,6.4f));
        if(!Crosses(a,b,box)){yield return Walk(target);if(finalApproach)yield return Walk(destination,.1f);yield break;}
        var corners=new[]{new Vector3(-2.3f,0,-3.9f),new Vector3(-2.3f,0,3.9f),new Vector3(2.3f,0,3.9f),new Vector3(2.3f,0,-3.9f)};
        float best=float.MaxValue;int first=-1,last=-1;
        for(int i=0;i<4;i++)for(int j=0;j<4;j++)
        {
            if(Crosses(a,corners[i],box)||Crosses(corners[j],b,box))continue;
            if(i!=j&&Crosses(corners[i],corners[j],box))continue;
            float cost=Vector3.Distance(a,corners[i])+Vector3.Distance(corners[i],corners[j])+Vector3.Distance(corners[j],b);
            if(cost<best){best=cost;first=i;last=j;}
        }
        if(first>=0)
        {
            var point=car.transform.TransformPoint(corners[first]);point.y=P.transform.position.y;yield return Walk(point);
            if(last!=first){point=car.transform.TransformPoint(corners[last]);point.y=P.transform.position.y;yield return Walk(point);}
        }
        yield return Walk(target);if(finalApproach)yield return Walk(destination,.1f);
    }
    static List<AimNode> Nodes(VehicleWashSurface wash,CarController car,bool dirtyOnly)
    {
        var points=wash.AimPoints;var normals=wash.AimNormals;var result=new List<AimNode>();var used=new HashSet<string>();
        var cells=typeof(VehicleWashSurface).GetField("cells",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(wash) as IList;
        for(int i=0;i<points.Count;i++)
        {
            if(dirtyOnly&&(float)cells[i].GetType().GetField("clean").GetValue(cells[i])>=1f)continue;
            var local=car.transform.InverseTransformPoint(points[i]);var n=car.transform.InverseTransformDirection(normals[i]);
            int face=n.y>.5f?4:Mathf.Abs(n.x)>Mathf.Abs(n.z)?n.x>0?0:1:n.z>0?2:3;
            string code=face+":"+Mathf.FloorToInt(local.x/.9f)+":"+Mathf.FloorToInt(local.y/.9f)+":"+Mathf.FloorToInt(local.z/.9f);
            if(!dirtyOnly&&!used.Add(code))continue;
            var cellType=cells[i].GetType();
            result.Add(new AimNode{local=local,normal=n,face=face,sample=i,
                approach=(Vector3)cellType.GetField("approach").GetValue(cells[i]),
                jump=(bool)cellType.GetField("jump").GetValue(cells[i])});
        }
        result.Sort((a,b)=>a.face.CompareTo(b.face));return result;
    }
    static IEnumerator Wash(CarController car)
    {
        Need(car!=null,"wash vehicle exists");L.SetWashTool(true);var wash=car.GetComponent<VehicleWashSurface>();
        Need(wash!=null&&wash.SampleCount>0,"wash targets derive from visible mesh");
        float wait=Time.time+45;
        if(L.AcceptedVehicles==1)while(car.pathProgress<L.WashProgress-.001f&&Time.time<wait){Inputs();yield return null;}
        var washCells=(IList)typeof(VehicleWashSurface).GetField("cells",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(wash);
        for(int pass=0;pass<5&&!wash.IsClean;pass++)
        {
            var nodes=Nodes(wash,car,pass>0);int count=0;
            foreach(var node in nodes)
            {
                if(wash.IsClean)break;
                if((float)washCells[node.sample].GetType().GetField("clean").GetValue(washCells[node.sample])>=1f)continue;
                var stand=car.transform.TransformPoint(node.approach);stand.y=P.transform.position.y;
                var localStand=car.transform.InverseTransformPoint(stand);var portal=localStand;
                if(Mathf.Abs(portal.x)<1.8f&&Mathf.Abs(portal.z)<3.25f)
                {
                    if(1.8f-Mathf.Abs(portal.x)<3.25f-Mathf.Abs(portal.z))portal.x=portal.x>=0?2.35f:-2.35f;
                    else portal.z=portal.z>=0?3.95f:-3.95f;
                }
                var outside=car.transform.TransformPoint(portal);outside.y=P.transform.position.y;
                yield return Around(car,outside);yield return Walk(stand,.12f);
                Result="running wash pass="+pass+" target="+(++count)+"/"+nodes.Count+" coverage="+wash.Coverage;
                yield return Hold(()=>car.transform.TransformPoint(node.local),node.jump?.95f:.5f,fire:true,jump:node.jump);
                if(node.jump)yield return Hold(null,.5f);
            }
        }
        Need(wash.IsClean,"whole exterior wash completed through held mouse input; coverage="+wash.Coverage);
        ScreenCapture.CaptureScreenshot("Temp/FirstProduction/wash-complete.png");
    }
    static IEnumerator ToAisle(float z=29.5f)
    {
        var target=P.transform.position;target.z=z;
        var car=L.LatestCar;
        if(car!=null&&car.gameObject.activeInHierarchy)yield return Around(car,target);
        else yield return Walk(target,.15f);
    }
    static IEnumerator Lever(PrototypeLineController.LeverPosition desired)
    {
        L.SetWashTool(false);var line=PrototypeLineController.Current;
        yield return ToAisle();
        var stand=line.leverRoot.position+Vector3.back*.8f;stand.y=P.transform.position.y;
        yield return Walk(stand,.15f);
        Func<Vector3> aim=()=>line.leverRoot.position+Vector3.up*1.35f;
        yield return Hold(aim,.3f);
        for(int i=0;i<4&&line.lever!=desired;i++)
        {yield return Hold(aim,.08f,fire:true);yield return Hold(aim,1.1f);}
        Need(line.lever==desired,"physical hammer lever selected "+desired);
    }
    static IEnumerator Assemble(CarController car)
    {
        L.SetWashTool(false);yield return Hold(null,.1f,key:Key.Digit2);
        var stock=L.progression.processes[0].humanStation.partStacks[0];int before=stock.Count;
        var side=Vector3.Dot(stock.ReadyBounds.center-car.transform.position,car.transform.right)>=0?car.transform.right:-car.transform.right;
        var pickup=stock.ReadyBounds.center-side*1.3f;pickup.y=P.transform.position.y;
        yield return ToAisle();
        var outer=P.transform.position;outer.x=stock.ReadyBounds.center.x-1.5f;
        yield return Walk(outer,.2f);yield return Walk(pickup,.15f);
        yield return Hold(()=>stock.ReadyBounds.center,.25f);
        yield return Hold(()=>stock.ReadyBounds.center,.25f,use:true);
        Need(P.interactor.HeldPart!=null,"dashboard picked up through E input");
        Need(stock.Count==before-1,"exactly one inventory part consumed");
        var part=car.GetComponentsInChildren<AssemblyPart>(true).First(x=>x.myType==PartType.Controll_21);
        if(L.AcceptedVehicles==1)
        {
            float wait=Time.time+40;
            while(car.pathProgress<L.GateProgress-.00015f&&Time.time<wait){Inputs();yield return null;}
        }
        var stand=P.interactor.AttachmentAimPoint(part)+side*1.4f;stand.y=P.transform.position.y;
        yield return Around(car,stand);
        yield return Hold(()=>P.interactor.AttachmentAimPoint(part),.25f);
        yield return Hold(()=>P.interactor.AttachmentAimPoint(part),2.8f,use:true);
        Need(part.IsPlayerAttached&&!P.interactor.IsAttaching,"dashboard hold attachment completed through E input");
        for(int i=0;i<8&&!part.IsAssembled;i++)
        {
            yield return Hold(()=>P.interactor.AttachmentAimPoint(part),.08f,fire:true);
            yield return Hold(()=>P.interactor.AttachmentAimPoint(part),1.05f);
        }
        Need(part.IsAssembled,"dashboard fastened by actual hammer swing contacts");
        int deliveries=L.SuccessfulDeliveries,money=EconomyManager.Instance.currentMoney;
        if(PrototypeLineController.Current.lever==PrototypeLineController.LeverPosition.Stop)
            yield return Lever(PrototypeLineController.LeverPosition.Low);
        float deadline=Time.time+65;
        while(L.SuccessfulDeliveries==deliveries&&Time.time<deadline){Inputs();yield return null;}
        Need(L.SuccessfulDeliveries==deliveries+1,"dolly handover completes delivery");
        Need(EconomyManager.Instance.currentMoney==money+L.settings.DeliveryPrice(1),"only agreed delivery payment earned");
        ScreenCapture.CaptureScreenshot("Temp/FirstProduction/delivery-"+L.SuccessfulDeliveries+".png");
    }
    static IEnumerator ManualSecond()
    {
        L.SetWashTool(false);var line=PrototypeLineController.Current;
        yield return ToAisle();
        var stand=new Vector3(line.supplyTunnel.position.x,P.transform.position.y,32.15f);
        yield return Walk(stand,.15f);
        Func<Vector3> aim=()=>new Vector3(line.supplyTunnel.position.x,1f,32.7f);
        yield return Hold(aim,.3f);
        int before=L.AcceptedVehicles,requests=L.spawner.TotalManualRequestsAccepted;
        for(int i=0;i<4&&L.spawner.TotalManualRequestsAccepted==requests;i++)
        {yield return Hold(aim,.08f,fire:true);yield return Hold(aim,1.1f);}
        float deadline=Time.time+8;while(L.AcceptedVehicles==before&&Time.time<deadline){Inputs();yield return null;}
        Need(L.AcceptedVehicles==before+1,"second vehicle requested by physical tunnel hammer contact");
        Need(!L.LatestCar.GetComponent<VehicleWashSurface>().IsClean,"pooled vehicle starts dirty");
        Need(!L.GateEnabled,"first-vehicle work gate does not hold second pooled lifetime");
    }
    static IEnumerator Invest()
    {
        var stand=L.InvestmentPosition;var side=P.transform.position-stand;side.y=0;stand+=side.normalized*1.5f;stand.y=P.transform.position.y;
        yield return Walk(stand);yield return Hold(null,.08f,key:Key.F);yield return Hold(null,.1f);
        Need(L.InvestmentOpen,"investment panel opened on foot by F");
        int money=EconomyManager.Instance.currentMoney,opened=L.OpenedProcesses,cost=L.settings.UnlockPrice(opened);
        yield return Hold(null,.08f,key:Key.Digit2);yield return Hold(null,.2f);
        Need(L.OpenedProcesses==opened+1,"next process unlocked through normal investment input");
        Need(EconomyManager.Instance.currentMoney==money-cost,"investment deducted actual earned money");
        ScreenCapture.CaptureScreenshot("Temp/FirstProduction/first-investment.png");
    }
    public static void StartEarnOnly() => Begin(All(false));
    public static void StartHireDelivery() => Begin(HireDelivery());

    static IEnumerator HireDelivery()
    {
        Need(L.OpenedProcesses == 1, "Run the dashboard-only hire check before unlocking the engine");
        int wallet = EconomyManager.Instance.currentMoney;
        var post = L.InvestmentPosition;
        var direction = P.transform.position-post;
        direction.y=0;
        var stand=post+direction.normalized*1.5f;
        stand.y=P.transform.position.y;
        yield return Walk(stand);
        yield return Hold(null,.08f,key:Key.F);
        yield return Hold(null,.1f);
        Need(L.InvestmentOpen,"Hiring post open");
        yield return Hold(null,.08f,key:Key.Digit1);
        yield return Hold(null,.2f);
        Need(L.FirstWorkerHired && EconomyManager.Instance.currentMoney==wallet-L.settings.firstHireCost,"Paid first hire");
        yield return Hold(null,.12f,key:Key.Escape);
        var entry=L.progression.processes[0];
        var worker=entry.workers.First(w=>w!=null && w.isActiveAndEnabled);
        var avatar=worker.GetComponentInChildren<Animator>();
        Need(avatar!=null && avatar.avatar.name.Contains("Kyle") && worker.GetComponentsInChildren<Renderer>().Any(r=>r.enabled),"Visible RobotKyle hired");
        var crew=L.progression.GetComponents<PrototypeProcessCrew>().First(c=>c.processIndex==0);
        int strikes=crew.StrikeCount, completed=crew.CompletedPartCount;
        int stock=entry.humanStation.partStacks[0].Count;
        int wins=L.SuccessfulDeliveries, income=L.DeliveryIncome;
        yield return Lever(PrototypeLineController.LeverPosition.Stop);
        yield return ManualSecond();
        var car=L.LatestCar;
        yield return Wash(car);
        yield return Lever(PrototypeLineController.LeverPosition.Low);
        // Stay at the physical lever while the car enters the detector. Strike twice
        // from Low to reach Stop before the car arrives at its authored dock.
        yield return WaitForDeliveryOutcome(() => crew.CurrentCar == car && crew.IsBusy, "Worker accepted arriving car");
        Func<Vector3> leverAim = () => PrototypeLineController.Current.leverRoot.position + Vector3.up * 1.35f;
        for (int hit = 0; hit < 4 && PrototypeLineController.Current.lever != PrototypeLineController.LeverPosition.Stop; hit++)
        {
            yield return Hold(leverAim, .08f, fire:true);
            yield return Hold(leverAim, 1.05f);
        }
        Need(PrototypeLineController.Current.lever == PrototypeLineController.LeverPosition.Stop && car.moveSpeed == 0 && car.pathProgress < crew.GateProgress, "Physical lever stopped the car before docking");
        yield return WaitForDeliveryOutcome(() => entry.humanStation.partStacks[0].Count == stock - 1, "Worker picked up one dashboard on the stopped line");
        var carried = crew.PreviewPartFor(worker);
        var progress = car.pathProgress;
        float resumeAt = Time.time + 52f;
        while (Time.time < resumeAt)
        {
            NeedStableCarry();
            Inputs();
            yield return null;
        }
        void NeedStableCarry()
        {
            if (car.pathProgress != progress || carried == null || crew.PreviewPartFor(worker) != carried ||
                !crew.IsBusy || entry.humanStation.partStacks[0].Count != stock - 1 || crew.StrikeCount != strikes)
                throw new InvalidOperationException("Interrupted carry while waiting for the lever");
        }
        Need(true, "Same dashboard held throughout 52 seconds of physical line stop");
        yield return Lever(PrototypeLineController.LeverPosition.Low);
        bool stopped=false;
        float until=Time.time+140;
        while(Time.time<until && L.SuccessfulDeliveries==wins && car.gameObject.activeInHierarchy)
        {
            stopped |= crew.Blocks(car) && !car.isMoving;
            Inputs(); yield return null;
        }
        Need(stopped && crew.CompletedPartCount==completed+1 && crew.StrikeCount>strikes,"Worker stopped vehicle and fastened dashboard: "+crew.LastFailure);
        Need(entry.humanStation.partStacks[0].Count==stock-1,"Single worker slot pickup");
        Need(L.SuccessfulDeliveries==wins+1 && L.DeliveryIncome==income+L.settings.DeliveryPrice(1),"Worker delivery paid");
    }
    static IEnumerator All(bool purchaseAfterEarning=true)
    {
        Evidence.Clear();Need(EconomyManager.Instance.currentMoney==0,"new session begins without injected money");
        while(L.LatestCar==null){Inputs();yield return null;}
        yield return Wash(L.LatestCar);yield return Assemble(L.LatestCar);
        yield return Lever(PrototypeLineController.LeverPosition.Stop);
        yield return ManualSecond();var car=L.LatestCar;
        yield return Wash(car);yield return Lever(PrototypeLineController.LeverPosition.Low);
        float before=car.pathProgress;yield return Hold(null,1f);
        Need(car.pathProgress>before&&!L.GateEnabled,"second vehicle moves before dashboard completion");
        while(car.pathProgress<L.WashProgress-.015f){Inputs();yield return null;}
        yield return Lever(PrototypeLineController.LeverPosition.Stop);
        yield return Assemble(car);
        if (purchaseAfterEarning) yield return Invest();
    }
    // Run after two standard deliveries with money still available and no active car.
    public static void StartEngineWithOlderCar() => Begin(EngineWithOlderCar());

    // Run after purchasing the engine with an otherwise empty line.
    public static void StartUnlockedEngineDelivery() => Begin(UnlockedEngineDelivery());

    static IEnumerator FastenRequiredPart(CarController car, int process)
    {
        var entry = L.progression.processes[process];
        var stock = entry.humanStation.partStacks[0];
        var kind = stock.partType;
        var part = car.GetComponentsInChildren<AssemblyPart>(true).First(p => p.myType == kind);
        var contract = car.GetComponent<ProductionContract>();
        Need(contract.Requires(kind) && !part.IsAssembled, "Outstanding contract part " + kind);
        L.SetWashTool(false);
        yield return Hold(null, .1f, key: Key.Digit2);
        int before = stock.Count;

        // Keep the verified dashboard approach. The engine slot's approach is
        // from the west, so the stationary vehicle can be rounded on the way.
        yield return ToAisle();
        var slot = stock.ReadyBounds.center;
        var pickup = process == 0 ? slot + car.transform.right * 1.3f : slot + Vector3.left * 1.25f;
        pickup.y = P.transform.position.y;
        var aisle = pickup;
        aisle.x = slot.x - 1.5f;
        aisle.z = P.transform.position.z;
        if (process == 1 && L.OpenedProcesses >= 3)
        {
            // Cross the line east of the cover stock and approach the engine from the north aisle.
            yield return Walk(new Vector3(51.5f, aisle.y, aisle.z), .22f);
            yield return Walk(new Vector3(51.5f, aisle.y, slot.z + 2f), .22f);
            aisle.z = slot.z + 2f;
        }
        yield return Walk(aisle, .22f);
        var obstacle = LineTrafficManager.Instance.ActiveCars
            .Where(v => v != null && v.gameObject.activeInHierarchy)
            .OrderBy(v => (v.transform.position - pickup).sqrMagnitude).First();
        yield return Around(obstacle, pickup);
        yield return Hold(() => stock.ReadyBounds.center, .3f);
        yield return Hold(() => stock.ReadyBounds.center, .2f, use: true);
        Need(P.interactor.HeldPart != null && stock.Count == before - 1, "One slot pickup for " + kind);

        if (process == 1)
        {
            var north = car.transform.position + car.transform.right * 3.75f;
            north.y = P.transform.position.y;
            yield return Walk(north, .2f);
        }
        var aim = new Func<Vector3>(() => P.interactor.AttachmentAimPoint(part));
        var stand = process == 1 || kind == PartType.EngineCover_23
            ? aim() + car.transform.forward * 1.4f
            : aim() - car.transform.right * 1.4f;
        stand.y = P.transform.position.y;
        yield return Around(car, stand);
        yield return Hold(aim, .25f);
        yield return Hold(aim, 2.8f, use: true);
        Need(part.IsPlayerAttached && !P.interactor.IsAttaching, "E attachment for " + kind);
        if (process == 1 || kind == PartType.EngineCover_23)
        {
            var center = aim();
            var nose = center + car.transform.forward * .8f;
            nose.y = P.transform.position.y;
            yield return Walk(nose, .16f);
            aim = () => center + car.transform.forward * .25f - car.transform.right * .22f + Vector3.up * .16f;
            yield return Hold(aim, .3f);
        }
        for (int hit = 0; hit < 8 && !part.IsAssembled; hit++)
        {
            yield return Hold(aim, .08f, fire: true);
            yield return Hold(aim, 1.05f);
        }
        Need(part.IsAssembled, "Physical hammer fastening for " + kind);
    }

    static IEnumerator WaitForDeliveryOutcome(Func<bool> done, string description)
    {
        float deadline = Time.time + 150f;
        while (!done() && Time.time < deadline)
        {
            Inputs();
            yield return null;
        }
        Need(done(), description);
    }

    static IEnumerator CompleteEngineContract(CarController car)
    {
        var contract = car.GetComponent<ProductionContract>();
        Need(contract.processCount == 2 && contract.payout == L.settings.DeliveryPrice(2),
            "New vehicle carries engine contract and new price");
        yield return Wash(car);
        // Take the vehicle past the tunnel before approaching the front with a hammer.
        yield return Lever(PrototypeLineController.LeverPosition.Low);
        yield return WaitForDeliveryOutcome(() => car.pathProgress >= L.WashProgress - .015f, "Engine car clear of tunnel");
        yield return Lever(PrototypeLineController.LeverPosition.Stop);
        yield return FastenRequiredPart(car, 0);
        yield return FastenRequiredPart(car, 1);
        AssemblyPart missing;
        Need(contract.Validate(out missing), "Both required assemblies and wash validated");
    }

    static IEnumerator UnlockedEngineDelivery()
    {
        Need(L.OpenedProcesses == 2 && L.OutletPrefix == 2, "Engine outlet already active");
        if (L.InvestmentOpen) yield return Hold(null, .15f, key: Key.Escape);
        yield return Lever(PrototypeLineController.LeverPosition.Stop);
        yield return ManualSecond();
        var car = L.LatestCar;
        var contract = car.GetComponent<ProductionContract>();
        int money = EconomyManager.Instance.currentMoney, accepted = L.SuccessfulDeliveries;
        yield return CompleteEngineContract(car);
        yield return Lever(PrototypeLineController.LeverPosition.Low);
        yield return WaitForDeliveryOutcome(() => L.SuccessfulDeliveries == accepted + 1, "Engine car handed over");
        Need(EconomyManager.Instance.currentMoney == money + contract.payout,
            "Agreed engine contract credited once");
    }

    public static void StartEngineCoverDelivery() => Begin(EngineCoverDelivery());

    static IEnumerator EngineCoverDelivery()
    {
        var entry = L.progression.processes[2];
        var stock = entry.humanStation.partStacks[0];
        Need(L.OpenedProcesses == 2 && !stock.gameObject.activeInHierarchy, "Cover inventory locked before purchase");
        if (L.InvestmentOpen) yield return Hold(null, .15f, key: Key.Escape);
        yield return Invest();
        yield return Hold(null, .15f, key: Key.Escape);
        Need(L.OutletPrefix == 3 && stock.HasReadyPart && stock.gameObject.activeInHierarchy &&
            entry.humanStation.gameObject.activeInHierarchy, "Engine cover station, stock and outlet opened");
        yield return Lever(PrototypeLineController.LeverPosition.Stop);
        yield return ManualSecond();
        var car = L.LatestCar;
        var contract = car.GetComponent<ProductionContract>();
        Need(contract.processCount == 3 && contract.Requires(PartType.EngineCover_23) &&
            contract.payout == L.settings.DeliveryPrice(3), "New car carries three-process contract");
        yield return Lever(PrototypeLineController.LeverPosition.Low);
        yield return WaitForDeliveryOutcome(() => car.pathProgress >= L.WashProgress - .015f, "Vehicle clear of tunnel");
        yield return Lever(PrototypeLineController.LeverPosition.Stop);
        yield return Wash(car);
        yield return Walk(new Vector3(car.transform.position.x + .8f, P.transform.position.y, 32.3f), .2f);
        yield return FastenRequiredPart(car, 0);
        yield return FastenRequiredPart(car, 1);
        Need(!contract.Validate(out _) && L.Objective.Contains(entry.displayName), "Outstanding cover named in work guidance");
        yield return FastenRequiredPart(car, 2);
        int wallet = EconomyManager.Instance.currentMoney, count = L.SuccessfulDeliveries;
        yield return Lever(PrototypeLineController.LeverPosition.Low);
        yield return WaitForDeliveryOutcome(() => L.SuccessfulDeliveries == count + 1, "Cover delivery accepted");
        Need(EconomyManager.Instance.currentMoney == wallet + contract.payout, "Three-process delivery paid once");
    }

    static IEnumerator EngineWithOlderCar()
    {
        Need(L.OpenedProcesses == 1 && L.SuccessfulDeliveries >= 2, "Two earned deliveries before mixed test");
        yield return Lever(PrototypeLineController.LeverPosition.Stop);
        yield return ManualSecond();
        var older = L.LatestCar;
        var original = older.GetComponent<ProductionContract>();
        var outlet = original.dispatch;
        int lowerPrice = original.payout;
        float originalEnd = original.endProgress;
        yield return Wash(older);
        yield return Lever(PrototypeLineController.LeverPosition.Low);
        yield return WaitForDeliveryOutcome(() => older.pathProgress >= L.WashProgress - .015f, "Old car cleared entry");
        yield return Lever(PrototypeLineController.LeverPosition.Stop);
        yield return FastenRequiredPart(older, 0);
        yield return ToAisle();
        yield return Invest();
        if (L.InvestmentOpen) yield return Hold(null, .15f, key: Key.Escape);
        Need(L.OutletPrefix == 1 && original.processCount == 1 && original.payout == lowerPrice &&
            original.endProgress == originalEnd && original.dispatch == outlet, "Old contract and outlet retained");
        yield return ManualSecond();
        var newer = L.LatestCar;
        Need(newer != older && older.gameObject.activeInHierarchy, "Both contract generations on line together");
        var next = newer.GetComponent<ProductionContract>();
        int successes = L.SuccessfulDeliveries;
        int money = EconomyManager.Instance.currentMoney;
        int higherPrice = next.payout;
        yield return CompleteEngineContract(newer);
        yield return Lever(PrototypeLineController.LeverPosition.Low);
        yield return WaitForDeliveryOutcome(() => L.SuccessfulDeliveries == successes + 1, "Old contract accepted first");
        Need(L.LastDeliveryAmount == lowerPrice && EconomyManager.Instance.currentMoney == money + lowerPrice,
            "Older vehicle earned its original price");
        yield return WaitForDeliveryOutcome(() => L.OutletPrefix == 2, "Outlet moved after old car handed over");
        yield return WaitForDeliveryOutcome(() => L.SuccessfulDeliveries == successes + 2, "New engine contract accepted");
        Need(L.LastDeliveryAmount == higherPrice && higherPrice > lowerPrice &&
            EconomyManager.Instance.currentMoney == money + lowerPrice + higherPrice,
            "Higher engine price and old price credited exactly once");
    }
}
#endif
