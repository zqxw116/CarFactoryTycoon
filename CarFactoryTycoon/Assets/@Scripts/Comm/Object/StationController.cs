using UnityEngine;

public class StationController : MonoBehaviour
{
    public enum StationState { Idle, PickingUp, MovingToCar, Assembling, Cooldown }

    [Header("공정 설정")]
    public PartType targetPartType;

    // [수정] 기본 속도를 1.0(1초)에서 0.2(5초)로 확 줄였습니다. (인스펙터에서도 수정 필요)
    [Tooltip("1초당 체결되는 비율 (0.2 = 5초 소요)")]
    public float assembleSpeed = 0.05f;

    [Header("판정 설정")]
    public float reachDistance = 2f;

    [Header("오브젝트 연결")]
    public RoboticArmIK robotArm;
    public Transform stationPilePos;

    private Transform trackingTarget;

    [Header("현재 공정 상태")]
    public StationState currentState = StationState.Idle;

    private GameObject spawnedPileMesh;
    private GameObject spawnedArmMesh;
    private CarController currentCar;
    private AssemblyPart targetCarPart;

    private float cooldownTimer = 0f;

    private void Start()
    {
        trackingTarget = new GameObject($"{gameObject.name}_TrackingTarget").transform;
        //trackingTarget.parent = this.transform;

        InitializePartResources();

        if (robotArm != null && stationPilePos != null)
        {
            robotArm.SetTarget(stationPilePos);
            robotArm.targetPartType = this.targetPartType;
        }
    }

    private void InitializePartResources()
    {
        string path = PartResourceManager.GetPrefabPath(targetPartType);
        if (string.IsNullOrEmpty(path)) return;

        GameObject partPrefab = Resources.Load<GameObject>(path);
        if (partPrefab == null) return;

        spawnedPileMesh = Instantiate(partPrefab, stationPilePos);
        spawnedPileMesh.transform.localPosition = Vector3.zero;
        spawnedPileMesh.transform.localRotation = Quaternion.identity;
        spawnedPileMesh.SetActive(true);

        if (robotArm != null && robotArm.endEffector != null)
        {
            spawnedArmMesh = Instantiate(partPrefab, robotArm.endEffector);
            spawnedArmMesh.transform.localPosition = Vector3.zero;
            spawnedArmMesh.transform.localRotation = Quaternion.identity;
            spawnedArmMesh.SetActive(false);
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
            currentState = StationState.PickingUp;
            robotArm.SetTarget(stationPilePos);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.TryGetComponent<CarController>(out var car) == false) return;

        if (car == currentCar)
        {
            ResetStation();
        }
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
            case StationState.PickingUp:
                if (Vector3.Distance(robotArm.endEffector.position, stationPilePos.position) <= reachDistance)
                {
                    if (spawnedPileMesh) spawnedPileMesh.SetActive(false);
                    if (spawnedArmMesh) spawnedArmMesh.SetActive(true);

                    currentState = StationState.MovingToCar;
                    robotArm.SetTarget(trackingTarget);
                }
                break;

            case StationState.MovingToCar:
                trackingTarget.position = targetCarPart.GetWorldDetachedPos();

                if (Vector3.Distance(robotArm.endEffector.position, trackingTarget.position) <= reachDistance)
                {
                    currentState = StationState.Assembling;
                    if (spawnedArmMesh) spawnedArmMesh.SetActive(false);
                }
                break;

            case StationState.Assembling:
                // 1. 계산: 현재 프레임에서 깎일 진행도 도출
                float nextProgress = targetCarPart.assemblyProgress - (assembleSpeed * Time.deltaTime);

                // 2. 적용: 변수에 직접 접근하지 않고 AssemblyPart의 전용 함수(Setter) 호출
                // (UpdateProgress 함수 안에서 위치 이동과 Clamp 처리를 스스로 다 해줍니다)
                targetCarPart.UpdateProgress(nextProgress);

                // 3. 추적: 로봇팔은 스스로 이동하고 있는 부품의 현재 위치를 그대로 따라가기만 하면 됨
                trackingTarget.position = targetCarPart.transform.position;

                // 4. 로그: 1.0 -> 0.0 으로 떨어지는 수치를 0% -> 100% 형태의 보기 편한 로그로 출력
                float percentString = (1f - targetCarPart.assemblyProgress) * 100f;
                Debug.Log($"[{gameObject.name}] 🔧 {targetCarPart.name} 조립 중... {percentString:F1}%");

                // 5. 완료 판정
                if (targetCarPart.assemblyProgress <= 0f)
                {
                    // 확실하게 0(체결 완료)으로 쐐기를 박음
                    targetCarPart.UpdateProgress(0f);
                    Debug.Log($"[{gameObject.name}] ✅ {targetCarPart.name} 체결 완전 성공!");

                    targetCarPart = null;

                    if (spawnedPileMesh) spawnedPileMesh.SetActive(true);

                    robotArm.SetTarget(stationPilePos);
                    cooldownTimer = 1.0f;
                    currentState = StationState.Cooldown;
                }
                break;

            case StationState.Cooldown:
                cooldownTimer -= Time.deltaTime;
                if (cooldownTimer <= 0f)
                {
                    currentState = StationState.Idle;
                }
                break;
        }
    }

    private void ResetStation()
    {
        if (spawnedArmMesh) spawnedArmMesh.SetActive(false);
        if (spawnedPileMesh) spawnedPileMesh.SetActive(true);

        if (robotArm != null) robotArm.SetTarget(stationPilePos);

        currentCar = null;
        targetCarPart = null;
        currentState = StationState.Idle;
    }
}