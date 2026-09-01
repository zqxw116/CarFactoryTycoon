using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

/// <summary>
/// 사람 작업자가 담당하는 정지형 공정. 차량을 게이트에 세워 두고 작업자가 부품을 붙인다.
///
///   Idle(게이트 대기) → 캡처 → Working(작업자들이 담당 부품 체결) → 전부 완료 시 방출 → Idle
///
/// 로봇팔 공정(StationController, 흐름형)과의 대비가 이 게임의 성장 곡선이다:
/// - 사람 공정은 차량을 세운다 → 라인이 stop-and-go로 덜컹거린다.
/// - 로봇팔로 교체한 공정은 흐르면서 체결한다 → 그 구간부터 차량이 멈추지 않는다.
///   전 라인 로봇팔화(엔딩) = 라인이 완전히 매끄럽게 흐르는 순간.
///
/// 재고(PartStack)가 비면 작업을 시작하지 못하고 차량이 게이트에 계속 서 있다 = 라인 정지.
/// 유저가 파일을 클릭해 응급 보충하거나, 보급 작업자가 채우면 재개된다.
/// 게이트 인프라(감속 도킹·뒷차 줄서기·통과 판정)는 LineTrafficManager가 전부 처리하므로
/// 이 공정은 뒷차의 존재를 몰라도 된다.
///
/// 성능 주의: CarController.GetUnassembledPart는 호출마다 로그를 남기는 무거운 조회다.
/// 캡처 시점에 담당 부품을 한 번만 조회해 pending 목록으로 캐시하고, 이후 프레임에서는 캐시만 본다.
/// </summary>
public class WorkerStation : MonoBehaviour, ILineGate
{
    public enum WorkState { Idle, Working }

    /// <summary>이 차량에서 아직 붙이지 못한 담당 부품 하나 (+ 어느 재고 파일에서 꺼낼지).</summary>
    private struct PendingPart
    {
        public AssemblyPart part;
        public int stackIndex;
        public bool stockTaken; // 재고를 이미 소모했는지 (배정 실패/중단 시 되돌리기 위해)
    }

    [Header("라인 연결")]
    [Tooltip("메인 라인 스플라인. 비어 있으면 'MainSpline' 이름으로 자동 탐색한다.")]
    public SplineContainer mainLineSpline;

    [Tooltip("켜면 이 오브젝트의 현재 위치에서 가장 가까운 스플라인 지점을 게이트로 잡는다.")]
    public bool autoProgressFromPosition = true;

    [Tooltip("차량이 멈춰 서는 게이트 지점(스플라인 진행도 0~1).")]
    [Range(0f, 1f)] public float gateProgress = 0.3f;

    [Header("공정 설정")]
    [Tooltip("이 공정이 담당하는 부품들. 작업자가 여러 명이면 미체결 부품을 하나씩 나눠 맡는다.")]
    public PartType[] targetPartTypes;

    [Tooltip("담당 부품별 재고 파일. targetPartTypes와 인덱스가 대응한다(개수가 모자라면 마지막 것을 공용).")]
    public PartStack[] partStacks;

    [Tooltip("이 공정에 배치된 작업자. 늘리면 담당 부품을 병렬로 처리해 공정 시간이 줄어든다.")]
    public Worker[] workers;

    [Tooltip("차량이 게이트 이 거리(m) 앞에 들어오면 작업자가 미리 부품을 가지러 출발한다(선행 트리거)." +
        " 차량은 그대로 게이트까지 이동해 정지하고, 더 느린 작업자가 뒤따라 도착해 체결한다 —" +
        " 차가 다 선 뒤에야 작업자가 움직이던 어색함이 사라진다." +
        " 0이면 기존 동작(게이트에 완전히 도착한 차량만 잡는다).")]
    public float leadDistance = 4f;

    [Tooltip("부품이 등장하는 지점의 '폴백'. partStacks가 연결돼 있으면 그 재고 파일의 맨 위 부품 자리에서" +
        " 집으므로 이 값은 쓰이지 않는다. 스택을 연결하지 않은 공정에서만 사용된다(미설정 시 이 오브젝트 위치).")]
    public Transform pilePos;

