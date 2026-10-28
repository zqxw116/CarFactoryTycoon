using UnityEngine;

/// <summary>
/// 재고 보급 전담 작업자 (GAME_DESIGN_PROGRESS.md 9-15). 창고에서 부품을 싣고 공정의 재고 파일(PartStack)을
/// 채운 뒤 반드시 창고로 돌아온다.
///
///   ToWarehouse → IdleAtWarehouse → Loading → ToStack → Unloading → Returning → IdleAtWarehouse …
///
/// - <b>공정→공정 직행 없음</b>: 한 번에 한 부품 종류만 운반하므로 하역 후에는 무조건 창고로 복귀한다(9-15 ⑵).
/// - 다음 대상은 창고에서 SupplyManager.TryReserveNext로 고르고 그 즉시 단독 예약된다.
///   예약 해제 경로 = 하역 완료 / 대상 무효(가는 중 공정 비활성·파일 소실) / 이 작업자 비활성·파괴.
/// - "조금이라도 비면" 채우러 간다(Count &lt; Capacity) — 0을 기다리면 이미 늦다(9-15 ⑶).
/// - 컨디션·피로 없음(9-15 ⑻-4). 창고 재고는 무한(⑻-2).
///
/// ★ Worker를 상속하지 않는 이유
///   WorkerStatusUI가 RequireComponent(Worker)이고 InputManager가 GetComponentInParent&lt;Worker&gt;로 클릭을
///   처리한다 — 상속하면 보급 작업자가 체결 작업자로 오인되어 클릭 시 휴식/작업가속이 걸린다.
///   그래서 이동·회전·애니메이터 구동(Worker의 private 메서드들)을 이 클래스에 그대로 복제했다.
///
/// 경로: NavMover의 NavMesh 길찾기로 설비를 돌아가며, 움직이는 차량에는 양보·측면 우회한다.
///   경로가 없으면 재시도하며 차량 사이에 끼면 안전한 탈출 방향을 찾는다.
///   moveSpeed·arriveRadius·turnSpeed는 이 컴포넌트 값이 원본이며 매 프레임 NavMover로 넘긴다.
/// </summary>
public class SupplyWorker : MonoBehaviour
{
    public enum SupplyState
    {
        ToWarehouse,      // 시작 위치 → 창고 적재 지점 (최초 1회)
        IdleAtWarehouse,  // 창고에서 다음 대상을 고르는 중 (채울 곳이 없으면 대기)
        Loading,          // 창고에서 부품 싣는 중
        ToStack,          // 부품을 들고 대상 재고 파일로 이동 중
        Unloading,        // 재고 파일에 부품 내려놓는 중
        Returning,        // 빈손으로 창고 복귀 중
    }

    [Header("매니저")]
    [Tooltip("비어 있으면 씬의 SupplyManager를 자동으로 찾는다(없으면 경고 1회 후 대기).")]
    [SerializeField] private SupplyManager manager;

    [Header("프로토타입 근무")]
    [Tooltip("프로토타입 근무 상태 API를 사용한다. 구형 작업자에서는 끈다.")]
    public bool prototypeMode;

    private bool workersOnDuty = true;
    private bool dutyPaused;
    private bool resumePending;
    private SupplyState pausedState;
    private float pausedStateTimer;
    private PartStack resumeStack;

    /// <summary>현재 근무 중인지. 기본값은 근무 중이다.</summary>
    public bool WorkersOnDuty => workersOnDuty;

    [Header("이동")]
    [Tooltip("기본 이동 속도(m/초). 실제 속도는 EffectiveMoveSpeed(개별 배율 × 전역 업그레이드 배율 적용)를 쓴다.")]
    public float moveSpeed = 2.0f;

    [Tooltip("개별 이동속도 배율 (이 작업자만의 업그레이드).")]
    public float moveSpeedMul = 1f;

    [Tooltip("목표 지점에 이 거리(m) 이내로 들어오면 도착으로 판정한다.")]
    public float arriveRadius = 0.25f;

    [Tooltip("이동 방향으로 몸을 돌리는 속도(도/초). 0이면 회전하지 않는다.")]
    public float turnSpeed = 540f;

    [Header("운반")]
    [Tooltip("기본 1회 운반 개수. 9-15 ⑻-3 확정값 6 = 파일 용량 12의 절반 → 가득 채우려면 2회 왕복(의도된 압박)." +
        " 실제 개수는 EffectiveCarry(개별 배율 × 전역 업그레이드 배율 적용)를 쓴다.")]
    public int carryAmount = 6;

