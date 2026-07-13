using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 체결 테스트 씬 전용 매니저.
/// UI에서 파츠를 선택하면:
///   1. 차량 파츠 상태 세팅 (이전=완료, 선택=분리, 이후=숨김)
///   2. StationPlacementDataSO의 씬 독립 값(PilePos/EndPos/작업존/robotLineSide)을 적용하고,
///      루트 위치·회전은 현재 LINE 상태 기준으로 재배치 (ApplyPlacement + ApplyLineSide)
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
    [Tooltip("부품별 씬 독립 배치(Pile/End/작업존/robotLineSide)를 저장한 SO. 루트 위치·회전은 이 씬의 LINE 기준으로 계산된다.")]
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

    [Header("바퀴 리프트 테스트 (선택)")]
    [Tooltip("할당하면 바퀴(Wheel_*) 부품 선택 시 일반 스테이션 대신 이 리프트 공정이 활성화되고," +
        " 차량은 매니저 구동(게이트 정지/캡처)으로 전환된다. 다른 부품 선택 시 다시 비활성화.")]
    public WheelStation wheelStation;

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

        bool isWheelTest = wheelStation != null && IsWheelType(targetType);

        // 리프트가 차량을 잡고 있으면 먼저 내려놓는다 — 차량 리셋(텔레포트) 후에 정리하면
        // 캡처 시점 위치(carBasePos)로 차량이 되돌아가 버린다
        if (wheelStation != null) wheelStation.CancelCycle();

        // 1. 루프 연동: 현재 파츠 기록 + 차량 시작 위치로 리셋
        //    바퀴 테스트는 게이트 정지/캡처가 필요해 매니저 구동(SetPath)으로 전환한다
        if (looper != null)
        {
            looper.SetCurrentPart(targetType);
            looper.useManagedDrive = isWheelTest;
            looper.ResetToStart();
        }

        car.SetCurretParts(targetType);

        // 2. 스테이션 전환: 바퀴 = WheelStation(리프트 공정), 그 외 = 일반 스테이션
        if (wheelStation != null) wheelStation.gameObject.SetActive(isWheelTest);
        station.gameObject.SetActive(!isWheelTest);
        if (isWheelTest) return; // 체결은 WheelStation이 바퀴 스테이션 4개를 직접 오케스트레이션


        // 3. 부품 타입에 맞춰 로봇팔(스테이션) 재배치.
        //    배치 데이터 SO는 매니저 지정값 우선, 없으면 스테이션 자신에 바인딩된 SO를 사용.
        //    ApplyPlacement = 씬 독립 값(PilePos/EndPos/작업존/robotLineSide),
        //    ApplyLineSide  = 루트 z/회전·작업존 center.x/z를 "현재 LINE 상태" 기준으로 재계산.
        //    → Car_Factory 3에서 저장한 튜닝이 이 씬의 라인 위치에 그대로 재현된다. (SO에 다시 저장하지 않음)
        //    둘 다 없으면 기존 robotLineSide 기반 단순 X-side 폴백.
        StationPlacementDataSO so = placementData != null ? placementData : station.placementData;
        if (so != null && so.Has(targetType))
        {
            station.ApplyPlacement(so.GetPlacement(targetType));
            station.ApplyLineSide();
        }
        else if (partDataSO != null && stationRoot != null)
        {
            var config = partDataSO.GetConfig(targetType);
            PositionStation(config.robotLineSide);
        }

        // 4. 스테이션 준비 (조립은 차량이 트리거에 진입할 때 OnTriggerEnter에서 시작됨)
        station.PrepareStation(targetType);
    }

    private static bool IsWheelType(PartType type) =>
        type == PartType.Wheel_FrontRight_41 || type == PartType.Wheel_BehindRight_42 ||
        type == PartType.Wheel_FrontLeft_43 || type == PartType.Wheel_BehindLeft_44;

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
