#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;
using Object = UnityEngine.Object;

/// <summary>Frame-driven technical measurements. Does not certify appearance or natural player feel.</summary>
public static class FactoryCharacterChecks
{
    public static string Result = "idle";
    static FactoryPlayerController player;
    static PlayerAssemblyInteractor hands;
    static StringBuilder log;
    static int failures;
    static bool auditFailed;
    static int oldMoney;
    static PrototypeProcessCrew testCrew;
    static bool oldDuty, oldOvertime;
    static bool oldEnabled, oldInput, oldVisible;
    static CursorLockMode oldCursor;
    static Vector3 oldPosition;
    static Quaternion oldRotation, oldViewRotation;
    static PrototypeLineController line;
    static PrototypeLineController.LeverPosition oldLever;
    static CarController testCar;
    static readonly Dictionary<PartStack,int> stock = new Dictionary<PartStack,int>();
    static ProductionProgression.ProcessEntry hiredEntry;
    static bool oldHired, oldAutomated;
    static readonly Dictionary<Worker,(Vector3,Quaternion,float,bool)> workers = new Dictionary<Worker,(Vector3,Quaternion,float,bool)>();
    static float started;
    static void Check(bool value, string text)
    { log.AppendLine((value ? "OK " : "FAIL ") + text); if (!value) failures++; }

