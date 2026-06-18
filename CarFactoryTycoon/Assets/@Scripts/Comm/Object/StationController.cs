using System.Collections.Generic;
using UnityEngine;

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

    private Transform trackingTarget;

    [Header("작업존 기즈모 (BoxCollider = 체결 사거리)")]
    [Tooltip("씬 뷰에서 BoxCollider 범위를 박스로 표시해 배치/크기 조정을 쉽게 한다")]
    public bool drawWorkZoneGizmo = true;
    public Color workZoneColor = new Color(0f, 1f, 0.4f, 1f);

    [Header("현재 공정 상태")]
    public StationState currentState = StationState.Idle;

    [SerializeField] private CarController currentCar;
    [SerializeField] private AssemblyPart targetCarPart;

    private float cooldownTimer = 0f;
    private bool manualMode = false;
    private bool reachEngaged = false; // 팔이 닿아 체결이 물린 상태(히스테리시스용)

    // 현재 작업존(트리거) 안에 들어와 있는 차량들. 스테이션이 비면 이 중에서 다음 대상을 고른다.
    private readonly List<CarController> carsInZone = new List<CarController>();


    private Transform GetRestTarget() => endPos != null ? endPos : stationPilePos;

    private void Start()
    {
        config = StationConfig.Instance; // 전역 설정 참조를 1회 캐싱 (Update마다 getter 호출 방지)
        trackingTarget = new GameObject($"{gameObject.name}_TrackingTarget").transform;
        trackingTarget.SetParent(transform, false); // 스테이션 자식으로 배치(하이러키 정리, 함께 파괴)

        if (robotArm != null && stationPilePos != null)
        {
            robotArm.SetTarget(GetRestTarget());
            robotArm.targetPartType = this.targetPartType;
        }
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
        if (currentState == StationState.Idle || robotArm == null) return;

        if (currentState != StationState.Cooldown && (currentCar == null || targetCarPart == null))
        {
            bool wasManual = manualMode;
            ResetStation();
            if (!wasManual) TryBeginAssembly();
            return;
        }

        switch (currentState)
        {
            case StationState.Assembling:
                // [연출] 로봇팔이 파츠를 향해 계속 따라가도록 추적 타겟 갱신
                trackingTarget.position = targetCarPart.GetArmLookWorldPos();

                // [판정] 로봇팔 끝(Rig_End=endEffector)이 파츠 ArmLookTarget에 충분히 닿았을 때만 체결 진행.
                // 작업존에 들어와도 팔이 아직 못 닿았으면 대기(준비)하고, 닿으면 체결값 증가,
                // 차량이 멀어져 거리가 벌어지면 다시 증가가 멈춘다. 완성 시간 = requiredWork / assembleSpeed
                float reachDist = robotArm.endEffector != null
                    ? Vector3.Distance(robotArm.endEffector.position, targetCarPart.GetArmLookWorldPos())
                    : float.MaxValue;

                // 히스테리시스: 가까워지면 물리고(engage), 확실히 멀어질 때만 푼다(release).
                // → 경계선에서 ON/OFF 토글되며 생기는 덜덜거림(떨림) 방지.
                if (!reachEngaged)
                {
                    if (reachDist <= config.assembleReachThreshold) reachEngaged = true;
                }
                else
                {
                    if (reachDist > config.assembleReachThreshold + config.reachReleaseMargin) reachEngaged = false;
                }

                if (reachEngaged)
                    targetCarPart.AddWork(config.assembleSpeed * Time.deltaTime);

                if (targetCarPart.IsAssembled)
                {
                    targetCarPart.ClearRuntimeDetached(); // 런타임 오버라이드 먼저 해제
                    targetCarPart.SetAssembled();          // 완전히 체결 완료 위치에 고정
                    Debug.Log($"[{gameObject.name}] ✅ {targetCarPart.name} 체결 완전 성공!");

                    targetCarPart = null;
                    currentCar = null;

                    if (stationPileMesh) stationPileMesh.SetActive(true);
                    robotArm.SetTarget(GetRestTarget());
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
    }

    /// <summary>파츠의 분리 위치를 stationPilePos로 설정하고 조립을 시작한다.</summary>
    private void StartAssembly()
    {
        // 1. 런타임 분리 위치를 stationPilePos로 오버라이드
        targetCarPart.SetRuntimeDetachedPose(stationPilePos.position, stationPilePos.rotation);
        // 2. 분리 위치(work=0 = pile)로 이동 (SetDetached 내부에서 SetActive(false) 호출됨)
        targetCarPart.SetDetached();
        // 3. 위치 확정 후 활성화 (SetDetached의 SetActive(false)를 덮어씀)
        targetCarPart.SetActive(true);

        // 스테이션 파일 메시 숨김 (파츠 오브젝트가 그 자리에 있으므로)
        if (stationPileMesh) stationPileMesh.SetActive(false);

        // 로봇팔이 파츠를 추적하도록 설정
        trackingTarget.position = targetCarPart.GetArmLookWorldPos();
        if (robotArm != null) robotArm.SetTarget(trackingTarget);

        reachEngaged = false; // 새 체결은 팔이 닿기 전(미engage) 상태에서 시작
        currentState = StationState.Assembling;
    }

    private void ResetStation()
    {
        if (stationPileMesh) stationPileMesh.SetActive(true);
        if (robotArm != null) robotArm.SetTarget(GetRestTarget());

        if (targetCarPart != null)
        {
            targetCarPart.Reset(); // progress=1, 비활성화, 런타임 오버라이드 해제
            targetCarPart = null;
        }

        currentCar = null;
        manualMode = false;
        reachEngaged = false;
        currentState = StationState.Idle;
    }

    /// <summary>
    /// 다음 공정을 준비하되 조립을 즉시 시작하지 않는다.
    /// 차량이 트리거에 진입하면 OnTriggerEnter가 자동으로 StartAssembly를 호출한다.
    /// RunTest처럼 차량을 출발점으로 리셋한 직후 호출하는 방식에 사용.
    /// </summary>
    public void PrepareStation(PartType newType)
    {
        manualMode = true; // OnTriggerExit로 인한 조립 취소 방지

        currentState = StationState.Idle;
        currentCar = null;
        targetCarPart = null;

        if (stationPileMesh) stationPileMesh.SetActive(true);

        targetPartType = newType;
        if (robotArm != null)
        {
            robotArm.targetPartType = newType;
            robotArm.SetTarget(stationPilePos);
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
            if (robotArm != null) robotArm.SetTarget(stationPilePos);
            return;
        }

        currentCar = targetCar;
        targetCarPart = part;
        StartAssembly();
    }

    // 작업존(트리거 BoxCollider)을 씬 뷰에 박스로 표시. 크기/위치 조정을 눈으로 보며 할 수 있다.
    private void OnDrawGizmos()
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
}
