using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 부품(PartType)별 로봇팔(StationController) 배치 데이터 — 씬 독립 값만 보관한다.
/// 스테이션 루트의 위치·회전은 여기 저장하지 않는다:
/// LineSettings + robotLineSide에서 파생되는 값이라 ApplyLineSide()가 씬마다 계산한다.
/// (작업존 center.x/z도 같은 이유로 라인 파생 — 높이 center.y와 size만 저장)
///
/// AssemblyPartDataSO가 "부품 조립(체결) 데이터"라면, 이쪽은 "로봇팔 배치 데이터".
/// TestPartsScene에서 튜닝·저장한 값을 Car_Factory 3 등 어느 씬에서든 그대로 적용할 수 있다.
/// </summary>
[System.Serializable]
public struct StationPlacement
{
    public PartType type;

    [Tooltip("라인 진행 방향 기준 좌/우 배치")]
    public RobotLineSideType robotLineSide;

    [Header("대기 위치 (스테이션 로컬)")]
    public Vector3 pileLocalPos;   // 파츠 대기
    public Vector3 endLocalPos;    // 로봇팔 대기

    [Header("작업존 (BoxCollider) — center는 라인 파생 기본값 + 오프셋으로 계산")]
    [Tooltip("라인 기준 center 기본값(x=zSpacing, y=0.5, z=±laneWidth)에 더해지는 부품별 추가 오프셋." +
        " x·y는 그대로 더하고, z는 robotLineSide 부호에 맞춰 대칭으로 더한다(+z = laneWidth가 커지는 방향).")]
    public Vector3 boxCenterOffset;
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
