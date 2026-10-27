using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 바퀴 전용 리프트 공정. 흐르면서 체결하는 일반 스테이션과 달리 차량을 세워서 작업한다:
///
///   Idle(게이트 대기) → Raising(차량 들어올림) → Working(4개 바퀴 전부 체결, 팔 개수별 순차 묶음)
///   → Lowering(내림) → 방출 → Idle
///
/// - LineTrafficManager에 게이트로 등록된다: 아직 통과하지 않은 차량은 liftProgress 지점을
///   넘지 못하고 감속 정지(도킹) → 이 스테이션이 캡처한다. 뒷차 줄서기는 트래픽이 자동 처리 —
///   이 공정은 뒷차의 존재를 몰라도 된다.
/// - 캡처한 차량은 isMoving=false: CarController의 SnapToProgress가 멈추므로 transform이
///   자유로워져 리프트가 Y를 직접 애니메이션할 수 있다.
/// - 로봇팔은 트리거 진입에 의존하지 않고 이 공정이 직접 오케스트레이션한다
///   (SetPartTypeAndStart = manualMode라 PhysX 이벤트가 체결을 취소하지 못함).
///   차량이 정지 상태라 거리≈0 → 기존 소프트 게이트가 그대로 전속 체결로 동작.
/// - 모든 바퀴가 IsAssembled가 된 뒤에만 리프트를 내리고 차량을 방출한다. 과거 workWindow 값은 더 이상 판정에 사용하지 않는다.
/// - 방출은 SetProgress로 차량을 게이트 진행도 위에 올려 통과 처리 후 isMoving=true.
/// </summary>
public class WheelStation : MonoBehaviour, ILineGate
{
    public bool flowThrough;
    public float workZoneMeters = 6f;

    public enum LiftState { Idle, Raising, Working, Lowering }

    [Header("라인 연결")]
    [Tooltip("비워두면 씬에서 'MainSpline' 이름의 오브젝트를 자동으로 찾습니다.")]
    public SplineContainer mainLineSpline;

    [Tooltip("켜면 이 오브젝트 위치에서 가장 가까운 스플라인 지점을 리프트 지점으로 사용 (Start에서 1회 계산)")]
    public bool autoProgressFromPosition = true;

    [Tooltip("리프트(정지) 지점의 스플라인 진행도(0~1). autoProgressFromPosition이 꺼져 있을 때만 직접 지정.")]
    [Range(0f, 1f)] public float liftProgress = 0.5f;

    [Header("리프트 연출")]
    [Tooltip("차량과 함께 승강할 리프트 기계 오브젝트 (선택). 로컬 Y로 애니메이션된다.")]
    public Transform liftPlatform;
    public float liftHeight = 0.6f;
    public float raiseDuration = 0.7f;
    public float lowerDuration = 0.5f;

    [Header("작업 (로봇팔 오케스트레이션)")]
    [Tooltip("바퀴 4개 스테이션. 인스펙터에 바인딩하면 자동으로 담당 바퀴 타입이 지정되고" +
        " 리프트 중심 기준으로 배치된다 (배열 순서 = 앞우/뒤우/앞좌/뒤좌).")]
    public StationController[] wheelStations;

    [Tooltip("자동 배치: 리프트 중심에서 앞/뒤 바퀴 스테이션까지의 라인 진행 방향 거리(m)." +
        " 앞바퀴 = +, 뒷바퀴 = - 로 중심 대칭 배치된다. 좌/우(lane)는 배치 SO의 robotLineSide를 따른다.")]
    public float stationAxialOffset = 1.5f;

    [Tooltip("동시에 가동할 팔 개수. 4개 wheelStations를 좌/우가 섞이도록 (앞우, 앞좌, 뒤우, 뒤좌) 순차 처리합니다.")]
    public int activeArmCount = 4;

    [Header("사람 작업 (기본 로봇 모드와 선택)")]
    public bool useHumanWorkers = false;
    [Tooltip("이 공정 전용 작업자 1~2명. 비활성 작업자는 추가 고용 대기 슬롯입니다.")]
    public Worker[] humanWorkers;
    [Tooltip("앞우/뒤우/앞좌/뒤좌 순서의 바퀴 재고 4개.")]
    public PartStack[] humanPartStacks;

    private bool humanCycle;
    private Worker[] cycleWorkers;
    private readonly Vector3[] humanHomes = new Vector3[2];
    private readonly int[] humanAssignments = { -1, -1 };
    private readonly bool[] humanParticipated = new bool[2];
    private readonly PartStack[] consumedStacks = new PartStack[4];

    /// <summary>구매 전환은 차량을 작업하지 않을 때만 허용한다. 시각 오브젝트 활성화는 호출자가 담당한다.</summary>
    public bool SetHumanMode(bool enabled)
    {
        if (currentState != LiftState.Idle) return false;
        useHumanWorkers = enabled;
        return true;
    }