    [Header("현재 상태 (디버그)")]
    public WorkState currentState = WorkState.Idle;
    [SerializeField] private CarController currentCar;

    [Header("기즈모")]
    public bool drawGizmo = true;
    public Color gateColor = new Color(0.2f, 1f, 0.6f, 1f);

    // 게이트에 '도킹했다'고 보는 거리(m). 트래픽 매니저가 게이트 앞 0.01m에 세우므로 이 값이면 충분하다.
    // leadDistance를 0으로 두면 캡처 거리도 이 값으로 떨어져 선행 트리거 이전 동작으로 롤백된다.
    private const float MinCaptureEpsilon = 0.1f;
    private const string MainSplineName = "MainSpline";

    /// <summary>ILineGate: 차량이 넘지 못하는 게이트 진행도.</summary>
    public float GateProgress => gateProgress;

    /// <summary>
    /// 차량이 이 게이트에 도킹(정지)했을 때 가질 월드 위치·회전을 스플라인에서 미리 샘플링한다.
    /// CarController.SnapToProgress와 동일한 계산식(SplineUtility.Evaluate)을 gateProgress에 적용한 것 —
    /// 실제 정지 지점은 LineTrafficManager가 GateStopOffset(0.01m) 만큼 살짝 앞에 세우므로 오차는 무시할 수준이다.
    /// Worker가 '차량을 따라가지 않고 도킹 예정 위치 옆에 미리 대기'하는 새 설계의 기준점.
    /// </summary>
    public bool TryGetDockPose(out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;
        if (mainLineSpline == null) return false;

        SplineUtility.Evaluate(mainLineSpline.Spline, gateProgress,
            out float3 localPos, out float3 localTangent, out float3 localUp);
        pos = mainLineSpline.transform.TransformPoint(localPos);

        if (math.length(localTangent) > 0.001f)
        {
            Vector3 worldDir = mainLineSpline.transform.TransformDirection(localTangent);
            Vector3 worldUp = mainLineSpline.transform.TransformDirection(localUp);
            rot = Quaternion.LookRotation(worldDir, worldUp);
        }
        return true;
    }

    /// <summary>ILineGate: 게이트 활성 여부. 꺼지면 차량이 그냥 통과한다.</summary>
    public bool GateEnabled => isActiveAndEnabled;

    // 이번 차량에서 아직 못 붙인 담당 부품들 (캡처 시 1회 조회 후 캐시)
    private readonly List<PendingPart> pending = new List<PendingPart>();

    // 작업자별로 지금 맡고 있는 부품 (workers와 인덱스 대응)
    private AssemblyPart[] assigned;

    // 이번 차량에서 부품을 하나라도 완성한 작업자 (workers와 인덱스 대응).
    // 컨디션 소모를 '차량당 1회'로 만들기 위한 <b>사이클 한정</b> 플래그로, 방출/중단 시 리셋된다.
    // 차량 참조를 기억하지 않으므로 CarPool이 같은 인스턴스를 재사용해도 오판할 여지가 없다
    // ("이전에 소모한 차량과 같은 인스턴스인가"로 판정하면 재사용된 차를 같은 차로 오인한다).
    private bool[] carParticipated;

    // 담당 부품이 없어 그냥 통과시킬 차량. 선행 거리에서 미리 판정한 뒤 게이트 도착까지 기다리는 동안
    // GetUnassembledPart(호출마다 로그를 남기는 무거운 조회)를 매 프레임 다시 하지 않기 위한 캐시.
    private CarController skipCar;

    private bool initialized = false;

    // 부품이 등장할 지점의 '폴백'(월드). 재고 파일(partStacks)이 연결돼 있으면 그 파일의 맨 위 슬롯을
    // 쓰므로 여기는 스택 미연결 시에만 사용된다. Worker.AssignWork는 월드 위치·회전을 받는다.
    private Vector3 PileWorldPos => pilePos != null ? pilePos.position : transform.position;
    private Quaternion PileWorldRot => pilePos != null ? pilePos.rotation : transform.rotation;

    private void OnEnable()
    {
        EnsureInit();
        if (autoProgressFromPosition && mainLineSpline != null)
            gateProgress = GetNearestProgress();
        LineTrafficManager.Instance.RegisterGate(this);
    }

