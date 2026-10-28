#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Text;
using UnityEngine;
using Object=UnityEngine.Object;

public static class FactoryPlayFeedbackChecks
{
    public static string Result="idle";
    static StringBuilder log;
    static FactoryPlayerController player;
    static PlayerAssemblyInteractor interactor;
    static CarController car;
    static bool environmentOnly;
    public static void Start(bool onlyEnvironment=false)
    {
        if(!Application.isPlaying)throw new InvalidOperationException("Play required");
        environmentOnly=onlyEnvironment;log=new StringBuilder();Result="running";
        player=Object.FindFirstObjectByType<FactoryPlayerController>();interactor=player.interactor;
        player.StartCoroutine(Guard());
    }
    static IEnumerator Guard()
    {
        var routine=Run();bool ok=true;
        while(true)
        {
            bool more=false;object wait=null;
            try {more=routine.MoveNext();if(more)wait=routine.Current;}
            catch(Exception ex){log.AppendLine("FAIL "+ex);ok=false;break;}
            if(!more)break;yield return wait;
        }
        Result=(ok?"PASS\n":"FAIL\n")+log;Debug.Log(Result);
    }
    static void Need(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static void Aim(AssemblyPart part)
    {
        var target=interactor.AttachmentAimPoint(part);
        player.Teleport(new Vector3(target.x,target.y-1.1f,target.z+1.7f));
        player.viewCamera.transform.SetPositionAndRotation(player.transform.position+Vector3.up*1.62f,Quaternion.LookRotation(target-(player.transform.position+Vector3.up*1.62f)));
        Physics.SyncTransforms();
    }
    static IEnumerator Run()
    {
        var line=PrototypeLineController.Current;line.SetLever(PrototypeLineController.LeverPosition.Stop);
        player.enabled=false;interactor.enabled=false;interactor.inputEnabled=true;Cursor.lockState=CursorLockMode.Locked;
        yield return null;
        if (!environmentOnly)
        {
        car=CarPool.Instance.Get();car.SetPath(line.spawner.mainLineSpline,0,StationWorkZone.ProgressAt(line.spawner.mainLineSpline,new Vector3(50,0,35)));ProductionProgression.Current.Stamp(car);
        var parts=car.GetComponentsInChildren<AssemblyPart>(true).Where(p=>p.myType!=PartType.Frame_1&&p.myType!=PartType.None).ToArray();
        var stacks=Object.FindObjectsByType<PartStack>(FindObjectsSortMode.None);
        Need(parts.Length==26,"26 parts required");
        for(int i=0;i<parts.Length;i++)
        {
            var part=parts[i];var stack=stacks.First(s=>s.partType==part.myType&&s.Count>0);
            player.Teleport(stack.transform.position-Vector3.up*.4f);Physics.SyncTransforms();int stock=stack.Count;
            Need(interactor.TryTake(stack),"take "+part.myType);
            Aim(part);Need(interactor.TryAttach(car),"start aim "+part.myType+" target="+interactor.AttachmentAimPoint(part)+" reach="+player.transform.position);
            if(i==0)
            {
                interactor.AdvanceHeldAttachment(true,.8f);Need(interactor.IsAttaching&&interactor.AttachmentProgress>.39f,"hold progress");
                interactor.AdvanceHeldAttachment(false,.01f);Need(!interactor.IsAttaching&&interactor.HeldPart!=null&&!part.IsPlayerAttached,"release must cancel");
                Aim(part);Need(interactor.TryAttach(car),"restart");player.viewCamera.transform.rotation=Quaternion.LookRotation(Vector3.up);
                interactor.AdvanceHeldAttachment(true,.8f);Need(!interactor.IsAttaching&&interactor.HeldPart!=null,"look away cancel");
                Aim(part);Need(interactor.TryAttach(car),"restart2");interactor.inputEnabled=false;interactor.AdvanceHeldAttachment(true,.01f);Need(!interactor.IsAttaching,"modal cancel");interactor.inputEnabled=true;
                Aim(part);Need(interactor.TryAttach(car),"restart3");log.AppendLine("Release, lost aim, disabled input: cancel with carried stock preserved.");
            }
            interactor.AdvanceHeldAttachment(true,1.99f);Need(interactor.IsAttaching&&!part.IsPlayerAttached,"early complete "+part.myType);
            interactor.AdvanceHeldAttachment(true,.011f);Need(!interactor.IsAttaching&&part.IsPlayerAttached&&interactor.HeldPart==null,"2sec completion "+part.myType);
            Need(stack.Count==stock-1,"stock charged more than once");
            for(int hit=0;hit<3;hit++){yield return new WaitForSeconds(.31f);Need(interactor.TryHammer(part),"hammer "+part.myType+" hit "+hit);}
            Need(part.IsAssembled,"incomplete part "+part.myType);Result="running assembly "+(i+1)+"/26";
        }
        log.AppendLine("All26 real stock parts: aimed hold2s then78 timed hammer hits passed.");
        Need(VehicleInspectionRules.CountAllMissing(car.GetComponentsInChildren<AssemblyPart>(true))==0,"complete car inspection");
        CarPool.Instance.Return(car);car=null;
        }
        // CharacterController paths, without teleporting through a barrier.
        foreach(var entry in new[]{new Vector3(57,-.36f,-5),new Vector3(43.4f,-.36f,3),new Vector3(48.9f,-.36f,-8)})
        {
            var target=entry.z==3?new Vector3(43.4f,0,-1):entry.x==57?new Vector3(57,0,-1):new Vector3(48.9f,0,-4.7f);
            player.Teleport(entry);player.transform.rotation=Quaternion.LookRotation(new Vector3(target.x-entry.x,0,target.z-entry.z));Physics.SyncTransforms();
            for(int step=0;step<180;step++){player.SimulateMovement(Vector2.up,false,false,1f/60f);Physics.SyncTransforms();}
            Need(Vector2.Distance(new Vector2(player.transform.position.x,player.transform.position.z),new Vector2(target.x,target.z))>1.5f,"hazard entry "+target);
            Need(player.transform.position.y>-.7f,"fell near hazard");
        }
        log.AppendLine("Shake/reject/water approach: CharacterController blocked, no falling.");
        foreach(var c in Object.FindObjectsByType<LineCrossing>(FindObjectsSortMode.None).Where(c=>c.name.Contains("_Row")))
        {
            var from=c.transform.TransformPoint(new Vector3(0,.045f,-3.25f));var to=c.transform.TransformPoint(new Vector3(0,.045f,3.25f));
            player.Teleport(from);player.transform.rotation=Quaternion.LookRotation((to-from).normalized);Physics.SyncTransforms();
            for(int step=0;step<140;step++){player.SimulateMovement(Vector2.up,false,false,1f/60f);Physics.SyncTransforms();}
            float along=Vector3.Dot(player.transform.position-from,(to-from).normalized);
            Need(along>6f,"walkway traversal "+c.name+" distance="+along+" pos="+player.transform.position);
            Need(player.transform.position.y>-.7f,"walkway fell "+c.name);
        }
        log.AppendLine("All6 row ramps crossed walking without jumping.");
        // Real rejected vehicle, then physics lifetime cleanup.
        var shake=Object.FindFirstObjectByType<ShakeTestStation>();
        car=CarPool.Instance.Get();car.SetPath(line.spawner.mainLineSpline,0,shake.gateProgress-.0001f);ProductionProgression.Current.Stamp(car);
        var assembled=car.GetComponentsInChildren<AssemblyPart>(true).Where(p=>p.myType!=PartType.Frame_1).Take(8).ToArray();foreach(var p in assembled)p.SetAssembled();
        float until=Time.time+12f;while(PrototypeCarDebris.ActiveCount==0&&Time.time<until)yield return null;
        Need(PrototypeCarDebris.ActiveCount>0&&PrototypeCarDebris.ActiveCount<=40,"no failure burst");
        Need(Object.FindObjectsByType<PrototypeCarDebris>(FindObjectsSortMode.None).All(d=>d.GetComponent<PortableAssemblyPart>()==null&&d.GetComponent<AssemblyPart>()==null&&d.GetComponent<Rigidbody>()!=null),"debris reusable");
        int debris=PrototypeCarDebris.ActiveCount;yield return new WaitForSeconds(4.5f);Need(PrototypeCarDebris.ActiveCount==0,"debris lifetime");
        log.AppendLine("Actual shake failure burst: "+debris+" rigidbody fragments, non-reusable, expired4.25s, cap40.");
        player.Teleport(player.SpawnPoint);player.enabled=true;interactor.enabled=true;interactor.inputEnabled=true;
    }
}
#endif
