using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class StationController : MonoBehaviour
{
    public enum StationState { Idle, Assembling, Cooldown }

    [Header("공정 설정")]
    public PartType targetPartType;

    // 체결 속도/사거리는 StationConfig(전역 싱글톤)에서 일괄 관리한다.
    private StationConfig config;

    [Header("오브젝트 연결")]
    public RoboticArmIK robotArm;
    public Transform stationPilePos;

    [Tooltip("체결 완료/리셋 후 로봇팔이 대기하는 위치 (미설정 시 PilePos 사용)")]
    public Transform endPos;

    [Tooltip("씬에 배치된 스테이션 파츠 오브젝트 (코드로 생성하지 않음)")]
    public GameObject stationPileMesh;

    public Transform trackingTarget;


    [Header("라인 좌/우 배치 (상위 LineSettings 기준)")]
    [Tooltip("인스펙터에서 값을 바꾸면 상위 LINE 오브젝트의 LineSettings 기준으로 즉시 재배치된다" +
        " (z=startZ±zSpacing / 회전 Y=방향인지 ±90 / 작업존 center.x·z). PilePos·EndPos는 자식이라 회전에 함께 따라감.")]
    public RobotLineSideType robotLineSide = RobotLineSideType.Left;

    [Tooltip("라인 파생 작업존 center 기본값(x=zSpacing, y=0.5, z=±laneWidth)에 더해지는 부품별 추가 오프셋." +
        " x·y는 그대로 더하고, z는 robotLineSide 부호에 맞춰 대칭으로 더한다(+z = laneWidth가 커지는 방향)." +
        " 스테이션 회전이 라인 방향/side를 이미 보정하므로 어느 라인에 배치해도 같은 의미로 동작한다.")]
    public Vector3 boxCenterOffset = Vector3.zero;

    [Tooltip("이 부품 타입의 배치를 저장/불러올 SO. 컨텍스트 메뉴 '배치 저장/불러오기'에 사용.")]
    public StationPlacementDataSO placementData;

    // 인스펙터에서 robotLineSide/boxCenterOffset이 바뀌었는지 감지하기 위한 마지막 적용값.
    [SerializeField, HideInInspector] private RobotLineSideType appliedSide = RobotLineSideType.Left;
    [SerializeField, HideInInspector] private Vector3 appliedBoxCenterOffset = Vector3.zero;

    [Header("현재 공정 상태")]
    public StationState currentState = StationState.Idle;

    [SerializeField] private CarController currentCar;
    [SerializeField] private AssemblyPart targetCarPart;

    [Header("작업존 기즈모 (BoxCollider = 체결 사거리)")]
    [Tooltip("씬 뷰에서 BoxCollider 범위를 박스로 표시해 배치/크기 조정을 쉽게 한다")]
    public bool drawWorkZoneGizmo = true;
    public Color workZoneColor = new Color(0f, 1f, 0.4f, 1f);

    [Header("복귀 경로 미리보기 기즈모")]
    [Tooltip("플레이 중, '지금 이 순간 복귀를 시작하면' 팔끝(endEffector)이 그리게 될 경로를 시뮬레이션해 표시." +
        " 가상 타겟 슬라이드 + IK 관절 속도제한/리밋까지 실제와 동일 로직으로 미래 프레임을 계산한다." +
        " 노랑(시작)→파랑(도착) 그라데이션, 경로 최저점은 빨간 구+높이 라벨.")]
    public bool drawReturnPreviewGizmo = true;

    [Header("대기 위치 기즈모 (PilePos / EndPos)")]
    [Tooltip("씬 뷰에서 PilePos(파츠 대기)·EndPos(로봇팔 대기) 위치를 구로 표시. 핸들로 드래그 편집 가능")]
    public bool drawRestGizmo = true;
    public float restGizmoRadius = 0.15f;
    public Color pilePosColor = new Color(1f, 0.6f, 0.1f, 1f); // 주황: 파츠 대기장소
    public Color endPosColor = new Color(0.2f, 0.6f, 1f, 1f);  // 파랑: 로봇팔 대기장소


    private float cooldownTimer = 0f;
    private bool manualMode = false;
    private bool reachEngaged = false;     // 팔이 threshold 이내로 한 번 도달했는지 (최초 캐치 latch)
    private float reachWorkFactor = 0f;    // 현재 거리 기반 체결 속도 배율 0~1 (기즈모 표시용)

    // 현재 작업존(트리거) 안에 들어와 있는 차량들. 스테이션이 비면 이 중에서 다음 대상을 고른다.
    private readonly List<CarController> carsInZone = new List<CarController>();


    private Transform GetRestTarget() => endPos != null ? endPos : stationPilePos;

    private void Start()
    {
        config = StationConfig.Instance; // 전역 설정 참조를 1회 캐싱 (Update마다 getter 호출 방지)

        if (robotArm != null && stationPilePos != null)
        {
            // 팔은 항상 가상 타겟(trackingTarget)만 추적한다 — 목표 전환은 타겟 자체의
            // 연속 이동(UpdateTrackingTarget)으로 처리해 팔끝 경로를 통제.
            robotArm.SetTarget(EnsureTrackingTarget());
            robotArm.targetPartType = this.targetPartType;
        }
    }

    /// <summary>가상 타겟을 (없으면 생성해) 반환한다. 생성 시 대기 위치(EndPos)에서 시작.</summary>
    private Transform EnsureTrackingTarget()
    {
        if (trackingTarget == null)
        {
            trackingTarget = new GameObject($"{gameObject.name}_TrackingTarget").transform;
            trackingTarget.SetParent(transform, false); // 스테이션 자식으로 배치(하이러키 정리, 함께 파괴)
            trackingTarget.rotation = Quaternion.identity; // 스테이션이 라인 좌/우에 따라 Y ±90° 회전돼 있어도 상속받지 않도록 월드 회전 고정
            Transform rest = GetRestTarget();
            if (rest != null) trackingTarget.position = rest.position;
        }
        return trackingTarget;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.TryGetComponent<CarController>(out var car) == false) return;

        // 진입 차량을 작업존 목록에 등록. (스테이션이 바쁘면 대기, 비면 TryBeginAssembly가 잡음)
        if (!carsInZone.Contains(car)) carsInZone.Add(car);

        if (!manualMode) TryBeginAssembly();
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.TryGetComponent<CarController>(out var car) == false) return;

        carsInZone.Remove(car);

        if (manualMode) return; // 수동 모드에서는 PhysX 이벤트 무시

        // 체결 중이던 차량이 작업존을 벗어남 → 체결 포기 후 제자리 복귀
        if (car == currentCar)
        {
            ResetStation();
            TryBeginAssembly(); // 아직 작업존에 남은 다른 차량이 있으면 즉시 재시도
        }
    }

    /// <summary>스테이션이 비어 있으면(Idle) 작업존 안 차량 중 이 공정의 미체결 파츠가 있는 차량으로 조립 시작.</summary>
    private void TryBeginAssembly()
    {
        if (currentState != StationState.Idle || robotArm == null) return;

        // 풀로 반환됐거나 파괴된 차량 정리
        carsInZone.RemoveAll(c => c == null || !c.gameObject.activeInHierarchy);

        foreach (CarController car in carsInZone)
        {
            AssemblyPart part = car.GetUnassembledPart(targetPartType);
            if (part == null) continue; // 해당 차량은 이 공정 파츠가 없거나 이미 체결됨

            currentCar = car;
            targetCarPart = part;
            StartAssembly();
            return;
        }
    }

    private void Update()
    {
        if (robotArm == null) return;

        // 체결 중 차량/파츠 참조가 사라졌으면(풀 반환 등) 리셋 후 재시도
        if (currentState == StationState.Assembling && (currentCar == null || targetCarPart == null))
        {
            bool wasManual = manualMode;
            ResetStation();
            if (!wasManual) TryBeginAssembly();
        }

        switch (currentState)
        {
            case StationState.Assembling:
                // [판정] 로봇팔 끝(Rig_End=endEffector)이 파츠 ArmLookTarget에 충분히 닿았을 때만 체결 진행.
                // 작업존에 들어와도 팔이 아직 못 닿았으면 대기(준비)하고, 닿으면 체결값 증가,
                // 차량이 멀어져 거리가 벌어지면 다시 증가가 멈춘다. 완성 시간 = requiredWork / assembleSpeed
                float reachDist = robotArm.endEffector != null
                    ? Vector3.Distance(robotArm.endEffector.position, targetCarPart.GetArmLookWorldPos())
                    : float.MaxValue;

                // 최초 캐치(latch): 팔 끝이 threshold 이내로 실제 도달해야 체결이 시작된다.
                // (스폰 직후 대기위치가 어중간하게 가까우면 감속 구간의 부분 속도만으로
                //  팔이 오기도 전에 슬금슬금 시작되는 것 방지)
                if (!reachEngaged && reachDist <= config.assembleReachThreshold) reachEngaged = true;

                // 소프트 게이트: threshold 이내=전속(1), threshold+margin 이상=정지(0), 사이는 선형 감속.
                // 이진 ON/OFF 게이트는 파츠가 팔보다 빠를 때 '전진→정지→전진' 스텝이 반복되며
                // 파츠·팔이 함께 덜덜거림 — 거리에 비례해 체결 속도를 줄이면 파츠가
                // 팔이 따라오는 속도로 자동 수렴해 연속적으로 부드럽게 움직인다.
                reachWorkFactor = reachEngaged
                    ? 1f - Mathf.InverseLerp(
                        config.assembleReachThreshold,
                        config.assembleReachThreshold + config.reachReleaseMargin,
                        reachDist)
                    : 0f;

                if (reachWorkFactor > 0f)
                    targetCarPart.AddWork(config.assembleSpeed * reachWorkFactor * Time.deltaTime);

                if (targetCarPart.IsAssembled)
                {
                    targetCarPart.SetAssembled(); // 완전히 체결 완료 위치에 고정
                    Debug.Log($"[{gameObject.name}] ✅ {targetCarPart.name} 체결 완전 성공!");

                    targetCarPart = null;
                    currentCar = null;

                    if (stationPileMesh) stationPileMesh.SetActive(true);
                    // 대기 위치 복귀는 UpdateTrackingTarget의 가상 타겟 슬라이드가 처리 (goal이 EndPos로 전환)
                    cooldownTimer = 1.0f;
                    currentState = StationState.Cooldown;
                }
                break;

            case StationState.Cooldown:
                cooldownTimer -= Time.deltaTime;
                if (cooldownTimer <= 0f)
                {
                    currentState = StationState.Idle;
                    // 쿨다운 후에도 작업존에 남아있는 차량이 있으면 다음 체결 시도
                    if (!manualMode) TryBeginAssembly();
                }
                break;
        }

        UpdateTrackingTarget();
    }

    // 가상 타겟 슬라이드: 팔이 추적하는 점을 순간이동 없이 목표(체결 중=파츠 ArmLookTarget,
    // 그 외=EndPos)로 연속 이동시킨다. 타겟이 한 프레임에 점프하면 CCD IK가 큰 오차를
    // 제멋대로의 관절 원호로 메우며 팔끝이 바닥을 뚫거나 위로 휘두른다 — 팔이 추적하는
    // 점 자체를 미끄러뜨리면 팔끝 경로가 직선에 가깝게 통제된다.
    // 복귀 중 새 체결이 시작돼도 현재 위치에서 새 목표로 방향만 꺾는다(모든 전환 공통).
    private void UpdateTrackingTarget()
    {
        if (trackingTarget == null) return;

        Vector3 goal = (currentState == StationState.Assembling && targetCarPart != null)
            ? targetCarPart.GetArmLookWorldPos()
            : GetRestTarget().position;

        trackingTarget.position = Vector3.MoveTowards(
            trackingTarget.position, goal, config.trackingTargetSpeed * Time.deltaTime);
    }

    /// <summary>파츠의 분리 위치를 stationPilePos로 설정하고 조립을 시작한다.</summary>
    private void StartAssembly()
    {
        // 1. 월드 조립 시작: pile(이 스테이션의 stationPilePos, 월드 고정)에서 출발하도록 설정
        // 시작 회전 오프셋 = stationPilePos.localRotation (배치 SO의 pileLocalEuler로 부품별 저장/적용).
        // '부착(도착) 회전 기준 상대값'이라 차량/라인 진행 방향과 무관하게 같은 연출 —
        // 예: Y=90이면 "부착 방향에서 90도 꺾여서 시작", 0이면 회전 연출 없음.
        // 저작값은 Left 스테이션 기준 — Right는 Y 부호를 반전해야 반대로 돌아 로봇팔을 뚫지 않는다.
        Quaternion startRotOffset = stationPilePos.localRotation;
        if (robotLineSide == RobotLineSideType.Right)
        {
            Vector3 e = startRotOffset.eulerAngles;
            startRotOffset = Quaternion.Euler(e.x, -e.y, e.z);
        }
        targetCarPart.BeginWorldAssembly(stationPilePos.position, startRotOffset);
        // 2. work=0 = pile 위치로 이동 (SetWork 내부에서 work<=0이면 SetActive(false) 호출됨)
        targetCarPart.SetWork(0f);
        // 3. 위치 확정 후 활성화 (위 SetActive(false)를 덮어씀)
        targetCarPart.SetActive(true);

        // 스테이션 파일 메시 숨김 (파츠 오브젝트가 그 자리에 있으므로)
        if (stationPileMesh) stationPileMesh.SetActive(false);

        // 로봇팔이 가상 타겟을 추적하도록 설정 — 타겟 위치는 텔레포트하지 않는다.
        // UpdateTrackingTarget이 현재 위치에서 파츠를 향해 미끄러뜨린다(복귀 도중 진입 포함).
        if (robotArm != null) robotArm.SetTarget(EnsureTrackingTarget());

        reachEngaged = false; // 새 체결은 팔이 threshold 이내로 도달(최초 캐치)해야 시작
        reachWorkFactor = 0f;
        currentState = StationState.Assembling;
    }

    private void ResetStation()
    {
        if (stationPileMesh) stationPileMesh.SetActive(true);
        if (robotArm != null) robotArm.SetTarget(EnsureTrackingTarget()); // 복귀 이동은 가상 타겟 슬라이드가 처리

        if (targetCarPart != null)
        {
            targetCarPart.Reset(); // progress=1, 비활성화, 런타임 오버라이드 해제
            targetCarPart = null;
        }

        currentCar = null;
        manualMode = false;
        reachEngaged = false;
        reachWorkFactor = 0f;
        currentState = StationState.Idle;
    }

    /// <summary>
    /// 다음 공정을 준비하되 조립을 즉시 시작하지 않는다.
    /// 차량이 트리거에 진입하면 OnTriggerEnter가 자동으로 StartAssembly를 호출한다.
    /// RunTest처럼 차량을 출발점으로 리셋한 직후 호출하는 방식에 사용.
    /// </summary>
    public void PrepareStation(PartType newType)
    {
        // 트리거 진입으로 조립이 시작되어야 하므로 자동(비수동) 모드여야 한다.
        // manualMode=true면 OnTriggerEnter의 TryBeginAssembly가 막혀 체결이 영원히 시작되지 않는다.
        manualMode = false;

        currentState = StationState.Idle;
        currentCar = null;
        targetCarPart = null;

        if (stationPileMesh) stationPileMesh.SetActive(true);

        targetPartType = newType;
        if (robotArm != null)
        {
            robotArm.targetPartType = newType;
            robotArm.SetTarget(EnsureTrackingTarget()); // 대기는 EndPos에서 — 가상 타겟이 EndPos로 슬라이드 (파츠가 나올 PilePos와 겹치지 않게)
        }
    }

    /// <summary>테스트 씬 등 외부에서 파츠 타입을 지정하고 즉시 조립을 시작할 때 호출</summary>
    public void SetPartTypeAndStart(PartType newType, CarController targetCar)
    {
        manualMode = true; // PhysX TriggerExit 이벤트가 조립을 취소하지 못하도록

        // 진행 중인 작업 초기화
        currentState = StationState.Idle;
        currentCar = null;

        if (targetCarPart != null)
        {
            // car.SetCurretParts()가 이미 파츠 상태를 처리했으므로 Reset 불필요
            // 여기서 Reset하면 이전 공정 파츠가 체결 완료 상태에서 다시 비활성화됨
            targetCarPart = null;
        }

        if (stationPileMesh) stationPileMesh.SetActive(true);

        // 새로운 파츠 타입 적용
        targetPartType = newType;
        if (robotArm != null) robotArm.targetPartType = newType;

        if (targetCar == null) return;

        AssemblyPart part = targetCar.GetUnassembledPart(targetPartType);
        if (part == null)
        {
            Debug.LogWarning($"[{gameObject.name}] {newType} 파츠를 차량에서 찾을 수 없습니다.");
            if (robotArm != null) robotArm.SetTarget(EnsureTrackingTarget());
            return;
        }

        currentCar = targetCar;
        targetCarPart = part;
        StartAssembly();
    }

    #region 좌/우 배치 & 데이터(SO)

    /// <summary>상위 LINE 오브젝트의 LineSettings를 찾는다(없으면 null).</summary>
    public LineSettings FindLine() => GetComponentInParent<LineSettings>();

    /// <summary>작업존 박스 center의 라인 기준 기본 높이.</summary>
    public const float BoxCenterBaseY = 0.5f;

    /// <summary>
    /// 라인 설정과 side로부터 작업존 박스 center를 계산한다 — 배치기(ApplyWorkZone)와 공용 공식.
    /// 기본값 (zSpacing, BoxCenterBaseY, ±laneWidth)에 부품별 offset을 더한다:
    /// x·y는 그대로, z는 side 부호에 맞춰 대칭(+offset.z = laneWidth가 커지는 방향).
    /// </summary>
    public static Vector3 GetLineBoxCenter(LineSettings line, bool isRight, Vector3 offset)
    {
        float sideSign = isRight ? 1f : -1f;
        return new Vector3(
            line.zSpacing + offset.x,
            BoxCenterBaseY + offset.y,
            sideSign * (line.laneWidth + offset.z));
    }

    /// <summary>
    /// robotLineSide 값을 설정하면서 appliedSide도 함께 맞춘다(재배치는 호출자가 이미 끝낸 상태).
    /// → OnValidate가 같은 배치를 다시 적용하지 않도록 한다. 배치기에서 사용.
    /// </summary>
    public void SetRobotLineSide(RobotLineSideType side)
    {
        robotLineSide = side;
        appliedSide = side;
    }

    /// <summary>
    /// robotLineSide에 맞춰 상위 LineSettings 기준으로 스테이션을 재배치한다.
    /// 배치기(RobotArmPlacerWindow.PlaceLine)와 동일한 규칙:
    ///   z(월드)  = startZ ± zSpacing   (Left=-, Right=+)
    ///   회전 Y   = (Right? +90 : -90) × (라인 방향 Left? +1 : -1)  ← 방향 인지
    ///   center.x = zSpacing / center.z = ±laneWidth (작업존을 차선 위로)
    /// PilePos·EndPos는 스테이션 자식이라 회전·이동에 함께 따라가므로 별도 처리하지 않는다.
    /// X·Y(높이)는 기존 값을 유지한다.
    /// </summary>
    public void ApplyLineSide()
    {
        LineSettings line = FindLine();
        if (line == null)
        {
            Debug.LogWarning($"[{name}] 상위에서 LineSettings를 찾지 못해 재배치를 건너뜁니다.");
            return;
        }

        bool isRight = robotLineSide == RobotLineSideType.Right;
        float dirSign = (line.direction == LineSettings.Direction.Left) ? 1f : -1f;

        // z(월드): X·Y는 유지하고 z만 라인 기준으로 재계산.
        // 라인 좌/우는 진행 방향 기준 → 방향이 Right면 물리적 lane도 반대(dirSign).
        Vector3 wp = transform.position;
        wp.z = line.startXZ.y + (isRight ? line.zSpacing : -line.zSpacing) * dirSign;
        transform.position = wp;

        // 회전 Y: 방향 인지
        float yRot = (isRight ? 90f : -90f) * dirSign;
        transform.rotation = Quaternion.Euler(0f, yRot, 0f);

        // 작업존 center = 라인 파생 기본값 + 부품별 boxCenterOffset (배치기와 공용 공식).
        // center.z는 z·회전과 달리 dirSign을 곱하지 않는다(방향 Right여도 반대로 뒤집지 않음).
        if (TryGetComponent<BoxCollider>(out var box))
            box.center = GetLineBoxCenter(line, isRight, boxCenterOffset);

        appliedSide = robotLineSide;
        appliedBoxCenterOffset = boxCenterOffset;
    }

    /// <summary>
    /// SO 배치 데이터(씬 독립 값)를 현재 스테이션에 적용한다:
    /// PilePos 로컬 위치·회전(파츠 시작 회전), EndPos 로컬, 작업존 size·boxCenterOffset, robotLineSide.
    /// 스테이션 루트 위치·회전과 작업존 center는 라인 파생값이라 건드리지 않는다 —
    /// 이어서 ApplyLineSide()를 호출해 현재 씬의 LINE 기준으로 마저 배치할 것(center에 offset 반영).
    /// </summary>
    public void ApplyPlacement(StationPlacement placement)
    {
        if (stationPilePos != null)
        {
            stationPilePos.localPosition = placement.pileLocalPos;
            stationPilePos.localRotation = Quaternion.Euler(placement.pileLocalEuler); // 파츠 시작 회전 (StartAssembly가 읽음)
        }
        if (endPos != null) endPos.localPosition = placement.endLocalPos;

        boxCenterOffset = placement.boxCenterOffset; // center는 ApplyLineSide가 offset 포함 공식으로 계산
        if (TryGetComponent<BoxCollider>(out var box))
            box.size = placement.boxSize;

        robotLineSide = placement.robotLineSide;
        appliedSide = placement.robotLineSide;
    }

    /// <summary>현재 스테이션 상태로부터 배치 데이터 구조체(씬 독립 값)를 만든다.</summary>
    public StationPlacement BuildPlacement()
    {
        var placement = new StationPlacement
        {
            type = targetPartType,
            robotLineSide = robotLineSide,
            pileLocalPos = stationPilePos != null ? stationPilePos.localPosition : Vector3.zero,
            pileLocalEuler = stationPilePos != null ? stationPilePos.localEulerAngles : Vector3.zero,
            endLocalPos = endPos != null ? endPos.localPosition : Vector3.zero,
            boxCenterOffset = boxCenterOffset,
        };
        if (TryGetComponent<BoxCollider>(out var box))
            placement.boxSize = box.size;
        return placement;
    }