    [Tooltip("개별 운반 개수 배율 (이 작업자만의 업그레이드).")]
    public float carryMul = 1f;

    [Tooltip("창고에서 싣는 데 걸리는 시간(초).")]
    public float loadDuration = 0.5f;

    [Tooltip("재고 파일에 내려놓는 데 걸리는 시간(초).")]
    public float unloadDuration = 0.5f;

    [Tooltip("창고에서 채울 곳이 없을 때 다시 찾아보는 간격(초). 매 프레임 전체 파일을 훑지 않기 위함.")]
    public float idleRetryInterval = 0.25f;

    [Header("운반 표시")]
    [Tooltip("운반 중 손에 보이는 상자. 비어 있으면 handPos(없으면 몸 앞)에 기본 큐브를 1개 만들어 쓴다." +
        " 작업자당 1개를 시작 시 미리 만들고 SetActive로만 켜고 끈다(왕복마다 생성하지 않음).")]
    public GameObject carryVisualPrefab;

    [Tooltip("상자를 붙일 손 Transform. 비어 있으면 몸 앞(handForward/handHeight)에 붙인다.")]
    public Transform handPos;

    [Tooltip("handPos 미설정 시 상자 위치 — 몸 중심에서 앞쪽 거리(m).")]
    public float handForward = 0.6f;

    [Tooltip("handPos 미설정 시 상자 위치 — 피벗 기준 높이(m).")]
    public float handHeight = 0.15f;

    [Tooltip("손(또는 몸 앞) 기준 상자 위치 미세 조정(로컬, m).")]
    public Vector3 carryOffset = Vector3.zero;

    [Tooltip("기본 큐브 상자의 한 변 길이(m, 월드). carryVisualPrefab을 쓰면 무시된다.")]
    public float carryBoxSize = 0.4f;

    [Header("애니메이션")]
    [Tooltip("걷기 애니메이션을 재생할 Animator. 비어 있으면 자식에서 자동으로 찾는다.")]
    [SerializeField] private Animator animator;

    [Tooltip("이동 속력(m/초)을 넣을 float 파라미터 이름. StarterAssets 블렌드 트리는 0=대기 / 2=걷기 / 6=달리기" +
        " 임계값이라 실제 속력을 그대로 넣으면 된다(기본 2.0 m/s = 걷기).")]
    public string speedParam = "Speed";

    [Tooltip("애니메이션 재생 배속 파라미터 이름. StarterAssets 컨트롤러는 기본값 0이라 매 프레임 1을 넣는다.")]
    public string motionSpeedParam = "MotionSpeed";

    [Tooltip("접지 여부 bool 파라미터 이름. 기본값 false면 낙하 상태로 빠지므로 매 프레임 true를 넣는다.")]
    public string groundedParam = "Grounded";

    [Tooltip("speedParam에 넣는 속력 배율 (1 = 실제 m/초).")]
    public float animSpeedScale = 1f;

    [Header("현재 상태 (디버그)")]
    public SupplyState currentState = SupplyState.ToWarehouse;

    [Tooltip("상태 전이 로그를 남긴다(전이 시점에만 — 매 프레임 로그 없음).")]
    public bool verboseLog = true;

    [Header("기즈모")]
    public bool drawGizmo = true;

    private const string LOG_TAG = "[Supply]";
    private const string CarryVisualName = "SupplyCarryBox";

    private SupplyManager.SupplyTarget target;   // 현재 예약한 대상 (예약 중일 때만 유효)
    private bool hasReservation = false;
    private int carrying = 0;                    // 들고 있는 부품 수
    private PartType carryingType = PartType.None;
    private float stateTimer = 0f;               // Loading/Unloading 경과, Idle 재탐색 대기
    private bool warnedNoManager = false;

    private UpgradeManager upgrade;              // Start에서 캐시 (OnDisable/OnDestroy에서 Instance 접근 금지)
    private GameObject carryVisual;
    private NavMover mover;                      // 이동 담당 (없으면 Awake에서 붙인다)

    // 애니메이터 파라미터 캐시 (Worker와 동일)
    private int speedHash, motionSpeedHash, groundedHash;
    private bool hasSpeedParam, hasMotionSpeedParam, hasGroundedParam;

    /// <summary>실제 이동 속도 = 기본 × 개별 배율 × 전역 업그레이드 배율.</summary>
    public float EffectiveMoveSpeed =>
        moveSpeed * moveSpeedMul * (upgrade != null ? upgrade.supplyMoveSpeedMultiplier : 1f);

