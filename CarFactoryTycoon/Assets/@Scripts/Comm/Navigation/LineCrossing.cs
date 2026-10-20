using System.Collections.Generic;
using Unity.AI.Navigation;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>
/// 라인(벨트)을 건너는 횡단보도. 벨트는 NavMesh에서 통행 불가로 구워지므로, 사람은 이 지점의
/// NavMeshLink로만 라인 반대편으로 넘어갈 수 있다.
///
/// ★ 건너기 전 판단 (NavMover가 링크 진입 지점에서 IsClear를 물어본다)
///   - 지금 횡단 구간(스플라인 진행도 ± zoneHalfWidth) 안에 차체가 걸쳐 있으면 대기.
///   - 상류에서 다가오는 차량이 '건너는 시간 + safetyTime' 동안 최고 속도(moveSpeed)로 와도
///     횡단 구간에 닿지 않을 때만 건넌다. 정지·감속 중인 차도 언제든 다시 출발할 수 있으므로
///     현재 속도가 아니라 moveSpeed(최고 속도)로 보수적으로 계산한다.
///   - 라인 밖 차량(크레인으로 들린 차 = LeaveLine으로 targetSpline이 비워짐)은 제외.
///   - 차가 jamTime 이상 구간을 막고 있으면 '정체'로 보고 링크 비용을 jamCost로 올린다 →
///     대기 중인 작업자가 경로를 다시 계산하면 다른 횡단보도가 있을 때 그쪽으로 돌아간다.
///
/// 배치: 이 오브젝트를 라인 근처에 놓으면 OnValidate가 가장 가까운 스플라인 지점에 붙이고
///   벨트를 가로지르는 방향(forward)으로 돌린다. 링크 양끝 = 로컬 (0,0,±halfSpan).
///   직선 구간·스테이션 작업존 밖에 둘 것. 바닥 표시는 Tools/CarFactory/내비메시 다시 굽기가 함께 만든다.
/// </summary>
[RequireComponent(typeof(NavMeshLink))]
public class LineCrossing : MonoBehaviour
{
    [Header("라인")]
    [Tooltip("건너는 라인의 스플라인. 비어 있으면 씬의 'MainSpline'을 찾는다.")]
    public SplineContainer spline;

    [Tooltip("배치 시 가장 가까운 스플라인 지점에 자동으로 붙이고 벨트를 가로지르는 방향으로 돌린다.")]
    public bool snapToSpline = true;

    [Tooltip("바닥 높이(월드 y). 링크 끝점이 이 높이의 NavMesh에 붙는다.")]
    public float floorY = -0.405f;

    [Header("치수")]
    [Tooltip("벨트 중심 → 링크 끝점 거리(m). 통행 불가 벨트 폭의 절반 + 작업자 반경 + 여유보다 커야 한다.")]
    public float halfSpan = 2.2f;

    [Tooltip("횡단 통로 반폭(m, 라인 진행 방향). 차체가 이 구간에 걸치면 건너지 않는다.")]
    public float zoneHalfWidth = 0.9f;

    [Tooltip("차량 반길이(m, 진행도 지점 기준 앞뒤). 차량 길이 약 4.5 + 여유.")]
    public float carHalfLength = 2.6f;

    [Header("판단")]
    [Tooltip("건너는 시간에 더하는 안전 여유(초).")]
    public float safetyTime = 1.0f;

    [Tooltip("차가 이 시간(초) 이상 횡단 구간을 막고 있으면 정체로 보고 링크 비용을 올린다(다른 횡단보도로 우회 유도).")]
    public float jamTime = 4f;

    [Tooltip("정체 시 링크 비용 배율. 다른 횡단보도가 없으면 결국 이 지점에서 기다린다.")]
    public float jamCost = 200f;

    [Tooltip("대기 위치 분산 간격(m, 라인 진행 방향). 여러 작업자가 같은 지점에서 겹치지 않게.")]
    public float waitSlotSpacing = 0.8f;

    [Header("기즈모")]
    public bool drawGizmo = true;

    private NavMeshLink link;
    private float baseCost = -1f;
    private float progress = -1f;            // 이 횡단보도의 스플라인 진행도(0~1)
    private float occupiedSince = -1f;       // 구간 점유가 시작된 시각(-1 = 비어 있음)
    private bool jammed;
    private float lastQueryTime = -999f;

