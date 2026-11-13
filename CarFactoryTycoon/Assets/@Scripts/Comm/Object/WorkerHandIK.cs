using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 사람 작업자의 두 손을 부품 잡이 지점에 붙이는 Humanoid IK (Unity 내장 OnAnimatorIK).
///
/// 왜 RoboticArmIK(커스텀 CCD)를 쓰지 않는가:
///   RoboticArmIK는 본 회전을 직접 덮어쓰는 방식이라 Animator가 매 프레임 쓰는 휴머노이드 본과 충돌한다.
///   내장 Humanoid IK는 애니메이션 평가 직후·본 쓰기 직전에 목표만 넘기면 Unity가 팔 2본 IK를 풀어 주므로
///   걷기 애니(다리)는 그대로 두고 팔만 목표에 붙일 수 있다.
///
/// 구조:
///   - OnAnimatorIK는 <b>Animator와 같은 GameObject</b>의 컴포넌트에서만 호출된다 → 이 컴포넌트는 Animator 오브젝트에 둔다.
///   - Worker가 Grip(부품, 잡이 지점 2개)/Release()로 목표만 넘기고, 가중치 블렌드·손 회전·팔꿈치 힌트·시선은 여기서 계산한다.
///   - 잡이 지점은 부품 로컬 좌표로 들고 있다가 매 IK 패스마다 월드로 환산한다 → 부품이 움직이면 손이 따라간다
///     ("부품이 손을 따라가는" 기존 HandPos 방식의 반대 — 걷기 애니로 팔이 흔들려도 부품이 흔들리지 않는다).
///   - Animator 컨트롤러의 Base Layer에 IK Pass가 켜져 있어야 호출된다(WorkerAnimator.controller).
///
/// 손 회전: Humanoid IK 목표 회전은 T포즈 기준(캐릭터 정면 +Z, 손가락 = 팔 바깥, 손바닥 = 아래, 엄지 = 앞)이다.
///   여기서는 "손가락은 부품 바깥(몸에서 먼 쪽), 손바닥은 서로 마주 보게, 엄지는 위"가 되도록 축을 맞춘다.
///   모델마다 손 본 축이 미묘하게 달라 어색하면 left/rightHandEuler로 보정한다(rotationWeight를 1보다 낮게 둔 이유).
/// </summary>
[RequireComponent(typeof(Animator))]
public class WorkerHandIK : MonoBehaviour
{
    [Header("가중치")]
    [Tooltip("잡기/놓기 가중치 블렌드 시간(초). 0이면 즉시 전환.")]
    public float blendTime = 0.25f;

    [Tooltip("손 위치 IK 최대 가중치.")]
    [Range(0f, 1f)] public float positionWeight = 1f;

    [Tooltip("손 회전 IK 최대 가중치. 1이면 손목이 계산 회전에 완전히 고정된다 — 모델 손 본 축 차이로" +
        " 손목이 꺾여 보일 수 있어 기본은 조금 낮춘다.")]
    [Range(0f, 1f)] public float rotationWeight = 0.7f;

    [Header("손 자세")]
    [Tooltip("손목(IK 목표 = 손 본)을 잡이 지점에서 몸 쪽으로 물리는 거리(m). 손 본은 손바닥이 아니라" +
        " 손목에 있으므로, 이 값이 없으면 손가락이 부품 속으로 파묻힌다.")]
    public float palmOffset = 0.07f;

    [Tooltip("왼손 회전 미세 보정(도, 계산된 손 회전 기준).")]
    public Vector3 leftHandEuler = Vector3.zero;

    [Tooltip("오른손 회전 미세 보정(도, 계산된 손 회전 기준).")]
    public Vector3 rightHandEuler = Vector3.zero;

    [Header("팔꿈치 힌트 (몸 기준)")]
    [Tooltip("팔꿈치 힌트를 어깨에서 몸 바깥쪽으로 미는 거리(m).")]
    public float hintOutward = 0.3f;

    [Tooltip("팔꿈치 힌트를 어깨에서 아래로 내리는 거리(m).")]
    public float hintDown = 0.3f;

    [Tooltip("팔꿈치 힌트를 어깨에서 뒤로 빼는 거리(m). 양수 = 등 쪽.")]
    public float hintBack = 0.05f;

    [Range(0f, 1f)] public float hintWeight = 0.8f;

    [Header("시선")]
    [Tooltip("잡고 있는 부품(잡이 지점 중앙)을 바라보는 가중치. 0이면 시선 IK를 쓰지 않는다.")]
    [Range(0f, 1f)] public float lookAtWeight = 0.7f;
    [Range(0f, 1f)] public float lookAtBodyWeight = 0.15f;
    [Range(0f, 1f)] public float lookAtHeadWeight = 0.8f;