    // Legacy scene compatibility only. Elapsed time never ends wheel assembly.
    [HideInInspector] public float workWindow = 6f;

    [Header("현재 상태 (디버그)")]
    public LiftState currentState = LiftState.Idle;
    [SerializeField] private CarController currentCar;

    // 게이트에 '도착'으로 판정하는 거리(m). 트래픽의 감속 도킹이 이 안까지 데려온다.
    private const float CaptureEpsilon = 0.1f;
    private const string MainSplineName = "MainSpline";

    /// <summary>트래픽 매니저가 참조하는 게이트 진행도. 이 앞의 차량은 여기를 넘지 못한다.</summary>
    public float GateProgress => liftProgress;
    /// <summary>ILineGate: 게이트 활성 여부.</summary>
    public bool GateEnabled => isActiveAndEnabled && !flowThrough;

    private float animT = 0f;          // 승강 애니메이션 진행도(0~1)
    private float workTimer = 0f;          // 작업 경과 시간(디버그 타임아웃 진단용)
    private bool workTimeoutReported = false;
    private float lastProgress;
    private bool stationSetupErrorReported = false;
    private CarController invalidPartsCar;
    private readonly AssemblyPart[] wheelParts = new AssemblyPart[4];
    private readonly int[] activeWaveIndices = new int[4];
    private int activeWaveCount = 0;
    private PartType[] originalStationPartTypes;
    private PartType[] originalArmPartTypes;
    private bool originalStationTypesCached = false;
    private Vector3 carBasePos;        // 캡처 시점 차량 위치 (승강 기준점)
    private float platformBaseY;       // 리프트 기계의 원래 로컬 Y
    private bool initialized = false;

    // 테스트 모드 전환 등으로 SetActive를 껐다 켜도 게이트가 다시 등록되도록 Start가 아닌
    // OnEnable에서 처리한다 (Start는 최초 활성화 때 한 번만 불려서, 재활성화 시
    // 게이트 미등록 상태로 차량이 정지 없이 그냥 통과해 버린다).
    private void OnEnable()
    {
        EnsureInit();
        if (autoProgressFromPosition && mainLineSpline != null)
            liftProgress = GetNearestProgress();
        LineTrafficManager.Instance.RegisterGate(this);
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
        if (mainLineSpline == null) Debug.LogError($"[WheelStation] '{MainSplineName}' 스플라인을 찾지 못했습니다.");

        if (liftPlatform != null) platformBaseY = liftPlatform.localPosition.y;
    }

    private void OnDisable()
    {
        CancelCycle(); // 사이클 도중 꺼지면 잡고 있던 차량을 내려놓고 정리
        var traffic = FindFirstObjectByType<LineTrafficManager>();
        if (traffic != null) traffic.UnregisterGate(this);
    }

    private float GetNearestProgress()
    {
        float3 localPoint = mainLineSpline.transform.InverseTransformPoint(transform.position);
        SplineUtility.GetNearestPoint(mainLineSpline.Spline, localPoint, out _, out float t);
        return Mathf.Clamp01(t);
    }

    private void Update()
    {
        if (flowThrough) { UpdateFlow(); return; }
        // 작업 도중 차량이 사라지면(풀 반환 등) 사이클 중단 후 초기화
        if (currentState != LiftState.Idle && (currentCar == null || !currentCar.gameObject.activeInHierarchy))
        {
            AbortCycle();
            return;
        }

        switch (currentState)
        {
            case LiftState.Idle:
                // 테스트 편의: 플레이 중 오브젝트를 옮기면 게이트 지점도 따라온다 (사이클 중에는 고정)
                if (autoProgressFromPosition && transform.hasChanged && mainLineSpline != null)
                {
                    liftProgress = GetNearestProgress();
                    transform.hasChanged = false;
                }
                TryCapture();
                break;

            case LiftState.Raising:
                animT += Time.deltaTime / Mathf.Max(0.01f, raiseDuration);
                ApplyLiftHeight(Mathf.SmoothStep(0f, liftHeight, animT));
                if (animT >= 1f) BeginWork();
                break;

            case LiftState.Working:
                workTimer += Time.deltaTime;
                float progress = 0f;
                foreach (var part in wheelParts) if (part != null) progress += part.Fill;
                if (progress > lastProgress + .00001f) { workTimer = 0f; lastProgress = progress; workTimeoutReported = false; }
                if (!workTimeoutReported && workTimer >= 30f)
                {
                    Debug.LogWarning($"[{name}] 바퀴 작업이 30초 동안 진행되지 않습니다. 팔 도달 범위와 연결을 확인하세요. 미완성 차량은 방출하지 않습니다.");
                    workTimeoutReported = true;
                }

                if (humanCycle)
                {
                    UpdateHumanWork();
                    break;
                }
                if (ActiveWaveIsAssembled())
                {
                    if (AllWheelPartsAssembled()) BeginLowering();
                    else StartNextWave();
                }
                break;

            case LiftState.Lowering:
                animT += Time.deltaTime / Mathf.Max(0.01f, lowerDuration);
                ApplyLiftHeight(Mathf.SmoothStep(liftHeight, 0f, animT));
                if (animT >= 1f) ReleaseCar();
                break;
        }
    }

