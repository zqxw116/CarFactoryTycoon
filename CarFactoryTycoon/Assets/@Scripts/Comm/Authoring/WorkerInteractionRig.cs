using UnityEngine;
using UnityEngine.Animations.Rigging;

/// <summary>Prebuilt package-based hand solver; runtime only switches weights and targets.</summary>
[DefaultExecutionOrder(150)]
[RequireComponent(typeof(Animator),typeof(WorkerHandIK))]
public sealed class WorkerInteractionRig : MonoBehaviour
{
    [SerializeField] Animator animator;
    [SerializeField] WorkerHandIK hands;
    [SerializeField] RigBuilder builder;
    [SerializeField] Rig rig;
    [SerializeField] Transform leftTarget,rightTarget,leftHint,rightHint;
    float originalPositionWeight=1f,originalRotationWeight=.7f,weight;
    bool ready;
    public float WristError { get; private set; }
    public float ActiveWeight => rig!=null?rig.weight:0;
    /// <summary>Authoring initialization, before the Animator creates its runtime transform bindings.</summary>
    public void Prepare()
    {
        if(Application.isPlaying)throw new System.InvalidOperationException("Prepare worker rigs in edit mode.");
        animator=GetComponent<Animator>();hands=GetComponent<WorkerHandIK>();
        originalPositionWeight=hands.positionWeight;originalRotationWeight=hands.rotationWeight;
        builder=GetComponent<RigBuilder>();if(builder==null)builder=gameObject.AddComponent<RigBuilder>();
        if(rig==null)
        {
            var root=new GameObject("ProcessInteractionRig");root.transform.SetParent(transform,false);rig=root.AddComponent<Rig>();
            BuildArm(true,out leftTarget,out leftHint);BuildArm(false,out rightTarget,out rightHint);
            builder.layers.Add(new RigLayer(rig));
        }
        rig.weight=0;enabled=false;builder.enabled=false;
    }
    void BuildArm(bool left,out Transform target,out Transform hint)
    {
        var root=new GameObject(left?"ProcessLeftArm":"ProcessRightArm");root.transform.SetParent(rig.transform,false);
        target=new GameObject(left?"ProcessLeftWrist":"ProcessRightWrist").transform;target.SetParent(transform,false);
        hint=new GameObject(left?"ProcessLeftElbow":"ProcessRightElbow").transform;hint.SetParent(transform,false);
        var constraint=root.AddComponent<TwoBoneIKConstraint>();var data=constraint.data;
        data.root=animator.GetBoneTransform(left?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm);
        data.mid=animator.GetBoneTransform(left?HumanBodyBones.LeftLowerArm:HumanBodyBones.RightLowerArm);
        data.tip=animator.GetBoneTransform(left?HumanBodyBones.LeftHand:HumanBodyBones.RightHand);
        target.SetPositionAndRotation(data.tip.position,data.tip.rotation);
        data.target=target;data.hint=hint;data.targetPositionWeight=1f;data.targetRotationWeight=.7f;data.hintWeight=.8f;constraint.data=data;
    }
    void Awake()
    {
        if(hands==null)hands=GetComponent<WorkerHandIK>();
        originalPositionWeight=hands.positionWeight;originalRotationWeight=hands.rotationWeight;
        ready=animator!=null&&builder!=null&&rig!=null;
    }
    void OnEnable()
    {
        if(!Application.isPlaying)return;
        if(!ready){Debug.LogError("Worker rig is not prepared. Use Prepare Worker Rigs in edit mode.",this);enabled=false;return;}
        hands.positionWeight=0;hands.rotationWeight=0;builder.enabled=true;
    }
    void Update()
    {
        if(!ready)return;
        bool target=hands.TryGetWristTargets(out var left,out var right,out var leftRotation,out var rightRotation);
        weight=Mathf.MoveTowards(weight,target?1f:0f,Time.deltaTime/Mathf.Max(.01f,hands.blendTime));rig.weight=weight*weight*(3f-2f*weight);
        if(!target)return;
        leftTarget.SetPositionAndRotation(left,leftRotation);rightTarget.SetPositionAndRotation(right,rightRotation);
        hands.GetElbowTargets(out var lHint,out var rHint);
        leftHint.position=lHint;rightHint.position=rHint;
    }
    void LateUpdate()
    {
        if(!ready||rig.weight<.9f){WristError=0;return;}
        WristError=Mathf.Max(Vector3.Distance(animator.GetBoneTransform(HumanBodyBones.LeftHand).position,leftTarget.position),Vector3.Distance(animator.GetBoneTransform(HumanBodyBones.RightHand).position,rightTarget.position));
    }
    void OnDisable()
    {
        if(hands!=null){hands.positionWeight=originalPositionWeight;hands.rotationWeight=originalRotationWeight;}
        if(rig!=null)rig.weight=0;if(builder!=null)builder.enabled=false;weight=0;
    }
}
