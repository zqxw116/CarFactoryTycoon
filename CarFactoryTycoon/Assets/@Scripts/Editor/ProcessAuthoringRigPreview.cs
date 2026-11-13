#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

/// <summary>Disposable model-only pose twin. No Worker/stock/traffic logic is executed.</summary>
public sealed class ProcessAuthoringRigPreview : IDisposable
{
    public GameObject root;
    public Animator avatar;
    public Transform part;
    public Worker source;
    public PartStack stock;
    public WorkerHandIK hands;
    public WorkerReachEnvelope reach;
    Transform[] bones;Vector3[] positions;Quaternion[] rotations;
    Vector3 displayScale;
    Transform[] sourceBones;
    AnimationClip bendClip, kneelClip;
    Vector3[] bendPositions;
    Quaternion[] bendRotations;
    AssemblyPart replayPart;
    Vector3 lastPartLocalPosition;
    Quaternion lastPartLocalRotation;
    public Vector3 StageOffset {get;private set;}
    Vector3 lastPosition;
    Quaternion lastRotation;
    bool rendered;
    public bool IsReaching {get;private set;}
    public bool Replaying {get;private set;}
    public bool TryCapturePlacement(Pose frame,out AuthoringPose pose)
    {
        pose=default;
        if(!rendered||Replaying||root==null)return false;
        if(Vector3.Distance(root.transform.position,lastPosition)<.0001f&&Quaternion.Angle(root.transform.rotation,lastRotation)<.01f)return false;
        pose=AuthoringPose.Capture(root.transform,frame);return true;
    }
    public bool TryCapturePart(out Pose pose)
    {
        pose=default;if(!rendered||Replaying||part==null)return false;
        if(Vector3.Distance(part.localPosition,lastPartLocalPosition)<.0001f&&Quaternion.Angle(part.localRotation,lastPartLocalRotation)<.01f)return false;
        pose=new Pose(part.position-StageOffset,part.rotation);return true;
    }
    public void Dispose(){if(root!=null)Object.DestroyImmediate(root);root=null;rendered=false;replayPart=null;}
    public void Ensure(Worker worker,PartStack stack)
    {
        if(root!=null&&source!=null&&source==worker&&stock==stack){source=worker;stock=stack;return;}
        Dispose();source=worker;stock=stack;if(worker?.handIK==null||stack?.ReadyDisplay==null)return;
        root=new GameObject("리깅 작업자 · 편집 전용");root.hideFlags=HideFlags.DontSave;root.tag="EditorOnly";root.SetActive(false);
        var actor=Object.Instantiate(worker.handIK.gameObject,root.transform,false);actor.name="리깅 아바타";
        avatar=actor.GetComponent<Animator>();hands=actor.GetComponent<WorkerHandIK>();
        Sanitize(actor);
        avatar.enabled=false;
        var clip=avatar.runtimeAnimatorController!=null?avatar.runtimeAnimatorController.animationClips.FirstOrDefault(x=>x.name.IndexOf("idle",StringComparison.OrdinalIgnoreCase)>=0):null;
        if(clip!=null)clip.SampleAnimation(actor,0);
        var clips = avatar.runtimeAnimatorController?.animationClips;
        bendClip = clips?.FirstOrDefault(c => c.name == "Worker_Bend_Reach");
        kneelClip = clips?.FirstOrDefault(c => c.name == "Worker_Kneel_Work");
        avatar.runtimeAnimatorController=null;
        part=Object.Instantiate(stack.ReadyDisplay,root.transform,false).transform;part.name="리깅용 부품";Sanitize(part.gameObject);displayScale=stack.ReadyDisplay.transform.lossyScale;
        root.SetActive(true);
        bones=avatar.GetComponentsInChildren<Transform>(true);positions=bones.Select(x=>x.localPosition).ToArray();rotations=bones.Select(x=>x.localRotation).ToArray();
        bendPositions = new Vector3[bones.Length]; bendRotations = new Quaternion[bones.Length];
        sourceBones=bones.Select(x=>{var path=AnimationUtility.CalculateTransformPath(x,avatar.transform);return string.IsNullOrEmpty(path)?worker.handIK.transform:worker.handIK.transform.Find(path);}).ToArray();
        reach=root.AddComponent<WorkerReachEnvelope>();reach.Measure(avatar);
        hands.CacheSkeleton();
    }
    static void Sanitize(GameObject model)
    {
        foreach(var b in model.GetComponentsInChildren<Behaviour>(true))b.enabled=false;
        foreach(var builder in model.GetComponentsInChildren<UnityEngine.Animations.Rigging.RigBuilder>(true)){builder.Clear();Object.DestroyImmediate(builder);}
        foreach(var script in model.GetComponentsInChildren<MonoBehaviour>(true))if(!(script is WorkerHandIK))Object.DestroyImmediate(script);
        foreach(var c in model.GetComponentsInChildren<Collider>(true))c.enabled=false;
        foreach(var r in model.GetComponentsInChildren<Rigidbody>(true)){r.isKinematic=true;r.detectCollisions=false;}
        foreach(var t in model.GetComponentsInChildren<Transform>(true)){t.gameObject.layer=2;t.gameObject.hideFlags=HideFlags.DontSave;t.gameObject.tag="EditorOnly";}
        foreach(var particles in model.GetComponentsInChildren<ParticleSystem>(true)){var main=particles.main;main.playOnAwake=false;particles.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);}
        foreach(var skin in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))skin.updateWhenOffscreen=true;
    }
    public void Tick(Worker worker,PartStack stack,ProcessGripSettings data,Vector3 offset,Vector3 stageHome,
        ProcessActorLayout placement=null,Pose frame=default,bool replay=false,bool forceReach=false,AssemblyPart attemptPart=null)
    {
        Ensure(worker,stack);if(root==null||data==null||!data.configured){if(root!=null)root.SetActive(false);return;}
        root.SetActive(true);StageOffset=offset;
        for(int i=0;i<bones.Length;i++){bones[i].localPosition=positions[i];bones[i].localRotation=rotations[i];}
        var a=ProcessPartGrip.Contact(stack.ReadyPosition,stack.ReadyRotation,displayScale,data.left);
        var b=ProcessPartGrip.Contact(stack.ReadyPosition,stack.ReadyRotation,displayScale,data.right);
        var across=b-a;var finger=Vector3.ProjectOnPlane(stack.ReadyRotation*Vector3.Scale(displayScale,data.finger),across);
        if(finger.sqrMagnitude<.00001f)finger=worker.transform.forward;finger.Normalize();
        a-=finger*hands.palmOffset;b-=finger*hands.palmOffset;
        var center=(a+b)*.5f;var away=Vector3.ProjectOnPlane(stageHome-center,Vector3.up);
        if(away.sqrMagnitude<.01f)away=-worker.transform.forward;
        if (!replay) SamplePosture(worker, center.y - stageHome.y, 0f);
        reach.Measure(avatar);
        var neutral=reach.BalancedPoseFor(a,b,stageHome.y,data.reachFraction);
        var defaultRotation=neutral.rotation;
        var defaultPosition=neutral.position+offset;
        var manual=placement!=null&&placement.hasRigPreviewPose;
        var position=manual?placement.rigPreviewPose.WorldPosition(frame):defaultPosition;
        var rotation=manual?placement.rigPreviewPose.WorldRotation(frame):defaultRotation;
        Replaying=replay;
        if(replay)
        {
            var laneOffset=offset+(manual?position-defaultPosition:Vector3.zero);
            root.transform.SetPositionAndRotation(worker.transform.position+laneOffset,worker.transform.rotation);
            for(int i=0;i<bones.Length;i++)if(sourceBones[i]!=null)
            {bones[i].localPosition=sourceBones[i].localPosition;bones[i].localRotation=sourceBones[i].localRotation;}
            if(attemptPart!=null)replayPart=attemptPart;
            var held=attemptPart!=null?attemptPart:worker.PrototypePart!=null?worker.PrototypePart:replayPart;
            if(held!=null&&held.gameObject.activeInHierarchy)
            {part.SetPositionAndRotation(held.transform.position+laneOffset,held.transform.rotation);part.localScale=held.transform.lossyScale;}
            else {part.SetPositionAndRotation(stack.ReadyPosition+laneOffset,stack.ReadyRotation);part.localScale=displayScale;}
            hands.CacheSkeleton();hands.SetPose(data);
            IsReaching=worker.handIK.IsHolding;
            if(IsReaching)hands.Grip(part,data.left,data.right,data.finger);else hands.Release();
        }
        else
        {
            replayPart=null;
            root.transform.SetPositionAndRotation(position,rotation);
            part.SetPositionAndRotation(stack.ReadyPosition+offset,stack.ReadyRotation);part.localScale=displayScale;
            float approach = Vector3.ProjectOnPlane(position - defaultPosition, Vector3.up).magnitude;
            SamplePosture(worker, center.y + offset.y - position.y, approach);
            hands.CacheSkeleton();hands.SetPose(data);
            reach.Measure(avatar);
            var leftTarget=a+offset;var rightTarget=b+offset;
            IsReaching=forceReach||reach.Contains(leftTarget,rightTarget);
            if(IsReaching)
            {
                hands.Grip(part,data.left,data.right,data.finger);
                hands.TryGetWristTargets(out var left,out var right,out var lr,out var rr);
                hands.GetElbowTargets(out var lh,out var rh);
                SolveArm(avatar,true,left,lh,lr);SolveArm(avatar,false,right,rh,rr);
            }
            else hands.Release();
        }
        reach.Measure(avatar);
        lastPosition=root.transform.position;lastRotation=root.transform.rotation;lastPartLocalPosition=part.localPosition;lastPartLocalRotation=part.localRotation;rendered=true;
    }
    void SamplePosture(Worker worker, float height, float distance)
    {
        worker.EvaluateWorkPosture(height, distance, out float weight, out float knee);
        for (int i = 0; i < bones.Length; i++)
            bones[i].SetLocalPositionAndRotation(positions[i], rotations[i]);
        if (weight < .001f || bendClip == null || kneelClip == null) return;
        bendClip.SampleAnimation(avatar.gameObject, bendClip.length * .5f);
        for (int i = 0; i < bones.Length; i++)
        {
            bendPositions[i] = bones[i].localPosition;
            bendRotations[i] = bones[i].localRotation;
        }
        kneelClip.SampleAnimation(avatar.gameObject, kneelClip.length * .5f);
        for (int i = 0; i < bones.Length; i++)
        {
            var pos = Vector3.Lerp(bendPositions[i], bones[i].localPosition, knee);
            var rot = Quaternion.Slerp(bendRotations[i], bones[i].localRotation, knee);
            bones[i].SetLocalPositionAndRotation(Vector3.Lerp(positions[i], pos, weight),
                Quaternion.Slerp(rotations[i], rot, weight));
        }
    }

    public static void SolveArm(Animator animator,bool left,Vector3 goal,Vector3 hint,Quaternion rotation)
    {
        var upper=animator.GetBoneTransform(left?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm);
        var lower=animator.GetBoneTransform(left?HumanBodyBones.LeftLowerArm:HumanBodyBones.RightLowerArm);
        var hand=animator.GetBoneTransform(left?HumanBodyBones.LeftHand:HumanBodyBones.RightHand);
        var start=upper.position;float a=Vector3.Distance(start,lower.position),b=Vector3.Distance(lower.position,hand.position);
        var direction=goal-start;float distance=Mathf.Clamp(direction.magnitude,Mathf.Abs(a-b)+.0001f,a+b-.0001f);
        if(direction.sqrMagnitude<.00001f)return;direction.Normalize();
        var bend=Vector3.ProjectOnPlane(hint-start,direction);if(bend.sqrMagnitude<.00001f)bend=Vector3.ProjectOnPlane(animator.transform.up,direction);bend.Normalize();
        float along=(a*a+distance*distance-b*b)/(2*distance);var elbow=start+direction*along+bend*Mathf.Sqrt(Mathf.Max(0,a*a-along*along));
        upper.rotation=Quaternion.FromToRotation(lower.position-start,elbow-start)*upper.rotation;
        lower.rotation=Quaternion.FromToRotation(hand.position-lower.position,start+direction*distance-lower.position)*lower.rotation;
        hand.rotation=rotation;
    }
}
#endif