    /// <summary>게이트에 도킹한 차량을 캡처한다. 설정/부품이 불완전하면 캡처하지 않고 게이트에 둔다.</summary>
    private void UpdateFlow()
    {
        if (invalidPartsCar != null && !StationWorkZone.Contains(invalidPartsCar, mainLineSpline, liftProgress, workZoneMeters)) invalidPartsCar = null;
        if (currentCar != null && !StationWorkZone.Contains(currentCar, mainLineSpline, liftProgress, workZoneMeters))
            EndFlowCycle();
        if (currentCar == null)
        {
            var car = StationWorkZone.Find(mainLineSpline, liftProgress, workZoneMeters);
            if (car == null || car == invalidPartsCar) return;
            if (!ValidateStationSetup(out _) || !CacheWheelParts(car)) { invalidPartsCar = car; return; }
            if (AllWheelPartsAssembled()) { invalidPartsCar = car; return; }
            humanCycle = useHumanWorkers;
            if (humanCycle)
            {
                cycleWorkers = (Worker[])humanWorkers.Clone();
                for (int i = 0; i < cycleWorkers.Length; i++)
                {
                    humanAssignments[i] = -1;
                    humanParticipated[i] = false;
                    if (cycleWorkers[i] != null) humanHomes[i] = cycleWorkers[i].transform.position;
                }
            }
            else CacheOriginalStationTypes();
            currentCar = car;
            BeginWork();
        }
        if (humanCycle) UpdateHumanWork();
        else if (ActiveWaveIsAssembled())
        {
            if (AllWheelPartsAssembled()) EndFlowCycle();
            else StartNextWave();
        }
    }

    private void EndFlowCycle()
    {
        invalidPartsCar = currentCar;
        for (int i = 0; i < wheelParts.Length; i++)
        {
            if (wheelParts[i] != null) wheelParts[i].StopWorldAssembly();
            consumedStacks[i] = null;
        }
        FinishHumanCycle(true);
        ResetWheelStationsAndRestoreTypes();
        currentCar = null;
        ClearWheelParts();
        currentState = LiftState.Idle;
    }

    private void TryCapture()
    {
        CarController car = LineTrafficManager.Instance.GetCarAtGate(liftProgress, CaptureEpsilon);
        if (car == null) return;

        if (!ValidateStationSetup(out string setupError))
        {
            if (!stationSetupErrorReported)
            {
                Debug.LogError($"[{name}] 바퀴 리프트 설정 오류: {setupError} 차량을 게이트에 유지합니다.");
                stationSetupErrorReported = true;
            }
            return;
        }
        stationSetupErrorReported = false;

        if (car == invalidPartsCar) return;
        if (!CacheWheelParts(car))
        {
            invalidPartsCar = car;
            Debug.LogError($"[{name}] {car.name}에서 휠 파츠 4종을 정확히 찾지 못했습니다. 미완성 방출을 막기 위해 차량을 게이트에 유지합니다.");
            return;
        }

        humanCycle = useHumanWorkers;
        if (!humanCycle) CacheOriginalStationTypes();
        if (AllWheelPartsAssembled())
        {
            ClearWheelParts();
            PassGate(car);
            return;
        }

        if (humanCycle)
        {
            cycleWorkers = (Worker[])humanWorkers.Clone();
            for (int i = 0; i < cycleWorkers.Length; i++)
            {
                humanAssignments[i] = -1;
                humanParticipated[i] = false;
                if (cycleWorkers[i] != null)
                {
                    humanHomes[i] = cycleWorkers[i].transform.position;
                    cycleWorkers[i].SetHome(humanHomes[i]);
                }
            }
        }
        currentCar = car;
        currentCar.isMoving = false;
        carBasePos = currentCar.transform.position;
        animT = 0f;
        currentState = LiftState.Raising;
    }

