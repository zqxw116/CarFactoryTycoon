#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using Object=UnityEngine.Object;

public static class FactoryFeedbackNov2Checks
{
    public static string Result="idle";
    static readonly StringBuilder log=new StringBuilder();
    static int failures;
    static FactoryPlayerController player;
    static PlayerAssemblyInteractor tool;
    static CarController car;
    static bool oldEnabled,oldInput;
    static Vector3 oldPosition;
    static Quaternion oldRotation;
    static float oldYaw;
    static void Need(bool ok,string message){if(!ok)failures++;log.AppendLine((ok?"OK ":"FAIL ")+message);}
    public static void Start()
    {
        if(!Application.isPlaying)throw new InvalidOperationException("Play required");
        if(Result.StartsWith("running"))return;
        player=Object.FindFirstObjectByType<FactoryPlayerController>();tool=player.interactor;
        if(tool.HeldPart!=null || tool.IsAttaching || tool.ExternalCarryTarget!=null)throw new InvalidOperationException("Empty hands required");
        oldEnabled=player.enabled;oldInput=tool.inputEnabled;oldPosition=player.transform.position;oldRotation=player.transform.rotation;oldYaw=player.ViewYaw;
        player.enabled=false;tool.inputEnabled=true;
        failures=0;log.Clear();Result="running";player.StartCoroutine(Guard(Run()));
    }
    static IEnumerator Guard(IEnumerator routine)
    {
        while(true)
        {
            bool more=false;object next=null;
            try{more=routine.MoveNext();if(more)next=routine.Current;}
            catch(Exception e){failures++;log.AppendLine(e.ToString());break;}
            if(!more)break;yield return next;
        }
        if(car!=null)CarPool.Instance.Return(car);car=null;
        player.RotateView(new Vector2(Mathf.DeltaAngle(player.ViewYaw,oldYaw),0));
        player.Teleport(oldPosition);player.transform.rotation=oldRotation;player.enabled=oldEnabled;tool.inputEnabled=oldInput;
        Result=(failures==0?"PASS\n":"FAIL "+failures+"\n")+log;
        Debug.Log(Result);
    }
    static IEnumerator Run()
    {
        Need(Mathf.Abs(WorkerHammerAnimation.Duration-1f)<.001f,"one second swing");
        player.Teleport(new Vector3(57,-.36f,-5));
        player.ResolveMoveDirection(Vector2.zero);
        var heading=player.ResolveMoveDirection(Vector2.up);var body=player.transform.rotation;
        player.RotateView(new Vector2(180,0));
        Need(Vector3.Distance(heading,player.ResolveMoveDirection(Vector2.up))<.001f,"W held direction survives 180 degree camera orbit");
        Need(Quaternion.Angle(body,player.transform.rotation)<.001f,"camera orbit does not rotate body");
        var right=player.ResolveMoveDirection(Vector2.right);
        Need(Mathf.Abs(Vector3.Dot(right,heading))<.001f,"adding sideways input retains captured movement basis");
        player.ResolveMoveDirection(Vector2.zero);
        Need(Vector3.Dot(heading,player.ResolveMoveDirection(Vector2.up))<-.99f,"new movement captures new view heading");
        for(int i=0;i<35;i++){player.SimulateMovement(Vector2.down,false,false,Time.deltaTime);yield return null;}
        Need(player.CarryAnchor!=null && Vector3.Dot(player.CarryAnchor.forward,player.BodyForward)>.99f,"backward movement rotates body and carry anchor together");
        Need(Vector3.Dot(player.bodyAnimator.transform.forward,player.BodyForward)>.98f,"visual and body agree while carrying");
        var mouse=UnityEngine.InputSystem.Mouse.current;
        if(mouse!=null)
        {
            var savedMouse=mouse.position.ReadValue();
            var uiPoint=new Vector2((PrototypeGui.Width-150)*PrototypeGui.Scale,Screen.height-75*PrototypeGui.Scale);
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(mouse,new UnityEngine.InputSystem.LowLevel.MouseState{position=uiPoint,buttons=1});
            UnityEngine.InputSystem.InputSystem.Update();
            player.SendMessage("Update");tool.SendMessage("Update");
            Need(player.PointerOverInterface && !tool.IsSwinging,"day HUD pointer blocks hammer input");
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(mouse,new UnityEngine.InputSystem.LowLevel.MouseState{position=savedMouse});
            UnityEngine.InputSystem.InputSystem.Update();tool.inputEnabled=true;
        }
        Need(tool.TrySwingHammer(),"swing starts");tool.AdvanceHammerSwing(.4f);
        Need(tool.SwingProgress>.39f,"swing actually advanced before retry");
        Need(tool.TrySwingHammer() && tool.SwingProgress<.001f,"new click restarts running swing immediately");
        Need(tool.TrySwingHammer() && tool.SwingProgress<.001f,"rapid repeated click restarts again");
        float deadline=Time.time+1.4f;while(tool.IsSwinging && Time.time<deadline)yield return null;
        Need(!tool.IsSwinging,"one second swing completes");
        var line=PrototypeLineController.Current;
        var lever=line.leverRoot.GetComponentInChildren<Collider>();
        var point=lever.bounds.center;
        Need(!line.TryHammerContact(point,point,.1f),"remote physical command rejected");
        var before=line.lever;
        var pos=point+Vector3.back*.85f;pos.y=oldPosition.y;
        player.Teleport(pos);player.transform.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(point-pos,Vector3.up));
        Need(line.TryHammerContact(point,point,.1f) && line.lever!=before,"near hammer contact advances lever");line.SetLever(before);
        var tunnel=line.supplyTunnel.GetComponentInChildren<Collider>();point=tunnel.ClosestPoint(player.transform.position+Vector3.up);
        pos=point+Vector3.back*.85f;pos.y=oldPosition.y;player.Teleport(pos);
        player.transform.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(point-pos,Vector3.up));
        int capacity=line.spawner.queueCapacity;line.spawner.queueCapacity=0;
        bool fullHit=line.TryHammerContact(point,point,.1f);
        var feedback=line.GetComponent<PrototypeTunnelFeedback>();
        var shake=(float)typeof(PrototypeTunnelFeedback).GetField("shakeTime",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(feedback);
        Need(fullHit && shake>0f,"full queue still accepts hammer feedback and shakes");line.spawner.queueCapacity=capacity;
        line.SetLever(PrototypeLineController.LeverPosition.Stop);
        var progression=ProductionProgression.Current;
        car=CarPool.Instance.Get();car.SetPath(progression.mainSpline,0,progression.processes[0].gateProgress+.0002f);progression.Stamp(car);
        yield return null;
        var hull=car.GetComponentInChildren<CarBodyCollision>();
        Collider roof=null;float top=float.NegativeInfinity;
        foreach(var box in hull.GetComponents<Collider>())if(box.enabled && box.bounds.max.y>top){roof=box;top=box.bounds.max.y;}
        Need(roof is MeshCollider meshHull && meshHull.convex && hull.GetComponents<Collider>().Length==1,"exterior hull is a single closed convex mesh");
        player.Teleport(new Vector3(roof.bounds.center.x,top+.05f,roof.bounds.center.z));
        for(int i=0;i<18;i++){player.SimulateMovement(Vector2.zero,false,false,Time.deltaTime);yield return null;}
        Need(player.SupportCar==car,"standing on car acquires support");
        var old=player.transform.position;car.transform.position+=Vector3.right*.5f;
        player.SimulateMovement(Vector2.zero,false,false,.02f);
        Need(Mathf.Abs((player.transform.position-old).x-.5f)<.08f,"standing actor follows vehicle translation");
        var local=car.transform.InverseTransformPoint(player.transform.position);
        car.transform.rotation=Quaternion.Euler(0,25,0)*car.transform.rotation;
        var expected=car.transform.TransformPoint(local);
        player.SimulateMovement(Vector2.zero,false,false,.02f);
        Need(Vector3.Distance(player.transform.position,expected)<.12f,"standing actor follows vehicle rotation");
        player.SimulateMovement(Vector2.zero,false,true,.02f);
        Need(player.SupportCar==null,"jump releases vehicle support");
        player.Teleport(car.transform.position+Vector3.right*2f);
        var query=player.GetComponent<PrototypeHammerContact>();
        var part=car.GetComponentsInChildren<AssemblyPart>(true)[0];Vector3 surface;
        query.TrySurfacePoint(part,new Ray(player.transform.position,Vector3.left),player.transform.position,4f,out surface);
        int count=query.CachedSurfaceCount;
        query.TrySurfacePoint(part,new Ray(player.transform.position,Vector3.left),player.transform.position,4f,out surface);
        Need(count>0 && query.CachedSurfaceCount==count,"query cache reuses vehicle surfaces");
        Need(GameObject.Find("RuntimeQueries")!=null && GameObject.Find("HammerQuerySurface")==null,"query cache grouped instead of unlabelled root objects");
    }
}
#endif