    public static string AuditScene()
    {
        var report = new StringBuilder(); int missingScripts=0, brokenReferences=0, humanoids=0; auditFailed=false;
        foreach(var root in SceneManager.GetActiveScene().GetRootGameObjects())
        foreach(var transform in root.GetComponentsInChildren<Transform>(true))
        {
            missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
            foreach(var component in transform.GetComponents<Component>())
            {
                if(component==null)continue;
                var serialized=new SerializedObject(component);var property=serialized.GetIterator();
                while(property.NextVisible(true))
                    if(property.propertyType==SerializedPropertyType.ObjectReference && property.objectReferenceValue==null && property.objectReferenceInstanceIDValue!=0)
                    {brokenReferences++;report.AppendLine("Broken reference "+transform.name+" / "+component.GetType().Name+"."+property.propertyPath);}
            }
        }
        var eligible = Object.FindObjectsByType<Worker>(FindObjectsInactive.Include,FindObjectsSortMode.None).Select(w=>w.gameObject)
            .Concat(Object.FindObjectsByType<SupplyWorker>(FindObjectsInactive.Include,FindObjectsSortMode.None).Select(w=>w.gameObject))
            .Concat(Object.FindObjectsByType<FactoryPlayerController>(FindObjectsInactive.Include,FindObjectsSortMode.None).Select(w=>w.gameObject))
            .Concat(Object.FindObjectsByType<FuelStation>(FindObjectsInactive.Include,FindObjectsSortMode.None).Where(f=>f.attendant!=null).Select(f=>f.attendant.gameObject)).Distinct().ToArray();
        foreach(var root in eligible)
        {
            var actor=root.GetComponentInChildren<Animator>(true);
            if(actor==null){report.AppendLine("Missing actor Animator "+root.name);continue;}
            if(actor.avatar==null || !actor.avatar.isValid || !actor.isHuman){report.AppendLine("Invalid humanoid "+actor.name);continue;}
            humanoids++;
            foreach(var bone in new[]{HumanBodyBones.Hips,HumanBodyBones.Head,HumanBodyBones.LeftFoot,HumanBodyBones.RightFoot,
                HumanBodyBones.LeftUpperArm,HumanBodyBones.LeftLowerArm,HumanBodyBones.LeftHand,
                HumanBodyBones.RightUpperArm,HumanBodyBones.RightLowerArm,HumanBodyBones.RightHand})
                if(actor.GetBoneTransform(bone)==null)report.AppendLine("Missing bone "+actor.name+" / "+bone);
        }
        auditFailed=missingScripts>0 || brokenReferences>0 || humanoids!=eligible.Length || report.Length>0;
        report.Insert(0,$"Scene missingScripts={missingScripts}, brokenSerializedRefs={brokenReferences}, actorHumanoids={humanoids}\n");
        return report.ToString();
    }
    public static void Start(bool contacts=true, bool npc=true)
    {
        if(!Application.isPlaying)throw new InvalidOperationException("Play required.");
        if(Result.StartsWith("running"))return;
        player=Object.FindFirstObjectByType<FactoryPlayerController>();
        if(player==null || player.interactor==null)throw new InvalidOperationException("Player references missing.");
        hands=player.interactor;
        if(hands.HeldPart!=null || hands.IsAttaching || hands.IsSwinging)throw new InvalidOperationException("Start with empty hands and no work in progress.");
        log=new StringBuilder();failures=0;stock.Clear();workers.Clear();hiredEntry=null;testCar=null;testCrew=null;oldMoney=EconomyManager.Instance.currentMoney;started=Time.realtimeSinceStartup;
        oldEnabled=player.enabled;oldInput=hands.inputEnabled;oldCursor=Cursor.lockState;oldVisible=Cursor.visible;
        oldPosition=player.transform.position;oldRotation=player.transform.rotation;oldViewRotation=player.viewCamera.transform.rotation;
        line=PrototypeLineController.Current;oldLever=line.lever;
        foreach(var pile in Object.FindObjectsByType<PartStack>(FindObjectsInactive.Include,FindObjectsSortMode.None))stock[pile]=pile.Count;
        player.enabled=false;hands.inputEnabled=true;Cursor.lockState=CursorLockMode.Locked;Cursor.visible=false;
        line.SetLever(PrototypeLineController.LeverPosition.Stop);Result="running";
        player.StartCoroutine(Guard(Run(contacts,npc)));
    }
    static IEnumerator Guard(IEnumerator routine)
    {
        var stack=new Stack<IEnumerator>();stack.Push(routine);
        while(stack.Count>0)
        {
            object next=null;bool more=false;
            try{more=stack.Peek().MoveNext();if(more)next=stack.Peek().Current;}
            catch(Exception ex){failures++;log.AppendLine("EXCEPTION "+ex);break;}
            if(!more){stack.Pop();continue;}
            if(Time.realtimeSinceStartup-started>65f){failures++;log.AppendLine("FAIL timeout 65s");break;}
            if(next is IEnumerator nested){stack.Push(nested);continue;}
            yield return next;
        }
        try{Cleanup();}catch(Exception ex){failures++;log.AppendLine("Cleanup failed: "+ex);}
        Result=(failures==0?"PASS\n":"FAIL "+failures+"\n")+log;
        Debug.Log(Result);
    }
    static IEnumerator Run(bool contacts,bool npc)
    {
        log.AppendLine(AuditScene()); Check(!auditFailed,"scene serialized references and all gameplay actor humanoid bone mappings");
        Check(player.bodyAnimator!=null && hands.bodyAnimator==player.bodyAnimator,"player animator reference shared with interaction");
        Check(hands.hammerAnchor==player.bodyAnimator.GetBoneTransform(HumanBodyBones.RightHand),"hammer anchor maps to actual right hand");
        var actors=Object.FindObjectsByType<Worker>(FindObjectsInactive.Include,FindObjectsSortMode.None);
        var supply=Object.FindObjectsByType<SupplyWorker>(FindObjectsInactive.Include,FindObjectsSortMode.None);
        Check(actors.Length==26 && supply.Length==1,$"gameplay roots workers={actors.Length}, supply={supply.Length}");
        foreach(var worker in actors)
            Check(worker.GetComponentInChildren<Animator>(true)!=null && (worker.handPos!=null || worker.handIK!=null),worker.name+" root retains animator/hand reference");
        // Reuse a known factory-floor aisle. All movement is CharacterController simulation over live frames.
        var origin=new Vector3(57f,-.36f,-5f);player.Teleport(origin);player.transform.rotation=Quaternion.identity;
        for(int frame=0;frame<12;frame++){player.SimulateMovement(Vector2.zero,false,false,Time.deltaTime);yield return null;}
        origin=player.transform.position;
        var directions=new[]{Vector2.up,Vector2.down,Vector2.left,Vector2.right,new Vector2(1,1),new Vector2(-1,1),new Vector2(1,-1),new Vector2(-1,-1)};
        foreach(bool sprint in new[]{false,true})foreach(var direction in directions)
        {
            player.Teleport(origin);Vector3 before=player.transform.position;
            for(int frame=0;frame<9;frame++){player.SimulateMovement(direction,sprint,false,Time.deltaTime);yield return null;}
            Vector3 delta=player.transform.position-before;delta.y=0;
            float speed=sprint?player.settings.playerSprintSpeed:player.settings.playerWalkSpeed;
            Vector2 expected=direction.normalized*speed;
            Check(Mathf.Abs(player.bodyAnimator.GetFloat("MoveX")-expected.x)<.02f && Mathf.Abs(player.bodyAnimator.GetFloat("MoveZ")-expected.y)<.02f,
                $"direction {direction} sprint={sprint} MoveX/Z={player.bodyAnimator.GetFloat("MoveX"):F2}/{player.bodyAnimator.GetFloat("MoveZ"):F2}, displacement={delta}");
        }
        player.Teleport(origin);bool takeoff=false,air=false,land=false;float highest=origin.y;
        for(int frame=0;frame<150;frame++)
        {
            player.SimulateMovement(Vector2.zero,false,frame==0,Time.deltaTime);yield return null;
            var state=player.bodyAnimator.GetCurrentAnimatorStateInfo(0);
            takeoff|=state.IsName("Takeoff");air|=state.IsName("Airborne");land|=state.IsName("Grounded Landing");highest=Mathf.Max(highest,player.transform.position.y);
            if(frame>15 && land)break;
        }
        Check(takeoff&&air&&land,$"jump states takeoff={takeoff},air={air},landing={land}; maximum root rise={highest-origin.y:F3}m");
        for(int settle=0;settle<30;settle++){player.SimulateMovement(Vector2.zero,false,false,Time.deltaTime);yield return null;}
        var left=player.bodyAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);var right=player.bodyAnimator.GetBoneTransform(HumanBodyBones.RightFoot);
        if(Physics.Raycast(player.transform.position+Vector3.up*.5f,Vector3.down,out var ground,2f,1<<0))
            log.AppendLine($"Foot heights relative to floor: left={left.position.y-ground.point.y:F3}m,right={right.position.y-ground.point.y:F3}m; numeric record only.");
        if(contacts)yield return HammerContacts();
        if(npc)yield return NpcContacts();
    }
    static IEnumerator SwingAndGrip()
    {
        Check(hands.TrySwingHammer(),"swing accepted");
        var grip=hands.hammerAnchor.Find("PlayerHammer");
        if(grip==null){Check(false,"PlayerHammer child missing");yield break;}
        var position=grip.localPosition;var rotation=grip.localRotation;float deadline=Time.time+1.2f;float drift=0,angle=0;
        do
        {
            yield return null;
            drift=Mathf.Max(drift,Vector3.Distance(position,grip.localPosition));angle=Mathf.Max(angle,Quaternion.Angle(rotation,grip.localRotation));
        }while(hands.IsSwinging && Time.time<deadline);
        int hammerLayer=player.bodyAnimator.GetLayerIndex("Hammer");
        Check(hammerLayer>=0 && player.bodyAnimator.GetLayerWeight(hammerLayer)<.001f,"hammer layer returns to zero after swing; base locomotion arms restored");
        Check(drift<.0001f && angle<.01f,$"fixed grip maximum drift={drift:F6}m rotation={angle:F4}deg");
    }
    static IEnumerator HammerContacts()
    {
        var progression=ProductionProgression.Current;var spline=line.spawner.mainLineSpline;
        testCar=CarPool.Instance.Get();testCar.SetPath(spline,0,Mathf.Clamp01(progression.processes[0].gateProgress+.05f/spline.CalculateLength()));progression.Stamp(testCar);
        var part=testCar.GetComponentsInChildren<AssemblyPart>(true).First(p=>p.myType==progression.processes[0].parts[0]);
        Check(part.TryBeginPlayerAttachment(player,part.AttachmentWorldPosition,part.AttachmentWorldRotation),"contact fixture begins attachment");
        part.AdvancePlayerAttachment(player,1f);Check(part.CompletePlayerAttachment(player),"contact fixture completes attachment");
        var target=hands.AttachmentAimPoint(part);
        player.Teleport(target+Vector3.up*(-1.1f)+testCar.transform.right*.80f);player.transform.rotation=Quaternion.LookRotation(new Vector3(target.x-player.transform.position.x,0,target.z-player.transform.position.z));
        player.viewCamera.transform.SetPositionAndRotation(player.transform.position+Vector3.up*1.62f,Quaternion.LookRotation(target-(player.transform.position+Vector3.up*1.62f)));
        Physics.SyncTransforms();yield return null;
        float before=part.currentWork;yield return SwingAndGrip();
        Check(part.currentWork>before,$"player actual head contact: work {before:F3}->{part.currentWork:F3}; head={hands.HammerHeadPosition},target={target}");
        player.Teleport(player.transform.position+Vector3.right*12f);player.viewCamera.transform.rotation=Quaternion.LookRotation(Vector3.up);
        before=part.currentWork;yield return SwingAndGrip();Check(part.currentWork==before,"empty-space swing awards no remote work");
        Check(hands.TrySwingHammer(),"cancellation fixture swing accepted");yield return null;
        hands.inputEnabled=false;yield return null;yield return null;
        int hammerLayer=player.bodyAnimator.GetLayerIndex("Hammer");
        Check(!hands.IsSwinging && player.bodyAnimator.GetLayerWeight(hammerLayer)<.001f,"input cancellation clears hammer layer");hands.inputEnabled=true;
        CarPool.Instance.Return(testCar);testCar=null;
    }
    static IEnumerator NpcContacts()
    {
        var progression=ProductionProgression.Current;hiredEntry=progression.processes[0];oldHired=hiredEntry.hired;oldAutomated=hiredEntry.automated;
        Check(!oldHired,"NPC fixture starts with first process unhired");if(oldHired)yield break;
        foreach(var worker in hiredEntry.workers)workers[worker]=(worker.transform.position,worker.transform.rotation,worker.condition,worker.gameObject.activeSelf);
        Check(progression.TryHireProcess(0),"NPC fixture hired");var crew=PrototypeProcessCrew.Ensure(progression,0);testCrew=crew;oldDuty=crew.WorkersOnDuty;oldOvertime=crew.Overtime;crew.SetDutyStatus(true,false);
        foreach(var pile in stock.Keys)if(hiredEntry.parts.Contains(pile.partType))pile.Fill_Full();
        int before=crew.StrikeCount;var spline=line.spawner.mainLineSpline;
        testCar=CarPool.Instance.Get();testCar.SetPath(spline,0,Mathf.Clamp01(hiredEntry.gateProgress+.05f/spline.CalculateLength()));progression.Stamp(testCar);
        float end=Time.time+26f; bool sawHammerClip=false;
        while(Time.time<end && crew.StrikeCount-before<3){foreach(var w in hiredEntry.workers){var a=w.GetComponentInChildren<Animator>();if(a!=null && a.layerCount>1 && a.GetCurrentAnimatorClipInfo(1).Any(c=>c.clip!=null && c.clip.name=="Worker_Hammer" && c.weight>.1f))sawHammerClip=true;}Result="running NPC automation "+(crew.StrikeCount-before)+"/3";yield return null;}
        Check(sawHammerClip,"NPC Mixamo hammer clip actually played");
        Check(crew.StrikeCount-before==3,$"NPC automatic timed hits={crew.StrikeCount-before}; completed parts={crew.CompletedPartCount}");
    }
    static void Cleanup()
    {
        if(testCar!=null && CarPool.Instance!=null)CarPool.Instance.Return(testCar);testCar=null;
        if(hiredEntry!=null)
        {
            hiredEntry.hired=oldHired;hiredEntry.automated=oldAutomated;
            if(testCrew!=null){testCrew.SetDutyStatus(false,false);testCrew.SetDutyStatus(oldDuty,oldOvertime);}
            typeof(ProductionProgression).GetMethod("ApplyProcess",BindingFlags.Instance|BindingFlags.NonPublic)?.Invoke(ProductionProgression.Current,new object[]{hiredEntry});
            foreach(var pair in workers){if(pair.Key==null)continue;pair.Key.PrototypeRelease();pair.Key.transform.SetPositionAndRotation(pair.Value.Item1,pair.Value.Item2);pair.Key.condition=pair.Value.Item3;pair.Key.gameObject.SetActive(pair.Value.Item4);}
        }
        foreach(var pair in stock)if(pair.Key!=null){int change=pair.Value-pair.Key.Count;if(change>0)pair.Key.Add(change);else if(change<0)for(int i=0;i<-change;i++)pair.Key.TryConsume();}
        if(EconomyManager.Instance!=null)EconomyManager.Instance.currentMoney=oldMoney;
        if(player!=null){player.Teleport(oldPosition);player.transform.rotation=oldRotation;player.viewCamera.transform.rotation=oldViewRotation;player.enabled=oldEnabled;}
        if(hands!=null)hands.inputEnabled=oldInput;
        if(line!=null)line.SetLever(oldLever);Cursor.lockState=oldCursor;Cursor.visible=oldVisible;
    }
}
#endif
