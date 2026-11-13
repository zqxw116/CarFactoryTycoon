#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Linq;
using System.Collections;
using UnityEngine;
using UnityEditor;

public static class FactoryProcessPoseAuthoringChecks
{
    static void Need(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Call(ProcessAuthoringWindow w,string method)=>typeof(ProcessAuthoringWindow).GetMethod(method,Flags).Invoke(w,null);
    static object Get(ProcessAuthoringWindow w,string field)=>typeof(ProcessAuthoringWindow).GetField(field,Flags).GetValue(w);
    static void Set(ProcessAuthoringWindow w,string field,object value)=>typeof(ProcessAuthoringWindow).GetField(field,Flags).SetValue(w,value);
    public static string EditRig()
    {
        var p=UnityEngine.Object.FindFirstObjectByType<ProcessAuthoringPreview>();Need(p!=null,"authoring scene");
        ProcessAuthoringWindow.Open();var w=Resources.FindObjectsOfTypeAll<ProcessAuthoringWindow>().First();
        Set(w,"processIndex",8);Set(w,"robot",false);Set(w,"tabIndex",1);Call(w,"Select");Call(w,"UpdateRigPreview");
        var preview=(ProcessAuthoringRigPreview)Get(w,"rigPreview");Need(preview?.root!=null,"pose twin created");
        Need(preview.avatar!=null&&preview.part!=null,"avatar and part visible");
        Need(preview.root.GetComponentsInChildren<Worker>(true).Length==0,"model twin has no gameplay Worker");
        Need((preview.root.hideFlags&HideFlags.DontSaveInEditor)!=0&&preview.root.CompareTag("EditorOnly"),"twin not saved/exported");
        Need(preview.reach.Ready&&preview.reach.leftVolume.isTrigger&&preview.reach.rightVolume.isTrigger,"measured trigger reach");
        int before=UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,FindObjectsSortMode.None).Length;
        int count=preview.stock.Count;for(int i=0;i<20;i++)Call(w,"UpdateRigPreview");
        Need(before==UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,FindObjectsSortMode.None).Length&&count==preview.stock.Count,"no twin accumulation or inventory consumption");
        var data=((ProcessAuthoringProfile)Get(w,"draft")).processes[8].stocks[0].grip;
        var old=data.leftWristEuler;var oldElbow=data.leftElbow;
        try
        {
            preview.hands.TryGetWristTargets(out var l,out var r,out var lr,out var rr);
            var elbowBefore=preview.avatar.GetBoneTransform(HumanBodyBones.LeftLowerArm).position;
            data.leftWristEuler+=new Vector3(20,0,0);data.leftElbow+=new Vector3(0,.3f,.2f);Call(w,"UpdateRigPreview");
            preview.hands.TryGetWristTargets(out l,out r,out var moved,out rr);
            Need(Quaternion.Angle(lr,moved)>15,"wrist pose changes twin");
            Need(Vector3.Distance(elbowBefore,preview.avatar.GetBoneTransform(HumanBodyBones.LeftLowerArm).position)>.001f,"elbow hint changes arm bend");
            var actual=p.progression.processes[8].workers[0].handIK;actual.SetPose(data);
            Need(actual.LeftPoseEuler==actual.leftHandEuler+data.leftWristEuler,"actual worker shares wrist settings");
        }
        finally{data.leftWristEuler=old;data.leftElbow=oldElbow;Call(w,"UpdateRigPreview");}
        p.IdleAtStation();Need(!p.Car.isMoving&&p.Idle,"explicit station idle");
        if(Application.isPlaying)Need(!PrototypeProcessCrew.Ensure(p.progression,8).IsBusy,"Idle no crew work");
        return "PASS twin uniqueness/no gameplay/no stock; wrist sharing; measured trigger; Idle";
    }
    public static string ReachMargin()
    {
        var go=new GameObject("ReachMarginProbe");
        try
        {
            var r=go.AddComponent<WorkerReachEnvelope>();r.leftLength=r.rightLength=.492f;
            r.leftShoulder=new Vector3(-.2f,1,0);r.rightShoulder=new Vector3(.2f,1,0);
            var a=new Vector3(-.2f,1,.3f);var b=new Vector3(.2f,1,.3f);
            var stand=r.StandFor(a,b,Vector3.back,0,.98f);go.transform.position=stand+Vector3.back*.005f;
            Need(r.Contains(a,b),"max reach must tolerate actual arrival error");Need(!r.Contains(a+Vector3.forward,b+Vector3.forward),"beyond arms must reject");
            return "PASS reach margin / outside rejection";
        }finally{UnityEngine.Object.DestroyImmediate(go);}
    }
    public static string PoseOwnership()
    {
        ProcessAuthoringWindow.Open();var w=Resources.FindObjectsOfTypeAll<ProcessAuthoringWindow>().First();Call(w,"UpdateRigPreview");
        var p=UnityEngine.Object.FindFirstObjectByType<ProcessAuthoringPreview>();var ik=p.progression.processes[8].workers[0].handIK;
        var field=typeof(WorkerHandIK).GetField("poseSettings",Flags);var previous=field.GetValue(ik);var held=new ProcessGripSettings();
        try{ik.SetPose(held);Call(w,"UpdateRigPreview");Need(object.ReferenceEquals(field.GetValue(ik),held),"inspection must preserve assigned-part pose");}
        finally{field.SetValue(ik,previous);}
        return "PASS actual pose ownership during inspection";
    }
    public static string MirroredWrist()
    {
        var partGo=new GameObject("MirrorWristProbe");var actorGo=new GameObject("MirrorHandProbe");
        try
        {
            var part=partGo.AddComponent<AssemblyPart>();partGo.SetActive(true);partGo.transform.localScale=new Vector3(-1,1,1);
            var data=new ProcessGripSettings{configured=true,left=new Vector3(0,0,-.2f),right=new Vector3(0,0,.2f),finger=Vector3.right};
            ProcessPartGrip.Apply(part,data,Vector3.one);var grip=part.GetComponent<PartGripOverride>();
            var hands=actorGo.AddComponent<WorkerHandIK>();hands.Grip(partGo.transform,grip.gripA,grip.gripB,grip.fingerDir);
            Need(hands.TryGetWristTargets(out var l,out var r,out var lr,out var rr),"active mirrored wrist targets");
            Need(Mathf.Abs(l.x+hands.palmOffset)<.00001f,"mirrored part preserves palm approach direction");
            return "PASS mirrored palm/wrist direction";
        }finally{UnityEngine.Object.DestroyImmediate(actorGo);UnityEngine.Object.DestroyImmediate(partGo);}
    }
    public static string IdleLayout()
    {
        ProcessAuthoringWindow.Open();var w=Resources.FindObjectsOfTypeAll<ProcessAuthoringWindow>().First();
        var original=(ProcessAuthoringProfile)Get(w,"draft");var fixture=UnityEngine.Object.Instantiate(original);var p=UnityEngine.Object.FindFirstObjectByType<ProcessAuthoringPreview>();
        bool dirty=(bool)Get(w,"dirty");bool unsaved=w.hasUnsavedChanges;
        try
        {
            Set(w,"draft",fixture);fixture.processes[8].actors[0].root.position.z+=.33f;Call(w,"ApplyLayout");
            string before=EditorJsonUtility.ToJson(fixture);p.IdleAtStationWithProfile(fixture);
            for(int i=0;i<20;i++)Call(w,"EditorTick");
            Need(before==EditorJsonUtility.ToJson(fixture),"Idle must not rewrite authored placement");
            return "PASS Idle staging preserves authored layout";
        }finally
        {
            Set(w,"draft",original);Call(w,"ApplyLayout");Set(w,"dirty",dirty);
            typeof(EditorWindow).GetProperty("hasUnsavedChanges",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).SetValue(w,unsaved);
            UnityEngine.Object.DestroyImmediate(fixture);
        }
    }
    public static string FixedTwin()
    {
        ProcessAuthoringWindow.Open();var w=Resources.FindObjectsOfTypeAll<ProcessAuthoringWindow>().First();Call(w,"UpdateRigPreview");
        var rig=(ProcessAuthoringRigPreview)Get(w,"rigPreview");var before=rig.root.transform.position;
        var old=rig.source.transform.position;
        try{rig.source.transform.position+=Vector3.right*.7f;Call(w,"UpdateRigPreview");Need(Vector3.Distance(before,rig.root.transform.position)<.00001f,"pose twin must stay fixed while actual worker walks");}
        finally{rig.source.transform.position=old;Call(w,"UpdateRigPreview");}
        return "PASS fixed twin independent of live movement";
    }
    public static string PlayDraft()
    {
        Need(Application.isPlaying,"Play required");var p=UnityEngine.Object.FindFirstObjectByType<ProcessAuthoringPreview>();
        var w=Resources.FindObjectsOfTypeAll<ProcessAuthoringWindow>().First();
        Need(object.ReferenceEquals(p.progression.authoringProfile,Get(w,"draft")),"Play entry must apply current unsaved draft");
        return "PASS unsaved draft connected on Play entry";
    }
    public static string MovableRig()
    {
        var w=Resources.FindObjectsOfTypeAll<ProcessAuthoringWindow>().First();
        typeof(ProcessAuthoringWindow).GetMethod("ResetRigPlacement",Flags).Invoke(w,new object[]{true});
        var rig=(ProcessAuthoringRigPreview)Get(w,"rigPreview");
        Need(rig!=null&&rig.IsReaching,"default forward reach");
        float[] angles=new float[2];int index=0;
        foreach(bool left in new[]{true,false})
        {
            var a=rig.avatar.GetBoneTransform(left?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm);
            var b=rig.avatar.GetBoneTransform(left?HumanBodyBones.LeftLowerArm:HumanBodyBones.RightLowerArm);
            var h=rig.avatar.GetBoneTransform(left?HumanBodyBones.LeftHand:HumanBodyBones.RightHand);
            var m=rig.avatar.GetBoneTransform(left?HumanBodyBones.LeftMiddleProximal:HumanBodyBones.RightMiddleProximal);
            angles[index++]=Vector3.Angle(b.position-a.position,h.position-b.position);
            var g=((ProcessAuthoringProfile)Get(w,"draft")).processes[8].stocks[0].grip;
            var across=rig.part.TransformVector(g.right-g.left);
            var finger=Vector3.ProjectOnPlane(rig.part.TransformVector(g.finger),across).normalized;
            Need(Vector3.Dot((m.position-h.position).normalized,finger)>.99f,"model fingers align without corrective eulers");
        }
        Need(Mathf.Abs(angles[0]-angles[1])<6&&Mathf.Max(angles[0],angles[1])<40,"both elbows extend symmetrically");
        var start=rig.root.transform.position;var rotation=rig.root.transform.rotation;
        rig.root.transform.position+=Vector3.back*4;Call(w,"UpdateRigPreview");
        Need(!rig.IsReaching,"outside reach shows Idle");
        var far=rig.root.transform.position;Call(w,"UpdateRigPreview");
        Need(Vector3.Distance(far,rig.root.transform.position)<.0001f,"manual placement is not overwritten");
        var d=(ProcessAuthoringProfile)Get(w,"draft");var actor=d.processes[8].actors.First(x=>x.kind==ProcessActorKind.Worker);
        Need(actor.hasRigPreviewPose,"manual pose recorded in profile");
        rig.root.transform.SetPositionAndRotation(start,rotation);Call(w,"UpdateRigPreview");
        Need(rig.IsReaching,"moving near reaches again");
        typeof(ProcessAuthoringWindow).GetMethod("ResetRigPlacement",Flags).Invoke(w,new object[]{false});
        return "PASS symmetric elbows "+angles[0]+"/"+angles[1]+"; calibrated fingers; drag/Idle/near/profile";
    }
    public static string ForceReachScope()
    {
        var w=Resources.FindObjectsOfTypeAll<ProcessAuthoringWindow>().First();Call(w,"UpdateRigPreview");
        var rig=(ProcessAuthoringRigPreview)Get(w,"rigPreview");
        var d=(ProcessAuthoringProfile)Get(w,"draft");var actor=d.processes[8].actors.First(x=>x.kind==ProcessActorKind.Worker);
        var p=UnityEngine.Object.FindFirstObjectByType<ProcessAuthoringPreview>();var frame=ProcessAuthoringLayout.Frame(p.progression,p.progression.processes[8]);
        var pose=AuthoringPose.Capture(rig.root.transform,frame);pose.position+=Vector3.forward*4;
        actor.rigPreviewPose=pose;actor.hasRigPreviewPose=true;Set(w,"forceRigReach",true);Set(w,"forceRigActor","another actor");
        Call(w,"UpdateRigPreview");
        Need(!rig.IsReaching,"other actor reset must not force this actor to reach");
        typeof(ProcessAuthoringWindow).GetMethod("ResetRigPlacement",Flags).Invoke(w,new object[]{false});
        return "PASS forced reach scoped to selected actor";
    }
    public static string SelectionSnapshot()
    {
        var p=UnityEngine.Object.FindFirstObjectByType<ProcessAuthoringPreview>();
        var field=typeof(ProcessAuthoringPreview).GetField("runStock",Flags);
        var snapshot=(System.Collections.Generic.Dictionary<PartStack,int>)field.GetValue(p);
        var stock=p.progression.processes[8].humanStation.partStacks[0];
        snapshot[stock]=123;
        p.Select(8,false,0);
        Need(snapshot.Count==0,"new selection must clear previous run inventory");
        return "PASS selection clears stale run snapshot";
    }
    public static string HullPreservesParts()
    {
        var root=new GameObject("EditorHullMutationProbe");root.SetActive(false);
        try
        {
            root.AddComponent<BoxCollider>();var child=new GameObject("ProbePart");child.transform.SetParent(root.transform,false);
            child.AddComponent<AssemblyPart>();var collider=child.AddComponent<BoxCollider>();child.layer=7;collider.enabled=true;
            CarBodyCollision.EnsureCreated(root,false);
            Need(child.layer==7&&collider.enabled,"editor hull must preserve part layers/colliders");
            return "PASS editor hull preserves authored physics";
        }finally{UnityEngine.Object.DestroyImmediate(root);}
    }
    public static string Surface()
    {
        Need(new AuthoringCurve().enabled,"new paths must be enabled by default");
        Need(typeof(ProcessAuthoringPreview).GetMethod("IdleAtStation")!=null,"explicit station idle required");
        Need(typeof(WorkerHandIK).GetMethod("SetPose")!=null,"shared wrist/elbow pose required");
        return "PASS default paths + Idle + shared pose surface";
    }
}
#endif
