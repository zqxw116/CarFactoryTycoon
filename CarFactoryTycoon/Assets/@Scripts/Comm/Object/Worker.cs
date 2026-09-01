using UnityEngine;

/// <summary>
/// 공정에 고정 배치되는 사람 작업자. 부품을 파일에서 집어 차량 부착점까지 옮겨 체결한다.
///
/// 로봇팔(StationController)과의 차이:
/// - 로봇팔은 IK 팔끝-파츠 거리로 체결 게이트를 만들지만, 사람은 "작업자가 부착점 옆에 도착했는가"로 게이트를 만든다.
/// - 체결은 AssemblyPart의 공개 API(BeginWorldAssembly / AddWork / SetAssembled)만 사용한다.
///   AssemblyPart가 LateUpdate에서 pile→부착점 월드 베지어를 자동 추종하므로 부품 이동 연출은 그대로 따라온다.
///   → 로봇팔 코드를 전혀 건드리지 않으므로 사람 공정과 로봇 공정이 같은 라인에 병존할 수 있다
///     (공정 단위로 사람↔로봇을 교체하는 것이 이 게임의 핵심 메커니즘).
///
/// 컨디션: 100%에서 시작해 차량 1대 담당 작업을 끝낼 때마다 감소하고, 0이 되면 강제 휴식에 들어간다.
/// 유저가 유휴 시간에 미리 쉬게 하면(선제 휴식) 더 짧게 쉬고 라인 정지를 예방할 수 있다.
/// </summary>
public class Worker : MonoBehaviour
{
    public enum WorkerState
    {
        GoingToWork,   // 출근 중 (입구 → 자기 자리)
        Idle,          // 자기 자리에서 대기 (작업할 차량 없음)
        Fetching,      // 부품 파일로 이동 중 (부품 집기)
        MovingToPart,  // 부품을 들고 차량 부착점으로 이동 중
        Working,       // 체결 작업 중
        Resting,       // 휴식 중
        OffWork,       // 퇴근
    }

    [Header("이동")]
    [Tooltip("작업자 이동 속도(m/초). 선행 트리거로 차량보다 먼저 출발하므로 굳이 빠를 필요가 없고," +
        " 빠르면 목표점(차량 부착점 기준)이 조금만 흔들려도 크게 튀어 덜덜거림이 눈에 띈다.")]
    public float moveSpeed = 1.7f;

    [Tooltip("목표 지점에 이 거리(m) 이내로 들어오면 도착으로 판정한다.")]
    public float arriveRadius = 0.25f;

    [Tooltip("이동 방향으로 몸을 돌리는 속도(도/초). 0이면 회전하지 않는다.")]
    public float turnSpeed = 540f;

    [Header("작업")]
    [Tooltip("1초당 처리하는 작업량(work/초). 로봇팔(StationConfig.assembleSpeed)보다 느리게 두어" +
        " 자동화의 처리량 우위를 만든다.")]
    public float assembleSpeed = 4f;

    [Tooltip("차량이 게이트에 도킹했을 때 부착점이 있을 자리를 기준으로, 라인 진행방향(z) 옆으로" +
        " 이만큼(m) 떨어진 지점에 미리 가서 대기한다. 좌/우는 자기 자리(homePos)가 원래 있던 쪽으로" +
        " 자동 결정된다. 차량 몸통과 겹치지 않을 만큼 커야 한다(과거 workStandDistance+bodyClearance" +
        " 두 값이 하던 역할을 이 값 하나가 겸한다 — 목표가 더 이상 매 프레임 움직이지 않으므로" +
        " '전후로 물러섰다가 측면으로 밀어내는' 2단 보정이 필요 없어졌다).")]
    public float standOffset = 1.1f;

    [Header("부품 들고 가기")]
    [Tooltip("부품을 든 손 위치. 실제 캐릭터 모델을 쓸 때는 손 본(Transform)을 그대로 꽂으면 된다 —" +
        " 부품이 이 트랜스폼의 위치와 회전을 둘 다 따라간다. 미설정 시 handForward/handHeight로 자동 계산.")]
    public Transform handPos;

    [Tooltip("handPos 미설정 시 쓰는 자동 손 위치 — 몸 중심에서 앞쪽 거리(m)." +
        " 기본 Capsule/BoxCollider 몸통은 반지름 0.5라 0.45는 가슴 '안쪽'이었다(부품이 몸에 파묻힘)." +
        " 0.6 = 몸 표면(0.5) 바깥 0.1m — 두 손으로 상자를 몸 앞에 안은 위치.")]
    public float handForward = 0.6f;