    private void OnDisable()
    {
        AbortCycle();
        LineTrafficManager.Instance.UnregisterGate(this);
    }

    private void EnsureInit()
    {
        if (initialized) return;
        initialized = true;

        if (mainLineSpline == null)
        {
            GameObject go = GameObject.Find(MainSplineName);
            if (go != null) go.TryGetComponent(out mainLineSpline);
        }
        if (mainLineSpline == null)
            Debug.LogError($"[WorkerStation] '{MainSplineName}' 스플라인을 찾지 못했습니다.");

        int workerCount = workers != null ? workers.Length : 0;
        assigned = new AssemblyPart[workerCount];
        carParticipated = new bool[workerCount];

        // 재고 파일에 담당 부품 타입을 알려준다(파일이 무엇을 쌓아 보여줄지)
        if (targetPartTypes != null)
        {
            for (int i = 0; i < targetPartTypes.Length; i++)
            {
                PartStack stack = GetStack(i);
                if (stack != null) stack.SetPartType(targetPartTypes[i]);
            }
        }

        // 작업자의 자기 자리 = 현재 배치된 위치
        if (workers != null)
        {
            for (int i = 0; i < workers.Length; i++)
                if (workers[i] != null) workers[i].SetHome(workers[i].transform.position);
        }
    }

    private float GetNearestProgress()
    {
        float3 localPoint = mainLineSpline.transform.InverseTransformPoint(transform.position);
        SplineUtility.GetNearestPoint(mainLineSpline.Spline, localPoint, out _, out float t);
        return Mathf.Clamp01(t);
    }

    /// <summary>index번째 담당 부품에 대응하는 재고 파일 (개수가 모자라면 마지막 것을 공용).</summary>
    private PartStack GetStack(int index)
    {
        if (partStacks == null || partStacks.Length == 0) return null;
        return partStacks[Mathf.Min(index, partStacks.Length - 1)];
    }

    private void Update()
    {
        // 작업 도중 차량이 사라지면(풀 반환 등) 정리
        if (currentState != WorkState.Idle && (currentCar == null || !currentCar.gameObject.activeInHierarchy))
        {
            AbortCycle();
            return;
        }

        switch (currentState)
        {
            case WorkState.Idle:
                // 테스트 편의: 플레이 중 오브젝트를 옮기면 게이트 지점도 따라온다 (작업 중에는 고정)
                if (autoProgressFromPosition && transform.hasChanged && mainLineSpline != null)
                {
                    gateProgress = GetNearestProgress();
                    transform.hasChanged = false;
                }
                TryCapture();
                break;

            case WorkState.Working:
                UpdateWorking();
                break;
        }
    }

    /// <summary>
    /// 게이트 앞 leadDistance 안에 들어온 차량을 캡처한다(선행 트리거). 캡처 시점에는 차량을 세우지 않는다 —
    /// 차량은 트래픽 매니저의 게이트 클램프로 알아서 게이트에 도킹하고, 그동안 작업자는 부품을 가지러 출발한다.
    /// 이 공정에서 붙일 부품이 없는 차는 게이트에 도착한 뒤 그냥 통과시킨다.
    /// </summary>
    private void TryCapture()
    {
        CarController car = LineTrafficManager.Instance.GetCarAtGate(gateProgress, CaptureDistance);
        if (car == null) return;

        // 이미 '붙일 것 없음'으로 판정한 차 — 도킹할 때까지 재조회하지 않는다(로그·GC 폭주 방지)
        if (car == skipCar)
        {
            if (!IsDocked(car)) return;
            PassGate(car);
            skipCar = null;
            return;
        }
        skipCar = null;

        // 담당 부품 조회는 여기서 딱 한 번 — 이후 프레임은 pending 캐시만 본다
        BuildPending(car);

        if (pending.Count == 0)
        {
            // 선행 거리에서 통과 처리하면 PassGate의 SetProgress가 차를 앞으로 순간이동시킨다
            // → 실제로 게이트에 도킹한 뒤에 통과시킨다.
            if (IsDocked(car)) PassGate(car);
            else skipCar = car;
            return;
        }

        currentCar = car;
        currentState = WorkState.Working; // 정지는 게이트 도착 시점(UpdateWorking)에서 처리한다
    }

