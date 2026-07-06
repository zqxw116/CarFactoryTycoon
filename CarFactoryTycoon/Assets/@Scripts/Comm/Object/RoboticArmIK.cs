using UnityEditor;
using UnityEngine;

public class RoboticArmIK : MonoBehaviour
{
    [System.Serializable]
    public class BasicJoint
    {
        public string jointName;
        public Transform bone;
        public Vector3 rotationAxis;

        [Header("모터 스펙 (도/초)")]
        public float baseSpeed = 180f; // 1초에 회전할 수 있는 최대 각도

        [Header("각도 제한 (Limits)")]
        public bool useLimits = false;
        public float minAngle = -45f;
        public float maxAngle = 45f;

        [Header("수평 유지 (Palletizing)")]
        public bool keepLevel = false;

        [HideInInspector] public Quaternion zeroRotation;
        [HideInInspector] public float currentAngle = 0f;
        [HideInInspector] public Vector3 initialLocalSky;
    }

    [Header("타겟 타입")]
    public PartType targetPartType;

    [Header("핵심 타겟")]
    public Transform target;
    public Transform endEffector;

    [Header("순수 관절 리스트")]
    public BasicJoint[] joints;

    [Header("타이쿤 시스템 (속도 보정)")]
    [Tooltip("유저 업그레이드 수치 (1.0 = 기본속도, 2.0 = 2배 빠름)")]
    public float trackingMultiplier = 1.0f;

    [Header("연산 설정")]
    [Range(1, 10)]
    public int iterations = 5;

    public void SetTarget(Transform _transform) => target = _transform;
    public void SetEndEffect(Transform _transform) => endEffector = _transform;

    /// <summary>
    /// 팔의 뿌리 위치(basePos)와 최대 도달 반경(maxReach)을 계산해 반환한다.
    /// maxReach = 관절 간 거리 합 + 마지막 관절→엔드이펙터 거리.
    /// 관절/엔드이펙터가 미설정이면 false.
    /// </summary>
    public bool TryGetReach(out Vector3 basePos, out float maxReach)
    {
        basePos = transform.position;
        maxReach = 0f;
        if (joints == null || joints.Length == 0 || endEffector == null || joints[0].bone == null)
            return false;

        float total = 0f;
        for (int i = 0; i < joints.Length - 1; i++)
            if (joints[i].bone != null && joints[i + 1].bone != null)
                total += Vector3.Distance(joints[i].bone.position, joints[i + 1].bone.position);
        total += Vector3.Distance(joints[joints.Length - 1].bone.position, endEffector.position);

        basePos = joints[0].bone.position;
        maxReach = total;
        return true;
    }

    [ContextMenu("★ [PM 추천] 순수 IK + 완벽 각도 제한 세팅")]
    public void AutoSetupBasic()
    {
        string[] targetNames = { "Rig_Arm_2", "Rig_Arm_3", "Rig_Arm_4", "Rig_Arm_5" };
        Vector3[] mechanicalAxes = { Vector3.up, Vector3.forward, Vector3.forward, Vector3.forward };

        bool[] useLimits = { false, true, true, true };
        float[] minAngles = { -360f, 0f, -150f, 130f };
        float[] maxAngles = { 360f, 90f, -50f, 180f };

        joints = new BasicJoint[4];
        Transform[] allChildren = GetComponentsInChildren<Transform>(true);

        for (int i = 0; i < targetNames.Length; i++)
        {
            foreach (Transform child in allChildren)
            {
                if (child.name == targetNames[i])
                {
                    joints[i] = new BasicJoint
                    {
                        jointName = child.name,
                        bone = child,
                        rotationAxis = mechanicalAxes[i],
                        baseSpeed = 150f, // 기본 모터 속도 세팅
                        useLimits = useLimits[i],
                        minAngle = minAngles[i],
                        maxAngle = maxAngles[i],
                        keepLevel = (child.name == "Rig_Arm_5")
                    };
                    break;
                }
            }
        }

        foreach (Transform child in allChildren)
        {
            if (child.name == "Rig_End") SetEndEffect(child);
            if (child.name == "IK Target") SetTarget(child); ;
        }

        Debug.Log("[RoboticArmIK] 각도 제한 및 모터 속도(Base Speed) 세팅이 완료되었습니다!");
    }