    // 대기 중인 작업자 → 슬롯 번호 (같은 쪽에서 기다리는 순서대로 0, +1, -1, +2 …)
    private readonly List<NavMover> waiters = new List<NavMover>();

    // LineTrafficManager.Instance는 없으면 새로 만들어버리므로(MonoSingleton) 직접 찾아 캐시한다.
    private static LineTrafficManager s_Traffic;
    private static float s_TrafficSearchTime = -999f;
    // 트래픽 매니저가 없는 씬(자율 이동 차량)용 폴백 목록 — 1초마다 갱신
    private static CarController[] s_FallbackCars = new CarController[0];
    private static float s_FallbackTime = -999f;

    /// <summary>링크가 정체(차가 오래 막고 있음) 상태인지.</summary>
    public bool IsJammed => jammed;

    /// <summary>링크 길이(m) — 끝에서 끝까지.</summary>
    public float Span => halfSpan * 2f;

    /// <summary>차량 진행 방향(라인 하류 방향, 월드).</summary>
    public Vector3 Downstream => transform.right;

    private void Reset()
    {
        AutoFindSpline();
        ApplyLink();
    }

    private void Awake()
    {
        link = GetComponent<NavMeshLink>();
        baseCost = link.costModifier;
        AutoFindSpline();
        CacheProgress();
    }

    private void OnDisable()
    {
        waiters.Clear();
        SetJammed(false);
    }

    private void AutoFindSpline()
    {
        if (spline != null) return;
        GameObject go = GameObject.Find("MainSpline");
        if (go != null) spline = go.GetComponent<SplineContainer>();
    }

    private void CacheProgress()
    {
        if (spline == null || spline.Splines.Count == 0) return;
        Vector3 local = spline.transform.InverseTransformPoint(transform.position);
        SplineUtility.GetNearestPoint(spline.Spline, (float3)local, out float3 _, out float t);
        progress = t;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        AutoFindSpline();
        if (snapToSpline) SnapToSpline();
        ApplyLink();
    }
#endif

    /// <summary>가장 가까운 스플라인 지점으로 옮기고 forward = 벨트를 가로지르는 방향, right = 차량 진행 방향으로 돌린다.</summary>
    public void SnapToSpline()
    {
        if (spline == null || spline.Splines.Count == 0) return;
        Vector3 local = spline.transform.InverseTransformPoint(transform.position);
        SplineUtility.GetNearestPoint(spline.Spline, (float3)local, out float3 _, out float t);
        Vector3 p = spline.EvaluatePosition(t);
        Vector3 tan = spline.EvaluateTangent(t);
        tan.y = 0f;
        if (tan.sqrMagnitude < 1e-6f) return;
        tan.Normalize();
        transform.position = new Vector3(p.x, floorY, p.z);
        // right = 진행 방향 → forward = 진행 방향을 y축으로 -90° 돌린 방향(라인 왼쪽)
        transform.rotation = Quaternion.LookRotation(Vector3.Cross(tan, Vector3.up), Vector3.up);
        progress = t;
    }