    private bool ValidateStationSetup(out string error)
    {
        if (useHumanWorkers)
        {
            if (humanWorkers == null || humanWorkers.Length < 1 || humanWorkers.Length > 2)
            { error = "사람 작업자는 1~2명이어야 합니다."; return false; }
            bool hasWorker = false;
            for (int i = 0; i < humanWorkers.Length; i++)
            {
                if (humanWorkers[i] == null) continue;
                if (i > 0 && humanWorkers[0] == humanWorkers[i])
                { error = "사람 작업자가 중복 연결됐습니다."; return false; }
                hasWorker |= humanWorkers[i].isActiveAndEnabled;
            }
            if (!hasWorker) { error = "활성 사람 작업자가 없습니다."; return false; }
            if (humanPartStacks == null || humanPartStacks.Length != 4)
            { error = "바퀴 재고 파일 4개가 필요합니다."; return false; }
            for (int i = 0; i < 4; i++)
                if (humanPartStacks[i] == null || humanPartStacks[i].partType != WheelTypes[i])
                { error = $"humanPartStacks[{i}]는 {WheelTypes[i]} 재고여야 합니다."; return false; }
            error = null;
            return true;
        }
        if (wheelStations == null || wheelStations.Length != WheelTypes.Length)
        {
            error = $"wheelStations는 4개가 필요합니다 (현재 {(wheelStations != null ? wheelStations.Length : 0)}개).";
            return false;
        }
        if (activeArmCount < 1 || activeArmCount > WheelTypes.Length)
        {
            error = $"activeArmCount는 1~{WheelTypes.Length} 범위여야 합니다 (현재 {activeArmCount}).";
            return false;
        }

        for (int i = 0; i < WheelTypes.Length; i++)
        {
            StationController station = wheelStations[i];
            if (station == null)
            {
                error = $"wheelStations[{i}] ({WheelTypes[i]})가 비어 있습니다.";
                return false;
            }
            for (int j = 0; j < i; j++)
            {
                if (wheelStations[j] == station)
                {
                    error = $"{station.name} 스테이션이 wheelStations[{j}]와 [{i}]에 중복 연결됐습니다.";
                    return false;
                }
            }
            if (station.robotArm == null)
            {
                error = $"{station.name} ({WheelTypes[i]})에 로봇팔이 없습니다.";
                return false;
            }
            if (flowThrough && (!station.isActiveAndEnabled || !station.robotArm.gameObject.activeInHierarchy))
            {
                error = "활성 로봇팔이 없습니다.";
                return false;
            }
            if (station.targetPartType != WheelTypes[i] || station.robotArm.targetPartType != WheelTypes[i])
            {
                error = $"wheelStations[{i}]는 {WheelTypes[i]} 전용이어야 합니다 (station={station.targetPartType}, arm={station.robotArm.targetPartType}).";
                return false;
            }
        }

        error = null;
        return true;
    }

    private bool CacheWheelParts(CarController car)
    {
        for (int i = 0; i < wheelParts.Length; i++) wheelParts[i] = null;

        AssemblyPart[] parts = car.GetComponentsInChildren<AssemblyPart>(true);
        for (int i = 0; i < parts.Length; i++)
        {
            AssemblyPart part = parts[i];
            if (part == null) continue;
            int wheelIndex = GetWheelIndex(part.myType);
            if (wheelIndex < 0) continue;
            if (wheelParts[wheelIndex] != null) return false;
            wheelParts[wheelIndex] = part;
        }

        for (int i = 0; i < wheelParts.Length; i++)
            if (wheelParts[i] == null) return false;
        return true;
    }

    private static int GetWheelIndex(PartType type)
    {
        for (int i = 0; i < WheelTypes.Length; i++)
            if (WheelTypes[i] == type) return i;
        return -1;
    }

    private bool AllWheelPartsAssembled()
    {
        for (int i = 0; i < wheelParts.Length; i++)
            if (wheelParts[i] == null || !wheelParts[i].IsAssembled) return false;
        return true;
    }

    private void CacheOriginalStationTypes()
    {
        if (originalStationTypesCached) return;
        originalStationPartTypes = new PartType[wheelStations.Length];
        originalArmPartTypes = new PartType[wheelStations.Length];
        for (int i = 0; i < wheelStations.Length; i++)
        {
            originalStationPartTypes[i] = wheelStations[i].targetPartType;
            originalArmPartTypes[i] = wheelStations[i].robotArm.targetPartType;
        }
        originalStationTypesCached = true;
    }

    private void ResetWheelStationsAndRestoreTypes()
    {
        if (wheelStations != null)
        {
            for (int i = 0; i < wheelStations.Length; i++)
                if (wheelStations[i] != null) wheelStations[i].ResetStation();
        }

        if (!originalStationTypesCached || wheelStations == null) return;
        int count = Mathf.Min(wheelStations.Length, originalStationPartTypes.Length);
        for (int i = 0; i < count; i++)
        {
            StationController station = wheelStations[i];
            if (station == null) continue;
            station.targetPartType = originalStationPartTypes[i];
            if (station.robotArm != null) station.robotArm.targetPartType = originalArmPartTypes[i];
        }
    }