    void Start()
    {
        foreach (var joint in joints)
        {
            if (joint.bone != null)
            {
                Vector3 euler = joint.bone.localEulerAngles;
                float startAngle = 0f;

                if (Mathf.Abs(joint.rotationAxis.x) > 0.5f) startAngle = euler.x;
                else if (Mathf.Abs(joint.rotationAxis.y) > 0.5f) startAngle = euler.y;
                else if (Mathf.Abs(joint.rotationAxis.z) > 0.5f) startAngle = euler.z;

                if (startAngle > 180f) startAngle -= 360f;

                joint.currentAngle = startAngle;
                joint.zeroRotation = joint.bone.localRotation * Quaternion.Inverse(Quaternion.AngleAxis(startAngle, joint.rotationAxis));

                joint.initialLocalSky = joint.bone.InverseTransformDirection(Vector3.up);
                joint.initialLocalSky = Vector3.ProjectOnPlane(joint.initialLocalSky, joint.rotationAxis).normalized;
            }
        }
    }

    void LateUpdate()
    {
        if (target == null || endEffector == null || joints.Length == 0) return;

        for (int i = 0; i < iterations; i++)
        {
            for (int j = joints.Length - 1; j >= 0; j--)
            {
                BasicJoint joint = joints[j];
                Transform bone = joint.bone;
                Vector3 axis = joint.rotationAxis;

                if (bone == null) continue;

                // 이번 반복에서 이 관절이 회전할 수 있는 최대 각도
                // baseSpeed(초당 회전 속도) * trackingMultiplier(속도 보정 계수) * Time.deltaTime(프레임 시간) * iterations(반복 횟수만큼 나눠서 한 번의 회전량을 줄임)
                float maxStep = (joint.baseSpeed * trackingMultiplier * Time.deltaTime) / iterations;

                // 수평 유지용 관절 처리
                if (joint.keepLevel)
                {
                    // 월드 기준 위쪽(Vector3.up)이 현재 bone의 로컬 기준으로 어느 방향인지 변환
                    Vector3 currentLocalSky = bone.InverseTransformDirection(Vector3.up);

                    // 회전축(axis) 방향 성분을 제거해서 axis에 수직인 평면 위의 방향만 남김
                    // 제어할 수 없는 방향을 제거하고, 이 관절이 실제로 회전해서 맞출 수 있는 방향만 남기는 코드
                    currentLocalSky = Vector3.ProjectOnPlane(currentLocalSky, axis).normalized;

                    // 투영 결과가 너무 작으면 방향 계산이 불안정하므로 제외
                    if (currentLocalSky.sqrMagnitude > 0.001f && joint.initialLocalSky.sqrMagnitude > 0.001f)
                    {
                        // 초기 수평 기준 방향과 현재 수평 방향의 차이 각도를 구함
                        // axis를 기준으로 시계/반시계 방향까지 포함해서 signed angle 계산
                        float angle = Vector3.SignedAngle(joint.initialLocalSky, currentLocalSky, axis);

                        // 한 프레임/한 반복에서 너무 많이 회전하지 않도록 속도 제한
                        angle = Mathf.Clamp(angle, -maxStep, maxStep);

                        // 제한된 보정 각도를 현재 관절 각도에 누적
                        joint.currentAngle += angle;

                        // 관절 각도 제한을 사용하는 경우 min/max 범위 안으로 제한
                        if (joint.useLimits)
                            joint.currentAngle = Mathf.Clamp(joint.currentAngle, joint.minAngle, joint.maxAngle);

                        // 초기 회전값을 기준으로 axis 방향 회전만 적용
                        bone.localRotation = joint.zeroRotation * Quaternion.AngleAxis(joint.currentAngle, axis);
                    }

                    // keepLevel 관절은 여기서 처리 끝
                    continue;
                }

                // 2. 일반 IK 추적 모드

                // endEffector와 target의 월드 위치를 현재 관절 bone 기준 로컬 위치로 변환
                Vector3 localEffector = bone.InverseTransformPoint(endEffector.position);
                Vector3 localTarget = bone.InverseTransformPoint(target.position);

                // 관절 회전축(axis) 방향 성분은 제거하고,
                // 이 관절이 실제로 회전해서 맞출 수 있는 평면 위 방향만 남김
                Vector3 projEffector = Vector3.ProjectOnPlane(localEffector, axis).normalized;
                Vector3 projTarget = Vector3.ProjectOnPlane(localTarget, axis).normalized;

                // 투영된 방향이 유효할 때만 각도 계산
                if (projEffector.sqrMagnitude > 0.001f && projTarget.sqrMagnitude > 0.001f)
                {
                    // 현재 endEffector 방향에서 target 방향으로 가기 위해
                    // axis 기준으로 몇 도 회전해야 하는지 계산
                    float angle = Vector3.SignedAngle(projEffector, projTarget, axis);

                    // 관절 제한이 있을 때,
                    // 최단 회전 방향이 제한 밖으로 나가면 반대 방향으로 돌아갈 수 있는지 확인
                    if (joint.useLimits)
                    {
                        float desired = joint.currentAngle + angle;

                        if (desired < joint.minAngle || desired > joint.maxAngle)
                        {
                            // 같은 목표 방향을 향하는 반대 회전 경로
                            float altAngle = angle > 0f ? angle - 360f : angle + 360f;
                            float altDesired = joint.currentAngle + altAngle;

                            // 반대 경로가 제한 범위 안이면 그 방향으로 회전
                            if (altDesired >= joint.minAngle && altDesired <= joint.maxAngle)
                                angle = altAngle;
                        }
                    }

                    // 이번 반복에서 회전 가능한 최대 각도만큼만 적용
                    angle = Mathf.Clamp(angle, -maxStep, maxStep);

                    // 현재 관절 각도에 회전량 누적
                    joint.currentAngle += angle;

                    // 최종 관절 각도를 제한 범위 안으로 보정
                    if (joint.useLimits)
                    {
                        joint.currentAngle = Mathf.Clamp(joint.currentAngle, joint.minAngle, joint.maxAngle);
                    }

                    // 초기 회전값 기준으로 axis 방향 currentAngle만큼 회전 적용
                    bone.localRotation = joint.zeroRotation * Quaternion.AngleAxis(joint.currentAngle, axis);
                }
            }
        }
    }
    #if UNITY_EDITOR

