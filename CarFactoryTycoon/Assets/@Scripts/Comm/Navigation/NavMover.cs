using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 사람(보급 작업자 등) 공용 이동 컴포넌트 — NavMesh 길찾기 + 횡단보도(LineCrossing) 판단 + 직선 폴백.
///
/// 사용법: 매 프레임 MoveTo(목표)를 부르고 true(도착)가 돌아오면 다음 상태로 넘어간다.
///   애니메이터는 CurrentSpeed(m/초)로 구동하고, 정지 중 바라보기는 FaceTowards를 쓴다.
///   (기존 Worker.MoveToward(목표) → bool 과 같은 호출 형태라 상태기계를 거의 바꾸지 않고 교체할 수 있다)
///
/// ★ 이동 방식
///   - NavMeshAgent가 장애물(소품·로봇팔 받침대·설비)을 돌아가는 경로를 찾는다. 벨트는 통행 불가로 구워져 있어
///     라인 반대편은 LineCrossing의 NavMeshLink로만 넘어간다.
///   - 링크는 자동 통과를 끄고(autoTraverseOffMeshLink=false) 직접 처리한다:
///     진입 지점 도착 → 대기(차 쪽을 바라봄) → LineCrossing.IsClear가 허락하면 걷는 속도로 건넘 → CompleteOffMeshLink.
///   - 회전은 에이전트에 맡기지 않고(updateRotation=false) 이동 방향으로 turnSpeed만큼 돌린다 — 기존 연출과 동일.
///
/// ★ 폴백 (경고 1회)
///   NavMesh가 없거나 시작/목적지가 NavMesh 밖, 경로가 무효면 기존 직선 이동(MoveTowards)으로 간다.
///   부분 경로(Partial)는 끝까지 간 뒤 남은 거리를 직선으로 마저 간다.
/// </summary>
[DisallowMultipleComponent]
public class NavMover : MonoBehaviour
{
    [Header("NavMesh 사용")]
    [Tooltip("끄면 항상 직선 이동(기존 방식).")]
    public bool useNavMesh = true;
    [Tooltip("Prototype safety: wait/retry instead of walking through blocked routes.")]
    public bool requireCompletePath = true;
    float nextRouteRetry;

    [Tooltip("시작 위치·목적지를 NavMesh에 붙일 때 찾는 최대 거리(m). 이보다 멀면 직선 폴백.")]
    public float sampleRadius = 1.5f;

    [Header("에이전트 (NavMeshAgent가 없으면 실행 시 이 값으로 붙인다)")]
    public float agentRadius = 0.35f;
    public float agentHeight = 1.85f;

    [Tooltip("피벗과 NavMesh 표면 사이 높이(m). 실측: 구운 NavMesh 표면 y≈-0.39, 보급 작업자 피벗 -0.305(발끝 메시 -0.39)" +
        " → 0.085면 기존 높이 그대로 발이 바닥에 닿는다.")]
    public float baseOffset = 0.085f;

    public float acceleration = 12f;
    public float angularSpeed = 720f;

    [Tooltip("회피 우선순위(낮을수록 우선). 같은 값끼리는 서로 비켜 준다.")]
    [Range(0, 99)] public int avoidancePriority = 50;

    [Header("이동")]
    [Tooltip("이동 속도(m/초). 소유 컴포넌트가 매 프레임 Speed로 덮어쓴다.")]
    public float speed = 2f;

    [Tooltip("목표 지점에 이 거리(m) 이내로 들어오면 도착으로 판정한다.")]
    public float arriveRadius = 0.25f;

    [Tooltip("이동 방향으로 몸을 돌리는 속도(도/초). 0이면 회전하지 않는다.")]
    public float turnSpeed = 540f;

    [Header("횡단보도")]
    [Tooltip("링크 진입 지점에서 대기 슬롯으로 옮겨 서는 속도 배율.")]
    public float slotMoveSpeedMul = 0.6f;

    [Tooltip("정체된 횡단보도에서 경로를 다시 계산하기까지 기다리는 최소 시간(초).")]
    public float rerouteDelay = 1.0f;

    [Header("디버그")]
    public bool verboseLog = false;

