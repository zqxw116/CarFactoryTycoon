using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 체결 테스트 씬 전용 매니저.
/// UI에서 파츠를 선택하면:
///   1. 차량 파츠 상태 세팅 (이전=완료, 선택=분리, 이후=숨김)
///   2. SO의 armSide에 따라 스테이션 루트를 좌/우 재배치
///   3. 로봇팔 조립 시작
///   4. TestCarLooper가 있으면 차량을 시작 위치로 리셋
/// </summary>
public class AssemblyTestManager : GameObjectSingleton<AssemblyTestManager>
{
    [Header("필수 참조")]
    public CarController car;
    public StationController station;

    [Header("파츠 데이터 SO")]
    [Tooltip("armSide 값을 읽기 위한 차량 파츠 데이터 SO")]
    public AssemblyPartDataSO partDataSO;

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
        RunTest(PartType.Frame);
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


        // 3. SO에서 armSide 읽어 스테이션 좌/우 재배치
        if (partDataSO != null && stationRoot != null)
        {
            var config = partDataSO.GetConfig(targetType);
            PositionStation(config.armSide);
        }

        // 4. 스테이션 준비 (조립은 차량이 트리거에 진입할 때 OnTriggerEnter에서 시작됨)
        station.PrepareStation(targetType);
    }

    // ─────────────────────────────────────────────
    // 스테이션 좌/우 배치
    // ─────────────────────────────────────────────

    private void PositionStation(ArmSide armSide)
    {
        float x = (armSide == ArmSide.Left)
            ? carCenterX - sideDistance
            : carCenterX + sideDistance;

        stationRoot.position = new Vector3(x, stationRoot.position.y, stationRoot.position.z);
        stationRoot.rotation = Quaternion.Euler(
            armSide == ArmSide.Left ? leftSideRotation : rightSideRotation
        );
    }
}
