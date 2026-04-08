using System.Collections.Generic;
using UnityEngine;
[System.Serializable]
public struct PartConfig
{
    public PartType type; // 어떤 부품인가? (Enum)

    [Header("위치 데이터")]
    public Vector3 assembledPos;
    public Vector3 assembledRot;
    public bool useIntermediate;
    public Vector3 intermediatePos;
    public Vector3 intermediateRot;
    public Vector3 detachedPos;
    public Vector3 detachedRot;
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
