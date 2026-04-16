using UnityEngine;

public class StationController : MonoBehaviour
{
    public enum StationState { Idle, Assembling, Cooldown }

    [Header("공정 설정")]
    public PartType targetPartType;

    [Tooltip("1초당 체결되는 비율 (0.05 = 20초 소요)")]
    public float assembleSpeed = 0.05f;

    [Tooltip("Rig_End가 이 거리(m) 이내에 들어와야 체결이 진행됩니다")]
    public float assembleReachThreshold = 0.3f;

    [Header("오브젝트 연결")]
    public RoboticArmIK robotArm;
    public Transform stationPilePos;

    [Tooltip("체결 완료/리셋 후 로봇팔이 대기하는 위치 (미설정 시 PilePos 사용)")]
    public Transform endPos;

    [Tooltip("씬에 배치된 스테이션 파츠 오브젝트 (코드로 생성하지 않음)")]
    public GameObject stationPileMesh;

    private Transform trackingTarget;

    [Header("현재 공정 상태")]
    public StationState currentState = StationState.Idle;

    [SerializeField] private CarController currentCar;
    [SerializeField] private AssemblyPart targetCarPart;

    private float cooldownTimer = 0f;
    private bool manualMode = false;


    private Transform GetRestTarget() => endPos != null ? endPos : stationPilePos;

    private void Start()
    {
        trackingTarget = new GameObject($"{gameObject.name}_TrackingTarget").transform;

        if (robotArm != null && stationPilePos != null)
        {
            robotArm.SetTarget(GetRestTarget());
            robotArm.targetPartType = this.targetPartType;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (currentState != StationState.Idle) return;
        if (other.TryGetComponent<CarController>(out var car) == false) return;

        targetCarPart = car.GetUnassembledPart(targetPartType);
        if (targetCarPart != null)
        {
            currentCar = car;
            StartAssembly();
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (manualMode) return; // 수동 모드에서는 PhysX 이벤트 무시
        if (other.TryGetComponent<CarController>(out var car) == false) return;

        if (car == currentCar)
            ResetStation();
    }

    private void Update()
    {
        if (currentState == StationState.Idle || robotArm == null) return;

        if (currentState != StationState.Cooldown && (currentCar == null || targetCarPart == null))
        {
            ResetStation();
            return;
        }

        switch (currentState)
        {
            case StationState.Assembling:
                // 로봇팔 끝(Rig_End)이 파츠에 충분히 근접했을 때만 체결 진행
                bool armReached = false;
                if (robotArm.endEffector != null)
                {
                    float dist = Vector3.Distance(robotArm.endEffector.position, trackingTarget.position);
                    armReached = dist <= assembleReachThreshold;
                }

                if (armReached)
                {
                    float nextProgress = targetCarPart.assemblyProgress - (assembleSpeed * Time.deltaTime);
                    targetCarPart.UpdateProgress(nextProgress);
                }

                // 파츠의 현재 위치를 실시간으로 추적 (체결 진행 여부와 무관하게 항상 업데이트)
                trackingTarget.position = targetCarPart.transform.position;

                if (targetCarPart.assemblyProgress <= 0f)
                {
                    targetCarPart.ClearRuntimeDetached(); // 런타임 오버라이드 먼저 해제
                    targetCarPart.UpdateProgress(0f);      // 완전히 체결 완료 위치에 고정
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
                    currentState = StationState.Idle;
                break;
        }
    }

    /// <summary>파츠의 분리 위치를 stationPilePos로 설정하고 조립을 시작한다.</summary>
    private void StartAssembly()
    {
        // 1. 런타임 분리 위치를 stationPilePos로 오버라이드
        targetCarPart.SetRuntimeDetachedPose(stationPilePos.position, stationPilePos.rotation);
        // 2. stationPilePos 위치로 이동 (UpdateProgress 내부에서 assemblyProgress==1 → SetActive(false) 호출됨)
        targetCarPart.UpdateProgress(1f);
        // 3. 위치 확정 후 활성화 (UpdateProgress의 SetActive(false)를 덮어씀)
        targetCarPart.SetActive(true);

        // 스테이션 파일 메시 숨김 (파츠 오브젝트가 그 자리에 있으므로)
        if (stationPileMesh) stationPileMesh.SetActive(false);

        // 로봇팔이 파츠를 추적하도록 설정
        trackingTarget.position = targetCarPart.transform.position;
        if (robotArm != null) robotArm.SetTarget(trackingTarget);

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
}