    [Tooltip("handPos 미설정 시 쓰는 자동 손 위치 — 피벗 기준 높이(m)." +
        " 가이드 셋업(빈 GameObject + 기본 Capsule 자식 / BoxCollider size(1,2,1) center(0,0,0))에서" +
        " 몸통은 피벗 기준 -1(발바닥) ~ +1(정수리)이다 → 기존 기본값 1.0은 '정확히 머리 끝'이라" +
        " 부품이 머리 위에 떠 보였다. 성인이 두 손으로 상자를 드는 높이는 신장의 약 0.57배이므로" +
        " 2m 몸에서 발바닥 위 약 1.15m = 피벗 기준 +0.15 (허리~명치).")]
    public float handHeight = 0.15f;

    [Tooltip("손 기준 부품 위치 미세 조정(손 로컬, m). 부품 피벗이 중심이 아니거나" +
        " 큐브가 손/몸에 파묻힐 때 이 값으로 밀어낸다.")]
    public Vector3 carryOffset = Vector3.zero;

    [Tooltip("손 기준 부품 회전 미세 조정(손 로컬, 도). 손 본에 붙였을 때 부품이 뒤집혀 보이면 여기서 돌린다.")]
    public Vector3 carryEuler = Vector3.zero;

    [Header("애니메이션")]
    [Tooltip("걷기 애니메이션을 재생할 Animator. 비어 있으면 자식에서 자동으로 찾는다.")]
    [SerializeField] private Animator animator;

    [Tooltip("이동 속력(m/초)을 넣을 float 파라미터 이름. StarterAssetsThirdPerson 컨트롤러의" +
        " 블렌드 트리는 0=대기 / 2=걷기 / 6=달리기 임계값이라 실제 속력을 그대로 넣으면 된다." +
        " 비우거나 컨트롤러에 없는 이름이면 조용히 무시된다.")]
    public string speedParam = "Speed";

    [Tooltip("애니메이션 재생 배속 파라미터(블렌드 상태의 Speed Multiplier) 이름." +
        " StarterAssets 컨트롤러는 이 값이 0이면 애니메이션이 아예 '정지'하므로 항상 1을 넣는다." +
        " 쓰지 않는 컨트롤러면 비워 두면 된다.")]
    public string motionSpeedParam = "MotionSpeed";

    [Tooltip("접지 여부 bool 파라미터 이름. StarterAssets 컨트롤러는 이게 false면 낙하 상태로 빠져" +
        " 걷기가 나오지 않는다 — 작업자는 늘 바닥에 있으므로 항상 true로 유지한다.")]
    public string groundedParam = "Grounded";

    [Tooltip("speedParam에 넣는 속력 배율. 컨트롤러의 블렌드 임계값이 다를 때 맞춘다(1 = 실제 m/초).")]
    public float animSpeedScale = 1f;

    [Header("컨디션")]
    [Tooltip("현재 컨디션(0~100). 0이 되면 강제 휴식.")]
    [Range(0f, 100f)] public float condition = 100f;

    [Tooltip("차량 1대분 담당 작업을 끝낼 때마다 소모되는 컨디션." +
        " 담당 부품을 몇 개 붙였든 상관없이 차량당 1회만 소모된다(공정이 차량을 방출하는 시점)." +
        " 기본값 기준 100 / 10 = 쉬지 않고 10대를 작업하면 1회 휴식.")]
    public float conditionCostPerCar = 10f;

    [Tooltip("유저가 유휴 중에 미리 쉬게 했을 때의 휴식 시간(초)." +
        " 강제 휴식보다 짧아야 선제 관리에 보상이 생긴다.")]
    public float preemptiveRestDuration = 3f;

    [Tooltip("컨디션 0으로 강제 휴식에 들어갔을 때의 휴식 시간(초)." +
        " 차량을 내보낸 직후 발동하므로 다음 차량이 게이트에 서서 기다린다 = 라인 정지." +
        " 선제 휴식과의 '비율'이 레버다 — 3.3배(3초 : 10초)라야 미리 쉬게 하는 개입에 값이 생긴다." +
        " 체결 작업자는 공정당 1명 고정이라 이 정지를 대신 받아줄 인원이 없다.")]
    public float forcedRestDuration = 10f;

