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
        if (!isRunning || car == null || spline == null || splineLength <= 0f) return;

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