    /// <summary>실제 1회 운반 개수 = 기본 × 개별 배율 × 전역 업그레이드 배율 (최소 1).</summary>
    public int EffectiveCarry =>
        Mathf.Max(1, Mathf.RoundToInt(carryAmount * carryMul * (upgrade != null ? upgrade.supplyCarryMultiplier : 1f)));

    /// <summary>지금 들고 있는 부품 수 — UI용.</summary>
    public int Carrying => carrying;

    /// <summary>지금 향하는(예약한) 재고 파일. 없으면 null — UI용.</summary>
    public PartStack TargetStack => hasReservation ? target.stack : null;

    /// <summary>근무일 경계에서 호출한다. 퇴근 중 화물은 보존하고 파일 예약만 풀어 준다.</summary>
    public void SetDutyStatus(bool onDuty)
    {
        if (workersOnDuty == onDuty) return;
        workersOnDuty = onDuty;
        if (!onDuty)
        {
            dutyPaused = true;
            resumePending = false;
            pausedState = currentState;
            pausedStateTimer = stateTimer;
            resumeStack = carrying > 0 && hasReservation ? target.stack : null;
            ReleaseReservation();
            if (mover != null) mover.Stop();
            return;
        }

        resumePending = dutyPaused;
    }

    private void Awake()
    {
        // NavMover.Start가 에이전트를 준비하므로 Start보다 먼저 붙여 둔다(씬에 미리 붙여 둔 값이 있으면 그대로 사용)
        mover = GetComponent<NavMover>();
        if (mover == null) mover = gameObject.AddComponent<NavMover>();
    }

    private void Start()
    {
        upgrade = UpgradeManager.Instance;
        if (manager == null) manager = SupplyManager.Instance;
        SetupAnimator();
        BuildCarryVisual();
        SetState(SupplyState.ToWarehouse);
    }

    private void OnDisable()
    {
        // 해제 경로 ③: 비활성화 — 예약을 쥔 채 사라지면 그 파일은 영원히 아무도 채우지 않는다.
        ReleaseReservation();
        carrying = 0;
        carryingType = PartType.None;
        if (carryVisual != null) carryVisual.SetActive(false);
        currentState = SupplyState.ToWarehouse; // 다시 켜지면 창고부터 시작
    }

    private void OnDestroy()
    {
        ReleaseReservation(); // OnDisable이 먼저 불리지만 파괴 경로도 명시적으로 막아 둔다
    }

    private void Update()
    {
        // 이동 파라미터는 이 컴포넌트가 원본 — 업그레이드로 속도가 바뀌어도 즉시 반영
        mover.Speed = EffectiveMoveSpeed;
        mover.arriveRadius = arriveRadius;
        mover.turnSpeed = turnSpeed;

        if (manager == null)
        {
            if (!warnedNoManager)
            {
                warnedNoManager = true;
                Debug.LogWarning($"{LOG_TAG} '{name}': 씬에 SupplyManager가 없어 보급 작업자가 대기합니다.", this);
            }
            UpdateAnimator();
            return;
        }

        if (!WorkersOnDuty)
        {
            mover.Stop();
            UpdateAnimator();
            return;
        }

        if (resumePending) ResumeAfterDuty();

        switch (currentState)
        {
            case SupplyState.ToWarehouse:
            case SupplyState.Returning:
                if (MoveToward(manager.LoadPoint)) SetState(SupplyState.IdleAtWarehouse);
                break;

            case SupplyState.IdleAtWarehouse:
                MoveToward(manager.LoadPoint); // 밀려났으면 적재 지점으로 복귀
                stateTimer -= Time.deltaTime;
                if (stateTimer > 0f) break;
                stateTimer = idleRetryInterval;
                if (manager.TryReserveNext(this, out SupplyManager.SupplyTarget next))
                {
                    target = next;
                    hasReservation = true;
                    SetState(SupplyState.Loading);
                }
                break;

            case SupplyState.Loading:
                if (!CheckTargetAlive()) break;
                stateTimer += Time.deltaTime;
                if (stateTimer < loadDuration) break;
                carrying = EffectiveCarry;
                carryingType = target.stack.partType;
                if (carryVisual != null) carryVisual.SetActive(true);
                SetState(SupplyState.ToStack);
                break;

            case SupplyState.ToStack:
                if (!CheckTargetAlive()) break;
                if (MoveToward(manager.GetApproachPoint(target.stack))) SetState(SupplyState.Unloading);
                break;

            case SupplyState.Unloading:
                if (!CheckTargetAlive()) break;
                FaceTowards(target.stack.transform.position);
                stateTimer += Time.deltaTime;
                if (stateTimer < unloadDuration) break;
                Unload();
                break;
        }

        UpdateAnimator();
    }

