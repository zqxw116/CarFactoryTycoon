using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 체결 테스트 씬 전용 매니저.
/// UI에서 파츠를 선택하면:
///   1. 차량 파츠 상태 세팅 (이전=완료, 선택=분리, 이후=숨김)
///   2. StationPlacementDataSO에서 부품 타입의 robotLineSide만 가져와, 현재 LINE 상태 기준으로 스테이션 재배치
///      (매니저 지정 SO 우선, 없으면 스테이션에 바인딩된 SO 사용. SO에 되돌려 저장하지 않음 — 테스트 표시용)
///   3. 로봇팔 조립 준비
///   4. TestCarLooper가 있으면 차량을 시작 위치로 리셋
/// </summary>
public class AssemblyTestManager : GameObjectSingleton<AssemblyTestManager>
{
    [Header("필수 참조")]
    public CarController car;
    public StationController station;

    [Header("파츠 데이터 SO")]
    [Tooltip("armSide 값을 읽기 위한 차량 파츠 데이터 SO (placementData 미설정 시의 폴백용)")]
    public AssemblyPartDataSO partDataSO;

    [Header("로봇팔 배치 데이터 SO (우선 적용)")]
    [Tooltip("부품별 스테이션 전체 배치(위치/회전/Pile/End/작업존)를 저장한 SO. 설정 시 이 데이터로 정확히 재배치한다.")]
    public StationPlacementDataSO placementData;

    [Header("로봇팔 좌/우 배치")]
    [Tooltip("좌우로 이동할 스테이션 루트 Transform (로봇팔 + 파일 포지션의 공통 부모)")]
    public Transform stationRoot;
    [Tooltip("차량 중심 X 좌표 (보통 0)")]
    public float carCenterX = 0f;
    [Tooltip("차량 중심에서 로봇팔까지의 거리")]
    public float sideDistance = 3f;
    [Tooltip("왼쪽 배치 시 stationRoot 회전값 (Y=90 이면 +X 방향을 향함)")]
    public Vector3 leftSideRotation = new Vector3(0f, 90f, 0f);
    [Tooltip("오른쪽 배치 시 stationRoot 회전값 (Y=-90 이면 -X 방향을 향함)")]
    public Vector3 rightSideRotation = new Vector3(0f, -90f, 0f);

    [Header("루프 연동 (선택)")]
    [Tooltip("할당하면 파츠 선택 시 차량을 시작 위치로 리셋한다")]
    public TestCarLooper looper;

    private void Start()
    {
        RunTest(PartType.Frame_1);
    }


    // ─────────────────────────────────────────────
    // 외부 진입점
    // ─────────────────────────────────────────────

    /// <summary>파츠를 선택해 조립 테스트를 시작한다.</summary>
    public void RunTest(PartType targetType)
    {
        if (targetType == PartType.None || car == null || station == null) return;

        // 1. 루프 연동: 현재 파츠 기록 + 차량 시작 위치로 리셋
        if (looper != null)
        {
            looper.SetCurrentPart(targetType);
            looper.ResetToStart();
        }

        car.SetCurretParts(targetType);


        // 3. 부품 타입에 맞춰 로봇팔(스테이션) 재배치.
        //    배치 데이터 SO는 매니저 지정값 우선, 없으면 스테이션 자신에 바인딩된 SO를 사용.
        //    저장된 전체 좌표를 그대로 쓰지 않고, robotLineSide만 가져와 "현재 LINE 상태" 기준으로 재배치한다.
        //    (테스트로 보기 위한 임시 배치일 뿐, SO에 다시 저장하지 않는다.)
        //    둘 다 없으면 기존 robotLineSide 기반 단순 X-side 폴백.
        StationPlacementDataSO so = placementData != null ? placementData : station.placementData;
        if (so != null && so.Has(targetType))
        {
            station.robotLineSide = so.GetPlacement(targetType).robotLineSide;
            station.ApplyLineSide(); // 상위 LineSettings 기준 z/회전/작업존 재계산 (SO 미저장)
        }
        else if (partDataSO != null && stationRoot != null)
        {
            var config = partDataSO.GetConfig(targetType);
            PositionStation(config.robotLineSide);
        }

        // 4. 스테이션 준비 (조립은 차량이 트리거에 진입할 때 OnTriggerEnter에서 시작됨)
        station.PrepareStation(targetType);
    }

    // ─────────────────────────────────────────────
    // 스테이션 좌/우 배치
    // ─────────────────────────────────────────────

    private void PositionStation(RobotLineSideType robotLineSide)
    {
        float x = (robotLineSide == RobotLineSideType.Left)
            ? carCenterX - sideDistance
            : carCenterX + sideDistance;

        stationRoot.position = new Vector3(x, stationRoot.position.y, stationRoot.position.z);
        stationRoot.rotation = Quaternion.Euler(
            robotLineSide == RobotLineSideType.Left ? leftSideRotation : rightSideRotation
        );
    }
}
