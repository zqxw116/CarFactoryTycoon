using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 부품(PartType)별 로봇팔(StationController) 배치 데이터.
/// 라인 진행 방향 기준 좌/우(armSide)와 그에 맞는 스테이션 루트 위치·회전,
/// PilePos·EndPos·작업존(BoxCollider) 값을 함께 보관한다.
///
/// AssemblyPartDataSO가 "부품 조립(체결) 데이터"라면, 이쪽은 "로봇팔 배치 데이터".
/// TestPartsScene에서 부품을 고르면 이 데이터로 스테이션을 정확히 재배치하고,
/// 추후 로봇팔 배치기 툴에서도 이 데이터로 일괄 배치할 수 있다.
/// </summary>
[System.Serializable]
public struct StationPlacement
{
    public PartType type;

    [Tooltip("라인 진행 방향 기준 좌/우 배치")]
    public RobotLineSideType robotLineSide;

    [Header("스테이션 루트 (StationController 오브젝트)")]
    public Vector3 stationLocalPos;
    public Vector3 stationEuler;

    [Header("대기 위치 (로컬)")]
    public Vector3 pileLocalPos;   // 파츠 대기
    public Vector3 endLocalPos;    // 로봇팔 대기

    [Header("작업존 (BoxCollider, 로컬)")]
    public Vector3 boxCenter;
    public Vector3 boxSize;
}

[CreateAssetMenu(fileName = "StationPlacementDataSO", menuName = "Scriptable Objects/StationPlacementDataSO")]
public class StationPlacementDataSO : ScriptableObject
{
    public string carModelName;
    public List<StationPlacement> placements = new List<StationPlacement>();

    /// <summary>부품 타입의 배치 데이터를 찾는다. 없으면 type=None인 기본값.</summary>
    public StationPlacement GetPlacement(PartType targetType) => placements.Find(x => x.type == targetType);

    /// <summary>해당 타입의 배치가 저장돼 있는지.</summary>
    public bool Has(PartType targetType) => placements.FindIndex(x => x.type == targetType) >= 0;

    /// <summary>배치 데이터를 추가하거나(없으면) 갱신한다(있으면).</summary>
    public void Set(StationPlacement placement)
    {
        int index = placements.FindIndex(x => x.type == placement.type);
        if (index >= 0) placements[index] = placement;
        else placements.Add(placement);
    }
}