#if UNITY_EDITOR
    // 인스펙터에서 robotLineSide/boxCenterOffset을 바꾸면 상위 LineSettings 기준으로 즉시 재배치.
    private void OnValidate()
    {
        if (robotLineSide == appliedSide && boxCenterOffset == appliedBoxCenterOffset) return;
        appliedSide = robotLineSide;
        appliedBoxCenterOffset = boxCenterOffset;

        // OnValidate 도중 Transform을 직접 바꾸면 경고가 날 수 있어 다음 에디터 틱으로 미룬다.
        EditorApplication.delayCall += () =>
        {
            if (this == null) return;
            Undo.RecordObject(transform, "Apply Robot Line Side");
            if (TryGetComponent<BoxCollider>(out var workZoneBox)) Undo.RecordObject(workZoneBox, "Apply Robot Line Side");
            ApplyLineSide();
            EditorUtility.SetDirty(this);
        };
    }

    [ContextMenu("배치 저장 (현재 → SO)")]
    public void SavePlacementToSO()
    {
        if (placementData == null) { Debug.LogError($"[{name}] placementData(SO)가 없습니다!"); return; }
        if (targetPartType == PartType.None) { Debug.LogError($"[{name}] targetPartType이 None이라 저장할 수 없습니다."); return; }

        placementData.Set(BuildPlacement());
        EditorUtility.SetDirty(placementData);
        AssetDatabase.SaveAssets();
        Debug.Log($"[{name}] {targetPartType} 배치 저장 완료! (robotLineSide={robotLineSide})");
    }

    [ContextMenu("배치 불러오기 (SO → 현재)")]
    public void LoadPlacementFromSO()
    {
        if (placementData == null) { Debug.LogError($"[{name}] placementData(SO)가 없습니다!"); return; }
        if (!placementData.Has(targetPartType))
        {
            Debug.LogWarning($"[{name}] SO에 {targetPartType} 배치 데이터가 없습니다.");
            return;
        }

        // ctrl + z 가능하게 — ApplyPlacement(파일/엔드/박스/side) + ApplyLineSide(루트)가 건드리는 대상 전부 기록
        var undoTargets = new List<Object> { transform, this };
        if (stationPilePos != null) undoTargets.Add(stationPilePos);
        if (endPos != null) undoTargets.Add(endPos);
        if (TryGetComponent<BoxCollider>(out var workZoneBox)) undoTargets.Add(workZoneBox);
        Undo.RecordObjects(undoTargets.ToArray(), "Load Station Placement");

        ApplyPlacement(placementData.GetPlacement(targetPartType)); // 씬 독립 값 적용
        ApplyLineSide();                                            // 루트 z/회전·작업존 center.x/z는 현재 LINE 기준
        EditorUtility.SetDirty(this);
        Debug.Log($"[{name}] {targetPartType} 배치 불러오기 완료! (robotLineSide={robotLineSide})");
    }