    /// <summary>캡처 대상으로 삼을 게이트 앞 거리(m). leadDistance가 0이면 도킹 판정 거리로 떨어진다.</summary>
    private float CaptureDistance => Mathf.Max(leadDistance, MinCaptureEpsilon);

    /// <summary>차량이 게이트에 도착해 사실상 멈춰 선 상태인지.</summary>
    private bool IsDocked(CarController car)
    {
        if (car == null) return false;
        float len = car.SplineLength;
        if (len <= 0f) return true;
        return (gateProgress - car.pathProgress) * len <= MinCaptureEpsilon;
    }

    private void BuildPending(CarController car)
    {
        pending.Clear();
        if (targetPartTypes == null) return;

        for (int i = 0; i < targetPartTypes.Length; i++)
        {
            AssemblyPart part = car.GetUnassembledPart(targetPartTypes[i]);
            if (part == null) continue;
            pending.Add(new PendingPart { part = part, stackIndex = i, stockTaken = false });
        }
    }

    private void UpdateWorking()
    {
        // 0) 게이트에 도착하면 정지시킨다. 선행 캡처 시점에는 차가 아직 다가오는 중이라
        //    여기서 세워야 "차는 계속 굴러오고 작업자는 미리 출발한다"가 성립한다.
        //    (도킹 자체는 트래픽 매니저의 게이트 클램프가 하고, 이 플래그는 작업 중 재출발을 막는다)
        if (currentCar.isMoving && IsDocked(currentCar)) currentCar.isMoving = false;

        // 1) 완료된 작업 회수 + 보상 지급
        CollectFinished();

        // 2) 유휴 작업자에게 남은 부품 배정 (재고가 있어야 함 — 없으면 차량이 계속 서 있다 = 라인 정지)
        TryAssignWork();

        // 3) 재고 부족으로 멈춘 상태를 작업자에게 알린다(머리 위 "부품없음" 표시용)
        SetStockBlocked(IsBlockedByStock());

        // 3.5) 차량이 실제로 도킹(정지)했는지를 작업자들에게 알린다 —
        //      작업자는 대기 지점에 먼저 도착해도 이 플래그가 서기 전까지 체결을 시작하지 않는다.
        SetCarDocked(!currentCar.isMoving);

        // 4) 담당 부품이 전부 체결됐으면 방출
        if (pending.Count == 0 && !AnyWorkerBusy()) ReleaseCar();
    }

    /// <summary>재고 부족 상태를 작업자들에게 전달한다(WorkerStatusUI가 표시).</summary>
    private void SetStockBlocked(bool blocked)
    {
        if (workers == null) return;
        for (int i = 0; i < workers.Length; i++)
            if (workers[i] != null) workers[i].stockBlocked = blocked;
    }

    /// <summary>차량이 실제로 도킹(정지)했는지를 작업자들에게 전달한다 — 체결 시작 게이트.</summary>
    private void SetCarDocked(bool docked)
    {
        if (workers == null) return;
        for (int i = 0; i < workers.Length; i++)
            if (workers[i] != null) workers[i].carDocked = docked;
    }

    /// <summary>체결이 끝난 배정을 회수하고 파츠 보상을 지급한다.</summary>
    private void CollectFinished()
    {
        if (assigned == null) return;

        for (int i = 0; i < assigned.Length; i++)
        {
            AssemblyPart part = assigned[i];
            if (part == null || !part.IsAssembled) continue;

            // 보상: 로봇팔 공정(StationController)과 동일하게 파츠 완료마다 지급
            Vector3 rewardPos = part.transform.position + Vector3.up * 0.5f;
            int reward = StationConfig.Instance.partReward;
            EconomyManager.Instance.Earn(reward, rewardPos);
            CashPopup.Show(rewardPos, reward);

            RemovePending(part);
            assigned[i] = null;

            // 이 차량에서 실제로 부품을 완성한 작업자만 표시해 둔다.
            // 컨디션은 여기서 깎지 않는다 — 같은 차량에서 부품을 2개 붙이면 2번 깎이기 때문.
            // 실제 소모는 차량 방출(ReleaseCar) 시점에 이 플래그를 보고 1회씩만 수행한다.
            if (carParticipated != null) carParticipated[i] = true;
        }
    }