    private void ResumeAfterDuty()
    {
        resumePending = false;
        dutyPaused = false;

        if (carrying > 0)
        {
            if (resumeStack != null && manager.TryReserveStack(this, resumeStack, out SupplyManager.SupplyTarget resumedTarget))
            {
                target = resumedTarget;
                hasReservation = true;
                currentState = pausedState;
                stateTimer = pausedStateTimer;
            }
            else
            {
                // 다른 작업자가 이미 채웠거나 대상이 사라졌다. 화물은 무한 창고로 반납한 것으로
                // 처리해 중복 하역을 막고 창고 복귀 후 새 예약을 받는다.
                carrying = 0;
                carryingType = PartType.None;
                if (carryVisual != null) carryVisual.SetActive(false);
                SetState(SupplyState.Returning);
            }
        }
        else if (pausedState == SupplyState.Loading)
        {
            // 퇴근 중 예약을 놓았으므로, 창고에서 다음 프레임 새 대상을 고른다.
            SetState(SupplyState.IdleAtWarehouse);
        }
        else
        {
            currentState = pausedState;
            stateTimer = pausedStateTimer;
        }

        resumeStack = null;
    }

    /// <summary>
    /// 하역: PartStack.Add가 용량을 넘는 분을 버리고 실제 추가량을 돌려준다
    /// (가는 동안 유저가 클릭으로 채웠으면 일부가 버려진다 — 창고 재고 무한이라 손실 없음).
    /// </summary>
    private void Unload()
    {
        PartStack stack = target.stack;
        int added = stack.Add(carrying);
        if (verboseLog)
            Debug.Log($"{LOG_TAG} '{name}' 하역 {stack.name} +{added}/{carrying}" +
                $" → {stack.Count}/{stack.Capacity}", this);

        carrying = 0;
        carryingType = PartType.None;
        if (carryVisual != null) carryVisual.SetActive(false);
        ReleaseReservation(); // 해제 경로 ①: 하역 완료
        SetState(SupplyState.Returning);
    }

    /// <summary>
    /// 해제 경로 ②: 대상 무효. 가는 중 공정이 꺼지거나 파일이 사라지면 예약을 풀고 창고로 돌아간다
    /// (들고 있던 부품은 창고로 반납 — 다른 공정에 맞지 않으므로 직행하지 않는다).
    /// </summary>
    private bool CheckTargetAlive()
    {
        if (hasReservation && SupplyManager.IsTargetAlive(target)) return true;

        if (verboseLog)
            Debug.Log($"{LOG_TAG} '{name}' 대상 무효({currentState}) — 예약 해제 후 창고 복귀", this);
        ReleaseReservation();
        carrying = 0;
        carryingType = PartType.None;
        if (carryVisual != null) carryVisual.SetActive(false);
        SetState(SupplyState.Returning);
        return false;
    }

    private void ReleaseReservation()
    {
        if (!hasReservation) return;
        hasReservation = false;
        if (manager != null) manager.Release(target.stack, this); // 파괴 중인 매니저는 Unity null로 걸러진다
        target = default;
    }

    private void SetState(SupplyState next)
    {
        if (verboseLog && next != currentState)
        {
            string extra = hasReservation && target.stack != null
                ? $" (대상 {target.stack.name} {target.stack.Count}/{target.stack.Capacity}, 운반 {carrying} {carryingType})"
                : "";
            Debug.Log($"{LOG_TAG} '{name}' {currentState} → {next}{extra}", this);
        }
        currentState = next;
        stateTimer = 0f;
    }