    /// <summary>링크 끝점을 로컬 (0,0,±halfSpan)으로 맞춘다.</summary>
    public void ApplyLink()
    {
        if (link == null) link = GetComponent<NavMeshLink>();
        if (link == null) return;
        link.startTransform = null;
        link.endTransform = null;
        link.startPoint = new Vector3(0f, 0f, -halfSpan);
        link.endPoint = new Vector3(0f, 0f, halfSpan);
        link.width = 0f;
        link.bidirectional = true;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 건너기 판단
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 지금 건너도 되는지. crossTime = 이 작업자가 링크를 건너는 데 걸리는 시간(초).
    /// lookAt에는 가장 위협적인 차량(없으면 상류 방향 한 점)을 돌려준다 — 대기 중 바라보기 연출용.
    /// </summary>
    public bool IsClear(float crossTime, out Vector3 lookAt)
    {
        lookAt = transform.position - Downstream * 6f;
        lastQueryTime = Time.time;
        if (progress < 0f) CacheProgress();
        if (progress < 0f) return true; // 스플라인 없음 — 판단 불가, 막지 않는다

        bool occupied = false, approaching = false;
        float nearest = float.MaxValue;
        float horizon = crossTime + safetyTime;

        IReadOnlyList<CarController> cars = GetCars();
        for (int i = 0; i < cars.Count; i++)
        {
            CarController car = cars[i];
            if (car == null || !car.gameObject.activeInHierarchy) continue;
            if (car.targetSpline != spline) continue;   // 라인 밖(들린 차 등) 또는 다른 라인
            float len = car.SplineLength;
            if (len <= 0f) continue;

            float s = car.pathProgress * len;
            float sc = progress * len;
            float front = s + carHalfLength, back = s - carHalfLength;
            float zoneMin = sc - zoneHalfWidth, zoneMax = sc + zoneHalfWidth;

            if (back <= zoneMax && front >= zoneMin)
            {
                occupied = true;
                lookAt = car.transform.position;
                nearest = 0f;
            }
            else if (front < zoneMin)
            {
                float dist = zoneMin - front;
                // 정지 중이어도 언제든 다시 출발할 수 있다 → 최고 속도로 보수적으로 계산
                if (dist <= car.moveSpeed * horizon) approaching = true;
                if (dist < nearest)
                {
                    nearest = dist;
                    lookAt = car.transform.position;
                }
            }
        }

        UpdateJam(occupied);
        return !occupied && !approaching;
    }

    private void UpdateJam(bool occupied)
    {
        if (!occupied)
        {
            occupiedSince = -1f;
            SetJammed(false);
            return;
        }
        if (occupiedSince < 0f) occupiedSince = Time.time;
        if (Time.time - occupiedSince >= jamTime) SetJammed(true);
    }

    private void SetJammed(bool on)
    {
        if (jammed == on) return;
        jammed = on;
        if (link == null) return;
        if (baseCost < 0f) baseCost = link.costModifier;
        link.costModifier = on ? jamCost : baseCost;
    }

    private void Update()
    {
        // 정체 표시가 켜진 채 아무도 묻지 않으면(대기자가 우회해 떠남) 스스로 다시 확인해 풀어 준다.
        if (!jammed || Time.time - lastQueryTime < 0.5f) return;
        IsClear(0f, out _);
    }

    private static IReadOnlyList<CarController> GetCars()
    {
        if (s_Traffic == null && Time.time - s_TrafficSearchTime > 1f)
        {
            s_TrafficSearchTime = Time.time;
            s_Traffic = FindFirstObjectByType<LineTrafficManager>();
        }
        if (s_Traffic != null) return s_Traffic.ActiveCars;

        if (Time.time - s_FallbackTime > 1f)
        {
            s_FallbackTime = Time.time;
            s_FallbackCars = FindObjectsByType<CarController>(FindObjectsSortMode.None);
        }
        return s_FallbackCars;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 대기 위치 분산
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>대기 등록 후 슬롯 오프셋(월드, 라인 진행 방향)을 돌려준다. 0번=정위치, 이후 ±번갈아.</summary>
    public Vector3 EnterWait(NavMover mover)
    {
        if (!waiters.Contains(mover)) waiters.Add(mover);
        int idx = waiters.IndexOf(mover);
        int k = (idx + 1) / 2;
        float sign = (idx % 2 == 1) ? 1f : -1f;
        return Downstream * (k * sign * waitSlotSpacing);
    }

    public void LeaveWait(NavMover mover)
    {
        waiters.Remove(mover);
    }

    /// <summary>NavMeshLink 소유자에서 LineCrossing을 찾는다(없으면 null).</summary>
    public static LineCrossing FromLinkOwner(Object owner)
    {
        Component c = owner as Component;
        return c != null ? c.GetComponent<LineCrossing>() : null;
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmo) return;
        Vector3 a = transform.TransformPoint(new Vector3(0f, 0f, -halfSpan));
        Vector3 b = transform.TransformPoint(new Vector3(0f, 0f, halfSpan));
        Gizmos.color = jammed ? Color.red : new Color(1f, 0.9f, 0.2f, 1f);
        Gizmos.DrawLine(a + Vector3.up * 0.05f, b + Vector3.up * 0.05f);
        Gizmos.DrawWireSphere(a, 0.25f);
        Gizmos.DrawWireSphere(b, 0.25f);
        // 횡단 판단 구간(라인 진행 방향 ± zoneHalfWidth)
        Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.6f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(Vector3.up * 0.5f, new Vector3(zoneHalfWidth * 2f, 1f, halfSpan * 2f));
        Gizmos.matrix = Matrix4x4.identity;
        // 상류 방향(차량이 오는 쪽)
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position + Vector3.up * 0.3f, transform.position - Downstream * 3f + Vector3.up * 0.3f);
    }
}