    private void OnDrawGizmos()
    {
        if (joints == null || joints.Length == 0 || endEffector == null) return;

        // 1. 전체 최대 사거리 계산 및 표시 (구체)
        if (!TryGetReach(out Vector3 basePos, out float totalLength)) return;

        // 최대 사거리 구체 그리기
        Gizmos.color = new Color(0, 1, 1, 0.2f); // 반투명한 사이언 색상
        Gizmos.DrawWireSphere(basePos, totalLength);
        
        // 2. 각 관절별 가동 범위 표시 (Arc)
        foreach (var joint in joints)
        {
            if (joint.bone == null) continue;

            // 관절 위치에 작은 구체 표시
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(joint.bone.position, 0.05f);

            if (joint.useLimits)
            {
                // 부모의 회전값을 고려한 월드 좌표 기준 축 계산
                Vector3 worldAxis = joint.bone.TransformDirection(joint.rotationAxis);
                
                // 가동 범위의 중심이 되는 '정면' 벡터 계산 (zeroRotation 기준)
                // 여기서는 간단하게 본의 Forward 방향을 기준으로 표시합니다.
                Vector3 forward = joint.bone.parent != null ? joint.bone.parent.forward : Vector3.forward;
                Vector3 fromVector = Quaternion.AngleAxis(joint.minAngle, worldAxis) * forward;

                Handles.color = new Color(1, 0.92f, 0.016f, 0.3f); // 노란색 호
                Handles.DrawSolidArc(
                    joint.bone.position, 
                    worldAxis, 
                    fromVector, 
                    joint.maxAngle - joint.minAngle, 
                    0.3f // 호의 반지름
                );

                // 최소/최대 각도 선 표시
                Handles.color = Color.red;
                Vector3 minVec = Quaternion.AngleAxis(joint.minAngle, worldAxis) * forward;
                Vector3 maxVec = Quaternion.AngleAxis(joint.maxAngle, worldAxis) * forward;
                Handles.DrawLine(joint.bone.position, joint.bone.position + minVec * 0.4f);
                Handles.DrawLine(joint.bone.position, joint.bone.position + maxVec * 0.4f);
            }
        }
    }
#endif
}