    private void ClearWheelParts()
    {
        for (int i = 0; i < wheelParts.Length; i++) wheelParts[i] = null;
        activeWaveCount = 0;
    }
    /// <summary>차량(+리프트 기계)의 승강 높이를 적용한다.</summary>
    private void ApplyLiftHeight(float height)
    {
        if (currentCar != null)
            currentCar.transform.position = carBasePos + Vector3.up * height;

        if (liftPlatform != null)
        {
            Vector3 lp = liftPlatform.localPosition;
            lp.y = platformBaseY + height;
            liftPlatform.localPosition = lp;
        }
    }

    /// <summary>리프트가 올라간 뒤, 최대 activeArmCount개 바퀴를 양쪽이 섞인 순서로 시작한다.</summary>
    private void BeginWork()
    {
        workTimer = 0f;
        lastProgress = 0f;
        workTimeoutReported = false;
        currentState = LiftState.Working;
        if (humanCycle) UpdateHumanWork();
        else StartNextWave();
    }

    private void UpdateHumanWork()
    {
        if (cycleWorkers == null) return;
        bool busy = false;
        for (int w = 0; w < cycleWorkers.Length; w++)
        {
            Worker worker = cycleWorkers[w];
            int assigned = humanAssignments[w];
            if (assigned >= 0 && wheelParts[assigned] != null && wheelParts[assigned].IsAssembled)
            {
                Vector3 rewardPos = wheelParts[assigned].transform.position + Vector3.up * .5f;
                int reward = StationConfig.Instance.partReward;
                EconomyManager.Instance.Earn(reward, rewardPos);
                CashPopup.Show(rewardPos, reward);
                if (ProductionProgression.Current != null) ProductionProgression.Current.MarkExperienced(wheelParts[assigned].myType);
                consumedStacks[assigned] = null;
                humanAssignments[w] = -1;
                humanParticipated[w] = true;
            }
            if (worker == null) continue;
            worker.carDocked = true;
            worker.stockBlocked = false;
            busy |= worker.IsBusy;
            if (!worker.isActiveAndEnabled || !worker.IsAvailable || humanAssignments[w] >= 0) continue;
            for (int order = 0; order < WheelSelectionOrder.Length; order++)
            {
                int index = WheelSelectionOrder[order];
                AssemblyPart part = wheelParts[index];
                if (part == null || part.IsAssembled || humanAssignments[0] == index || humanAssignments[1] == index) continue;
                // A staffed pair stays on its original side of the raised car. An inactive
                // second hiring slot still permits the single-worker fallback to do all four.
                bool paired = cycleWorkers.Length == 2
                    && cycleWorkers[0] != null && cycleWorkers[0].isActiveAndEnabled
                    && cycleWorkers[1] != null && cycleWorkers[1].isActiveAndEnabled;
                if (paired)
                {
                    Vector3 localHome = currentCar.transform.InverseTransformPoint(humanHomes[w]);
                    Vector3 localWheel = currentCar.transform.InverseTransformPoint(
                        part.transform.parent.TransformPoint(part.assembledPos));
                    if ((localHome.x < 0f) != (localWheel.x < 0f)) continue;
                }
                PartStack stack = humanPartStacks[index];
                Vector3 pickPos = stack.GetTopSlotWorldPos();
                Quaternion pickRot = stack.GetTopSlotWorldRot();
                if (!stack.TryConsume()) { worker.stockBlocked = true; continue; }
                // Worker chooses the approach side from home. A single worker must approach
                // each wheel from its own outer side, including wheels across the car.
                Vector3 wheelHome = part.transform.parent.TransformPoint(part.assembledPos);
                wheelHome += currentCar.transform.right * (part.assembledPos.x < 0f ? -2f : 2f);
                wheelHome.y = humanHomes[w].y;
                worker.SetHome(wheelHome);
                if (!worker.AssignWork(part, pickPos, pickRot, currentCar.transform.position, currentCar.transform.rotation, true))
                { stack.Add(1); continue; }
                humanAssignments[w] = index;
                if (flowThrough) part.SetStationWorkWindow(mainLineSpline, liftProgress, workZoneMeters);
                consumedStacks[index] = stack;
                worker.stockBlocked = false;
                busy = true;
                break;
            }
        }
        if (AllWheelPartsAssembled() && !busy) BeginLowering();
    }

