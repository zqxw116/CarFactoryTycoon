using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

/// <summary>
/// 테스트 씬 전용 차량 루프 컨트롤러.
/// CarController의 자체 이동을 끄고, 직접 Spline 진행도를 관리한다.
/// startProgress(0.1) ~ 1.0 구간을 반복 이동하며 체결 과정을 시연한다.
/// </summary>
public class TestCarLooper : MonoBehaviour
{
    [Header("참조")]
    public CarController car;
    public AssemblyTestManager testManager;
    public SplineContainer spline;

    [Header("루프 설정")]
    [Tooltip("루프 시작 진행도 (0~1)")]
    [Range(0f, 1f)]
    public float startProgress = 0.1f;
    [Tooltip("차량 이동 속도 (m/s)")]
    public float moveSpeed = 2f;

    [Header("매니저 구동 (바퀴 리프트 테스트)")]
    [Tooltip("켜면 차량 이동을 SetPath → LineTrafficManager에 맡긴다: 게이트 감속 정지·리프트 캡처가" +
        " 실제 라인과 동일하게 동작한다. 끄면 기존처럼 루퍼가 직접 진행도를 구동한다(트리거 체결 테스트용)." +
        " AssemblyTestManager가 바퀴 부품 선택 시 자동으로 켠다.")]
    public bool useManagedDrive = false;
    [Tooltip("매니저 구동 모드에서 이 진행도에 도달하면 루프를 재시작한다 —" +
        " CarController가 1.0에서 실행하는 출고/풀 반환 로직에 들어가기 전에 가로챈다.")]
    [Range(0.5f, 0.99f)] public float managedRestartProgress = 0.97f;

    // ─────────────────────────────────────────────
    // 내부 상태
    // ─────────────────────────────────────────────

    private PartType currentPart = PartType.None;
    private bool isRunning = false;
    private float splineLength = 0f;

    // ─────────────────────────────────────────────
    // 초기화
    // ─────────────────────────────────────────────

    private void Start()
    {
        if (car != null)
            car.isMoving = false; // CarController 자체 이동 비활성

        if (spline != null)
            splineLength = spline.CalculateLength();

        ResetToStart();
    }

    // ─────────────────────────────────────────────
    // 매 프레임 이동
    // ─────────────────────────────────────────────

    private void Update()
    {
        if (!isRunning || car == null || spline == null) return;

        if (useManagedDrive)
        {
            // 이동은 LineTrafficManager가 구동(게이트 클램프 포함) — 루퍼는 끝 도달 감시만 한다.
            // 차량이 비활성화됐거나(출고/풀 반환 등) 끝 근처에 도달하면 재시작.
            if (!car.gameObject.activeInHierarchy || car.pathProgress >= managedRestartProgress)
                Respawn();
            return;
        }

        if (splineLength <= 0f) return;

        car.pathProgress += (moveSpeed / splineLength) * Time.deltaTime;

        if (car.pathProgress >= 1f)
        {
            Respawn();
            return;
        }

        ApplySplineTransform(car.pathProgress);
    }

    // ─────────────────────────────────────────────
    // 공개 API
    // ─────────────────────────────────────────────

    /// <summary>현재 테스트 중인 파츠를 기록한다.</summary>
    public void SetCurrentPart(PartType type)
    {
        currentPart = type;
    }

    /// <summary>차량을 startProgress 위치로 즉시 리셋하고 이동을 재개한다.</summary>
    public void ResetToStart()
    {
        if (car == null) return;

        if (useManagedDrive && spline != null)
        {
            // 이동을 CarController + LineTrafficManager에 맡긴다 (등록·게이트 클램프·감속 도킹 포함)
            if (!car.gameObject.activeSelf) car.gameObject.SetActive(true); // 진행도 1.0 도달로 풀 반환됐을 때 복구
            car.SetPath(spline, moveSpeed, startProgress);
            isRunning = true;
            return;
        }

        car.isMoving = false; // 매니저 구동에서 직접 구동으로 전환 시 SetPath가 켠 자율 이동 차단
        car.pathProgress = startProgress;
        ApplySplineTransform(startProgress);
        isRunning = true;
    }

    // ─────────────────────────────────────────────
    // 내부
    // ─────────────────────────────────────────────

    private void Respawn()
    {
        if (currentPart != PartType.None && testManager != null)
            testManager.RunTest(currentPart); // RunTest 내부에서 ResetToStart 호출
        else
            ResetToStart(); // testManager 없을 때만 자체 리셋
    }

    private void ApplySplineTransform(float progress)
    {
        if (spline == null) return;

        SplineUtility.Evaluate(
            spline.Spline, progress,
            out float3 localPos,
            out float3 localTangent,
            out float3 localUp);

        car.SetPosition(spline.transform.TransformPoint(localPos));

        if (math.length(localTangent) > 0.001f)
        {
            Vector3 worldDir = spline.transform.TransformDirection(localTangent);
            Vector3 worldUp  = spline.transform.TransformDirection(localUp);
            car.transform.rotation = Quaternion.LookRotation(worldDir, worldUp);
        }
    }
}
