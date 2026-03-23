using UnityEngine;
using System.Collections.Generic; // List 사용을 위해 필요 (초기화 시에만 사용)

public class RoboticArmIK : MonoBehaviour
{
    [System.Serializable]
    public class MechanicalJoint
    {
        public string jointName;
        public Transform bone;
        public bool isActive = true;
        public Vector3 allowedAxis;

        [Header("IK 제어")]
        [Range(0f, 1f)] public float flexibility = 1f;
        [Tooltip("관절의 초당 회전 속도 (업그레이드 체감을 위해 개별 설정)")]
        public float rotationSpeed = 180f;

        [Header("각도 제한")]
        public bool useLimits = false;
        public float minAngle = -45f;
        public float maxAngle = 45f;

        [Header("수평 유지 (Palletizing)")]
        public bool keepLevel = false;

        [HideInInspector] public Quaternion initialRot;
        [HideInInspector] public float currentAngle = 0f;
    }

    [Header("IK 설정 및 타겟")]
    public Transform target;
    public Transform endEffector;
    public bool isWorking = false;

    [Header("뷰(View) 관리 - 타이쿤 최적화")]
    [Tooltip("로봇팔 끝에 미리 달아둔 부품 모델링 (껐다 켜기용)")]
    public GameObject attachedPartView;

    [Header("관절 리스트")]
    public MechanicalJoint[] joints;

    [Header("연산 디테일 및 타이쿤 스탯")]
    [Range(1, 10)] public int iterations = 3;
    [Range(0.1f, 1f)] public float damping = 0.5f;

    [Tooltip("전체 로봇팔 속도 배율 (타이쿤 업그레이드 시 이 값을 올려주세요)")]
    public float globalSpeedMultiplier = 1.0f;

    [ContextMenu("★ [PM 추천] 계층 기반 자동 바인딩 (수평 유지 포함)")]
    public void AutoSetupJoints()
    {
        Vector3[] mechanicalAxes = {
            Vector3.up,      // 1번: 베이스 좌우 회전
            Vector3.right,   // 2번: 하단 관절 상하
            Vector3.right,   // 3번: 중간 관절 상하
            Vector3.right,   // 4번: 상단 관절 상하
            Vector3.up,      // 5번: 손목 회전
            Vector3.right,   // 6번: 손목 꺾임 (수평 유지)
            Vector3.forward  // 7번: 툴 회전
        };

        bool[] defaultActiveStates = { true, true, true, true, false, true, false };
        bool[] defaultKeepLevel = { false, false, false, false, false, true, false };

        // GC가 발생하지만 에디터 세팅용(ContextMenu)이므로 런타임 성능과 무관합니다.
        List<MechanicalJoint> tempList = new List<MechanicalJoint>();
        Transform currentBone = transform;

        for (int i = 0; i < mechanicalAxes.Length; i++)
        {
            if (currentBone.childCount == 0) break;
            currentBone = currentBone.GetChild(0); // 자식 노드를 타고 내려감

            tempList.Add(new MechanicalJoint
            {
                jointName = $"{i + 2}번 관절 ({currentBone.name})",
                bone = currentBone,
                allowedAxis = mechanicalAxes[i],
                isActive = defaultActiveStates[i],
                keepLevel = defaultKeepLevel[i],
                flexibility = 1f,
                rotationSpeed = 180f, // 기본 관절 속도
                useLimits = false
            });
        }

        joints = tempList.ToArray();

        // EndEffector 자동 찾기
        Transform[] allChildren = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < allChildren.Length; i++)
        {
            if (allChildren[i].name == "TargetPoint" || allChildren[i].name == "EndEffector")
            {
                endEffector = allChildren[i];
                break;
            }
        }

