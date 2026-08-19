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
    [Tooltip("작업자 이동 속도(m/초).")]
    public float moveSpeed = 2.5f;

    [Tooltip("목표 지점에 이 거리(m) 이내로 들어오면 도착으로 판정한다.")]
    public float arriveRadius = 0.25f;

    [Tooltip("이동 방향으로 몸을 돌리는 속도(도/초). 0이면 회전하지 않는다.")]
    public float turnSpeed = 540f;

    [Header("작업")]
    [Tooltip("1초당 처리하는 작업량(work/초). 로봇팔(StationConfig.assembleSpeed)보다 느리게 두어" +
        " 자동화의 처리량 우위를 만든다.")]
    public float assembleSpeed = 4f;

    [Tooltip("차량 부착점에서 이만큼(m) 떨어진 지점에 서서 작업한다. 차량/부품과 겹치지 않게.")]
    public float workStandDistance = 0.9f;

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

    [Header("기즈모")]
    public bool drawGizmo = true;

    // 자기 자리(대기 위치). Start에서 현재 위치로 잡고, WorkerStation이 지정할 수도 있다.
    private Vector3 homePos;
    private bool homeSet = false;

    private AssemblyPart targetPart;     // 담당 부품 (차량에 붙일 것)
    private Vector3 pileWorldPos;        // 부품을 집는 파일 위치
    private float restTimer = 0f;

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
    }

    /// <summary>자기 자리(대기 위치)를 지정한다.</summary>
    public void SetHome(Vector3 pos)
    {
        homePos = pos;
        homeSet = true;
    }

    /// <summary>
    /// 담당 작업을 배정한다. 작업자가 파일로 이동해 부품을 집고, 차량 부착점으로 옮겨 체결한다.
    /// <paramref name="pilePos"/>/<paramref name="pileWorldRot"/> = 부품이 등장해 대기할 <b>월드</b> 위치·회전
    /// (재고 파일의 맨 위 슬롯에 쌓여 있던 그 자세를 그대로 넘기면 된다).
    /// 배정에 실패하면(이미 작업 중이거나 컨디션 없음) false.
    /// </summary>
    public bool AssignWork(AssemblyPart part, Vector3 pilePos, Quaternion pileWorldRot)
    {
        if (part == null || !IsAvailable) return false;

        targetPart = part;   // GetAttachWorldRot()가 참조하므로 아래 호출보다 먼저 대입해야 한다
        pileWorldPos = pilePos;

        // 부품을 파일 위치에 등장시키고 월드 보간을 시작한다(fill=0이면 파일에 고정돼 대기).
        // 회전은 AssemblyPart 규약상 '부착(도착) 회전 기준 상대 오프셋'으로 넘겨야 하므로,
        // 쌓여 있던 월드 자세를 그대로 재현하려면 부착 회전의 역을 곱한다(UpdateCarryPose와 동일한 변환).
        // 순서 주의: SetWork(0)은 내부에서 SetActive(false)를 호출하므로 활성화가 마지막이어야 한다
        // (StationController.StartAssembly와 동일한 순서).
        targetPart.BeginWorldAssembly(pilePos, Quaternion.Inverse(GetAttachWorldRot()) * pileWorldRot);
        targetPart.SetWork(0f);
        targetPart.SetActive(true);

        currentState = WorkerState.Fetching;
        return true;
    }

    /// <summary>진행 중인 작업을 포기하고 자기 자리로 돌아간다(차량 소실·공정 취소 등).</summary>
    public void CancelWork()
    {
        targetPart = null;
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
    }

    private void UpdateWorking()
    {
        if (targetPart == null)
        {
            CancelWork();
            return;
        }

        // 작업 위치를 계속 따라간다(차량이 멈춰 있어도 부착점 기준을 유지)
        bool inPlace = MoveToward(GetWorkStandPos());

        // 게이트: 작업자가 부착점 옆에 서 있을 때만 체결이 진행된다
        if (!inPlace) return;

        targetPart.AddWork(assembleSpeed * Time.deltaTime);

        if (!targetPart.IsAssembled) return;

        targetPart.SetAssembled();

        // 품질은 작업 당시 컨디션에 비례한다 — 지친 작업자가 붙이면 체결은 100%여도 품질이 낮다
        targetPart.quality = Mathf.Lerp(qualityAtZeroCondition, 1f, ConditionFill);
        targetPart = null;

        // 컨디션은 여기서 깎지 않는다. 한 작업자가 같은 차량에서 부품을 여러 개 붙일 수 있어
        // 부품마다 깎으면 담당 부품 수만큼 빨리 지치고 "10대마다 1회 휴식" 리듬이 깨진다
        // (담당 부품 2개면 5대 만에 휴식). → 차량 1대분 작업이 끝났을 때
        // WorkerStation이 ConsumeConditionForCar()를 차량당 정확히 1회 호출한다.
        currentState = WorkerState.Idle;
    }

    /// <summary>부착점에서 workStandDistance만큼 떨어진, 작업자가 서는 지점.</summary>
    private Vector3 GetWorkStandPos()
    {
        Vector3 attach = GetAttachWorldPos();
        Vector3 dir = attach - homePos;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;

        // 부착점에서 자기 자리 쪽으로 물러선 위치 — 차량·부품과 겹치지 않게
        Vector3 stand = attach - dir.normalized * workStandDistance;
        stand.y = homePos.y;
        return stand;
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

        transform.position = Vector3.MoveTowards(transform.position, flatTarget, moveSpeed * Time.deltaTime);

        if (turnSpeed > 0f)
        {
            Vector3 dir = flatTarget - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f)
            {
                Quaternion want = Quaternion.LookRotation(dir.normalized, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turnSpeed * Time.deltaTime);
            }
        }
        return false;
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