#endif

    #endregion

    // 작업존(트리거 BoxCollider)을 씬 뷰에 박스로 표시. 크기/위치 조정을 눈으로 보며 할 수 있다.
    private void DrawWorkZoneGizmo()
    {
        if (!drawWorkZoneGizmo) return;
        if (!TryGetComponent<BoxCollider>(out var box)) return;

        // 콜라이더는 로컬 좌표(center/size) 기준이므로 트랜스폼 행렬을 적용
        Matrix4x4 old = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;

        Gizmos.color = workZoneColor;
        Gizmos.DrawWireCube(box.center, box.size);

        Color fill = workZoneColor;
        fill.a = 0.12f;
        Gizmos.color = fill;
        Gizmos.DrawCube(box.center, box.size);

        Gizmos.matrix = old;
    }

    // PilePos(파츠 대기)·EndPos(로봇팔 대기) 위치를 씬 뷰에 구로 표시.
    // 실제 드래그 편집은 StationControllerEditor.OnSceneGUI의 위치 핸들이 담당.
    private void OnDrawGizmos()
    {
        DrawWorkZoneGizmo();
        DrawRestGizmos();
        DrawReachGizmo();
        DrawReturnPreviewGizmo();
    }

    // 복귀 경로 미리보기: 지금 이 자세에서 복귀(가상 타겟이 EndPos로 슬라이드)를 시작하면
    // 팔끝이 실제로 그리게 될 경로를 IK 시뮬레이션으로 계산해 표시.
    // 바닥 뚫기/휘두름이 '어느 구간에서, 얼마나' 생기는지 눈으로 확인하는 용도.
    private static readonly List<Vector3> previewPath = new List<Vector3>(128); // 재사용 버퍼 (기즈모 계산용)

    private void DrawReturnPreviewGizmo()
    {
        if (!drawReturnPreviewGizmo || !Application.isPlaying) return;
        if (robotArm == null || robotArm.endEffector == null || trackingTarget == null || config == null) return;
        Transform rest = GetRestTarget();
        if (rest == null) return;

        const float dt = 1f / 30f;
        const int steps = 120; // 4초 분량

        // 가상 타겟 슬라이드까지 실제 복귀와 동일하게 시뮬레이션
        Vector3 simTracker = trackingTarget.position;
        Vector3 goal = rest.position;
        robotArm.SimulateTrajectory(_ =>
        {
            simTracker = Vector3.MoveTowards(simTracker, goal, config.trackingTargetSpeed * dt);
            return simTracker;
        }, dt, steps, previewPath);

        if (previewPath.Count < 2) return;

        // 노랑(시작=현재 팔끝) → 파랑(도착) 그라데이션 폴리라인
        int lowestIdx = 0;
        for (int i = 1; i < previewPath.Count; i++)
        {
            Gizmos.color = Color.Lerp(Color.yellow, Color.cyan, (float)i / (previewPath.Count - 1));
            Gizmos.DrawLine(previewPath[i - 1], previewPath[i]);
            if (previewPath[i].y < previewPath[lowestIdx].y) lowestIdx = i;
        }

        // 경로 최저점 표시 — 바닥(y=0 근처) 아래로 파고드는 깊이를 바로 읽을 수 있게
        Vector3 lowest = previewPath[lowestIdx];
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(lowest, 0.08f);
#if UNITY_EDITOR
        Handles.Label(lowest, $"최저점 y={lowest.y:F2}");
#endif
    }

    // 체결 거리 게이트 디버그: Assembling 중 팔 끝(endEffector)↔파츠 ArmLookTarget 거리를
    // 선 + 거리 라벨로 표시. 물림(reachEngaged)=초록 / 풀림=빨강 — 게이트가 실제로 보고 있는 거리 확인용.
    private void DrawReachGizmo()
    {
        if (currentState != StationState.Assembling || targetCarPart == null) return;
        if (robotArm == null || robotArm.endEffector == null) return;

        Vector3 tip = robotArm.endEffector.position;
        Vector3 look = targetCarPart.GetArmLookWorldPos();

        Gizmos.color = reachWorkFactor > 0f ? Color.green : Color.red;
        Gizmos.DrawLine(tip, look);
        Gizmos.DrawWireSphere(look, 0.05f);
#if UNITY_EDITOR
        string state = !reachEngaged ? "대기(미도달)" : $"{reachWorkFactor * 100f:F0}%";
        Handles.Label((tip + look) * 0.5f, $"{Vector3.Distance(tip, look):F2}m {state}");
#endif
    }

    private void DrawRestGizmos()
    {
        if (!drawRestGizmo) return;

        if (stationPilePos != null)
        {
            Gizmos.color = pilePosColor;
            Gizmos.DrawWireSphere(stationPilePos.position, restGizmoRadius);
            Gizmos.DrawLine(transform.position, stationPilePos.position);
            // 파츠 시작 회전(pileLocalEuler) 방향 표시 — forward 방향선
            Gizmos.DrawRay(stationPilePos.position, stationPilePos.forward * restGizmoRadius * 3f);
        }

        if (endPos != null)
        {
            Gizmos.color = endPosColor;
            Gizmos.DrawWireSphere(endPos.position, restGizmoRadius);
            Gizmos.DrawLine(transform.position, endPos.position);
        }
    }
}
