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

                // [핵심 로직] 이번 루프에서 관절이 움직일 수 있는 '최대 각도(스피드 제한)' 계산
                // 공식: (기본속도 * 업그레이드계수 * 프레임시간) / 반복횟수
                float maxStep = (joint.baseSpeed * trackingMultiplier * Time.deltaTime) / iterations;

                // 1. 수평 유지 모드 (Rig_Arm_5)
                if (joint.keepLevel)
                {
                    Vector3 currentLocalSky = bone.InverseTransformDirection(Vector3.up);
                    currentLocalSky = Vector3.ProjectOnPlane(currentLocalSky, axis).normalized;

                    if (currentLocalSky.sqrMagnitude > 0.001f && joint.initialLocalSky.sqrMagnitude > 0.001f)
                    {
                        float angle = Vector3.SignedAngle(joint.initialLocalSky, currentLocalSky, axis);

                        // [스피드 제한 적용] 한 번에 다 돌지 못하고 최대 스피드(maxStep) 만큼만 돌아감!
                        angle = Mathf.Clamp(angle, -maxStep, maxStep);

                        joint.currentAngle += angle;

                        if (joint.useLimits)
                            joint.currentAngle = Mathf.Clamp(joint.currentAngle, joint.minAngle, joint.maxAngle);

                        bone.localRotation = joint.zeroRotation * Quaternion.AngleAxis(joint.currentAngle, axis);
                    }
                    continue;
                }

                // 2. 일반 IK 추적 모드
                Vector3 localEffector = bone.InverseTransformPoint(endEffector.position);
                Vector3 localTarget = bone.InverseTransformPoint(target.position);

                Vector3 projEffector = Vector3.ProjectOnPlane(localEffector, axis).normalized;
                Vector3 projTarget = Vector3.ProjectOnPlane(localTarget, axis).normalized;

                if (projEffector.sqrMagnitude > 0.001f && projTarget.sqrMagnitude > 0.001f)
                {
                    float angle = Vector3.SignedAngle(projEffector, projTarget, axis);

                    // [스피드 제한 적용] 타겟이 아무리 멀어도 최대 스피드(maxStep) 만큼만 쫓아감!
                    angle = Mathf.Clamp(angle, -maxStep, maxStep);

                    joint.currentAngle += angle;

                    if (joint.useLimits)
                    {
                        joint.currentAngle = Mathf.Clamp(joint.currentAngle, joint.minAngle, joint.maxAngle);
                    }

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
        float totalLength = 0;
        for (int i = 0; i < joints.Length - 1; i++)
        {
            if (joints[i].bone != null && joints[i + 1].bone != null)
                totalLength += Vector3.Distance(joints[i].bone.position, joints[i + 1].bone.position);
        }
        // 마지막 뼈에서 엔드이펙터까지의 거리 추가
        totalLength += Vector3.Distance(joints[joints.Length - 1].bone.position, endEffector.position);

        // 첫 번째 관절(뿌리) 위치 기준
        Vector3 basePos = joints[0].bone.position;

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