        Debug.Log($"[RoboticArmIK] 바인딩 완료! 총 {joints.Length}개의 관절이 세팅되었습니다.");
    }

    private void Start()
    {
        // 초기 각도 백업
        for (int i = 0; i < joints.Length; i++)
        {
            MechanicalJoint joint = joints[i];
            if (joint.bone != null)
            {
                joint.initialRot = joint.bone.localRotation;
                joint.currentAngle = 0f;
            }
        }

        // 시작 시 부품 뷰 끄기
        ToggleAttachedPart(false);
    }

    private void LateUpdate()
    {
        if (!isWorking || target == null || endEffector == null || joints.Length == 0) return;

        // 최적화: Update 내에서 매번 프로퍼티에 접근하지 않고 지역 변수로 캐싱
        float dt = Time.deltaTime;
        Vector3 targetPos = target.position;
        Vector3 effectorPos = endEffector.position;

        // 1. 역기구학(IK) 핵심 연산 루프 (CCD 로직)
        for (int i = 0; i < iterations; i++)
        {
            for (int j = joints.Length - 1; j >= 0; j--)
            {
                MechanicalJoint joint = joints[j];
                if (!joint.isActive || joint.bone == null) continue;

                Transform bone = joint.bone;
                Vector3 toEffector = effectorPos - bone.position;
                Vector3 toTarget = targetPos - bone.position;

                // 로컬 스페이스로 변환
                Vector3 localToEffector = bone.InverseTransformDirection(toEffector);
                Vector3 localToTarget = bone.InverseTransformDirection(toTarget);

                // 허용된 축(Axis) 평면에 투영
                Vector3 projEffector = Vector3.ProjectOnPlane(localToEffector, joint.allowedAxis).normalized;
                Vector3 projTarget = Vector3.ProjectOnPlane(localToTarget, joint.allowedAxis).normalized;

                if (projEffector.sqrMagnitude > 0.001f && projTarget.sqrMagnitude > 0.001f)
                {
                    // 목표 각도 계산
                    float angle = Vector3.SignedAngle(projEffector, projTarget, joint.allowedAxis);
                    float deltaAngle = angle * damping * joint.flexibility;

                    // [기획 핵심] 타이쿤 지연 시스템: 관절 속도 * 전체 속도 배율
                    // 이 값이 작으면 지나가는 차량(target)을 IK가 쫓아가지 못하고 엉뚱한 곳을 허우적거림
                    float actualSpeed = joint.rotationSpeed * globalSpeedMultiplier;
                    float maxAngleThisIteration = (actualSpeed * dt) / iterations;

                    deltaAngle = Mathf.Clamp(deltaAngle, -maxAngleThisIteration, maxAngleThisIteration);
                    joint.currentAngle += deltaAngle;

                    if (joint.useLimits)
                    {
                        joint.currentAngle = Mathf.Clamp(joint.currentAngle, joint.minAngle, joint.maxAngle);
                    }

                    // 회전 적용
                    bone.localRotation = joint.initialRot * Quaternion.AngleAxis(joint.currentAngle, joint.allowedAxis);

                    // 뼈대가 움직였으므로 Effector 위치를 갱신해줘야 다음 관절 계산이 정확해짐
                    effectorPos = endEffector.position;
                }
            }
        }

        // 2. 수평 유지 (Palletizing) 강제 보정 로직
        for (int i = 0; i < joints.Length; i++)
        {
            MechanicalJoint joint = joints[i];
            if (joint.keepLevel && joint.bone != null)
            {
                // 월드 Up 벡터와 관절의 Up 벡터를 맞추어 평행하게 만듦
                Quaternion alignRotation = Quaternion.FromToRotation(joint.bone.up, Vector3.up);
                joint.bone.rotation = alignRotation * joint.bone.rotation;
            }
        }
    }

    /// <summary>
    /// StationController에서 호출하여 로봇팔 끝 위치를 확인
    /// </summary>
    public Vector3 GetEndEffectorPosition()
    {
        return endEffector != null ? endEffector.position : transform.position;
    }

    /// <summary>
    /// 로봇팔 끝단의 부품 렌더링을 켜거나 끕니다. (Zero GC 최적화)
    /// </summary>
    public void ToggleAttachedPart(bool isVisible)
    {
        if (attachedPartView != null && attachedPartView.activeSelf != isVisible)
        {
            attachedPartView.SetActive(isVisible);
        }
    }
}