    private void RemovePending(AssemblyPart part)
    {
        for (int i = 0; i < pending.Count; i++)
        {
            if (pending[i].part != part) continue;
            pending.RemoveAt(i);
            return;
        }
    }

    /// <summary>유휴 작업자에게 미체결 부품을 배정한다(재고 1개 소모).</summary>
    private void TryAssignWork()
    {
        if (workers == null || assigned == null) return;

        for (int w = 0; w < workers.Length; w++)
        {
            Worker worker = workers[w];
            if (worker == null || assigned[w] != null || !worker.IsAvailable) continue;

            for (int p = 0; p < pending.Count; p++)
            {
                PendingPart entry = pending[p];
                if (entry.part == null || IsAlreadyAssigned(entry.part)) continue;

                // 부품이 등장할 지점. 기본은 폴백(pilePos)이고, 재고 파일이 있으면 바로 아래에서
                // '방금 걷어낼 맨 위 부품 자리'로 덮어쓴다.
                Vector3 pickPos = PileWorldPos;
                Quaternion pickRot = PileWorldRot;

                // 재고가 없으면 이 부품은 지금 붙일 수 없다 → 라인이 멈춘 채 보충을 기다린다
                PartStack stack = GetStack(entry.stackIndex);
                if (!entry.stockTaken && stack != null)
                {
                    // ★ 순서 필수: TryConsume이 재고를 줄이면 top 슬롯이 한 칸 내려간다.
                    //   소모 '전에' 읽어야 방금 사라진 바로 그 부품 자리에서 집는 그림이 된다.
                    //   (재고 0이면 스택 원점으로 폴백되지만 곧바로 TryConsume이 false라 쓰이지 않는다.
                    //    여러 작업자가 같은 프레임에 배정받아도 각자 소모 직전 값을 읽으므로 슬롯이 겹치지 않는다)
                    pickPos = stack.GetTopSlotWorldPos();
                    pickRot = stack.GetTopSlotWorldRot();

                    if (!stack.TryConsume()) continue;
                    entry.stockTaken = true;
                    pending[p] = entry;
                }

                bool dockValid = TryGetDockPose(out Vector3 dockPos, out Quaternion dockRot);
                if (worker.AssignWork(entry.part, pickPos, pickRot, dockPos, dockRot, dockValid))
                {
                    assigned[w] = entry.part;
                    break;
                }

                // 배정 실패 시 소모한 재고를 되돌린다
                if (entry.stockTaken && stack != null)
                {
                    stack.Add(1);
                    entry.stockTaken = false;
                    pending[p] = entry;
                }
            }
        }
    }

    private bool IsAlreadyAssigned(AssemblyPart part)
    {
        if (assigned == null) return false;
        for (int i = 0; i < assigned.Length; i++)
            if (assigned[i] == part) return true;
        return false;
    }

    private bool AnyWorkerBusy()
    {
        if (workers == null) return false;
        for (int i = 0; i < workers.Length; i++)
            if (workers[i] != null && workers[i].IsBusy) return true;
        return false;
    }

    private void ReleaseCar()
    {
        // 컨디션 소모 지점 = 차량 1대분 작업 완료. 여기는 담당 부품이 전부 끝나고
        // 모든 작업자가 유휴일 때만 도달하므로(UpdateWorking의 AnyWorkerBusy 게이트)
        // 체결 도중에 강제 휴식이 끼어들어 부품이 공중에 멈추는 일이 없다.
        ConsumeConditionForFinishedCar();

        PassGate(currentCar);
        currentCar = null;
        pending.Clear();
        SetStockBlocked(false);
        SetCarDocked(false);
        currentState = WorkState.Idle; // 다음 대기 차량은 다음 프레임 TryCapture가 잡는다
    }