    private const string LOG_TAG = "[NavMover]";

    private NavMeshAgent agent;
    private bool navReady;           // 에이전트가 NavMesh 위에 올라가 있음
    private bool warnedFallback;
    private Vector3 destination;
    private Vector3 sampledDestination; // NavMesh에 붙인 목적지(경로의 실제 끝)
    private bool hasDestination;
    private int destSetFrame = -1;
    private bool arrivedAtDest;      // 현재 목적지에 도착해 경로를 내려놓은 상태
    private bool straightFallback;  // 이번 목적지는 직선으로 간다(경로 무효·NavMesh 밖)
    private float movedThisFrame;
    private int lastMoveFrame = -1;

    // 횡단 상태
    private enum CrossPhase { None, Waiting, Crossing }
    private CrossPhase crossPhase = CrossPhase.None;
    private LineCrossing crossing;
    private Vector3 crossFrom, crossTo, slotOffset;
    private float waitStartTime;
    private LineCrossing reroutedFrom;   // 정체로 한 번 우회를 시도한 횡단보도(같은 곳에서 반복 우회 방지)

    /// <summary>이동 속도(m/초). 에이전트에도 즉시 반영된다.</summary>
    public float Speed
    {
        get => speed;
        set
        {
            speed = value;
            if (agent != null && agent.isActiveAndEnabled) agent.speed = value;
        }
    }

    /// <summary>이번 프레임 실제 이동 속력(m/초) — 걷기 애니메이션용.</summary>
    public float CurrentSpeed
    {
        get
        {
            float dt = Time.deltaTime;
            float manual = (lastMoveFrame == Time.frameCount && dt > 0f) ? movedThisFrame / dt : 0f;
            if (crossPhase != CrossPhase.None || !navReady || straightFallback || !agent.enabled) return manual;
            Vector3 v = agent.velocity; v.y = 0f;
            return Mathf.Max(manual, v.magnitude);
        }
    }

    /// <summary>횡단보도 앞에서 차가 지나가길 기다리는 중인지.</summary>
    public bool IsWaitingToCross => crossPhase == CrossPhase.Waiting;