    /// <summary>
    /// 운반 표시 상자를 1개만 미리 만든다. 기본 큐브는 콜라이더를 제거한다 —
    /// 남겨 두면 클릭 레이캐스트를 가로챈다(CLAUDE.md: 월드 공간 프리미티브 콜라이더 함정).
    /// </summary>
    private void BuildCarryVisual()
    {
        Transform parent = handPos != null ? handPos : transform;

        // 실행 중인 작업자를 복제해 고용하면(SupplyManager.Hire) 런타임에 만든 상자까지 함께 복제된다
        // → 이미 있으면 그대로 재사용해 상자가 2개가 되지 않게 한다.
        Transform existing = parent.Find(CarryVisualName);
        if (existing != null)
        {
            carryVisual = existing.gameObject;
            carryVisual.SetActive(false);
            return;
        }

        if (carryVisualPrefab != null)
        {
            carryVisual = Instantiate(carryVisualPrefab, parent);
        }
        else
        {
            carryVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            carryVisual.transform.SetParent(parent, false);
            Collider col = carryVisual.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // 부모 스케일과 무관하게 월드 기준 한 변 carryBoxSize가 되도록 보정한다.
            Vector3 ls = parent.lossyScale;
            carryVisual.transform.localScale = new Vector3(
                carryBoxSize / Mathf.Max(0.0001f, Mathf.Abs(ls.x)),
                carryBoxSize / Mathf.Max(0.0001f, Mathf.Abs(ls.y)),
                carryBoxSize / Mathf.Max(0.0001f, Mathf.Abs(ls.z)));
        }

        carryVisual.name = CarryVisualName;
        Vector3 basePos = handPos != null ? Vector3.zero : new Vector3(0f, handHeight, handForward);
        carryVisual.transform.localPosition = basePos + carryOffset;
        carryVisual.transform.localRotation = Quaternion.identity;
        carryVisual.SetActive(false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 이동·애니메이션 — 애니메이터 구동은 Worker의 private 구현을 복제 (상속 금지 사유는 클래스 주석 참조),
    // 이동·회전은 공용 NavMover에 맡긴다
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Animator와 파라미터 존재 여부를 시작 시 1회만 확인해 캐시한다(경고도 1회만).</summary>
    private void SetupAnimator()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            animator = null;
            Debug.LogWarning($"{LOG_TAG} '{name}': Animator(또는 Animator Controller)가 없어" +
                " 걷기 애니메이션 없이 이동합니다.", this);
            return;
        }

        speedHash = Animator.StringToHash(speedParam);
        motionSpeedHash = Animator.StringToHash(motionSpeedParam);
        groundedHash = Animator.StringToHash(groundedParam);

        hasSpeedParam = HasParam(speedParam, AnimatorControllerParameterType.Float);
        hasMotionSpeedParam = HasParam(motionSpeedParam, AnimatorControllerParameterType.Float);
        hasGroundedParam = HasParam(groundedParam, AnimatorControllerParameterType.Bool);

        if (!hasSpeedParam)
            Debug.LogWarning($"{LOG_TAG} '{name}': Animator에 float 파라미터 '{speedParam}'가 없어" +
                " 걷기 애니메이션이 재생되지 않습니다.", this);
    }

    private bool HasParam(string paramName, AnimatorControllerParameterType type)
    {
        if (string.IsNullOrEmpty(paramName)) return false;
        AnimatorControllerParameter[] ps = animator.parameters;
        for (int i = 0; i < ps.Length; i++)
            if (ps[i].type == type && ps[i].name == paramName) return true;
        return false;
    }

    /// <summary>이번 프레임 실제 이동 속력(NavMover — 에이전트 속도 또는 NavMesh 이동량)으로 걷기 애니메이션을 구동한다.</summary>
    private void UpdateAnimator()
    {
        if (animator == null) return;

        float speed = mover.CurrentSpeed;

        if (hasSpeedParam) animator.SetFloat(speedHash, speed * animSpeedScale);
        if (hasMotionSpeedParam) animator.SetFloat(motionSpeedHash, 1f); // 0이면 애니메이션이 멈춘다
        if (hasGroundedParam) animator.SetBool(groundedHash, true);      // 늘 바닥 위
    }

    /// <summary>목표로 이동한다(NavMesh 길찾기·차량 예측 회피 포함, NavMover 참조). 도착했으면 true.</summary>
    private bool MoveToward(Vector3 target) => mover.MoveTo(target);

    /// <summary>수평면에서 지정 지점을 바라보도록 turnSpeed로 회전한다.</summary>
    private void FaceTowards(Vector3 worldPoint) => mover.FaceTowards(worldPoint);

    private void OnDrawGizmos()
    {
        if (!drawGizmo) return;

        if (hasReservation && target.stack != null)
        {
            Gizmos.color = new Color(1f, 0.5f, 0.1f, 1f);
            Gizmos.DrawLine(transform.position + Vector3.up, target.stack.transform.position + Vector3.up);
        }

#if UNITY_EDITOR
        UnityEditor.Handles.color = new Color(0.4f, 1f, 0.4f, 1f);
        string label = $"보급 {currentState}";
        if (carrying > 0) label += $" / {carryingType} x{carrying}";
        UnityEditor.Handles.Label(transform.position + Vector3.up * 2.2f, label);
#endif
    }
}
