using UnityEngine;
using System.Collections.Generic;

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
        public float rotationSpeed = 180f;

        [Header("각도 제한")]
        public bool useLimits = false;
        public float minAngle = -45f;
        public float maxAngle = 45f;

        // [신규 기능] 물건을 수평으로 들기 위한 팔레타이징 스위치
        [Header("수평 유지 (Palletizing)")]
        public bool keepLevel = false;

        [HideInInspector] public Quaternion initialRot;
        [HideInInspector] public float currentAngle = 0f;
    }

    [Header("IK 설정")]
    public Transform target;
    public Transform endEffector;

    [Header("관절 리스트")]
    public MechanicalJoint[] joints;

    [Header("연산 디테일")]
    public bool isWorking = false;
    [Range(1, 10)] public int iterations = 3;
    [Range(0.1f, 1f)] public float damping = 0.5f;

    [ContextMenu("★ [PM 추천] 계층 기반 자동 바인딩 (수평 유지 포함)")]
    public void AutoSetupJoints()
    {
        Vector3[] mechanicalAxes = {
            Vector3.up,
            Vector3.right,
            Vector3.right,
            Vector3.right,
            Vector3.up,
            Vector3.right,
            Vector3.forward
        };

        bool[] defaultActiveStates = { true, true, true, true, false, true, false };

        // [신규] Cube_2_2 (인덱스 5) 관절에만 '수평 유지'를 True로 기본 세팅
        bool[] defaultKeepLevel = { false, false, false, false, false, true, false };

        List<MechanicalJoint> tempList = new List<MechanicalJoint>();
        Transform currentBone = transform;

        for (int i = 0; i < mechanicalAxes.Length; i++)
        {
            if (currentBone.childCount == 0) break;
            currentBone = currentBone.GetChild(0);

            tempList.Add(new MechanicalJoint
            {
                jointName = $"{i + 2}번 관절 ({currentBone.name})",
                bone = currentBone,
                allowedAxis = mechanicalAxes[i],
                isActive = defaultActiveStates[i],
                keepLevel = defaultKeepLevel[i], // 수평 유지 옵션 할당
                flexibility = 1f,
                rotationSpeed = 180f,
                useLimits = false
            });
        }

        joints = tempList.ToArray();

        Transform[] allChildren = GetComponentsInChildren<Transform>(true);
        foreach (Transform child in allChildren)
        {
            if (child.name == "TargetPoint")
            {
                endEffector = child;
                break;
            }
        }

        Debug.Log($"[RoboticArmIK] 바인딩 완료! 7번 관절(Cube_2_2)에 수평 유지(Palletizing) 기능이 적용되었습니다.");
    }

    void Start()
    {
        foreach (var joint in joints)
        {
            if (joint.bone != null)
            {
                joint.initialRot = joint.bone.localRotation;
                joint.currentAngle = 0f;
            }
        }
    }

    void LateUpdate()
    {
        if (!isWorking || target == null || endEffector == null || joints.Length == 0) return;

        // 1. 기존 IK 연산 루프 (관절 꺾기)
        for (int i = 0; i < iterations; i++)
        {
            for (int j = joints.Length - 1; j >= 0; j--)
            {
                MechanicalJoint joint = joints[j];

                if (!joint.isActive || joint.bone == null) continue;

                Transform bone = joint.bone;
                Vector3 toEffector = endEffector.position - bone.position;
                Vector3 toTarget = target.position - bone.position;

                Vector3 localToEffector = bone.InverseTransformDirection(toEffector);
                Vector3 localToTarget = bone.InverseTransformDirection(toTarget);

                Vector3 projEffector = Vector3.ProjectOnPlane(localToEffector, joint.allowedAxis).normalized;
                Vector3 projTarget = Vector3.ProjectOnPlane(localToTarget, joint.allowedAxis).normalized;

                if (projEffector.sqrMagnitude > 0.001f && projTarget.sqrMagnitude > 0.001f)
                {
                    float angle = Vector3.SignedAngle(projEffector, projTarget, joint.allowedAxis);
                    float deltaAngle = angle * damping * joint.flexibility;

                    float maxAngleThisIteration = (joint.rotationSpeed * Time.deltaTime) / iterations;
                    deltaAngle = Mathf.Clamp(deltaAngle, -maxAngleThisIteration, maxAngleThisIteration);

                    joint.currentAngle += deltaAngle;

                    if (joint.useLimits)
                    {
                        joint.currentAngle = Mathf.Clamp(joint.currentAngle, joint.minAngle, joint.maxAngle);
                    }

                    bone.localRotation = joint.initialRot * Quaternion.AngleAxis(joint.currentAngle, joint.allowedAxis);
                }
            }
        }

        // 2. [핵심 신규 로직] IK 연산이 전부 끝난 후, 수평을 유지해야 하는 관절 고정
        foreach (var joint in joints)
        {
            if (joint.keepLevel && joint.bone != null)
            {
                // 해당 관절의 Y축(up)을 월드의 하늘(Vector3.up)과 강제로 일치시킵니다.
                // 좌우 방향(Yaw) 회전은 그대로 유지되면서, 위아래 기울기(Pitch, Roll)만 평평해집니다.
                Quaternion alignRotation = Quaternion.FromToRotation(joint.bone.up, Vector3.up);
                joint.bone.rotation = alignRotation * joint.bone.rotation;
            }
        }
    }
}