    [Header("Procedural support")]
    public bool bodySupport;
    [Range(0f,.25f)] public float maximumBodyShift=.12f;
    [Range(0f,1f)] public float plantedFootWeight=.7f;
    public float footSoleOffset=.08f;
    bool feetOwned;
    private Vector3 supportPreviousPosition;
    private Animator animator;

    // 잡고 있는 대상과 잡이 지점(대상 로컬). Release 후에도 블렌드 아웃 동안은 유지한다.
    private Transform gripTarget;
    private Vector3 leftLocal, rightLocal, fingerLocal = Vector3.forward;
    private bool holding;
    ProcessGripSettings poseSettings;
    public void SetPose(ProcessGripSettings settings){poseSettings=settings;}
    public Vector3 LeftPoseEuler => leftHandEuler+(poseSettings!=null?poseSettings.leftWristEuler:Vector3.zero);
    public Vector3 RightPoseEuler => rightHandEuler+(poseSettings!=null?poseSettings.rightWristEuler:Vector3.zero);
    private float weight;
    Quaternion leftPalmBasis,rightPalmBasis;
    bool palmBasisReady;

    // 어깨(상완 본) 위치를 루트 로컬로 캐시 — 팔꿈치 힌트 기준. OnAnimatorIK 시점의 본 Transform은
    // 이번 프레임 IK 적용 전 값이라 흔들리므로, 시작 시 1회 잰 값을 몸 기준 고정점으로 쓴다.
    private Vector3 leftShoulderLocal = new Vector3(-0.2f, 1.4f, 0f);
    private Vector3 rightShoulderLocal = new Vector3(0.2f, 1.4f, 0f);

    /// <summary>현재 무언가를 잡고 있는지(블렌드 아웃 중이면 false).</summary>
    public bool IsHolding => holding;

    /// <summary>현재 적용 중인 IK 가중치(0~1).</summary>
    public float CurrentWeight => weight;

    private void Awake()
    {
        animator = GetComponent<Animator>();
        supportPreviousPosition=transform.position;
        if (animator == null || !animator.isHuman) return;

        Transform l = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform r = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        if (l != null) leftShoulderLocal = transform.InverseTransformPoint(l.position);
        if (r != null) rightShoulderLocal = transform.InverseTransformPoint(r.position);
        CachePalmBasis();
    }

    /// <summary>
    /// 대상을 두 손으로 잡는다. 잡이 지점·손가락 방향은 <paramref name="target"/> 로컬 좌표.
    /// 가중치는 blendTime 동안 0→1로 올라간다.
    /// </summary>
    public void Grip(Transform target, Vector3 leftGripLocal, Vector3 rightGripLocal, Vector3 fingerDirLocal)
    {
        gripTarget = target;
        leftLocal = leftGripLocal;
        rightLocal = rightGripLocal;
        fingerLocal = fingerDirLocal.sqrMagnitude > 1e-6f ? fingerDirLocal.normalized : Vector3.forward;
        holding = true;
    }

    /// <summary>손을 놓는다. 가중치가 blendTime 동안 0으로 내려가는 사이에는 마지막 대상을 계속 따라간다.</summary>
    public void Release()
    {
        holding = false;
    }