    [Tooltip("컨디션이 0일 때 붙인 부품의 품질. 컨디션 100%면 품질 1.0, 낮아질수록 이 값까지 떨어진다." +
        " 체결은 100%여도 품질이 낮으면 검사 공정(방수/압력/속도)에서 불량으로 걸린다 —" +
        " 로봇팔은 항상 1.0이라, 자동화가 진행될수록 검사 통과율이 올라간다.")]
    [Range(0f, 1f)] public float qualityAtZeroCondition = 0.5f;

    [Header("현재 상태 (디버그)")]
    public WorkerState currentState = WorkerState.Idle;

    /// <summary>
    /// 담당 공정의 재고가 비어 일을 받지 못하는 상태(= 라인 정지 원인). WorkerStation이 매 프레임 갱신하고
    /// WorkerStatusUI가 "부품없음" 표시에 사용한다.
    /// </summary>
    [HideInInspector] public bool stockBlocked = false;

    /// <summary>
    /// 담당 차량이 게이트에 실제로 도킹(정지)했는지. WorkerStation이 매 프레임 갱신한다.
    /// 대기 지점(정적으로 미리 계산된 지점)에 먼저 도착해도, 이 플래그가 서기 전까지는 체결(AddWork)을
    /// 시작하지 않는다 — "체결은 차량이 실제 도킹한 뒤 시작한다"는 설계 규칙의 게이트.
    /// </summary>
    [HideInInspector] public bool carDocked = false;

    [Header("기즈모")]
    public bool drawGizmo = true;

    // 자기 자리(대기 위치). Start에서 현재 위치로 잡고, WorkerStation이 지정할 수도 있다.
    private Vector3 homePos;
    private bool homeSet = false;

    private AssemblyPart targetPart;     // 담당 부품 (차량에 붙일 것)
    private Vector3 pileWorldPos;        // 부품을 집는 파일 위치
    private Quaternion pileWorldRot = Quaternion.identity; // 파일에 놓여 있던 부품의 월드 회전
    private float restTimer = 0f;

    // 애니메이터 파라미터 캐시 (없으면 조용히 스킵 — 매 프레임 로그를 남기지 않는다)
    private int speedHash, motionSpeedHash, groundedHash;
    private bool hasSpeedParam, hasMotionSpeedParam, hasGroundedParam;

    // 이번 프레임 MoveToward가 실제로 이동시킨 거리(m). 프레임 간 위치 델타 대신 이 값을 쓰면
    // 순간이동·회전 같은 외부 변화에 속도가 튀지 않는다.
    private float movedThisFrame = 0f;

    // 작업 대기 지점(정적 월드 좌표) — AssignWork 시점에 도킹 예정 위치 기준으로 1회만 계산해 캐시한다.
    // 차량이 실제로 움직여도 재계산하지 않는다(그래서 떨리지 않는다).
    private Vector3 standWorldPos;
    private bool standValid = false;

    /// <summary>부품을 든 손의 월드 위치. handPos에 손 본을 꽂으면 본의 위치를 그대로 쓴다.</summary>
    private Vector3 HandWorldPos => handPos != null
        ? handPos.position
        : transform.position + transform.forward * handForward + Vector3.up * handHeight;

    /// <summary>손의 월드 회전. handPos에 손 본을 꽂으면 본의 회전을 그대로 쓴다(미설정 시 몸통 회전).</summary>
    private Quaternion HandWorldRot => handPos != null ? handPos.rotation : transform.rotation;

    /// <summary>손에 들린 부품이 있어야 할 월드 회전 (손 회전 × 인스펙터 미세 조정).</summary>
    private Quaternion CarryWorldRot => HandWorldRot * Quaternion.Euler(carryEuler);

    /// <summary>손에 들린 부품이 있어야 할 월드 위치 (손 위치 + 손 로컬 미세 조정).</summary>
    private Vector3 CarryWorldPos => HandWorldPos + HandWorldRot * carryOffset;

    public float ConditionFill => Mathf.Clamp01(condition / 100f);
    public bool IsResting => currentState == WorkerState.Resting;

    /// <summary>새 작업을 받을 수 있는 상태인지 (자기 자리에서 대기 중 + 컨디션 남음).</summary>
    public bool IsAvailable => currentState == WorkerState.Idle && condition > 0f;

