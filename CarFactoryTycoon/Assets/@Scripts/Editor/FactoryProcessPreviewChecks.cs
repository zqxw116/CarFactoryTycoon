#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

public static class FactoryProcessPreviewChecks
{
    public static string Result="idle";
    static ProcessAuthoringPreview preview;
    static ProcessAuthoringProfile original,fixture;
    static int consoleErrors;
    static void OnLog(string text,string stack,LogType type){if(type==LogType.Error||type==LogType.Exception)consoleErrors++;}
    static readonly StringBuilder log=new StringBuilder();
    static void Need(bool value,string message){if(!value)throw new InvalidOperationException(message);log.AppendLine("OK "+message);}
    public static void Start()
    {
        if(!Application.isPlaying)throw new InvalidOperationException("Play required.");
        preview=Object.FindFirstObjectByType<ProcessAuthoringPreview>();
        if(preview==null)throw new InvalidOperationException("Authoring scene required.");
        if(Result.StartsWith("running"))return;
        original=preview.progression.authoringProfile;fixture=Object.Instantiate(original);
        preview.progression.authoringProfile=fixture;consoleErrors=0;Application.logMessageReceived+=OnLog;Result="running";log.Clear();preview.StartCoroutine(Guard());
    }
    static IEnumerator Guard()
    {
        IEnumerator routine=Run();Exception error=null;
        while(true)
        {
            bool more=false;object next=null;
            try{more=routine.MoveNext();if(more)next=routine.Current;}catch(Exception e){error=e;}
            if(error!=null||!more)break;yield return next;
        }
        preview.Stop();preview.progression.authoringProfile=original;
        Application.logMessageReceived-=OnLog;
        if(error==null && consoleErrors>0)error=new InvalidOperationException("Console errors during preview checks: "+consoleErrors);
        if(fixture!=null)Object.Destroy(fixture);
        Result=(error==null?"PASS":"FAIL "+error)+"\n"+log;
        SessionState.SetString("FactoryProcessPreviewChecks",Result);Debug.Log(Result);
    }
    public static void StartMatrix()
    {
        if(!Application.isPlaying)throw new InvalidOperationException("Play required.");
        preview=Object.FindFirstObjectByType<ProcessAuthoringPreview>();
        original=preview.progression.authoringProfile;fixture=Object.Instantiate(original);
        preview.progression.authoringProfile=fixture;consoleErrors=0;Application.logMessageReceived+=OnLog;Result="running";log.Clear();preview.StartCoroutine(MatrixGuard());
    }
    static IEnumerator MatrixGuard()
    {
        var run=Matrix();Exception error=null;
        while(true){bool more=false;object next=null;try{more=run.MoveNext();if(more)next=run.Current;}catch(Exception e){error=e;}if(error!=null||!more)break;yield return next;}
        preview.Stop();preview.progression.authoringProfile=original;Application.logMessageReceived-=OnLog;
        if(error==null&&consoleErrors>0)error=new InvalidOperationException("Console errors "+consoleErrors);
        Object.Destroy(fixture);Result=(error==null?"PASS":"FAIL "+error)+"\n"+log;Debug.Log(Result);
    }
    static IEnumerator Matrix()
    {
        foreach(int index in new[]{1,8,13,14,15,16,preview.progression.processes.Length})foreach(bool robot in new[]{false,true})
        {
            preview.Select(index,robot,0);int beforeStock=index<preview.progression.processes.Length?preview.progression.processes[index].humanStation.partStacks.Sum(x=>x.Count):0;preview.SetSpeed(20);preview.PlayOnce();float deadline=Time.realtimeSinceStartup+16;
            while(Time.realtimeSinceStartup<deadline&&preview.Status!="1회 완료"&&!preview.Status.Contains("미완료")&&!preview.Status.Contains("불가")){Result="running matrix "+index+" robot="+robot;yield return null;}
            Need(preview.Status=="1회 완료","matrix "+index+" robot="+robot+" => "+preview.Status);
            if(index<preview.progression.processes.Length)Need(beforeStock-preview.progression.processes[index].humanStation.partStacks.Sum(x=>x.Count)==preview.progression.processes[index].parts.Length,"matrix inventory exact "+index+" robot="+robot);
        }
        preview.Select(0,false,0);preview.SetSpeed(20);preview.PlayOnce();
        for(int i=0;i<3;i++)yield return null;
        preview.Pause(true);
        var entry=preview.progression.processes[0];var record=fixture.processes[0];var stock=entry.humanStation.partStacks[0];var layout=record.FindStock(ProcessAuthoringLayout.Key(stock));
        layout.pickup.position.z+=.25f;
        var expected=ProcessAuthoringLayout.WithDistance(layout.pickup,layout.side,fixture.actorDistance).WorldPosition(ProcessAuthoringLayout.Frame(preview.progression,entry));
        preview.Retry();
        Need(Vector3.Distance(stock.ReadyPosition,expected)<.001f,"saved pickup layout reapplied on retry");
        preview.Stop();
        preview.SetSpeed(20);preview.Pause(true);bool recovery=SessionState.GetBool("ProcessAuthoring.RecoveryDirty",false);SessionState.SetBool("ProcessAuthoring.RecoveryDirty",false);try{var window=ScriptableObject.CreateInstance<ProcessAuthoringWindow>();Object.DestroyImmediate(window);}finally{SessionState.SetBool("ProcessAuthoring.RecoveryDirty",recovery);}Need(Mathf.Abs(Time.timeScale-1f)<.001f,"closing paused workbench restores clock");
        var fuel=preview.fuelStation;var model=fixture.vehicles[0];model.overrideFuelPort=true;model.fuelPort=new Vector3(.7f,1.1f,.4f);
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(FuelStation).GetField("currentCar",flags).SetValue(fuel,preview.Car);
        var port=(Vector3)typeof(FuelStation).GetMethod("PortPosition",flags).Invoke(fuel,null);
        Need(Vector3.Distance(port,preview.Car.transform.TransformPoint(model.fuelPort))<.001f,"fuel port override resolved by shared station");
        fuel.CancelCycle();
        preview.Select(0,false,0);var worker=record.actors.First(x=>x.kind==ProcessActorKind.Worker);worker.hasWorkPose=true;worker.workPose=worker.home;worker.workPose.position.z+=6;
        preview.PlayOnce();float end=Time.realtimeSinceStartup+16;
        while(Time.realtimeSinceStartup<end&&preview.Status!="1회 완료"&&!preview.Status.Contains("미완료")){yield return null;}
        Need(preview.Status.Contains("미완료"),"unreachable work pose cannot falsely complete");
    }
    public static string CheckCarryGrip(Worker worker,AssemblyPart part)
    {
        worker.PrototypeGrip(part);
        WorkerHandIK.TryComputeGrip(part.transform,worker.maxGripHalfSpan,worker.gripEndInset,worker.gripFaceInset,out var a,out var b,out var finger);
        var expected=worker.transform.position+worker.transform.forward*worker.handForward+Vector3.up*worker.handHeight+worker.transform.rotation*worker.carryOffset;
        var actual=worker.PrototypeCarryPoint+worker.PrototypeCarryRotation*Vector3.Scale((a+b)*.5f,part.transform.lossyScale);
        if(Vector3.Distance(expected,actual)>.001f)throw new InvalidOperationException("Carry must align grip center rather than part pivot");
        return "PASS: carry grip center aligned within1mm";
    }
    static IEnumerator Run()
    {
        Need(UnityEngine.SceneManagement.SceneManager.sceneCount==1,"one isolated scene");
        var actor=fixture.processes[0].actors.First(x=>x.kind==ProcessActorKind.Worker);
        actor.useAnimationRigging=true;actor.bodySupport=true;
        actor.fetchPath.enabled=true;actor.fetchPath.control1=Vector3.up*.05f;actor.fetchPath.control2=Vector3.zero;
        actor.approachPath.enabled=true;actor.approachPath.control1=Vector3.up*.05f;actor.approachPath.control2=Vector3.zero;
        actor.carryPath.enabled=true;actor.carryPath.control1=Vector3.up*.2f;actor.carryPath.control2=Vector3.up*.1f;
        preview.Select(0,false,0);var entry=preview.progression.processes[0];var stock=entry.humanStation.partStacks[0];
        int count=stock.Count;var crew=PrototypeProcessCrew.Ensure(preview.progression,0);int complete=crew.CompletedPartCount;
        preview.SetSpeed(20);preview.PlayOnce();float deadline=Time.realtimeSinceStartup+25;
        while(Time.realtimeSinceStartup<deadline && preview.Status!="1회 완료" && !preview.Status.Contains("불가")){Result="running: curved worker + rig";yield return null;}
        Need(preview.Status=="1회 완료"&&crew.CompletedPartCount==complete+1,"worker curves + package rig complete at20x");
        Need(stock.Count==count-1,"worker stock consumed once");
        Need(entry.workers[0].handIK.GetComponent<WorkerInteractionRig>()!=null,"Animation Rigging solver built");
        preview.Select(0,true,0);count=stock.Count;complete=crew.CompletedPartCount;int hits=crew.StrikeCount;
        preview.PlayOnce();deadline=Time.realtimeSinceStartup+25;
        while(Time.realtimeSinceStartup<deadline&&preview.Status!="1회 완료"&&!preview.Status.Contains("불가")){Result="running: cold robot20x";yield return null;}
        Need(preview.Status=="1회 완료"&&crew.CompletedPartCount==complete+1,"cold robot waiting pose +20x moving fastening");
        Need(stock.Count==count-1&&crew.StrikeCount-hits==3,"robot three hits and one stock");
        preview.Select(0,false,0);count=stock.Count;preview.SetSpeed(20);preview.PlayOnce();deadline=Time.realtimeSinceStartup+20;
        while(Time.realtimeSinceStartup<deadline&&stock.Count==count){Result="running: wait pickup for retry";yield return null;}
        Need(stock.Count==count-1,"retry fixture actually picked up");
        preview.Pause(true);var position=preview.Car.transform.position;float wait=Time.realtimeSinceStartup+.15f;
        while(Time.realtimeSinceStartup<wait)yield return null;
        Need(Time.timeScale==0&&Vector3.Distance(position,preview.Car.transform.position)<.0001f,"pause freezes simulation");
        preview.Retry();Need(stock.Count==count,"retry restores run-start inventory");
        deadline=Time.realtimeSinceStartup+25;
        while(Time.realtimeSinceStartup<deadline&&preview.Status!="1회 완료"&&!preview.Status.Contains("불가"))yield return null;
        Need(preview.Status=="1회 완료"&&stock.Count==count-1,"retry repicks exactly once without waiting for vehicle entry");
        preview.SetSpeed(.1f);preview.PlayOnce();deadline=Time.realtimeSinceStartup+5;
        while(Time.realtimeSinceStartup<deadline&&!preview.Running)yield return null;
        var before=preview.Car.pathProgress;for(int i=0;i<6;i++)yield return null;
        Need(Time.timeScale==.1f&&preview.Car.pathProgress>before&&preview.Car.pathProgress-before<.02f,"0.1x advances slowly");
        preview.Stop();Need(Mathf.Abs(Time.timeScale-1f)<.0001f,"stop restores time scale");
        preview.Select(0,false,0);preview.SetSpeed(20);count=stock.Count;int runs=preview.CompletedRuns;preview.autoRun=true;preview.PlayOnce();deadline=Time.realtimeSinceStartup+20;
        while(Time.realtimeSinceStartup<deadline&&preview.CompletedRuns<runs+2)yield return null;
        preview.autoRun=false;preview.Stop();Need(preview.CompletedRuns>=runs+2&&stock.Count<=count-2,"automatic repeat consumes real inventory");
        while(stock.Count>0)stock.TryConsume();stock.ResetReadySlot();preview.PlayOnce();deadline=Time.realtimeSinceStartup+12;
        while(Time.realtimeSinceStartup<deadline&&!preview.Status.Contains("미완료"))yield return null;
        Need(preview.Status.Contains("미완료")&&stock.Count==0,"empty inventory passes and reports incomplete");
    }
}
#endif