    private void FinishHumanCycle(bool cancelled)
    {
        if (!humanCycle || cycleWorkers == null) return;
        for (int w = 0; w < cycleWorkers.Length; w++)
        {
            Worker worker = cycleWorkers[w];
            if (worker != null)
            {
                worker.CancelWork();
                worker.SetHome(humanHomes[w]);
                worker.carDocked = false;
                worker.stockBlocked = false;
                if (!cancelled && humanParticipated[w]) worker.ConsumeConditionForCar();
            }
            humanAssignments[w] = -1;
            humanParticipated[w] = false;
        }
        for (int i = 0; i < consumedStacks.Length; i++)
        {
            if (consumedStacks[i] != null && (wheelParts[i] == null || !wheelParts[i].IsAssembled))
            {
                consumedStacks[i].Add(1);
                if (wheelParts[i] != null) wheelParts[i].SetWork(0f);
            }
            consumedStacks[i] = null;
        }
        cycleWorkers = null;
    }

    private void StartNextWave()
    {
        activeWaveCount = 0;
        int waveCapacity = Mathf.Clamp(activeArmCount, 1, WheelTypes.Length);
        for (int order = 0; order < WheelSelectionOrder.Length; order++)
        {
            int wheelIndex = WheelSelectionOrder[order];
            if (wheelParts[wheelIndex] == null || wheelParts[wheelIndex].IsAssembled) continue;
            if (activeWaveCount >= waveCapacity) break;

            StationController station = wheelStations[wheelIndex];
            activeWaveIndices[activeWaveCount++] = wheelIndex;
            station.SetPartTypeAndStart(WheelTypes[wheelIndex], currentCar);
            if (station.currentState != StationController.StationState.Assembling)
            {
                Debug.LogError($"[{name}] {WheelTypes[wheelIndex]} 체결을 시작하지 못했습니다. 4개 바퀴가 모두 체결될 때까지 차량을 유지합니다.");
            }
        }

        if (activeWaveCount == 0)
        {
            if (AllWheelPartsAssembled()) BeginLowering();
            else Debug.LogError($"[{name}] 미체결 바퀴가 남았지만 시작할 팔을 찾지 못했습니다. 차량을 유지합니다.");
        }
    }

    private bool ActiveWaveIsAssembled()
    {
        if (activeWaveCount == 0) return false;
        for (int i = 0; i < activeWaveCount; i++)
        {
            AssemblyPart part = wheelParts[activeWaveIndices[i]];
            if (part == null || !part.IsAssembled) return false;
        }
        return true;
    }

    private void BeginLowering()
    {
        if (flowThrough) { EndFlowCycle(); return; }
        if (!AllWheelPartsAssembled())
        {
            Debug.LogError($"[{name}] 바퀴 4개가 모두 체결되지 않아 리프트 하강을 차단했습니다.");
            return;
        }

        ResetWheelStationsAndRestoreTypes();
        activeWaveCount = 0;
        animT = 0f;
        currentState = LiftState.Lowering;
    }
    private void ReleaseCar()
    {
        if (!AllWheelPartsAssembled())
        {
            Debug.LogError($"[{name}] 방출 직전 휠 체결 확인 실패. 미완성 출고를 막기 위해 차를 다시 올려 대기합니다.");
            animT = 0f;
            currentState = LiftState.Raising;
            return;
        }

        ApplyLiftHeight(0f);
        ResetWheelStationsAndRestoreTypes();
        FinishHumanCycle(false);
        PassGate(currentCar);
        currentCar = null;
        ClearWheelParts();
        currentState = LiftState.Idle;
    }
    /// <summary>차량을 게이트 진행도 위에 올려 통과 처리하고 재출발시킨다.</summary>
    private void PassGate(CarController car)
    {
        if (car == null) return;
        car.SetProgress(liftProgress); // progress ≥ 게이트 → 트래픽의 게이트 클램프에서 제외된다
        car.isMoving = true;
    }

    /// <summary>
    /// 진행 중인 사이클을 즉시 중단하고 초기 상태로 되돌린다. 잡고 있던 차량은 원래 높이로
    /// 내려놓고 재출발시킨다(게이트 통과 처리는 하지 않음 — 게이트가 살아 있으면 다시 잡힌다).
    /// 테스트 모드 전환(SetActive(false))이나 차량 리셋 전에 외부에서 호출한다.
    /// </summary>
    public void CancelCycle()
    {
        if (flowThrough) { EndFlowCycle(); return; }
        if (currentState == LiftState.Idle) return;

        if (currentCar != null && currentCar.gameObject.activeInHierarchy)
        {
            ApplyLiftHeight(0f);
            currentCar.isMoving = true;
        }
        AbortCycle();
    }

    private void AbortCycle()
    {
        FinishHumanCycle(true);
        if (currentCar != null && currentCar.gameObject.activeInHierarchy)
            ApplyLiftHeight(0f);

        ResetWheelStationsAndRestoreTypes();
        currentCar = null;
        ClearWheelParts();
        if (liftPlatform != null)
        {
            Vector3 lp = liftPlatform.localPosition;
            lp.y = platformBaseY;
            liftPlatform.localPosition = lp;
        }
        animT = 0f;
        workTimer = 0f;
        workTimeoutReported = false;
        currentState = LiftState.Idle;
    }
    #region 바퀴 스테이션 자동 배치