    /// <summary>작업(이동·체결)을 수행하는 중인지.</summary>
    public bool IsBusy => currentState == WorkerState.Fetching
                       || currentState == WorkerState.MovingToPart
                       || currentState == WorkerState.Working;

    /// <summary>유저가 선제 휴식을 시킬 수 있는 상태인지 (유휴 + 컨디션이 깎여 있음).</summary>
    public bool CanRestNow => currentState == WorkerState.Idle && condition < 100f;

    private void Start()
    {
        if (!homeSet) SetHome(transform.position);
        SetupAnimator();
    }

    /// <summary>Animator와 파라미터 존재 여부를 시작 시 1회만 확인해 캐시한다(경고도 1회만).</summary>
    private void SetupAnimator()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            Debug.LogWarning($"[Worker] '{name}': Animator(또는 Animator Controller)가 없어" +
                " 걷기 애니메이션 없이 이동합니다.");
            return;
        }

        speedHash = Animator.StringToHash(speedParam);
        motionSpeedHash = Animator.StringToHash(motionSpeedParam);
        groundedHash = Animator.StringToHash(groundedParam);

        hasSpeedParam = HasParam(speedParam, AnimatorControllerParameterType.Float);
        hasMotionSpeedParam = HasParam(motionSpeedParam, AnimatorControllerParameterType.Float);
        hasGroundedParam = HasParam(groundedParam, AnimatorControllerParameterType.Bool);

        if (!hasSpeedParam)
            Debug.LogWarning($"[Worker] '{name}': Animator에 float 파라미터 '{speedParam}'가 없어" +
                " 걷기 애니메이션이 재생되지 않습니다(컨트롤러의 파라미터 이름을 인스펙터에서 맞춰 주세요).");
    }

    private bool HasParam(string paramName, AnimatorControllerParameterType type)
    {
        if (string.IsNullOrEmpty(paramName)) return false;
        AnimatorControllerParameter[] ps = animator.parameters;
        for (int i = 0; i < ps.Length; i++)
            if (ps[i].type == type && ps[i].name == paramName) return true;
        return false;
    }

    /// <summary>자기 자리(대기 위치)를 지정한다.</summary>
    public void SetHome(Vector3 pos)
    {
        homePos = pos;
        homeSet = true;
    }

    /// <summary>
    /// 담당 작업을 배정한다. 작업자가 파일로 이동해 부품을 집고, 차량 부착점 옆(정적 대기 지점)으로 옮겨 체결한다.
    /// <paramref name="pilePos"/>/<paramref name="pileWorldRot"/> = 부품이 등장해 대기할 <b>월드</b> 위치·회전
    /// (재고 파일의 맨 위 슬롯에 쌓여 있던 그 자세를 그대로 넘기면 된다).
    /// <paramref name="dockPos"/>/<paramref name="dockRot"/> = 차량이 게이트에 도킹했을 때 가질 예정 월드 위치·회전
    /// (WorkerStation.TryGetDockPose). <paramref name="dockPoseValid"/>가 false면(스플라인 미설정 등)
    /// 배정 시점의 차량 실제 트랜스폼을 1회만 폴백으로 사용한다.
    /// 배정에 실패하면(이미 작업 중이거나 컨디션 없음) false.
    /// </summary>
    public bool AssignWork(AssemblyPart part, Vector3 pilePos, Quaternion pileWorldRot,
        Vector3 dockPos, Quaternion dockRot, bool dockPoseValid)
    {
        if (part == null || !IsAvailable) return false;

        targetPart = part;   // GetAttachWorldRot()가 참조하므로 아래 호출보다 먼저 대입해야 한다
        ClearStandCache();   // 새 차량·새 부품이므로 이전 대기 지점은 무효
        pileWorldPos = pilePos;
        this.pileWorldRot = pileWorldRot; // 파일 자세는 Fetching 동안 매 프레임 재고정에 쓰인다

        // 부품을 파일 위치에 등장시키고 월드 보간을 시작한다(fill=0이면 파일에 고정돼 대기).
        // 회전은 AssemblyPart 규약상 '부착(도착) 회전 기준 상대 오프셋'으로 넘겨야 하므로,
        // 쌓여 있던 월드 자세를 그대로 재현하려면 부착 회전의 역을 곱한다(UpdateCarryPose와 동일한 변환).
        // 순서 주의: SetWork(0)은 내부에서 SetActive(false)를 호출하므로 활성화가 마지막이어야 한다
        // (StationController.StartAssembly와 동일한 순서).
        targetPart.BeginWorldAssembly(pilePos, Quaternion.Inverse(GetAttachWorldRot()) * pileWorldRot);
        targetPart.SetWork(0f);
        targetPart.SetActive(true);

        // 대기 지점은 여기서 1회만 계산한다 — 차량이 실제로 다가와도 다시 계산하지 않는다.
        ComputeStandWorldPos(dockPos, dockRot, dockPoseValid);

        currentState = WorkerState.Fetching;
        return true;
    }

    /// <summary>진행 중인 작업을 포기하고 자기 자리로 돌아간다(차량 소실·공정 취소 등).</summary>
    public void CancelWork()
    {
        targetPart = null;
        ClearStandCache();
        if (currentState == WorkerState.Resting) return; // 휴식은 그대로 마치게 둔다
        currentState = WorkerState.Idle;
    }

    /// <summary>유저 클릭 — 유휴 중에 미리 휴식시킨다(선제 휴식). 성공하면 true.</summary>
    public bool TryPreemptiveRest()
    {
        if (!CanRestNow) return false;
        BeginRest(false);
        return true;
    }

    /// <summary>유저 클릭 — 작업 중인 체결에 작업량을 추가한다(참여형 개입). 성공하면 true.</summary>
    public bool TryBoostWork(float amount)
    {
        if (currentState != WorkerState.Working || targetPart == null) return false;
        targetPart.AddWork(amount);
        return true;
    }

    /// <summary>
    /// 담당 차량 1대분 작업이 끝났을 때 WorkerStation이 호출한다 — <b>차량당 정확히 1회</b>.
    /// 컨디션을 소모하고 0이 되면 강제 휴식에 들어간다.
    /// (기본값 100 / 10 → 쉬지 않고 10대를 작업하면 1회 휴식)
    ///
    /// 소모 시점을 '체결 완료'가 아니라 '차량 방출'로 잡은 이유:
    /// - 한 작업자가 같은 차량에서 부품을 여러 개 붙여도 소모는 1회여야 한다.
    /// - WorkerStation.ReleaseCar는 담당 부품이 전부 끝나고 <b>모든 작업자가 유휴일 때만</b> 호출되므로
    ///   (UpdateWorking의 AnyWorkerBusy 게이트) 체결 도중에 휴식이 끼어들어 부품이 공중에 멈추지 않는다.
    /// - 중단(AbortCycle)된 사이클은 호출되지 않는다 = 1대분 작업이 성립하지 않으면 소모도 없다.
    /// </summary>
    public void ConsumeConditionForCar()
    {
        // 이미 쉬는 중이면 건드리지 않는다: 어차피 휴식이 끝나면 100으로 회복되고,
        // 유저의 선제 휴식(3초)을 강제 휴식(5초)으로 덮어써 선제 관리에 벌을 주게 된다.
        if (currentState == WorkerState.Resting) return;

        condition = Mathf.Max(0f, condition - conditionCostPerCar);
        if (condition <= 0f) BeginRest(true); // 강제 휴식 → 다음 차량이 게이트에서 대기 = 라인 정지
    }

    private void BeginRest(bool forced)
    {
        restTimer = forced ? forcedRestDuration : preemptiveRestDuration;
        currentState = WorkerState.Resting;
    }

    private void Update()
    {
        movedThisFrame = 0f;

        switch (currentState)
        {
            case WorkerState.GoingToWork:
                if (MoveToward(homePos)) currentState = WorkerState.Idle;
                break;

            case WorkerState.Idle:
                // 자기 자리에서 벗어나 있으면 돌아간다
                MoveToward(homePos);
                break;

            case WorkerState.Fetching:
                // 파일에 도착하면 부품을 들고 차량 부착점으로 향한다
                if (targetPart == null) { CancelWork(); break; }
                // 파일 위 부품의 월드 자세를 매 프레임 재고정한다(= UpdateCarryPose의 파일 버전).
                // AssemblyPart는 시작 회전을 '부착 회전 기준 상대 오프셋'으로 들고 있어, 선행 트리거로
                // 차량이 다가오며 회전하는 동안 오프셋을 갱신하지 않으면 아직 파일에 놓인 부품이 차를 따라 돈다.
                targetPart.BeginWorldAssembly(pileWorldPos,
                    Quaternion.Inverse(GetAttachWorldRot()) * pileWorldRot);
                if (MoveToward(pileWorldPos)) currentState = WorkerState.MovingToPart;
                break;

            case WorkerState.MovingToPart:
            {
                // 부착점은 차량 자식이라 매 프레임 움직인다 → 목표를 계속 갱신
                if (targetPart == null) { CancelWork(); break; }

                // 이동이 먼저다. 목표(GetWorkStandPos)는 '차량 부착점' 기준이라 작업자 자신의 위치와 무관하다
                // — 손 위치에서 목표를 유도하면 목표가 작업자를 따라다녀 영원히 도착하지 못한다
                //   (GetAttachWorldPos 주석의 진동 버그 참조).
                bool arrived = MoveToward(GetWorkStandPos());

                // 부품을 손에 들고 간다: pile 기준점을 '이동 후' 손 위치로 매 프레임 갱신하면 fill=0인 부품이
                // 손에 딱 붙어 따라온다(이동 전에 갱신하면 부품이 1프레임 뒤처져 보인다).
                // BeginWorldAssembly는 순수 setter라 진행도에는 영향이 없다.
                UpdateCarryPose();

                // 작업 위치에 도착하면 여기서 pile 갱신을 멈춘다 → 부품은 '손에서 부착점으로' 이동하게 된다
                if (arrived) currentState = WorkerState.Working;
                break;
            }

            case WorkerState.Working:
                UpdateWorking();
                break;

            case WorkerState.Resting:
                restTimer -= Time.deltaTime;
                MoveToward(homePos);
                if (restTimer <= 0f)
                {
                    condition = 100f; // 선제·강제 모두 전량 회복 (차이는 시간과 타이밍뿐)
                    currentState = WorkerState.Idle;
                }
                break;
        }

        UpdateAnimator();
    }

    /// <summary>
    /// 이번 프레임 실제 이동량으로 걷기 애니메이션을 구동한다.
    /// Animator나 파라미터가 없으면 아무것도 하지 않는다(경고는 SetupAnimator에서 1회만).
    /// 체결 중(Working) 전용 애니메이션은 이번 범위 밖 — 필요해지면 여기에 상태별 분기를 추가한다.
    /// </summary>
    private void UpdateAnimator()
    {
        if (animator == null) return;

        float dt = Time.deltaTime;
        float speed = dt > 0f ? movedThisFrame / dt : 0f;

        if (hasSpeedParam) animator.SetFloat(speedHash, speed * animSpeedScale);
        if (hasMotionSpeedParam) animator.SetFloat(motionSpeedHash, 1f); // 0이면 애니메이션이 멈춘다
        if (hasGroundedParam) animator.SetBool(groundedHash, true);      // 작업자는 늘 바닥 위
    }

    private void UpdateWorking()
    {
        if (targetPart == null)
        {
            CancelWork();
            return;
        }

        // 목표는 정적 지점이라 도착하면 더 이상 움직이지 않는다 — 이진 도착 판정을 써도
        // (기존 소프트 게이트가 막던) '뒤처짐→재추격' 진동이 생기지 않는다.
        bool arrived = MoveToward(GetWorkStandPos());
        if (!arrived) return; // 아직 대기 지점으로 걸어가는 중

        // 대기 지점에 도착해도, 차량이 실제로 게이트에 도킹(정지)하기 전에는 체결을 시작하지 않는다
        // (선행 트리거로 작업자가 차보다 먼저 도착해 서서 기다리는 것은 의도된 동작).
        FaceTowards(GetAttachWorldPos());
        if (!carDocked) return;

        targetPart.AddWork(assembleSpeed * Time.deltaTime);

        if (!targetPart.IsAssembled) return;

        targetPart.SetAssembled();

        // 품질은 작업 당시 컨디션에 비례한다 — 지친 작업자가 붙이면 체결은 100%여도 품질이 낮다
        targetPart.quality = Mathf.Lerp(qualityAtZeroCondition, 1f, ConditionFill);
        targetPart = null;
        ClearStandCache();

        // 컨디션은 여기서 깎지 않는다. 한 작업자가 같은 차량에서 부품을 여러 개 붙일 수 있어
        // 부품마다 깎으면 담당 부품 수만큼 빨리 지치고 "10대마다 1회 휴식" 리듬이 깨진다
        // (담당 부품 2개면 5대 만에 휴식). → 차량 1대분 작업이 끝났을 때
        // WorkerStation이 ConsumeConditionForCar()를 차량당 정확히 1회 호출한다.
        currentState = WorkerState.Idle;
    }

    /// <summary>
    /// AssignWork 시점에 1회만 계산해 캐시하는 정적 대기 지점.
    ///
    /// 도킹 예정 위치·회전(dockPos/dockRot) 기준으로 부착점(targetPart.assembledPos, 차량 로컬)을
    /// 월드로 환산한 뒤, 라인 진행방향(도킹 회전의 로컬 x축 = 측면) 쪽으로 standOffset만큼 민다.
    /// 어느 쪽(좌/우)으로 미는지는 자기 자리(homePos)가 도킹 기준 로컬 좌표에서 부착점보다
    /// x가 작은 쪽(음)인지 큰 쪽(양)인지로 판정한다(기존 PushOutOfCar의 좌우 판별 방식과 동일한 발상).
    /// z(라인 진행방향)는 부착점과 동일하게 둔다 — 결과는 차량이 실제로 움직여도 다시 계산되지 않는
    /// 고정 월드 좌표라, 여기로 걸어가는 동안 목표가 전혀 흔들리지 않는다.
    /// </summary>
    private void ComputeStandWorldPos(Vector3 dockPos, Quaternion dockRot, bool dockPoseValid)
    {
        standValid = false;
        if (targetPart == null) return;

        if (!dockPoseValid)
        {
            // 폴백: 스플라인 예측이 불가능하면(mainLineSpline 미설정 등) 배정 시점의 실제 차량
            // 트랜스폼을 1회만 사용한다. 이후에는 재계산하지 않으므로 여전히 '정적'이지만,
            // 차량이 아직 멀리서 다가오는 중이면 정확도가 떨어질 수 있다.
            Transform holder = targetPart.transform.parent;
            if (holder == null) return;
            dockPos = holder.position;
            dockRot = holder.rotation;
        }

        Vector3 attachLocal = targetPart.assembledPos;

        // 자기 자리가 도킹 기준으로 부착점의 어느 쪽(x)에 있는지로 좌/우를 정한다
        Vector3 homeLocal = Quaternion.Inverse(dockRot) * (homePos - dockPos);
        float side = homeLocal.x - attachLocal.x;

        Vector3 standLocal = attachLocal;
        standLocal.x += side < 0f ? -standOffset : standOffset;

        Vector3 world = dockPos + dockRot * standLocal;
        world.y = homePos.y;

        standWorldPos = world;
        standValid = true;
    }

    /// <summary>작업자가 걸어가서 대기/작업할 지점. 배정 시 계산해 둔 정적 지점을 그대로 돌려준다.</summary>
    private Vector3 GetWorkStandPos()
    {
        // 안전망: 배정 시 대기 지점 계산이 실패했으면(targetPart 없음 등) 부착점을 그대로 반환한다.
        return standValid ? standWorldPos : GetAttachWorldPos();
    }

    private void ClearStandCache()
    {
        standValid = false;
    }

    /// <summary>
    /// 차량 부착점(체결 완료 지점)의 월드 위치.
    /// AssemblyPart.ApplyWorldPose가 목적지로 쓰는 식(부모.TransformPoint(assembledPos))과 똑같이 계산해
    /// '작업자가 서는 곳'과 '부품이 도착하는 곳'이 어긋나지 않게 한다.
    ///
    /// ★ 여기에 AssemblyPart.GetArmLookWorldPos()를 쓰면 안 된다 — 그것은 '부품의 현재 위치'라서
    ///   부품을 손에 들고 가는 동안에는 작업자 자신의 손을 가리킨다. 그러면
    ///     목표 = 손 - (집→손 방향) * workStandDistance
    ///   가 되어 작업자→목표 벡터가 (앞 * handForward - 바깥 * workStandDistance)로 고정되고,
    ///   그 크기는 아무리 움직여도 (workStandDistance - handForward) 아래로 내려가지 않는다.
    ///   기본값에서 0.9 - 0.6 = 0.3m > arriveRadius(0.25) → 영원히 도착 판정이 안 나고,
    ///   목표가 항상 작업자보다 '집 쪽(뒤)'이라 작업자가 뒤로 끌려가며 제자리에서 떨었다(관측된 버그).
    ///   (수정 전 기본값 0.9 - 0.45 = 0.45m로 하한이 arriveRadius의 1.8배였다.)
    /// </summary>
    private Vector3 GetAttachWorldPos()
    {
        Transform holder = targetPart.transform.parent; // = AssemblyPart.cachedParent (파츠는 런타임 리페어런트 없음)
        return holder != null
            ? holder.TransformPoint(targetPart.assembledPos)
            : targetPart.GetArmLookWorldPos();
    }

    /// <summary>차량 부착점의 월드 회전 (AssemblyPart.ApplyWorldPose의 worldEndRot과 동일 식).</summary>
    private Quaternion GetAttachWorldRot()
    {
        Transform holder = targetPart.transform.parent;
        return holder != null
            ? holder.rotation * Quaternion.Euler(targetPart.assembledRot)
            : targetPart.transform.rotation;
    }

    /// <summary>
    /// 들고 있는 부품을 손에 붙인다 (위치 + 회전).
    ///
    /// AssemblyPart는 시작 회전을 '부착(도착) 회전 기준 상대 오프셋'으로 받아
    /// Slerp(부착회전 * 오프셋, 부착회전, fill)로 보간한다. 따라서 fill=0에서 부품이 손 회전을 그대로
    /// 따르게 하려면 부착 회전의 역(inverse)을 곱해 넘겨야 한다.
    /// → 운반 중(fill=0)에는 부품이 손 회전 그대로, 체결이 진행되면 부착 회전으로 자연스럽게 풀린다.
    ///
    /// 실제 캐릭터 모델을 쓸 때는 handPos에 손 본을 꽂기만 하면 위치·회전이 모두 손을 따라간다
    /// (본이 애니메이션으로 움직여도 목표 지점은 부착점 기준이라 이동에 영향을 주지 않는다).
    /// </summary>
    private void UpdateCarryPose()
    {
        if (targetPart == null) return;
        targetPart.BeginWorldAssembly(CarryWorldPos, Quaternion.Inverse(GetAttachWorldRot()) * CarryWorldRot);
    }

    /// <summary>
    /// 목표로 이동한다. 도착했으면 true.
    /// 목표의 y를 작업자 자신의 높이로 덮어써 거리 판정을 수평(XZ)으로 만든다 —
    /// 손 높이·부착점 높이가 섞여도 "수평으로는 닿았는데 3D 거리로는 못 닿는" 상황이 생기지 않는다.
    /// Vector3.MoveTowards는 목표를 지나치지 않으므로(clamp) 오버슈트 진동도 없다.
    /// </summary>
    private bool MoveToward(Vector3 target)
    {
        Vector3 flatTarget = new Vector3(target.x, transform.position.y, target.z);
        float dist = Vector3.Distance(transform.position, flatTarget);
        if (dist <= arriveRadius) return true;

        Vector3 before = transform.position;
        transform.position = Vector3.MoveTowards(before, flatTarget, moveSpeed * Time.deltaTime);
        movedThisFrame += Vector3.Distance(before, transform.position);

        FaceTowards(flatTarget);
        return false;
    }

    /// <summary>수평면에서 지정 지점을 바라보도록 turnSpeed로 회전한다.</summary>
    private void FaceTowards(Vector3 worldPoint)
    {
        if (turnSpeed <= 0f) return;

        Vector3 dir = worldPoint - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;

        Quaternion want = Quaternion.LookRotation(dir.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turnSpeed * Time.deltaTime);
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmo) return;

        // 자기 자리
        if (homeSet)
        {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 1f);
            Gizmos.DrawWireSphere(homePos, 0.2f);
            Gizmos.DrawLine(transform.position, homePos);
        }

        // 손(부품이 붙는 지점)과 들고 있는 부품의 자세.
        // 플레이하지 않고도 handForward/handHeight/carryOffset/carryEuler를 눈으로 맞출 수 있다.
        Vector3 carry = CarryWorldPos;
        Gizmos.color = new Color(1f, 0.85f, 0.2f, 1f);
        Gizmos.DrawLine(transform.position, carry);
        Matrix4x4 prevMatrix = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(carry, CarryWorldRot, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 0.25f); // 들고 있는 큐브 부품의 대략 크기
        Gizmos.matrix = prevMatrix;

#if UNITY_EDITOR
        UnityEditor.Handles.color = condition > 50f ? Color.green : (condition > 20f ? Color.yellow : Color.red);
        UnityEditor.Handles.Label(transform.position + Vector3.up * 2.2f,
            $"{currentState} / 컨디션 {condition:F0}%");
#endif
    }
}
