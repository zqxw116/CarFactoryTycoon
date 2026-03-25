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
            if (child.name == "Rig_End") endEffector = child;
            if (child.name == "IK Target") target = child;
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
}