    /// <summary>NavMesh로 이동 중인지(false = 직선 폴백).</summary>
    public bool UsingNavMesh => navReady && !straightFallback;

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
    }

    private void Start()
    {
        TryInitAgent();
    }

    private void OnDisable()
    {
        EndCrossWait();
        crossPhase = CrossPhase.None;
        hasDestination = false;
        arrivedAtDest = false;
    }

    /// <summary>
    /// 에이전트를 준비한다. 고용(Instantiate 복제) 시에도 Warp로 현재 위치에 다시 올린다.
    /// NavMesh가 근처에 없으면 에이전트를 끄고 직선 폴백.
    /// </summary>
    private void TryInitAgent()
    {
        navReady = false;
        if (!useNavMesh) return;

        if (!NavMesh.SamplePosition(transform.position, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas))
        {
            if (agent != null) agent.enabled = false;
            WarnFallbackOnce("시작 위치 근처에 NavMesh가 없음");
            return;
        }

        if (agent == null)
        {
            agent = gameObject.AddComponent<NavMeshAgent>();
            agent.radius = agentRadius;
            agent.height = agentHeight;
            agent.baseOffset = baseOffset;
            agent.acceleration = acceleration;
            agent.angularSpeed = angularSpeed;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            agent.avoidancePriority = avoidancePriority;
        }
        agent.enabled = true;
        agent.speed = speed;
        agent.stoppingDistance = 0f;          // 도착 판정은 arriveRadius로 직접 한다
        agent.autoBraking = true;
        agent.updateRotation = false;         // 회전은 FaceTowards로 (기존 연출 유지)
        agent.autoTraverseOffMeshLink = false; // 횡단보도는 직접 판단·이동
        agent.autoRepath = true;
        agent.Warp(hit.position);
        navReady = agent.isOnNavMesh;
        if (!navReady) WarnFallbackOnce("에이전트를 NavMesh에 올리지 못함");
    }

    /// <summary>목표로 이동한다. 도착했으면 true. 매 프레임 호출한다.</summary>
    public bool MoveTo(Vector3 target)
    {
        if (!navReady) return MoveStraight(target);

        bool onLink = crossPhase != CrossPhase.None || (agent.enabled && agent.isOnOffMeshLink);
        Vector3 pos = transform.position;

        // 이미 목표 근처면 경로 요청 없이 도착(대기 상태에서 매 프레임 호출돼도 경로를 다시 구하지 않는다)
        if (!onLink && FlatDist(pos, target) <= arriveRadius)
        {
            if (hasDestination && agent.enabled && agent.isOnNavMesh) agent.ResetPath();
            hasDestination = false;
            straightFallback = false;
            return true;
        }

        bool newDest = !hasDestination || FlatDist(target, destination) > 0.05f;

        // 같은 목표에 이미 도착(NavMesh에 붙인 지점 기준)했다면 경로를 다시 구하지 않는다. 밀려났으면 다시 출발.
        if (arrivedAtDest && !newDest)
        {
            if (FlatDist(pos, sampledDestination) <= arriveRadius) return true;
            newDest = true;
        }
        if (newDest) SetDestination(target);

        if (straightFallback) return MoveStraight(target);

        // 횡단보도 처리
        if (onLink || agent.isOnOffMeshLink)
        {
            UpdateOffMeshLink();
            return false;
        }

        if (agent.pathPending) return false;
        if (requireCompletePath && agent.pathStatus != NavMeshPathStatus.PathComplete)
        { straightFallback = true; return MoveStraight(target); }

        // 도착 판정: NavMesh에 붙인 목적지 기준(목표가 장애물에 살짝 묻혀 있어도 가장 가까운 지점에서 도착)
        if (FlatDist(pos, sampledDestination) <= arriveRadius && agent.remainingDistance <= arriveRadius)
        {
            agent.ResetPath();
            arrivedAtDest = true; // hasDestination은 유지 — 같은 목표로 다시 불려도 경로를 새로 구하지 않게
            return true;
        }

        // 경로 끝에 도착했는데 목표가 아직 멀다(부분 경로·NavMesh 밖 목표) → 남은 거리를 직선으로
        // (경로를 요청한 바로 그 프레임은 remainingDistance가 갱신 전일 수 있어 판정하지 않는다)
        if (Time.frameCount > destSetFrame && (agent.hasPath == false || agent.remainingDistance <= 0.05f))
        {
            if (agent.pathStatus != NavMeshPathStatus.PathComplete || agent.remainingDistance <= 0.05f)
            {
                WarnFallbackOnce($"경로 끝에서 목표까지 {FlatDist(pos, target):F2}m 남음 — 직선으로 마저 이동");
                straightFallback = true;
                agent.ResetPath();
                return MoveStraight(target);
            }
        }

        Vector3 v = agent.desiredVelocity;
        v.y = 0f;
        if (v.sqrMagnitude > 0.01f) FaceTowards(pos + v);
        return false;
    }

    /// <summary>멈춘다(경로 해제). 횡단 중이면 끝까지 건넌 뒤 멈추도록 횡단은 유지한다.</summary>
    public void Stop()
    {
        if (crossPhase == CrossPhase.Crossing) return;
        EndCrossWait();
        hasDestination = false;
        if (navReady && agent.isOnNavMesh && !agent.isOnOffMeshLink) agent.ResetPath();
    }

    private void SetDestination(Vector3 target)
    {
        destination = target;
        sampledDestination = target;
        hasDestination = true;
        arrivedAtDest = false;
        straightFallback = false;

        if (crossPhase != CrossPhase.None) return; // 횡단 처리 중엔 끝난 뒤 다시 계산한다

        if (!NavMesh.SamplePosition(target, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas))
        {
            WarnFallbackOnce($"목적지 {target}가 NavMesh 밖");
            straightFallback = true;
            return;
        }
        if (!EnsureAgentOnMesh())
        {
            straightFallback = true;
            return;
        }
        sampledDestination = hit.position;
        destSetFrame = Time.frameCount;
        if (!agent.SetDestination(hit.position))
        {
            WarnFallbackOnce($"목적지 {target} 경로 요청 실패");
            straightFallback = true;
        }
    }

    /// <summary>직선 폴백으로 꺼 둔 에이전트를 다시 켜고 현재 위치(NavMesh에 붙인 지점)로 올린다.</summary>
    private bool EnsureAgentOnMesh()
    {
        if (agent.enabled && agent.isOnNavMesh) return true;
        if (!NavMesh.SamplePosition(transform.position, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas))
        {
            WarnFallbackOnce("현재 위치 근처에 NavMesh가 없음");
            return false;
        }
        agent.enabled = true;
        agent.speed = speed;
        agent.Warp(hit.position);
        return agent.isOnNavMesh;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 횡단보도 (Off-Mesh Link) — 대기 → 판단 → 건너기
    // ─────────────────────────────────────────────────────────────────────

    private void UpdateOffMeshLink()
    {
        if (crossPhase == CrossPhase.None)
        {
            OffMeshLinkData data = agent.currentOffMeshLinkData;
            crossing = LineCrossing.FromLinkOwner(data.owner);

            // 양방향 링크는 들어온 쪽이 start — 방어적으로 가까운 쪽을 출발점으로
            Vector3 a = data.startPos, b = data.endPos;
            if (FlatDist(transform.position, b) < FlatDist(transform.position, a)) { Vector3 t = a; a = b; b = t; }
            crossFrom = a;
            crossTo = b;

            if (crossing == null)
            {
                crossPhase = CrossPhase.Crossing; // 일반 링크 — 판단 없이 걷는다
                slotOffset = Vector3.zero;
            }
            else
            {
                crossPhase = CrossPhase.Waiting;
                waitStartTime = Time.time;
                slotOffset = crossing.EnterWait(this);
                if (!NavMesh.SamplePosition(crossFrom + slotOffset, out _, 0.3f, NavMesh.AllAreas)
                    || !NavMesh.SamplePosition(crossTo + slotOffset, out _, 0.3f, NavMesh.AllAreas))
                    slotOffset = Vector3.zero;
                if (verboseLog) Debug.Log($"{LOG_TAG} '{name}' 횡단보도 {crossing.name} 도착 — 대기", this);
            }
        }

        if (crossPhase == CrossPhase.Waiting)
        {
            // 대기 슬롯으로 천천히 옮겨 선다
            Vector3 slot = crossFrom + slotOffset;
            StepToward(slot, speed * slotMoveSpeedMul, false);

            float crossTime = FlatDist(crossFrom, crossTo) / Mathf.Max(0.1f, speed);
            if (crossing == null || crossing.IsClear(crossTime, out Vector3 lookAt))
            {
                EndCrossWait();
                crossPhase = CrossPhase.Crossing;
                if (verboseLog) Debug.Log($"{LOG_TAG} '{name}' 횡단 시작 ({Time.time - waitStartTime:F1}초 대기)", this);
            }
            else
            {
                FaceTowards(lookAt);

                // 정체: 차가 오래 막고 있으면 한 번 경로를 다시 계산해 본다(다른 횡단보도가 있으면 그쪽으로)
                if (crossing.IsJammed && reroutedFrom != crossing && Time.time - waitStartTime >= rerouteDelay)
                {
                    reroutedFrom = crossing;
                    if (verboseLog) Debug.Log($"{LOG_TAG} '{name}' {crossing.name} 정체 — 경로 재계산", this);
                    EndCrossWait();
                    crossPhase = CrossPhase.None;
                    agent.Warp(transform.position); // 링크에서 내려온다(경로 해제)
                    SetDestination(destination);
                }
                return;
            }
        }

        if (crossPhase == CrossPhase.Crossing)
        {
            Vector3 end = crossTo + slotOffset;
            if (StepToward(end, speed, true))
            {
                agent.CompleteOffMeshLink();
                // 슬롯만큼 옆으로 비켜 건넜으면 링크 끝점으로 순간이동하지 않도록 현재 위치로 다시 올린다
                if (slotOffset != Vector3.zero) agent.Warp(transform.position);
                crossPhase = CrossPhase.None;
                crossing = null;
                reroutedFrom = null;
                if (hasDestination)
                {
                    Vector3 d = destination;
                    hasDestination = false;
                    SetDestination(d);
                }
            }
        }
    }

    private void EndCrossWait()
    {
        if (crossing != null) crossing.LeaveWait(this);
    }

    /// <summary>링크 위 수동 이동(수평). 도착하면 true. 높이는 링크 끝점 높이 + baseOffset을 따른다.</summary>
    private bool StepToward(Vector3 point, float moveSpeed, bool face)
    {
        Vector3 goal = new Vector3(point.x, point.y + (agent != null ? agent.baseOffset : baseOffset), point.z);
        Vector3 before = transform.position;
        if (FlatDist(before, goal) <= 0.02f) { transform.position = goal; return true; }

        transform.position = Vector3.MoveTowards(before, goal, moveSpeed * Time.deltaTime);
        if (crossPhase == CrossPhase.Crossing && crossing != null)
        {
            var p = transform.position;
            float across = Mathf.Abs(crossing.transform.InverseTransformPoint(p).z);
            p.y = goal.y + crossing.walkwayRise * Mathf.Clamp01((crossing.halfSpan - across) / Mathf.Max(.1f, crossing.halfSpan - 1.2f));
            transform.position = p;
        }
        AddMoved(Vector3.Distance(before, transform.position));
        if (face) FaceTowards(goal);
        return FlatDist(transform.position, goal) <= 0.02f;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 직선 폴백 · 공용 유틸
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>기존 직선 이동(Worker.MoveToward와 동일). 도착했으면 true.</summary>
    private bool MoveStraight(Vector3 target)
    {
        if (requireCompletePath && useNavMesh)
        {
            if (agent != null && agent.enabled && agent.isOnNavMesh) agent.ResetPath();
            if (Time.time >= nextRouteRetry)
            {
                nextRouteRetry = Time.time + 1f;
                TryInitAgent();
                if (navReady) { hasDestination = false; SetDestination(target); }
            }
            return false;
        }
        // 직선 이동 중엔 에이전트를 꺼 둔다 — 켜 두면 transform 이동이 NavMesh 경계에 붙잡힌다.
        // 다음 SetDestination에서 EnsureAgentOnMesh가 다시 켜고 현재 위치로 올린다.
        if (agent != null && agent.enabled) agent.enabled = false;

        Vector3 flatTarget = new Vector3(target.x, transform.position.y, target.z);
        float dist = Vector3.Distance(transform.position, flatTarget);
        if (dist <= arriveRadius)
        {
            hasDestination = false;
            straightFallback = false;
            return true;
        }

        Vector3 before = transform.position;
        transform.position = Vector3.MoveTowards(before, flatTarget, speed * Time.deltaTime);
        AddMoved(Vector3.Distance(before, transform.position));

        FaceTowards(flatTarget);
        return false;
    }

    private void AddMoved(float d)
    {
        if (lastMoveFrame != Time.frameCount)
        {
            lastMoveFrame = Time.frameCount;
            movedThisFrame = 0f;
        }
        movedThisFrame += d;
    }

    /// <summary>수평면에서 지정 지점을 바라보도록 turnSpeed로 회전한다.</summary>
    public void FaceTowards(Vector3 worldPoint)
    {
        if (turnSpeed <= 0f) return;

        Vector3 dir = worldPoint - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;

        Quaternion want = Quaternion.LookRotation(dir.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turnSpeed * Time.deltaTime);
    }

    private static float FlatDist(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private void WarnFallbackOnce(string reason)
    {
        if (warnedFallback) return;
        warnedFallback = true;
        Debug.LogWarning($"{LOG_TAG} '{name}': {reason} — 직선 이동으로 폴백합니다(이 작업자 경고 1회).", this);
    }

    private void OnDrawGizmosSelected()
    {
        if (agent == null || !agent.hasPath) return;
        Vector3[] corners = agent.path.corners;
        Gizmos.color = Color.green;
        for (int i = 1; i < corners.Length; i++)
            Gizmos.DrawLine(corners[i - 1] + Vector3.up * 0.1f, corners[i] + Vector3.up * 0.1f);
    }
}