    // wheelStations 인덱스 → 담당 바퀴. 배열에 바인딩만 하면 이 순서로 타입이 자동 지정된다.
    private static readonly int[] WheelSelectionOrder = { 0, 2, 1, 3 }; // 좌/우를 먼저 하나씩 선택

    private static readonly PartType[] WheelTypes =
    {
        PartType.Wheel_FrontRight_41,
        PartType.Wheel_BehindRight_42,
        PartType.Wheel_FrontLeft_43,
        PartType.Wheel_BehindLeft_44,
    };

    private static bool IsLeftWheel(PartType type) =>
        type == PartType.Wheel_FrontLeft_43 || type == PartType.Wheel_BehindLeft_44;

    /// <summary>
    /// 바인딩된 바퀴 스테이션들을 리프트 중심 기준으로 라인에 맞게 배치한다:
    /// - targetPartType = 배열 순서(앞우/뒤우/앞좌/뒤좌)로 자동 지정
    /// - 좌/우(lane)는 배치 SO(placementData)에 저장된 robotLineSide — 없으면 바퀴 이름의 좌/우로 폴백
    /// - X(라인 진행 방향)는 리프트 중심에서 앞바퀴 +stationAxialOffset / 뒷바퀴 -stationAxialOffset 대칭
    /// - z·회전·작업존 center는 ApplyLineSide가 현재 LINE 기준으로 계산
    /// 인스펙터에서 wheelStations/stationAxialOffset을 바꾸면 OnValidate가 자동 호출한다.
    /// </summary>
    [ContextMenu("바퀴 스테이션 자동 배치")]
    public void PlaceWheelStations()
    {
        if (wheelStations == null) return;

        // 라인 탐색: 자신의 상위 우선, 없으면 스테이션들의 상위
        LineSettings myLine = GetComponentInParent<LineSettings>();
        LineSettings line = myLine;
        bool anyBound = false;
        for (int i = 0; i < wheelStations.Length; i++)
        {
            if (wheelStations[i] == null) continue;
            anyBound = true;
            if (line == null) line = wheelStations[i].FindLine();
        }
        if (!anyBound) return;
        if (line == null)
        {
            Debug.LogWarning($"[{name}] 상위/스테이션에서 LineSettings를 찾지 못해 자동 배치를 건너뜁니다." +
                " WheelStation 또는 스테이션을 LINE 오브젝트 자식으로 두세요.");
            return;
        }

        // 진행 방향 부호: 배치기 규칙과 동일 (Left = X 감소, Right = X 증가)
        float travelSign = line.direction == LineSettings.Direction.Left ? -1f : 1f;

        int n = Mathf.Min(wheelStations.Length, WheelTypes.Length);
        for (int i = 0; i < n; i++)
        {
            StationController st = wheelStations[i];
            if (st == null) continue;

            PartType wheelType = WheelTypes[i];
            st.targetPartType = wheelType;
            if (st.robotArm != null) st.robotArm.targetPartType = wheelType;

            // 이 리프트가 LINE 자식이면 스테이션들을 리프트 자식으로 편입:
            // WheelStation SetActive 토글에 4개 팔이 함께 켜지고 꺼지며,
            // ApplyLineSide도 리프트의 상위 LINE을 그대로 찾는다.
            // (리프트가 LINE 밖이면 계층은 건드리지 않고 스테이션 자신의 상위 LINE을 사용)
            if (myLine != null && st.transform.parent != transform)
                st.transform.SetParent(transform, true);
            else if (myLine == null && st.FindLine() == null)
                st.transform.SetParent(line.transform, true);

            // 좌/우 배치: SO에 저장된 robotLineSide 우선 (PilePos/EndPos/작업존 크기도 함께 적용)
            if (st.placementData != null && st.placementData.Has(wheelType))
                st.ApplyPlacement(st.placementData.GetPlacement(wheelType));
            else
                st.SetRobotLineSide(IsLeftWheel(wheelType) ? RobotLineSideType.Left : RobotLineSideType.Right);

            // X = 리프트 중심 기준 앞/뒤 등간격 (앞바퀴 = 진행 방향 앞)
            bool isFront = wheelType == PartType.Wheel_FrontRight_41 || wheelType == PartType.Wheel_FrontLeft_43;
            Vector3 pos = st.transform.position;
            pos.x = transform.position.x + travelSign * (isFront ? stationAxialOffset : -stationAxialOffset);
            st.transform.position = pos;

            st.ApplyLineSide(); // z·회전·작업존 center를 현재 라인 기준으로

            // 작업존 트리거는 끈다 — 게이트 대기(정지) 중 트리거 진입만으로 조기 체결되는 것 방지.
            // 바퀴 체결은 이 공정이 SetPartTypeAndStart(manualMode)로 직접 시작한다.
            if (st.TryGetComponent<BoxCollider>(out var box)) box.enabled = false;
        }
    }

#if UNITY_EDITOR
    // 인스펙터 바인딩/간격 변경 감지용 — 마지막으로 배치를 적용한 입력값
    [SerializeField, HideInInspector] private StationController[] appliedWheelStations;
    [SerializeField, HideInInspector] private float appliedAxialOffset = -1f;

