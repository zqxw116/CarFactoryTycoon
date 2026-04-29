using System.Collections.Generic;
using UnityEngine;

/// <summary>라인 진행 방향 기준 로봇팔 배치 방향</summary>
public enum ArmSide { Left, Right }

[System.Serializable]
public struct PartConfig
{
    public PartType type;

    [Header("로봇팔 배치")]
    [Tooltip("라인 진행 방향 기준으로 로봇팔이 왼쪽에 배치될지 오른쪽에 배치될지 지정")]
    public ArmSide armSide;

    [Header("위치 데이터")]
    public Vector3 assembledPos;   // progress=0.0 체결 완료
    public Vector3 assembledRot;
    public Vector3 midPos;         // progress=0.3 첫 번째 중간 꺾임
    public Vector3 midRot;
    public Vector3 mid2Pos;        // progress=0.6 두 번째 중간 꺾임
    public Vector3 mid2Rot;

}

[CreateAssetMenu(fileName = "AssemblyPartDataSO", menuName = "Scriptable Objects/AssemblyPartDataSO")]
public class AssemblyPartDataSO : ScriptableObject
{
    public string carModelName;
    public List<PartConfig> partConfigs = new List<PartConfig>();

    // 특정 부품 타입의 데이터를 빠르게 찾아주는 편의 함수
    public PartConfig GetConfig(PartType targetType)
    {
        return partConfigs.Find(x => x.type == targetType);
    }
}