    /// <summary>
    /// 이번 차량에서 부품을 완성한 작업자들의 컨디션을 <b>1인당 1회씩</b> 소모하고 플래그를 리셋한다.
    /// 부품을 몇 개 붙였는지와 무관하며, 이 공정에서 붙일 게 없어 그냥 통과시킨 차량(TryCapture의
    /// pending.Count == 0 경로)은 애초에 여기를 지나지 않으므로 소모도 없다.
    /// </summary>
    private void ConsumeConditionForFinishedCar()
    {
        if (workers == null || carParticipated == null) return;

        for (int i = 0; i < carParticipated.Length; i++)
        {
            if (!carParticipated[i]) continue;
            carParticipated[i] = false;
            if (i < workers.Length && workers[i] != null) workers[i].ConsumeConditionForCar();
        }
    }

    /// <summary>참여 플래그만 리셋한다(컨디션 소모 없음).</summary>
    private void ClearParticipation()
    {
        if (carParticipated == null) return;
        for (int i = 0; i < carParticipated.Length; i++) carParticipated[i] = false;
    }

    /// <summary>차량을 게이트 진행도 위에 올려 통과 처리하고 재출발시킨다.</summary>
    private void PassGate(CarController car)
    {
        if (car == null) return;
        car.SetProgress(gateProgress); // progress >= 게이트 → 트래픽의 게이트 클램프에서 제외된다
        car.isMoving = true;
    }

    /// <summary>진행 중인 작업을 중단하고 초기 상태로 되돌린다(차량 소실·비활성화 등).</summary>
    private void AbortCycle()
    {
        if (workers != null)
        {
            for (int i = 0; i < workers.Length; i++)
                if (workers[i] != null) workers[i].CancelWork();
        }
        if (assigned != null)
        {
            for (int i = 0; i < assigned.Length; i++) assigned[i] = null;
        }

        // 중단된 사이클은 컨디션을 소모하지 않는다 — 차량이 사라져 '1대분 작업'이 성립하지 않는다.
        // (여기서 소모해 버리면 차량 소실·공정 비활성화가 반복될 때 부당하게 지친다)
        ClearParticipation();

        // 소모했지만 붙이지 못한 재고는 파일로 되돌린다(재고 유실 방지)
        for (int i = 0; i < pending.Count; i++)
        {
            if (!pending[i].stockTaken) continue;
            PartStack stack = GetStack(pending[i].stackIndex);
            if (stack != null) stack.Add(1);
        }
        pending.Clear();
        SetStockBlocked(false);
        SetCarDocked(false);

        if (currentCar != null && currentCar.gameObject.activeInHierarchy)
            currentCar.isMoving = true;

        currentCar = null;
        currentState = WorkState.Idle;
    }

    /// <summary>이 공정이 재고 부족으로 멈춘 상태인지 (라인 정지 원인 표시용).</summary>
    public bool IsBlockedByStock()
    {
        // 상태 대신 pending으로 판정한다 — pending은 캡처~방출 사이에만 채워져 있으므로
        // 선행 캡처로 앞당겨진 페치 단계에서도 "부품없음" 표시가 동일하게 뜬다.
        if (pending.Count == 0 || AnyWorkerBusy()) return false;

        for (int i = 0; i < pending.Count; i++)
        {
            if (pending[i].stockTaken) continue;
            PartStack stack = GetStack(pending[i].stackIndex);
            if (stack != null && stack.IsEmpty) return true;
        }
        return false;
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmo) return;

        // 게이트 지점
        if (mainLineSpline != null)
        {
            float3 local = mainLineSpline.Spline.EvaluatePosition(gateProgress);
            Vector3 world = mainLineSpline.transform.TransformPoint(local);
            Gizmos.color = gateColor;
            Gizmos.DrawWireSphere(world, 0.4f);
            Gizmos.DrawLine(world, world + Vector3.up * 2f);
            Gizmos.DrawLine(transform.position, world);
        }

        // 부품 파일 위치
        Gizmos.color = new Color(1f, 0.6f, 0.1f, 1f);
        Gizmos.DrawWireSphere(PileWorldPos, 0.2f);

#if UNITY_EDITOR
        UnityEditor.Handles.color = gateColor;
        string label = $"{name}  게이트 {gateProgress:F3}  [{currentState}]";
        if (IsBlockedByStock()) label += "  [재고 없음 → 라인 정지]";
        UnityEditor.Handles.Label(transform.position + Vector3.up * 2.5f, label);
#endif
    }
}