    private void OnValidate()
    {
        if (Application.isPlaying) return;
        if (!PlacementInputChanged()) return;

        appliedWheelStations = wheelStations != null ? (StationController[])wheelStations.Clone() : null;
        appliedAxialOffset = stationAxialOffset;

        // OnValidate 도중 Transform·부모 변경은 경고가 날 수 있어 다음 에디터 틱으로 미룬다 (StationController와 동일 패턴)
        EditorApplication.delayCall += () =>
        {
            if (this == null) return;
            PlaceWheelStations();
            // 배치 결과(스테이션 이동/타입 변경)가 씬 저장에 포함되도록 더티 처리
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        };
    }

    private bool PlacementInputChanged()
    {
        if (!Mathf.Approximately(appliedAxialOffset, stationAxialOffset)) return true;

        int len = wheelStations != null ? wheelStations.Length : 0;
        int appliedLen = appliedWheelStations != null ? appliedWheelStations.Length : 0;
        if (len != appliedLen) return true;
        for (int i = 0; i < len; i++)
            if (wheelStations[i] != appliedWheelStations[i]) return true;
        return false;
    }
#endif

    #endregion

    // 리프트 지점을 씬 뷰에 표시: 스플라인 위 정지 지점에 차량 크기 와이어 박스 + 승강 범위.
    private void OnDrawGizmosSelected()
    {
        SplineContainer spline = mainLineSpline;
        if (spline == null)
        {
            GameObject go = GameObject.Find(MainSplineName);
            if (go != null) go.TryGetComponent(out spline);
        }
        if (spline == null) return;

        // 에디터(비플레이)에서는 autoProgress 지점을 실시간 미리보기
        float t = liftProgress;
        if (!Application.isPlaying && autoProgressFromPosition)
        {
            float3 localPoint = spline.transform.InverseTransformPoint(transform.position);
            SplineUtility.GetNearestPoint(spline.Spline, localPoint, out _, out float nearest);
            t = Mathf.Clamp01(nearest);
        }

        spline.Evaluate(t, out float3 wp, out float3 wt, out float3 wu);
        Vector3 pos = (Vector3)wp;
        Quaternion rot = ((Vector3)wt).sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation((Vector3)wt, (Vector3)wu)
            : Quaternion.identity;

        // 오브젝트 → 리프트 지점 연결선
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(transform.position, pos);

        // 정지 지점 차량 박스 (상태 색: 대기=청록 / 작업=주황)
        Vector3 carSize = new Vector3(2f, 1.5f, 4.5f);
        Matrix4x4 old = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(pos + rot * new Vector3(0f, carSize.y * 0.5f, 0f), rot, Vector3.one);
        Gizmos.color = currentState == LiftState.Idle ? new Color(0f, 1f, 1f, 0.9f) : new Color(1f, 0.5f, 0f, 0.9f);
        Gizmos.DrawWireCube(Vector3.zero, carSize);
        Gizmos.matrix = old;

        // 승강 범위 표시
        Gizmos.color = Color.green;
        Gizmos.DrawLine(pos, pos + Vector3.up * liftHeight);

        // 캡처 존: 게이트 직전 CaptureEpsilon(m) 구간 — 이 안까지 도킹한 차량만 TryCapture가 잡는다.
        // 트래픽의 정지점(게이트 −1cm)이 이 구간 안에 들어와야 정상 동작 (5cm라 확대해야 보임)
        Vector3 captureCenter = pos - rot * Vector3.forward * (CaptureEpsilon * 0.5f);
        Gizmos.color = Color.magenta;
        Gizmos.matrix = Matrix4x4.TRS(captureCenter + Vector3.up * 0.25f, rot, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(carSize.x, 0.5f, CaptureEpsilon));
        Gizmos.matrix = old;

#if UNITY_EDITOR
        Handles.Label(pos + Vector3.up * (liftHeight + 0.3f),
            $"WheelStation [{currentState}] t={t:F3}");
        Handles.Label(captureCenter + Vector3.up * 0.1f, $"캡처존 {CaptureEpsilon:F2}m");
#endif
    }
}
