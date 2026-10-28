#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

public static class FactoryPhysicalFeedbackChecks
{
    public static string Result="idle";
    static readonly StringBuilder log=new StringBuilder();
    static FactoryPlayerController player;
    static PlayerAssemblyInteractor tool;
    static ProductionProgression progression;
    static PrototypeDayCycle day;
    static CarController car,second;
    static GameObject probe;
    static int testGroup;
    static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Need(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    public static void Start(int group=0)
    {
        player=Object.FindFirstObjectByType<FactoryPlayerController>();tool=player.interactor;
        progression=ProductionProgression.Current;day=PrototypeDayCycle.Current;
        testGroup=group;Result="running";log.Clear();player.StartCoroutine(Guard());
    }
    static IEnumerator Guard()
    {
        var routines=new Stack<IEnumerator>();routines.Push(testGroup==1?Navigation():testGroup==2?Effects():Run());bool pass=true;
        while(routines.Count>0)
        {
            bool moved=false;object wait=null;
            try{moved=routines.Peek().MoveNext();if(moved)wait=routines.Peek().Current;}
            catch(Exception e){log.AppendLine("FAIL "+e);pass=false;break;}
            if(!moved){routines.Pop();continue;}if(wait is IEnumerator nested){routines.Push(nested);continue;}yield return wait;
        }
        if(tool.HeldPart!=null)tool.TryThrow();
        if(car!=null&&car.gameObject.activeInHierarchy)CarPool.Instance.Return(car);
        if(second!=null&&second.gameObject.activeInHierarchy)CarPool.Instance.Return(second);
        if(probe!=null)Object.Destroy(probe);
        player.Teleport(player.SpawnPoint);player.enabled=true;tool.enabled=true;tool.inputEnabled=true;day.enabled=true;
        player.SetFirstPerson(false);Cursor.lockState=CursorLockMode.Locked;
        Result=(pass?"PASS\n":"FAIL\n")+log;Debug.Log(Result);
    }
    static CarController Spawn(float progress)
    {var c=CarPool.Instance.Get();c.SetPath(progression.mainSpline,0,progress);progression.Stamp(c);return c;}
    static void Attach(AssemblyPart part)
    {part.Reset();Need(part.TryBeginPlayerAttachment(tool,part.AttachmentWorldPosition,part.AttachmentWorldRotation),"attach begin");Need(part.CompletePlayerAttachment(tool),"attach end");}
    static void Aim(AssemblyPart part)
    {
        var target=tool.AttachmentAimPoint(part);
        var eye=target+Vector3.up*.32f+Vector3.forward*1.35f;
        player.Teleport(eye-Vector3.up*1.62f);
        player.transform.rotation=Quaternion.LookRotation(Vector3.back);
        player.viewCamera.transform.SetPositionAndRotation(eye,Quaternion.LookRotation(target-eye));
        Physics.SyncTransforms();tool.RefreshNearbyCars();
        typeof(PlayerAssemblyInteractor).GetMethod("UpdateFocus",Private).Invoke(tool,null);
    }
    static void Swing()
    {Need(tool.TrySwingHammer(),"swing request rejected");for(int i=0;i<36;i++)tool.AdvanceHammerSwing(.01f);}
    static IEnumerator Run()
    {
        player.enabled=false;tool.enabled=false;tool.inputEnabled=true;day.enabled=false;Cursor.lockState=CursorLockMode.Locked;
        PrototypeLineController.Current.SetLever(PrototypeLineController.LeverPosition.Stop);
        foreach(var active in Object.FindObjectsByType<CarController>(FindObjectsSortMode.None).Where(c=>c.gameObject.activeInHierarchy))CarPool.Instance.Return(active);
        car=Spawn(progression.processes[0].gateProgress+7f/progression.mainSpline.CalculateLength());
        var parts=car.GetComponentsInChildren<AssemblyPart>(true);
        var part=parts.First(p=>p.myType==PartType.Hood_53);Attach(part);Aim(part);
        int money=EconomyManager.Instance.currentMoney;
        Need(tool.TrySwingHammer(),"empty swing accepted");Need(part.currentWork==0&&EconomyManager.Instance.currentMoney==money,"click paid before contact");
        player.viewCamera.transform.rotation=Quaternion.LookRotation(Vector3.right);
        for(int i=0;i<36;i++)tool.AdvanceHammerSwing(.01f);
        Need(part.currentWork==0&&EconomyManager.Instance.currentMoney==money,"look-away swing must miss");
        log.AppendLine("Blank/aim-away swing: motion starts, click does not grant work, rotated camera misses without reward.");
        for(int i=0;i<3;i++)
        {
            yield return new WaitForSeconds(.35f);Aim(part);Swing();
            Need(EconomyManager.Instance.currentMoney==money+i+1,"mesh contact hit reward "+i+"; fill="+part.Fill+"; focus="+tool.FocusedPart);
            Need(MoneyUI.Instance.LastSpawnedCoinCount==1,"one unit must spawn one coin");
        }
        Need(part.IsAssembled,"three physical contacts complete part");
        part.SetWork(0);part.AddWork(part.requiredWork);Need(EconomyManager.Instance.currentMoney==money+3,"same-lifetime repeat paid");
        Attach(part);tool.SetPlayerHammerHits(2);money=EconomyManager.Instance.currentMoney;
        yield return new WaitForSeconds(.35f);Aim(part);Swing();
        Need(EconomyManager.Instance.currentMoney==money+2&&MoneyUI.Instance.LastSpawnedCoinCount==2&&!part.IsAssembled,"upgraded hammer first impact pays2");
        yield return new WaitForSeconds(.35f);Aim(part);Swing();
        Need(part.IsAssembled&&EconomyManager.Instance.currentMoney==money+3&&MoneyUI.Instance.LastSpawnedCoinCount==1,"upgraded remainder pays1");
        Need(day.SoldCars==0&&day.SalesIncome==0&&day.MissionIncome==0,"work must not count as sale");tool.SetPlayerHammerHits(3);
        log.AppendLine("Actual mesh/head contacts: 1+1+1, upgraded2+1, exact1/2 coin counts, no duplicate payout or sale/mission credit.");
        // Two nearby cars may offer the same carried attachment, even while looking elsewhere.
        part=parts.First(p=>p.myType==progression.processes[0].parts[0]);part.Reset();
        second=Spawn(car.pathProgress+5f/car.SplineLength);var other=second.GetComponentsInChildren<AssemblyPart>(true).First(p=>p.myType==part.myType);other.Reset();
        var stock=Object.FindObjectsByType<PartStack>(FindObjectsSortMode.None).First(s=>s.partType==part.myType);
        player.Teleport(stock.transform.position);Physics.SyncTransforms();Need(tool.TryTake(stock),"stock pickup");
        player.Teleport((car.transform.position+second.transform.position)*.5f+Vector3.forward*1.65f);Physics.SyncTransforms();tool.RefreshNearbyCars();
        player.viewCamera.transform.rotation=Quaternion.LookRotation(Vector3.up);
        var display=Object.FindFirstObjectByType<PrototypeDefectDisplay>();display.RefreshDisplay();
        Need(tool.NearbyCars.Count>=2&&display.PreviewOf(part)&&display.PreviewOf(other),"two-car proximity preview; cars="+tool.NearbyCars.Count+" previews="+display.PreviewPartCount);
        tool.TryThrow();player.Teleport(car.transform.position+Vector3.forward*24);tool.RefreshNearbyCars();display.RefreshDisplay();
        Need(display.DistantPartMarkerCount>0&&display.PreviewPartCount==0,"distant faint defect markers");
        Need(GameObject.Find("DistantDefectMarkers").GetComponents<MeshRenderer>().Length==1,"far markers must batch");
        log.AppendLine("Two nearby cars show carried-part previews without aiming; distant defects use one batched line mesh, no transparent vehicle copies.");
        var debug=player.GetComponent<PrototypePlayerTestControls>();
        var body=car.GetComponentsInChildren<Collider>().First(c=>c.gameObject.layer==11);
        player.Teleport(body.bounds.center+Vector3.forward*(body.bounds.extents.z+.35f)-Vector3.up*.7f);Physics.SyncTransforms();debug.RefreshContactCar();
        Need(debug.ContactCar==car,"debug contact selection");money=EconomyManager.Instance.currentMoney;
        Need(debug.TrySetNearbyVehicle(true),"debug all");Need(parts.Where(p=>p.myType!=PartType.None&&p.myType!=PartType.Frame_1).All(p=>p.IsAssembled),"debug complete");
        Need(debug.TrySetNearbyVehicle(false),"debug last two");Need(parts.Count(p=>p.myType!=PartType.None&&p.myType!=PartType.Frame_1&&!p.IsAssembled)==2,"exactly last two missing");
        Need(EconomyManager.Instance.currentMoney==money,"debug pays no money");
        player.SetFirstPerson(true);Need(player.FirstPerson&&player.firstPersonCamera.Priority>player.thirdPersonCamera.Priority,"FP priorities");
        Need(player.bodyAnimator.GetComponentsInChildren<SkinnedMeshRenderer>(true).All(r=>r.forceRenderingOff),"FP own body hidden");player.SetFirstPerson(false);
        Need(player.bodyAnimator.GetComponentsInChildren<SkinnedMeshRenderer>(true).All(r=>!r.forceRenderingOff),"TP own body restored");
        log.AppendLine("Debug all / last2 reset pays0, nearest contact car selected; first-person body hidden and third-person restored.");
        CarPool.Instance.Return(second);second=null;CarPool.Instance.Return(car);car=null;
        yield return Navigation();
    }
    static IEnumerator Effects()
    {
        day.enabled=false;
        var line=PrototypeLineController.Current;var belt=Object.FindFirstObjectByType<SplineConveyorDeformer>();
        line.SetLever(PrototypeLineController.LeverPosition.High);
        Need(belt.GetSurfaceVelocity(line.leverRoot.position).magnitude>.1f,"moving belt");
        line.SetLever(PrototypeLineController.LeverPosition.Stop);
        Need(belt.GetSurfaceVelocity(line.leverRoot.position).sqrMagnitude<.00001f,"same-frame stop");
        var tunnel=Object.FindFirstObjectByType<PrototypeTunnelFeedback>();
        var shakeTimer=typeof(PrototypeTunnelFeedback).GetField("shakeTime",Private);
        var smokeTimer=typeof(PrototypeTunnelFeedback).GetField("smokeTime",Private);
        shakeTimer.SetValue(tunnel,0f);smokeTimer.SetValue(tunnel,0f);
        line.SetLever(PrototypeLineController.LeverPosition.Low);
        Need((float)shakeTimer.GetValue(tunnel)==0f&&(float)smokeTimer.GetValue(tunnel)==0f,"lever animated tunnel");
        line.SetLever(PrototypeLineController.LeverPosition.Stop);
        line.spawner.RequestVehicle();Need((float)shakeTimer.GetValue(tunnel)>0,"request must animate tunnel");
        yield return new WaitForSeconds(1.2f);
        Need((float)shakeTimer.GetValue(tunnel)<=0f,"automatic queue exit restarted tunnel shake");
        var paint=Object.FindFirstObjectByType<PaintBooth>();
        Need(paint.GetComponentsInChildren<LineRenderer>().Length==0&&paint.GetComponentsInChildren<ParticleSystem>().Any(p=>p.main.maxParticles==64),"paint portal/mist");
        log.AppendLine("Belt speed1→0 in same call; lever does not shake/smoke tunnel; explicit request does; auto exit does not restart. Paint has solid portal, no laser, bounded64 mist.");
        var shake=Object.FindFirstObjectByType<ShakeTestStation>();
        car=Spawn(shake.gateProgress-.0001f);
        var parts=car.GetComponentsInChildren<AssemblyPart>(true);
        foreach(var p in parts)p.SetAssembled();
        var loose=parts.First(p=>p.myType!=PartType.Frame_1&&VehicleInspectionRules.DomainOf(p.myType)==VehicleInspectionDomain.Assembly);
        Attach(loose);loose.TryPlayerHammer(3);Physics.SyncTransforms();
        var originalRotation=car.transform.rotation;bool during=false,breaking=false;float until=Time.time+12;
        while(Time.time<until&&car.gameObject.activeInHierarchy)
        {
            if(shake.currentState==ShakeTestStation.TestState.Testing&&PrototypeCarDebris.ActiveCount>0)during=true;
            if(shake.currentState==ShakeTestStation.TestState.Breaking)
            {breaking=true;Need(Quaternion.Angle(car.transform.rotation,originalRotation)<.05f,"reject rotates car");}
            Need(PrototypeCarDebris.ActiveCount<=40,"debris cap");yield return null;
        }
        Need(during&&breaking&&!car.gameObject.activeInHierarchy,"drop while shaking before rejection: during="+during+" breaking="+breaking+" active="+car.gameObject.activeInHierarchy);car=null;
        yield return new WaitForSeconds(4.5f);Need(PrototypeCarDebris.ActiveCount==0,"debris cleanup");
        log.AppendLine("Unfastened part drops during active shaking; rejection preserves frame rotation, no terminal spin/explosion; debris cap40 and lifetime cleanup passed.");
    }
    static IEnumerator Navigation()
    {
        if(day.IsSettlement)day.ContinueNextDay();
        Time.timeScale=1f;day.enabled=false;player.enabled=false;tool.enabled=false;tool.inputEnabled=false;
        PrototypeLineController.Current.SetLever(PrototypeLineController.LeverPosition.Stop);
        PrototypeLineController.Current.enabled=false;
        Need(Object.FindObjectsByType<LineCrossing>(FindObjectsInactive.Include,FindObjectsSortMode.None).Length==0,"crossings remain");
        car=Spawn(progression.processes[0].gateProgress+12f/progression.mainSpline.CalculateLength());
        Physics.SyncTransforms();
        var body=car.GetComponentsInChildren<Collider>().First(c=>c.gameObject.layer==11);var b=body.bounds;
        var start=b.center+Vector3.forward*(b.extents.z+1.6f);start.y=0;
        var goal=b.center-Vector3.forward*(b.extents.z+1.6f);goal.y=0;
        Need(NavMesh.SamplePosition(start,out var sample,2f,NavMesh.AllAreas),"nav start");
        probe=new GameObject("DynamicVehicleNavigationProbe");probe.transform.position=sample.position;
        var mover=probe.AddComponent<NavMover>();mover.speed=2.8f;
        yield return null;var agent=probe.GetComponent<NavMeshAgent>();
        float until=Time.time+18;bool arrived=false;float closest=float.PositiveInfinity;
        while(Time.time<until&&!arrived)
        {
            arrived=mover.MoveTo(goal);closest=Mathf.Min(closest,Vector3.Distance(probe.transform.position,goal));
            Need(agent.isOnNavMesh,"probe left NavMesh");
            Need(!body.bounds.Contains(probe.transform.position+Vector3.up*.25f),"probe passed through car");
            yield return null;
        }
        Need(arrived,"stationary car detour stalled: "+probe.GetComponent<PrototypeVehicleAvoidance>().DiagnosticState+" pos="+probe.transform.position+" goal="+goal+" closest="+closest);
        log.AppendLine("NavMesh: no crossings; stopped vehicle detoured around, stayed on mesh, no vehicle penetration.");
        PrototypeLineController.Current.SetLever(PrototypeLineController.LeverPosition.Low);
        float oldProgress=car.pathProgress;arrived=false;until=Time.time+18;
        while(Time.time<until&&!arrived){arrived=mover.MoveTo(start);yield return null;}
        Need(arrived&&car.pathProgress>oldProgress,"moving-car crossing/destination change: arrived="+arrived+" progress="+car.pathProgress+" old="+oldProgress+" speed="+car.moveSpeed+" "+probe.GetComponent<PrototypeVehicleAvoidance>().DiagnosticState);
        PrototypeLineController.Current.SetLever(PrototypeLineController.LeverPosition.Stop);
        CarPool.Instance.Return(car);car=Spawn(progression.processes[0].gateProgress);
        second=Spawn(progression.processes[0].gateProgress+6.2f/car.SplineLength);Physics.SyncTransforms();
        var a=car.GetComponentsInChildren<Collider>().First(c=>c.gameObject.layer==11).bounds;
        var b2=second.GetComponentsInChildren<Collider>().First(c=>c.gameObject.layer==11).bounds;
        var between=new Vector3((a.min.x+b2.max.x)*.5f,0,(a.center.z+b2.center.z)*.5f);
        Need(NavMesh.SamplePosition(between,out sample,1.5f,NavMesh.AllAreas)&&agent.Warp(sample.position),"pinch fixture binding");
        goal=between-Vector3.forward*4;goal.y=sample.position.y;arrived=false;until=Time.time+14;
        while(Time.time<until&&!arrived&&Vector3.Distance(probe.transform.position,between)<1.2f){arrived=mover.MoveTo(goal);yield return null;}
        Need(Vector3.Distance(probe.transform.position,between)>=1.2f||arrived,"two-car common side escape: "+probe.GetComponent<PrototypeVehicleAvoidance>().DiagnosticState+" pos="+probe.transform.position);
        PrototypeLineController.Current.SetLever(PrototypeLineController.LeverPosition.Low);until=Time.time+24;
        while(Time.time<until&&!arrived){arrived=mover.MoveTo(goal);yield return null;}
        Need(arrived,"resume route when stopped-car gap opens: "+probe.GetComponent<PrototypeVehicleAvoidance>().DiagnosticState+" pos="+probe.transform.position);
        PrototypeLineController.Current.SetLever(PrototypeLineController.LeverPosition.Stop);
        log.AppendLine("Moving car crossing/target reversal arrived; narrow two-car gap exited to safety, then route resumed when the line opened. No movement Warp.");
        CarPool.Instance.Return(second);second=null;CarPool.Instance.Return(car);car=null;Object.Destroy(probe);probe=null;
        var supply=Object.FindFirstObjectByType<SupplyManager>();
        var stocks=Object.FindObjectsByType<PartStack>(FindObjectsSortMode.None);
        foreach(var stock in stocks)stock.Fill_Full();
        var destination=stocks.Where(s=>s.transform.position.z<20&&s.partType!=PartType.None).OrderBy(s=>(s.transform.position-supply.LoadPoint).sqrMagnitude).First();
        while(destination.TryConsume()){}
        var worker=supply.Hire();Need(worker!=null,"supply hire");until=Time.time+60;
        while(Time.time<until&&destination.Count==0)yield return null;
        Need(destination.Count>0,"real supplier delivery: "+worker.currentState+" at"+worker.transform.position+" "+worker.GetComponent<NavMover>().DiagnosticState);
        until=Time.time+60;while(Time.time<until&&worker.currentState!=SupplyWorker.SupplyState.IdleAtWarehouse)yield return null;
        Need(worker.currentState==SupplyWorker.SupplyState.IdleAtWarehouse,"supplier warehouse return: "+worker.currentState);
        log.AppendLine("Actual hired supply worker loaded at warehouse, refilled another-row stock, and returned to warehouse using new NavMesh navigation.");
    }
}
#endif