    public bool TryGetWristTargets(out Vector3 left,out Vector3 right,out Quaternion leftRotation,out Quaternion rightRotation)
    {
        left=right=Vector3.zero;leftRotation=rightRotation=Quaternion.identity;
        if(gripTarget==null || !gripTarget.gameObject.activeInHierarchy || (!holding&&weight<=0))return false;
        var a=gripTarget.TransformPoint(leftLocal);var b=gripTarget.TransformPoint(rightLocal);
        var across=b-a;if(across.sqrMagnitude<.000001f)across=transform.right;across.Normalize();
        var finger=Vector3.ProjectOnPlane(gripTarget.TransformVector(fingerLocal),across);
        if(finger.sqrMagnitude<.000001f)finger=transform.forward;finger.Normalize();
        var thumb=Vector3.Cross(finger,across);
        rightRotation=WristRotation(false,finger,across);
        leftRotation=WristRotation(true,finger,across);
        left=a-finger*palmOffset;right=b-finger*palmOffset;return true;
    }
    void ApplyBodySupport(float blend,Vector3 left,Vector3 right)
    {
        var movement=Vector3.ProjectOnPlane(transform.position-supportPreviousPosition,Vector3.up);
        supportPreviousPosition=transform.position;
        if(!bodySupport){if(feetOwned){SupportFoot(AvatarIKGoal.LeftFoot,HumanBodyBones.LeftFoot,0);SupportFoot(AvatarIKGoal.RightFoot,HumanBodyBones.RightFoot,0);feetOwned=false;}return;}
        feetOwned=true;
        var shoulder=transform.TransformPoint((leftShoulderLocal+rightShoulderLocal)*.5f);
        var toward=(left+right)*.5f-shoulder;
        var horizontal=Vector3.ProjectOnPlane(toward,Vector3.up);
        if(Vector3.Dot(horizontal,transform.forward)<0)horizontal=Vector3.zero;
        var shift=Vector3.ClampMagnitude(horizontal*.14f,maximumBodyShift)*blend;
        shift.y=Mathf.Clamp(toward.y*.1f,-maximumBodyShift,maximumBodyShift)*blend;
        animator.bodyPosition+=shift;
        float pin=movement.magnitude/Mathf.Max(.001f,Time.deltaTime)<.15f?blend*plantedFootWeight:0f;
        SupportFoot(AvatarIKGoal.LeftFoot,HumanBodyBones.LeftFoot,pin);
        SupportFoot(AvatarIKGoal.RightFoot,HumanBodyBones.RightFoot,pin);
    }
    void SupportFoot(AvatarIKGoal goal,HumanBodyBones bone,float weight)
    {
        var foot=animator.GetBoneTransform(bone);
        if(foot==null)return;
        if(weight>0 && Physics.Raycast(foot.position+Vector3.up*.4f,Vector3.down,out var hit,1.4f,(1<<0)|(1<<11),QueryTriggerInteraction.Ignore))
        {
            animator.SetIKPositionWeight(goal,weight);animator.SetIKPosition(goal,hit.point+Vector3.up*footSoleOffset);
            animator.SetIKRotationWeight(goal,weight*.5f);animator.SetIKRotation(goal,Quaternion.FromToRotation(foot.up,hit.normal)*foot.rotation);
        }
        else{animator.SetIKPositionWeight(goal,0);animator.SetIKRotationWeight(goal,0);}
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (animator == null) return;
        int posture = animator.GetLayerIndex("Work posture");
        int gripLayer = posture >= 0 && animator.GetLayerWeight(posture) > 0f ? posture : 0;
        if (layerIndex != gripLayer) return;

        float step = blendTime > 0f ? Time.deltaTime / blendTime : 1f;
        weight = Mathf.MoveTowards(weight, holding ? 1f : 0f, step);

        // 대상이 사라졌거나(풀 반환·취소로 비활성) 가중치가 0이면 IK를 끈다.
        if (weight <= 0f || gripTarget == null || !gripTarget.gameObject.activeInHierarchy)
        {
            if (gripTarget != null && !gripTarget.gameObject.activeInHierarchy) weight = 0f;
            ClearIK();
            return;
        }

        // 블렌드는 smoothstep으로 — 선형보다 잡는 순간·놓는 순간이 덜 튄다.
        float w = weight * weight * (3f - 2f * weight);

        Vector3 leftGrip = gripTarget.TransformPoint(leftLocal);
        Vector3 rightGrip = gripTarget.TransformPoint(rightLocal);
        Vector3 finger = gripTarget.TransformVector(fingerLocal);

        // 잡이 축(왼→오른). 부품이 회전해도 이 축 기준으로 손바닥을 마주 보게 한다.
        Vector3 across = rightGrip - leftGrip;
        if (across.sqrMagnitude < 1e-6f) across = transform.right;
        across.Normalize();
        // 손가락 방향은 잡이 축에 직교화한다(부품 회전 중 두 축이 비스듬해져도 회전이 뒤틀리지 않게).
        finger = Vector3.ProjectOnPlane(finger, across);
        if (finger.sqrMagnitude < 1e-6f) finger = transform.forward;
        finger.Normalize();
        Vector3 thumbUp = Vector3.Cross(finger, across); // 오른손 기준 엄지 방향(위)

        // T포즈 기준 오른손: 손가락 +X, 손등 +Y, 엄지 +Z → 손가락=finger, 손등=across(바깥), 엄지=위.
        Quaternion rightRot = WristRotation(false,finger,across);
        // T포즈 기준 왼손: 손가락 -X, 손등 +Y, 엄지 +Z → -X=finger, 손등=-across(바깥), 엄지=위.
        Quaternion leftRot = WristRotation(true,finger,across);

        // 손 본은 손목이므로 잡이 지점에서 손가락 반대 방향으로 물린다.
        Vector3 leftWrist = leftGrip - finger * palmOffset;
        Vector3 rightWrist = rightGrip - finger * palmOffset;

        ApplyBodySupport(w,leftWrist,rightWrist);
        float pw = w * positionWeight, rw = w * (poseSettings!=null&&poseSettings.anatomicalWrist?1f:rotationWeight);
        animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, pw);
        animator.SetIKPositionWeight(AvatarIKGoal.RightHand, pw);
        animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, rw);
        animator.SetIKRotationWeight(AvatarIKGoal.RightHand, rw);
        animator.SetIKPosition(AvatarIKGoal.LeftHand, leftWrist);
        animator.SetIKPosition(AvatarIKGoal.RightHand, rightWrist);
        animator.SetIKRotation(AvatarIKGoal.LeftHand, leftRot);
        animator.SetIKRotation(AvatarIKGoal.RightHand, rightRot);

        // 팔꿈치: 어깨에서 몸 바깥·아래·약간 뒤 — 팔이 몸 안쪽으로 꺾여 가슴을 파고들지 않게.
        GetElbowTargets(out var leftHint,out var rightHint);
        animator.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, w * hintWeight);
        animator.SetIKHintPositionWeight(AvatarIKHint.RightElbow, w * hintWeight);
        animator.SetIKHintPosition(AvatarIKHint.LeftElbow, leftHint);
        animator.SetIKHintPosition(AvatarIKHint.RightElbow, rightHint);

        if (lookAtWeight > 0f)
        {
            animator.SetLookAtWeight(w * lookAtWeight, lookAtBodyWeight, lookAtHeadWeight, 0f, 0.5f);
            animator.SetLookAtPosition((leftGrip + rightGrip) * 0.5f);
        }
    }

    Quaternion PalmBasis(bool left)
    {
        var h=animator.GetBoneTransform(left?HumanBodyBones.LeftHand:HumanBodyBones.RightHand);
        var m=animator.GetBoneTransform(left?HumanBodyBones.LeftMiddleProximal:HumanBodyBones.RightMiddleProximal);
        var i=animator.GetBoneTransform(left?HumanBodyBones.LeftIndexProximal:HumanBodyBones.RightIndexProximal);
        var k=animator.GetBoneTransform(left?HumanBodyBones.LeftLittleProximal:HumanBodyBones.RightLittleProximal);
        if(h==null||m==null||i==null||k==null)return Quaternion.identity;
        var finger=h.InverseTransformVector(m.position-h.position);
        var thumb=Vector3.ProjectOnPlane(h.InverseTransformVector(i.position-k.position),finger);
        return finger.sqrMagnitude>.000001f&&thumb.sqrMagnitude>.000001f?Quaternion.LookRotation(finger,thumb):Quaternion.identity;
    }
    void CachePalmBasis()
    {
        if(animator==null||!animator.isHuman)return;
        leftPalmBasis=PalmBasis(true);rightPalmBasis=PalmBasis(false);palmBasisReady=true;
    }
    public Quaternion WristRotation(bool left,Vector3 finger,Vector3 across)
    {
        var correction=Quaternion.Euler(left?LeftPoseEuler:RightPoseEuler);
        if(poseSettings!=null&&poseSettings.anatomicalWrist)
        {
            if(!palmBasisReady){animator=GetComponent<Animator>();CachePalmBasis();}
            var thumb=Vector3.Cross(finger,across);
            if(thumb.sqrMagnitude<.00001f)thumb=transform.up;
            return Quaternion.LookRotation(finger,thumb)*Quaternion.Inverse(left?leftPalmBasis:rightPalmBasis)*correction;
        }
        return (left?Quaternion.LookRotation(Vector3.Cross(-finger,-across),-across):Quaternion.LookRotation(Vector3.Cross(finger,across),across))*correction;
    }
    public void GetElbowTargets(out Vector3 left,out Vector3 right)
    {
        var l=poseSettings!=null?poseSettings.leftElbow:new Vector3(-hintOutward,-hintDown,-hintBack);
        var r=poseSettings!=null?poseSettings.rightElbow:new Vector3(hintOutward,-hintDown,-hintBack);
        left=transform.TransformPoint(leftShoulderLocal+l);right=transform.TransformPoint(rightShoulderLocal+r);
    }
    public void CacheSkeleton()
    {
        animator=GetComponent<Animator>();if(animator==null||!animator.isHuman)return;
        leftShoulderLocal=transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.LeftUpperArm).position);
        rightShoulderLocal=transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.RightUpperArm).position);
        CachePalmBasis();
    }
    private void ClearIK()
    {
        supportPreviousPosition=transform.position;
        if(bodySupport||feetOwned){feetOwned=false;animator.SetIKPositionWeight(AvatarIKGoal.LeftFoot,0);animator.SetIKPositionWeight(AvatarIKGoal.RightFoot,0);animator.SetIKRotationWeight(AvatarIKGoal.LeftFoot,0);animator.SetIKRotationWeight(AvatarIKGoal.RightFoot,0);}
        animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0f);
        animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 0f);
        animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 0f);
        animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 0f);
        animator.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, 0f);
        animator.SetIKHintPositionWeight(AvatarIKHint.RightElbow, 0f);
        animator.SetLookAtWeight(0f);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 잡이 지점 자동 계산
    // ─────────────────────────────────────────────────────────────────────

    private static readonly List<MeshFilter> meshBuffer = new List<MeshFilter>();

    /// <summary>
    /// 부품의 잡이 지점 2개(부품 로컬)와 손가락 방향을 구한다. 배정 시 1회만 호출할 것(매 프레임 금지).
    ///
    /// 1) 부품에 PartGripOverride가 있으면 그 값을 그대로 쓴다.
    /// 2) 없으면 메시 바운즈(부품 로컬, 비활성 자식 포함)의 <b>긴 수평축</b>(x 또는 z) 양끝을 잡는다.
    ///    - 끝에서 <paramref name="endInset"/>만큼 안쪽, 단 중심에서 <paramref name="maxHalfSpan"/>을 넘지 않는다
    ///      (팔 길이로 닿을 수 있는 폭 — 1.6m 범퍼 양끝을 그대로 잡으면 팔이 일자로 펴져도 닿지 않는다).
    ///    - 높이는 바운즈 중앙, 깊이는 몸 쪽 면(긴 축이 x면 z 최소면, z면 x 최소면)에서 <paramref name="faceInset"/> 안쪽.
    ///    - 손가락 방향 = 깊이축 +방향(몸 쪽 면 → 바깥 면).
    /// 메시가 없으면 false.
    /// </summary>
    public static bool TryComputeGrip(Transform part, float maxHalfSpan, float endInset, float faceInset,
        out Vector3 gripA, out Vector3 gripB, out Vector3 fingerDir)
    {
        gripA = gripB = Vector3.zero;
        fingerDir = Vector3.forward;
        if (part == null) return false;

        if (part.TryGetComponent(out PartGripOverride ov) && ov.enabled)
        {
            gripA = ov.gripA;
            gripB = ov.gripB;
            fingerDir = ov.fingerDir.sqrMagnitude > 1e-6f ? ov.fingerDir.normalized : Vector3.forward;
            return true;
        }

        meshBuffer.Clear();
        part.GetComponentsInChildren(true, meshBuffer);
        Matrix4x4 toPart = part.worldToLocalMatrix;
        Bounds local = default;
        bool has = false;
        for (int i = 0; i < meshBuffer.Count; i++)
        {
            MeshFilter mf = meshBuffer[i];
            if (mf.sharedMesh == null) continue;
            Bounds b = mf.sharedMesh.bounds;
            Matrix4x4 m = toPart * mf.transform.localToWorldMatrix;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = b.center + new Vector3(
                    (c & 1) == 0 ? -b.extents.x : b.extents.x,
                    (c & 2) == 0 ? -b.extents.y : b.extents.y,
                    (c & 4) == 0 ? -b.extents.z : b.extents.z);
                Vector3 p = m.MultiplyPoint3x4(corner);
                if (!has) { local = new Bounds(p, Vector3.zero); has = true; }
                else local.Encapsulate(p);
            }
        }
        meshBuffer.Clear();
        if (!has) return false;

        Vector3 ctr = local.center, ext = local.extents;
        if (ext.x >= ext.z)
        {
            float half = Mathf.Clamp(ext.x - endInset, 0.05f, Mathf.Max(0.05f, maxHalfSpan));
            float face = local.min.z + Mathf.Min(faceInset, ext.z);
            gripA = new Vector3(ctr.x - half, ctr.y, face);
            gripB = new Vector3(ctr.x + half, ctr.y, face);
            fingerDir = Vector3.forward;
        }
        else
        {
            float half = Mathf.Clamp(ext.z - endInset, 0.05f, Mathf.Max(0.05f, maxHalfSpan));
            float face = local.min.x + Mathf.Min(faceInset, ext.x);
            gripA = new Vector3(face, ctr.y, ctr.z - half);
            gripB = new Vector3(face, ctr.y, ctr.z + half);
            fingerDir = Vector3.right;
        }
        return true;
    }
}
