using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 5라인 방수 테스트 공정. 인형뽑기형 갠트리 크레인이 라인 위 차량을 집어 들어,
/// 라인 중간에 걸쳐 놓인 지상 유리 수조에 담갔다 꺼내며 방수 상태를 판정한다.
///
///   Idle → HangerDescending → Grabbing → Lifting → TravelToPool
///   → Submerging → Testing (기포 / 불량 시 수중 스파크) → Emerging → TravelToLine → Lowering → Releasing
///   → [Pass]   Cooldown: 크레인이 차량을 releasePoint(수조 뒤 벨트)에 내려놓음 → 라인 정상 이동
///   → [Defect] Rejecting: 해치(rejectPoint) 위에서 차량을 놓음 → 줄어들며 구멍으로 빠짐 → 풀 반환 → Cooldown(해치 닫힘)
///
/// - LineTrafficManager에 게이트로 등록: 차량이 게이트(수조 앞) 앞에서 감속 정지 (WheelStation과 동일 방식).
///   뒷차는 게이트에 막히므로 게이트~방출 지점 사이(수조 구간)로 들어오지 않는다.
/// - 잡힌 차량은 isMoving=false: 집게의 hangerGrabPoint가 차량 transform을 직접 제어하여 수조로 이송.
/// - 통과 방출은 SetProgress(releaseProgress)로 수조 뒤 지점으로 진행도를 점프 + isMoving=true.
///   방출 지점 앞 minGap 안에 앞차가 있으면 공중에서 기다렸다가 내려놓는다 (차량 겹침 방지).
/// - 크레인 = 브리지(부모 로컬 X) + 트롤리(브리지 로컬 Z) + 훅(트롤리 로컬 Y, 케이블 길이 변화).
///   상태기계는 '그랩 지점(차량 기준점)의 목표 위치'만 계산하고, ApplyCranePose가 세 축으로 나눠 적용한다.
/// - 입수/출수(Submerging/Emerging)는 SmoothStep 감속 이동. 소요 시간 = 이동 거리 / 속도 (평균 속도 유지).
///   물 튀김은 차량 기준점(바퀴 바닥)이 수면(poolEntryPoint Y)을 통과하는 순간 재생,
///   출수 직후부터 라인 하강 전까지 dripVFX(물방울)가 차량을 따라 떨어진다. 통과 차량은 출수 순간 passVFX(물보라).
/// - 폐기 연출로 줄인 차량 스케일은 풀 반환 직후·사이클 중단 시 캡처 당시 값으로 복원한다
///   (CarPool.Get에서도 프리팹 스케일로 한 번 더 보정).
///
/// [씬 셋업]
///  - 빈 오브젝트에 이 컴포넌트 추가, 게이트 위치(수조 앞 라인 위)에 배치.
///  - craneBridge / craneTrolley / craneHook: 부모-자식 순서로 연결. hangerGrabPoint는 craneHook 자식.
///    초기 hangerGrabPoint 위치 = 크레인 홈 (게이트 바로 위 공중에 두면 캡처 시 수직 하강).
///  - poolEntryPoint: 수면 위치 Transform. X/Z = 수조 중심, Y = 수면 높이 (splashVFX를 자식으로 두면 됨).
///  - poolBottomPoint: 수중 최저 위치 Transform (normalBubbles/defectBubbles 자식 권장). 차량이 완전히 잠기는 깊이.
///  - releasePoint: 수조 뒤 벨트 위 / rejectPoint: 해치 구멍 중심(Y = 구멍 면 높이).
///  - dripVFX: hangerGrabPoint 자식 권장 (차량과 함께 이동).
/// </summary>
public class WaterTestStation : MonoBehaviour, ILineGate
{
    public enum WaterTestState
    {
        Idle,
        HangerDescending,   // 집게가 차량 위치로 하강
        Grabbing,           // 집게 체결 연출 대기
        Lifting,            // 공중으로 상승
        TravelToPool,       // 수조 위로 수평 이동
        Submerging,         // 입수 (하강)
        Testing,            // 검사 중 (물속 체류)
        Emerging,           // 출수 (상승)
        TravelToLine,       // 방출 지점(라인) 또는 해치 위로 수평 이동
        Lowering,           // 라인 높이 / 해치 위 낙하 높이로 하강
        Releasing,          // 집게 해제 연출
        Rejecting,          // 불량 차량이 해치 구멍으로 빠지며 사라짐 (스케일 축소)
        Cooldown
    }

    public enum WaterTestResult { None, Pass, MinorDefect, MajorDefect }

    // ─── 라인 연결 ───────────────────────────────────────────────────────────────
    [Header("라인 연결")]
    [Tooltip("비워두면 씬에서 'MainSpline' 이름의 오브젝트를 찾습니다.")]
    public SplineContainer mainLineSpline;
    [Tooltip("켜면 이 오브젝트 위치에서 가장 가까운 스플라인 지점을 게이트로 사용 (OnEnable에서 1회 계산).")]
    public bool autoProgressFromPosition = true;
    [Range(0f, 1f)]
    [Tooltip("게이트 스플라인 진행도(0~1). autoProgressFromPosition이 꺼져 있을 때 직접 지정.")]
    public float gateProgress = 0.5f;

    // ─── 크레인 연결 ─────────────────────────────────────────────────────────────
    [Header("크레인 연결 (인형뽑기형 갠트리)")]
    [Tooltip("브리지(가로 보). 부모 로컬 X 방향으로만 이동한다 (레일과 평행).")]
    public Transform craneBridge;
    [Tooltip("트롤리(브리지 위 이동 대차). craneBridge 자식, 브리지 로컬 Z 방향으로만 이동.")]
    public Transform craneTrolley;
    [Tooltip("훅(집게 머리). craneTrolley 자식, 트롤리 로컬 Y 방향으로만 승강.")]
    public Transform craneHook;
    [Tooltip("craneHook 자식. 차량이 매달리는 기준점 (차량 피벗 = 바퀴 바닥 중심) — 이 위치로 차량이 따라온다.")]
    public Transform hangerGrabPoint;
    [Tooltip("케이블 피벗 (craneTrolley 자식, 피벗 = 윗끝, 메시는 로컬 -Y로 뻗음). 훅까지 거리에 맞춰 Y 스케일을 늘린다.")]
    public Transform craneCable;
    [Tooltip("케이블 피벗 스케일 1일 때의 케이블 길이 (m).")]
    public float craneCableLength = 1f;
    [Tooltip("집게 손가락 피벗들. 닫힘 = 씬 배치 회전, 열림 = 로컬 X축으로 clawOpenAngle만큼 회전 (바깥으로 벌어지게 배치).")]
    public Transform[] clawFingers;
    [Tooltip("집게가 벌어지는 각도 (도).")]
    public float clawOpenAngle = 35f;

    // ─── 경로 포인트 ─────────────────────────────────────────────────────────────
    [Header("경로 포인트")]
    [Tooltip("차량 캡처 위치 Y + 이 값 = 공중 경유 높이. 수조 유리벽 위를 충분히 넘는 값으로 설정.")]
    public float liftHeight = 5f;
    [Tooltip("수면 지점. 크레인이 이 X/Z 위 공중까지 수평 이동한 뒤 하강한다. Y = 수면 높이 (물 튀김 타이밍 기준). splashVFX를 여기 자식에 두길 권장.")]
    public Transform poolEntryPoint;
    [Tooltip("수중 최저 지점. 차량이 완전히 잠기는 깊이. normalBubbles/defectBubbles를 여기 자식에 두길 권장.")]
    public Transform poolBottomPoint;
    [Tooltip("통과 차량을 라인에 내려놓는 위치. 수조 뒤 벨트 위에 배치 (여기 위치의 스플라인 진행도로 SetProgress).")]
    public Transform releasePoint;
    [Range(0f, 1f)]
    [Tooltip("releasePoint의 스플라인 진행도 (0~1). autoProgressFromPosition이 켜져 있으면 자동 계산.")]
    public float releaseProgress = 0.55f;

    // ─── 이동 속도 ───────────────────────────────────────────────────────────────
    [Header("이동 속도 (m/s)")]
    public float hangerDescendSpeed = 2.5f; // 차량 위치로 하강
    public float liftSpeed          = 2f;   // 공중 상승
    public float travelSpeed        = 3f;   // 수평 이동 (수조/라인/해치)
    public float submergeSpeed      = 0.8f; // 입수 하강 (평균 속도, SmoothStep 감속)
    public float emergeSpeed        = 0.8f; // 출수 상승 (평균 속도, SmoothStep 감속)
    public float lowerSpeed         = 1.5f; // 라인/해치 하강
    public float hangerReturnSpeed  = 3f;   // 쿨다운 중 홈 복귀

    // ─── 타이밍 ──────────────────────────────────────────────────────────────────
    [Header("타이밍 (초)")]
    public float grabDuration    = 0.5f; // 집게 체결 연출 (손가락 닫힘 시간)
    public float testDuration    = 3f;   // 물속 체류(검사) 시간
    public float releaseDuration = 0.4f; // 집게 해제 연출 (손가락 열림 시간)
    public float cooldownDuration= 1f;

    // ─── 검사 판정 ────────────────────────────────────────────────────────────────
    [Header("검사 판정")]
    [Tooltip("미체결 파츠가 이 수 이상이면 심각한 불량 (보너스 없음). 미만이면 경미한 불량 (반액 보너스).")]
    public int majorDefectThreshold = 3;
    [Tooltip("정상 통과 보너스 ($).")]
    public int passBonus = 200;

    // ─── 불량 폐기 ───────────────────────────────────────────────────────────────
    [Header("불량 폐기 (바닥 해치)")]
    [Tooltip("해치 구멍 중심. Y = 구멍 면 높이. 크레인이 이 위 rejectDropHeight에서 차량을 놓는다.")]
    public Transform rejectPoint;
    [Tooltip("해치 문짝 피벗(경첩)들. 닫힘 = 씬 배치 회전, 열림 = 로컬 X축 -rejectHatchOpenAngle 회전 (문짝은 피벗 로컬 +Z로 뻗게 배치).")]
    public Transform[] rejectHatchLeaves;
    [Tooltip("해치 문짝이 열리는 각도 (도). 90 이상이면 바깥으로 젖혀진다.")]
    public float rejectHatchOpenAngle = 105f;
    [Tooltip("해치가 완전히 열리거나 닫히는 데 걸리는 시간 (초).")]
    public float rejectHatchDuration = 0.6f;
    [Tooltip("해치 구멍 면 위 몇 m에서 차량을 놓을지.")]
    public float rejectDropHeight = 1f;
    [Tooltip("놓은 뒤 차량이 줄어들며 구멍으로 빠져 사라지는 시간 (초).")]
    public float rejectVanishDuration = 0.9f;
    [Tooltip("구멍 면 아래로 가라앉는 깊이 (m).")]
    public float rejectSinkDepth = 1.6f;
    [Tooltip("빠지는 동안 차량이 도는 각도 (도, Y축).")]
    public float rejectSpinAngle = 40f;

    // ─── 이펙트 ──────────────────────────────────────────────────────────────────
    [Header("이펙트")]
    [Tooltip("입수/출수 시 물 튀김. poolEntryPoint 근처에 배치 권장.")]
    public ParticleSystem splashVFX;
    [Tooltip("검사 중 일반 공기방울. poolBottomPoint 근처에 배치 권장.")]
    public ParticleSystem normalBubbles;
    [Tooltip("불량 부위 공기방울. 미체결 파츠가 있을 때 normalBubbles와 함께 재생.")]
    public ParticleSystem defectBubbles;
    [Tooltip("통과 차량 출수 순간의 물보라 (수면 위치).")]
    public ParticleSystem passVFX;
    [Tooltip("불량 감지 수중 스파크 (검사 중 재생, 출수 시 정지). 수조 안 차량 높이에 배치.")]
    public ParticleSystem failVFX;
    [Tooltip("출수 후 차량에서 떨어지는 물방울 (루프). hangerGrabPoint 자식 권장. 출수 시 재생, 라인 하강 시작 시 정지.")]
    public ParticleSystem dripVFX;

    // ─── 디버그 ──────────────────────────────────────────────────────────────────
    [Header("현재 상태 (디버그)")]
    public WaterTestState currentState = WaterTestState.Idle;
    [SerializeField] private CarController currentCar;
    [SerializeField] private int missingPartCount;
    [SerializeField] private WaterTestResult lastResult;

    // ─── 내부 ────────────────────────────────────────────────────────────────────
    // 게이트 도착 판정 거리 (m). LineTrafficManager 감속 도킹이 이 안까지 데려온다.
    private const float CaptureEpsilon = 0.1f;
    private const string MainSplineName = "MainSpline";

    private bool   initialized;
    private float  timer;

    // 현재 그랩 지점 월드 위치. 상태기계가 이 값을 움직이고 ApplyCranePose가 크레인 세 축으로 나눠 적용.
    private Vector3    grabPos;
    // 그랩포인트 → 훅 오프셋 (캐시). 훅 목표 = grabPos + grabToHook.
    private Vector3    grabToHook;
    // 초기 그랩 위치 → 쿨다운 후 복귀용 (크레인 홈)
    private Vector3    grabHomePos;
    // 캡처 시점 차량 월드 위치. 라인 복귀·공중 높이 계산의 기준.
    private Vector3    carCapturePos;
    // 캡처 시점 차량 로컬 스케일. 폐기 축소 연출 후 풀 반환·중단 시 복원.
    private Vector3    carOriginalScale = Vector3.one;
    // 공중 경유 Y (= carCapturePos.y + liftHeight)
    private float      airHeight;

    // 집게 손가락 닫힘 회전(씬 배치) / 닫힘 정도 (0=열림, 1=닫힘)
    private Quaternion[] fingerClosedRot;
    private float        clawT;
    // 해치 문짝 닫힘 회전(씬 배치) / 열림 정도 (0=닫힘, 1=열림)
    private Quaternion[] hatchClosedRot;
    private float        hatchT;

    // 입수/출수 SmoothStep 이동 (그랩포인트 기준 시작·목표, 진행도, 소요 시간)
    private Vector3 dipFrom;
    private Vector3 dipTo;
    private float   dipT;
    private float   dipDuration;
    // 이번 입수/출수 구간에서 수면 통과 처리(물 튀김) 여부
    private bool    surfaceCrossed;

    // 폐기 소멸 연출 (놓은 위치·회전, 진행도)
    private Vector3    vanishFrom;
    private Quaternion vanishRot;
    private float      vanishT;

    // ILineGate 구현
    public float GateProgress => gateProgress;
    public bool  GateEnabled  => isActiveAndEnabled;

    // ─── 생명주기 ────────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        EnsureInit();
        if (autoProgressFromPosition && mainLineSpline != null)
        {
            gateProgress    = GetNearestProgress();
            // releasePoint가 지정되어 있으면 그 위치의 스플라인 진행도 자동 계산
            if (releasePoint != null)
                releaseProgress = ComputeProgressFromWorld(releasePoint.position);
        }
        LineTrafficManager.Instance.RegisterGate(this);
    }

    private void OnDisable()
    {
        CancelCycle();
        LineTrafficManager.Instance.UnregisterGate(this);
    }

    private void EnsureInit()
    {
        if (initialized) return;
        initialized = true;

        if (mainLineSpline == null)
        {
            var go = GameObject.Find(MainSplineName);
            if (go != null) go.TryGetComponent(out mainLineSpline);
        }
        if (mainLineSpline == null)
            Debug.LogError($"[WaterTestStation] '{MainSplineName}' 스플라인을 찾지 못했습니다.");

        // 그랩포인트 → 훅 오프셋 (ApplyCranePose에서 반복 계산 방지) + 초기 위치 = 홈
        if (hangerGrabPoint != null)
        {
            grabPos     = hangerGrabPoint.position;
            grabHomePos = grabPos;
            if (craneHook != null) grabToHook = craneHook.position - hangerGrabPoint.position;
        }

        // 집게 손가락: 씬 배치 = 닫힘. 대기 중에는 벌린 상태로 시작
        if (clawFingers != null)
        {
            fingerClosedRot = new Quaternion[clawFingers.Length];
            for (int i = 0; i < clawFingers.Length; i++)
                if (clawFingers[i] != null) fingerClosedRot[i] = clawFingers[i].localRotation;
        }
        clawT = 0f;
        ApplyClaw();

        // 해치 문짝: 씬 배치 = 닫힘
        if (rejectHatchLeaves != null)
        {
            hatchClosedRot = new Quaternion[rejectHatchLeaves.Length];
            for (int i = 0; i < rejectHatchLeaves.Length; i++)
                if (rejectHatchLeaves[i] != null) hatchClosedRot[i] = rejectHatchLeaves[i].localRotation;
        }
        hatchT = 0f;
        ApplyHatch();
        ApplyCranePose();
    }

    // ─── 업데이트 ────────────────────────────────────────────────────────────────

    private void Update()
    {
        // 사이클 중 차량이 사라지면 중단 후 쿨다운
        bool inCycle = currentState != WaterTestState.Idle && currentState != WaterTestState.Cooldown;
        if (inCycle && (currentCar == null || !currentCar.gameObject.activeInHierarchy))
        {
            AbortCycle();
            return;
        }

        switch (currentState)
        {
            case WaterTestState.Idle:
                // 에디터에서 이 오브젝트를 옮기면 게이트 진행도 실시간 갱신
                if (autoProgressFromPosition && transform.hasChanged && mainLineSpline != null)
                {
                    gateProgress = GetNearestProgress();
                    transform.hasChanged = false;
                }
                TryCapture();
                break;

            case WaterTestState.HangerDescending:
                // 집게 그랩포인트가 차량 위치에 도달하면 집게 체결 시작
                if (MoveHangerGrabTo(currentCar.transform.position, hangerDescendSpeed))
                    BeginGrabbing();
                break;

            case WaterTestState.Grabbing:
                timer -= Time.deltaTime;
                if (timer <= 0f) BeginLifting();
                break;

            case WaterTestState.Lifting:
                if (MoveHangerGrabTo(LiftPos(), liftSpeed))
                    BeginTravelToPool();
                break;

            case WaterTestState.TravelToPool:
                // 수조 연결 없으면 즉시 입수로 (fallback)
                if (poolEntryPoint == null) { BeginSubmerging(); break; }
                if (MoveHangerGrabTo(PoolAirPos(), travelSpeed))
                    BeginSubmerging();
                break;

            case WaterTestState.Submerging:
                // 수조 바닥 연결 없으면 즉시 검사로 (fallback)
                if (poolBottomPoint == null) { BeginTesting(); break; }
                if (StepDip(submerging: true))
                    BeginTesting();
                break;

            case WaterTestState.Testing:
                timer -= Time.deltaTime;
                if (timer <= 0f) BeginEmerging();
                break;

            case WaterTestState.Emerging:
                if (StepDip(submerging: false))
                    BeginTravelToLine();
                break;

            case WaterTestState.TravelToLine:
                // 통과: 방출 지점 앞에 앞차가 남아 있으면 공중에서 대기 (내려놓으면 겹침)
                if (MoveHangerGrabTo(AirPosOf(GetDestPos()), travelSpeed) &&
                    !(lastResult == WaterTestResult.Pass && IsReleaseSpotBlocked()))
                    BeginLowering();
                break;

            case WaterTestState.Lowering:
                // 불량: 해치가 다 열린 뒤에 놓는다 (해치는 UpdateHatch가 TravelToLine부터 열기 시작)
                if (MoveHangerGrabTo(GetDestPos(), lowerSpeed) &&
                    (lastResult == WaterTestResult.Pass || IsHatchOpen()))
                    BeginReleasing();
                break;

            case WaterTestState.Releasing:
                timer -= Time.deltaTime;
                if (timer <= 0f) ReleaseCar();
                break;

            case WaterTestState.Rejecting:
                if (StepVanish()) VanishCar();
                break;

            case WaterTestState.Cooldown:
                timer -= Time.deltaTime;
                // 크레인 홈 복귀 (해치는 UpdateHatch가 닫음). 홈 도착 전에는 다음 캡처를 받지 않는다
                bool atHome = ReturnCraneHome();
                if (timer <= 0f && atHome)
                    currentState = WaterTestState.Idle;
                break;
        }

        UpdateClaw();
        UpdateHatch();

        // Grabbing 이후 Releasing까지: 차량이 집게 그랩포인트 위치를 따른다
        if (currentCar != null && IsCarAttached() && hangerGrabPoint != null)
            currentCar.transform.position = hangerGrabPoint.position;
    }

    // Grabbing(집게 체결 시작) ~ Releasing(해제) 구간 = 차량이 집게에 붙어 있는 상태
    private bool IsCarAttached() =>
        currentState >= WaterTestState.Grabbing &&
        currentState <= WaterTestState.Releasing;

    // 라인 위 공중 경유 위치 (X/Z=캡처 지점, Y=airHeight)
    private Vector3 LiftPos() => new Vector3(carCapturePos.x, airHeight, carCapturePos.z);

    // 수조 위 공중 위치 (X/Z=poolEntryPoint, Y=airHeight). poolEntryPoint 없으면 LiftPos 폴백.
    private Vector3 PoolAirPos() => poolEntryPoint != null
        ? new Vector3(poolEntryPoint.position.x, airHeight, poolEntryPoint.position.z)
        : LiftPos();

    // ─── 상태 전환 ───────────────────────────────────────────────────────────────

    private void TryCapture()
    {
        CarController car = LineTrafficManager.Instance.GetCarAtGate(gateProgress, CaptureEpsilon);
        if (car == null) return;

        currentCar          = car;
        currentCar.isMoving = false;              // 트래픽 이동 정지, transform 자유화
        carCapturePos       = currentCar.transform.position; // 라인 복귀 기준
        carOriginalScale    = currentCar.transform.localScale; // 폐기 축소 후 복원 기준
        airHeight           = carCapturePos.y + liftHeight;
        currentState        = WaterTestState.HangerDescending;
    }

    private void BeginGrabbing()
    {
        timer        = grabDuration;
        currentState = WaterTestState.Grabbing;
    }

    private void BeginLifting()
    {
        currentState = WaterTestState.Lifting;
    }

    private void BeginTravelToPool()
    {
        currentState = WaterTestState.TravelToPool;
    }

    private void BeginSubmerging()
    {
        // 입수 스플래시는 수면 통과 시점에 StepDip이 재생
        if (poolBottomPoint != null) BeginDip(poolBottomPoint.position, submergeSpeed);
        currentState = WaterTestState.Submerging;
    }

    private void BeginTesting()
    {
        // 판정: 미체결 수 기준
        missingPartCount = currentCar != null ? currentCar.CountUnassembledParts() : 0;
        lastResult = missingPartCount == 0                      ? WaterTestResult.Pass
                   : missingPartCount < majorDefectThreshold    ? WaterTestResult.MinorDefect
                   : WaterTestResult.MajorDefect;

        // 기포 재생 (결과와 무관하게 normal, 누락 있으면 defect 추가)
        PlayVFX(normalBubbles);
        if (missingPartCount > 0)
        {
            PlayVFX(defectBubbles);
            // 수중 스파크 연출: 불량 감지 (출수 시 정지)
            PlayVFX(failVFX);
        }

        timer        = testDuration;
        currentState = WaterTestState.Testing;
    }

    private void BeginEmerging()
    {
        StopVFX(normalBubbles);
        StopVFX(defectBubbles);
        StopVFX(failVFX);

        // 출수 스플래시·물방울·통과 물보라는 수면 통과 시점에 StepDip이 재생
        BeginDip(PoolAirPos(), emergeSpeed);
        currentState = WaterTestState.Emerging;
    }

    private void BeginTravelToLine()
    {
        // 불량 차량: 라인으로 돌아가지 않으므로 트래픽 목록에서 즉시 제거
        // → 뒷차들이 게이트 클램프에서 풀려 진행 가능
        if (lastResult != WaterTestResult.Pass && currentCar != null)
            currentCar.LeaveLine();

        currentState = WaterTestState.TravelToLine;
    }

    private void BeginLowering()
    {
        StopVFX(dripVFX); // 라인/해치 위에서는 물방울 그침 (남은 방울은 자연 낙하)
        currentState = WaterTestState.Lowering;
    }

    private void BeginReleasing()
    {
        timer        = releaseDuration;
        currentState = WaterTestState.Releasing;
    }

    private void ReleaseCar()
    {
        if (currentCar == null)
        {
            timer        = cooldownDuration;
            currentState = WaterTestState.Cooldown;
            return;
        }

        if (lastResult == WaterTestResult.Pass)
        {
            // ── 정상 통과: 보너스 지급 후 releasePoint에서 라인 복귀 ───────────────
            Debug.Log($"<color=green>[WaterTest] ✅ {currentCar.name} 방수 테스트 통과! (+{passBonus})</color>");
            if (EconomyManager.Instance != null)
            {
                Vector3 rewardPos = currentCar.transform.position + Vector3.up * 2f;
                EconomyManager.Instance.Earn(passBonus, rewardPos);
                CashPopup.Show(rewardPos, passBonus, 1.4f);
            }
            // releaseProgress 위치로 SetProgress → 수조 뒤 지점에서 재출발
            currentCar.transform.localScale = carOriginalScale;
            currentCar.SetProgress(releaseProgress);
            currentCar.isMoving = true;
            currentCar          = null;

            timer        = cooldownDuration;
            currentState = WaterTestState.Cooldown;
        }
        else
        {
            // ── 불량: 해치 위에서 놓은 차량을 구멍으로 떨어뜨려 폐기 ─────────────
            Debug.LogWarning($"<color=red>[WaterTest] ✗ {currentCar.name} 방수 실패! " +
                             $"(누락 {missingPartCount}개) → 폐기 처리</color>");
            BeginRejecting();
        }
    }

    private void BeginRejecting()
    {
        vanishFrom   = currentCar.transform.position;
        vanishRot    = currentCar.transform.rotation;
        vanishT      = 0f;
        currentState = WaterTestState.Rejecting;
    }

    // ─── 유틸 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 그랩 지점을 grabWorldTarget으로 MoveTowards 이동하고 크레인에 적용.
    /// 도달 시 true. 그랩포인트 미연결 시 즉시 true (애니메이션 없이 상태 진행 → 로직은 정상 동작).
    /// </summary>
    private bool MoveHangerGrabTo(Vector3 grabWorldTarget, float speed)
    {
        if (hangerGrabPoint == null) return true;
        grabPos = Vector3.MoveTowards(grabPos, grabWorldTarget, speed * Time.deltaTime);
        ApplyCranePose();
        return (grabPos - grabWorldTarget).sqrMagnitude < 0.0025f;
    }

    /// <summary>
    /// grabPos를 크레인 세 축으로 나눠 적용한다: 브리지 X(부모 로컬) → 트롤리 Z(브리지 로컬) → 훅 Y(트롤리 로컬).
    /// 케이블은 트롤리~훅 거리에 맞춰 늘인다. 훅 미연결 시 그랩포인트를 직접 옮긴다.
    /// </summary>
    private void ApplyCranePose()
    {
        if (hangerGrabPoint == null) return;
        if (craneHook == null)
        {
            hangerGrabPoint.position = grabPos;
            return;
        }

        Vector3 hookTarget = grabPos + grabToHook;
        if (craneBridge != null)
        {
            Vector3 lp = craneBridge.parent != null ? craneBridge.parent.InverseTransformPoint(hookTarget) : hookTarget;
            Vector3 p  = craneBridge.localPosition;
            p.x = lp.x;
            craneBridge.localPosition = p;
        }
        if (craneTrolley != null)
        {
            Vector3 lp = craneTrolley.parent != null ? craneTrolley.parent.InverseTransformPoint(hookTarget) : hookTarget;
            Vector3 p  = craneTrolley.localPosition;
            p.z = lp.z;
            craneTrolley.localPosition = p;
        }
        {
            Vector3 lp = craneHook.parent != null ? craneHook.parent.InverseTransformPoint(hookTarget) : hookTarget;
            Vector3 p  = craneHook.localPosition;
            p.y = lp.y;
            craneHook.localPosition = p;
        }

        if (craneCable != null)
        {
            float len = Vector3.Distance(craneCable.position, craneHook.position);
            Vector3 s = craneCable.localScale;
            s.y = Mathf.Max(0.01f, len / Mathf.Max(0.01f, craneCableLength));
            craneCable.localScale = s;
        }
    }

    /// <summary>쿨다운 중 크레인 홈 복귀: 먼저 홈 높이까지 수직 상승 → 수평 이동 (수조 유리벽·라인 설비를 가로지르지 않게).</summary>
    private bool ReturnCraneHome()
    {
        if (hangerGrabPoint == null) return true;
        if (grabPos.y < grabHomePos.y - 0.01f)
        {
            MoveHangerGrabTo(new Vector3(grabPos.x, grabHomePos.y, grabPos.z), hangerReturnSpeed);
            return false;
        }
        return MoveHangerGrabTo(grabHomePos, hangerReturnSpeed);
    }

    /// <summary>집게 손가락: 체결~하강 구간은 닫고(grabDuration), 그 외에는 연다(releaseDuration).</summary>
    private void UpdateClaw()
    {
        if (clawFingers == null || clawFingers.Length == 0) return;
        bool  close = currentState >= WaterTestState.Grabbing && currentState <= WaterTestState.Lowering;
        float dur   = close ? grabDuration : releaseDuration;
        clawT = Mathf.MoveTowards(clawT, close ? 1f : 0f, Time.deltaTime / Mathf.Max(0.05f, dur));
        ApplyClaw();
    }

    private void ApplyClaw()
    {
        if (clawFingers == null || fingerClosedRot == null) return;
        float angle = clawOpenAngle * (1f - Mathf.SmoothStep(0f, 1f, clawT));
        for (int i = 0; i < clawFingers.Length && i < fingerClosedRot.Length; i++)
            if (clawFingers[i] != null)
                clawFingers[i].localRotation = fingerClosedRot[i] * Quaternion.Euler(angle, 0f, 0f);
    }

    /// <summary>해치: 불량 차량을 해치로 옮기는 동안(TravelToLine~Rejecting) 열고, 그 외(쿨다운 등)에는 닫는다.</summary>
    private void UpdateHatch()
    {
        if (rejectHatchLeaves == null || rejectHatchLeaves.Length == 0) return;
        bool open = lastResult != WaterTestResult.Pass && lastResult != WaterTestResult.None &&
                    currentState >= WaterTestState.TravelToLine && currentState <= WaterTestState.Rejecting;
        hatchT = Mathf.MoveTowards(hatchT, open ? 1f : 0f, Time.deltaTime / Mathf.Max(0.05f, rejectHatchDuration));
        ApplyHatch();
    }

    private void ApplyHatch()
    {
        if (rejectHatchLeaves == null || hatchClosedRot == null) return;
        float angle = rejectHatchOpenAngle * Mathf.SmoothStep(0f, 1f, hatchT);
        for (int i = 0; i < rejectHatchLeaves.Length && i < hatchClosedRot.Length; i++)
            if (rejectHatchLeaves[i] != null)
                rejectHatchLeaves[i].localRotation = hatchClosedRot[i] * Quaternion.Euler(-angle, 0f, 0f);
    }

    // 해치 문짝이 없으면 항상 열린 것으로 취급
    private bool IsHatchOpen() => rejectHatchLeaves == null || rejectHatchLeaves.Length == 0 || hatchT >= 1f;

    /// <summary>
    /// 방출 지점부터 minGap 안에 앞차가 있는지. 있으면 지금 내려놓을 때 앞차와 겹친다.
    /// GetCarAtGate(방출+minGap, minGap) = 진행도 [방출, 방출+minGap) 구간의 차량 (잡혀 있는 현재 차량은 게이트 쪽이라 제외).
    /// </summary>
    private bool IsReleaseSpotBlocked()
    {
        if (currentCar == null || currentCar.SplineLength <= 0f) return false;
        var traffic = LineTrafficManager.Instance;
        float clear = traffic.minGap;
        return traffic.GetCarAtGate(releaseProgress + clear / currentCar.SplineLength, clear) != null;
    }

    /// <summary>입수/출수 SmoothStep 이동 준비. 소요 시간 = 거리 / 평균 속도.</summary>
    private void BeginDip(Vector3 grabWorldTarget, float speed)
    {
        dipFrom        = hangerGrabPoint != null ? grabPos : grabWorldTarget;
        dipTo          = grabWorldTarget;
        dipT           = 0f;
        dipDuration    = Vector3.Distance(dipFrom, dipTo) / Mathf.Max(0.01f, speed);
        surfaceCrossed = false;
    }

    /// <summary>
    /// 입수/출수 한 프레임 진행. 그랩포인트(=차량 기준점)가 수면 Y를 통과하는 순간 물 튀김,
    /// 출수 시에는 물방울(+통과 차량은 물보라) 재생을 함께 시작한다. 도달 시 true. 그랩포인트 미연결 시 즉시 true.
    /// </summary>
    private bool StepDip(bool submerging)
    {
        if (hangerGrabPoint == null) return true;

        dipT += Time.deltaTime / Mathf.Max(0.01f, dipDuration);
        float k = Mathf.SmoothStep(0f, 1f, dipT);
        grabPos = Vector3.Lerp(dipFrom, dipTo, k);
        ApplyCranePose();

        if (!surfaceCrossed && poolEntryPoint != null)
        {
            float surfaceY = poolEntryPoint.position.y;
            float y        = grabPos.y;
            if (submerging ? y <= surfaceY : y >= surfaceY)
            {
                surfaceCrossed = true;
                PlayVFX(splashVFX);
                if (!submerging)
                {
                    PlayVFX(dripVFX);
                    if (lastResult == WaterTestResult.Pass) PlayVFX(passVFX);
                }
            }
        }
        return dipT >= 1f;
    }

    /// <summary>
    /// 폐기 소멸 한 프레임: 놓은 위치에서 해치 중심으로 모이며 가속 낙하, 스케일 1→0, Y축으로 살짝 회전.
    /// 구멍 면 아래 부분은 구멍(검은 면)·바닥에 가려져 '빠지는' 것처럼 보인다. 끝나면 true.
    /// </summary>
    private bool StepVanish()
    {
        if (currentCar == null) return true;

        vanishT += Time.deltaTime / Mathf.Max(0.05f, rejectVanishDuration);
        float k = Mathf.Clamp01(vanishT);

        Vector3 hole = rejectPoint != null ? rejectPoint.position : vanishFrom;
        Vector3 p    = Vector3.Lerp(vanishFrom, new Vector3(hole.x, vanishFrom.y, hole.z), k);
        p.y = vanishFrom.y - (vanishFrom.y - hole.y + rejectSinkDepth) * k * k;

        Transform t = currentCar.transform;
        t.position   = p;
        t.rotation   = vanishRot * Quaternion.Euler(0f, rejectSpinAngle * k, 0f);
        t.localScale = carOriginalScale * (1f - Mathf.SmoothStep(0f, 1f, k));
        return vanishT >= 1f;
    }

    /// <summary>진행 중인 사이클을 취소한다. 차량이 있으면 상태에 따라 라인 복귀 또는 풀 반환.</summary>
    private void CancelCycle()
    {
        if (currentState == WaterTestState.Idle || currentState == WaterTestState.Cooldown) return;
        StopVFX(normalBubbles);
        StopVFX(defectBubbles);
        StopVFX(failVFX);
        if (currentCar != null && currentCar.gameObject.activeInHierarchy)
        {
            currentCar.transform.localScale = carOriginalScale; // 폐기 축소 중 취소 대비
            if (currentCar.targetSpline != null)
            {
                // 아직 스플라인에 연결 → 게이트 이전으로 복귀 후 재출발
                currentCar.SetProgress(gateProgress);
                currentCar.isMoving = true;
            }
            else
            {
                // LeaveLine() 이미 호출됨 (불량 이송·폐기 중 취소) → 풀 반환
                if (CarPool.Instance != null) CarPool.Instance.Return(currentCar);
                else currentCar.gameObject.SetActive(false);
            }
        }
        AbortCycle();
    }

    /// <summary>비정상 상황(차량 소실 등)에서 사이클 중단 후 쿨다운 진입.</summary>
    private void AbortCycle()
    {
        StopVFX(normalBubbles);
        StopVFX(defectBubbles);
        StopVFX(failVFX);
        StopVFX(dripVFX);
        // 외부에서 비활성화된 차량이라도 스케일은 되돌려 둔다 (재사용 대비)
        if (currentCar != null) currentCar.transform.localScale = carOriginalScale;
        currentCar   = null;
        lastResult   = WaterTestResult.None;
        timer        = cooldownDuration;
        currentState = WaterTestState.Cooldown;
    }

    /// <summary>불량 차량을 풀에 반환(스케일 복원)하고 Cooldown으로 진입한다.</summary>
    private void VanishCar()
    {
        if (currentCar != null)
        {
            if (CarPool.Instance != null) CarPool.Instance.Return(currentCar);
            else currentCar.gameObject.SetActive(false);
            // 비활성 상태에서 원래 스케일로 복원 → 재사용 시 정상 크기
            currentCar.transform.localScale = carOriginalScale;
            currentCar = null;
        }
        timer        = cooldownDuration;
        currentState = WaterTestState.Cooldown;
    }

    // 결과에 따른 목적지 위치 (그랩포인트 = 차량 기준점)
    // Pass  → releasePoint (수조 뒤 라인 위)
    // Defect→ rejectPoint 위 rejectDropHeight (해치 위에서 놓음)
    private Vector3 GetDestPos() => lastResult == WaterTestResult.Pass ? GetReleasePos() : GetRejectPos();
    private Vector3 GetReleasePos() => releasePoint != null ? releasePoint.position : carCapturePos;
    private Vector3 GetRejectPos()  => rejectPoint  != null ? rejectPoint.position + Vector3.up * rejectDropHeight : carCapturePos;

    // 지면 위치에서 공중 이동 좌표 생성 (X/Z 유지, Y = airHeight)
    private Vector3 AirPosOf(Vector3 groundPos) => new Vector3(groundPos.x, airHeight, groundPos.z);

    private static void PlayVFX(ParticleSystem ps)
    {
        if (ps == null) return;
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ps.Play();
    }

    private static void StopVFX(ParticleSystem ps)
    {
        if (ps == null) return;
        ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    private float GetNearestProgress()
    {
        float3 local = mainLineSpline.transform.InverseTransformPoint(transform.position);
        SplineUtility.GetNearestPoint(mainLineSpline.Spline, local, out _, out float t);
        return Mathf.Clamp01(t);
    }

    private float ComputeProgressFromWorld(Vector3 worldPos)
    {
        if (mainLineSpline == null) return 0f;
        float3 local = mainLineSpline.transform.InverseTransformPoint(worldPos);
        SplineUtility.GetNearestPoint(mainLineSpline.Spline, local, out _, out float t);
        return Mathf.Clamp01(t);
    }

    // ─── 기즈모 ──────────────────────────────────────────────────────────────────

    private void OnDrawGizmos()
    {
        SplineContainer spline = mainLineSpline;
        if (spline == null)
        {
            var go = GameObject.Find(MainSplineName);
            if (go != null) go.TryGetComponent(out spline);
        }
        if (spline == null) return;

        // 에디터 비플레이 중에는 autoProgress 지점 실시간 미리보기
        float t = (!Application.isPlaying && autoProgressFromPosition)
            ? ComputeNearestT(spline)
            : gateProgress;

        spline.Evaluate(t, out float3 wp, out float3 wt, out float3 wu);
        Vector3   gatePos = (Vector3)wp;
        Quaternion gateRot = ((Vector3)wt).sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation((Vector3)wt, (Vector3)wu)
            : Quaternion.identity;

        // 오브젝트 → 게이트 연결선
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(transform.position, gatePos);

        // 게이트 정지 지점 차량 박스 (대기=시안, 작동 중=파랑)
        Vector3 carSize = new Vector3(2f, 1.5f, 4.5f);
        Matrix4x4 old   = Gizmos.matrix;
        Gizmos.matrix   = Matrix4x4.TRS(gatePos + gateRot * new Vector3(0f, carSize.y * 0.5f, 0f), gateRot, Vector3.one);
        Gizmos.color    = currentState == WaterTestState.Idle
            ? new Color(0f, 1f, 1f, 0.8f)
            : new Color(0.1f, 0.4f, 1f, 0.9f);
        Gizmos.DrawWireCube(Vector3.zero, carSize);
        Gizmos.matrix   = old;

        // 경로 미리보기: 게이트 → 공중 → 수조 위 → 수중
        float airY = gatePos.y + liftHeight;
        Vector3 aboveGate = new Vector3(gatePos.x, airY, gatePos.z);

        Gizmos.color = new Color(0.3f, 0.85f, 1f, 0.9f);
        Gizmos.DrawLine(gatePos, aboveGate);
        Gizmos.DrawWireSphere(aboveGate, 0.2f);

        if (poolEntryPoint != null)
        {
            Vector3 abovePool = new Vector3(poolEntryPoint.position.x, airY, poolEntryPoint.position.z);
            Gizmos.DrawLine(aboveGate, abovePool);

            Gizmos.color = new Color(0.1f, 0.5f, 1f, 0.9f);
            Gizmos.DrawLine(abovePool, poolEntryPoint.position);
            Gizmos.DrawWireSphere(poolEntryPoint.position, 0.35f);

            // 수조 위 → 방출 지점(초록) / 해치(빨강)
            if (releasePoint != null)
            {
                Vector3 aboveRelease = new Vector3(releasePoint.position.x, airY, releasePoint.position.z);
                Gizmos.color = new Color(0.2f, 1f, 0.3f, 0.9f);
                Gizmos.DrawLine(abovePool, aboveRelease);
                Gizmos.DrawLine(aboveRelease, releasePoint.position);
            }
            if (rejectPoint != null)
            {
                Vector3 aboveReject = new Vector3(rejectPoint.position.x, airY, rejectPoint.position.z);
                Gizmos.color = new Color(1f, 0.25f, 0.2f, 0.9f);
                Gizmos.DrawLine(abovePool, aboveReject);
                Gizmos.DrawLine(aboveReject, rejectPoint.position + Vector3.up * rejectDropHeight);
                Gizmos.DrawWireSphere(rejectPoint.position, 0.35f);
            }
        }

        if (poolBottomPoint != null)
        {
            Vector3 from = poolEntryPoint != null ? poolEntryPoint.position
                : new Vector3(gatePos.x, airY, gatePos.z);
            Gizmos.color = new Color(0f, 0.2f, 1f, 0.9f);
            Gizmos.DrawLine(from, poolBottomPoint.position);
            Gizmos.DrawWireSphere(poolBottomPoint.position, 0.35f);
        }

        // 캡처 존 (게이트 바로 앞 CaptureEpsilon m 구간)
        Gizmos.color  = Color.magenta;
        Gizmos.matrix = Matrix4x4.TRS(
            gatePos - gateRot * Vector3.forward * (CaptureEpsilon * 0.5f) + Vector3.up * 0.25f,
            gateRot, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(carSize.x, 0.5f, CaptureEpsilon));
        Gizmos.matrix = old;

#if UNITY_EDITOR
        Handles.Label(gatePos + Vector3.up * (liftHeight + 0.5f),
            $"WaterTest [{currentState}]\nmissing={missingPartCount}  result={lastResult}");
        Handles.Label(gatePos - gateRot * Vector3.forward * CaptureEpsilon,
            $"캡처존 {CaptureEpsilon:F2}m");
#endif
    }

    private float ComputeNearestT(SplineContainer spline)
    {
        float3 local = spline.transform.InverseTransformPoint(transform.position);
        SplineUtility.GetNearestPoint(spline.Spline, local, out _, out float t);
        return Mathf.Clamp01(t);
    }

#if UNITY_EDITOR
    // ─── 임시 VFX 자동 생성 (에디터 전용) ───────────────────────────────────────
    //
    // WaterTestStation 인스펙터 우클릭 → "임시 VFX 생성 (테스트용)"
    //
    // 생성 위치:
    //   splashVFX     → poolEntryPoint 자식 (없으면 this 자식)
    //   normalBubbles → poolBottomPoint 자식
    //   defectBubbles → poolBottomPoint 자식 (X +0.3 오프셋)
    //   passVFX       → poolEntryPoint 자식 (수면, 출수 물보라)
    //   failVFX       → poolBottomPoint 자식, Y +1.0 (물속 차량 높이 스파크)
    //
    // 이미 슬롯이 연결된 VFX는 건너뜀 (중복 생성 없음).
    // 입자는 원형 소프트 텍스처(VFX_SoftCircle)로 그린다 (텍스처 없는 파티클 재질은 사각형으로 보임).

    [ContextMenu("임시 VFX 생성 (테스트용)")]
    private void CreateTempVFX()
    {
        UnityEditor.Undo.SetCurrentGroupName("Create Temp Water Test VFX");
        int group = UnityEditor.Undo.GetCurrentGroup();

        if (splashVFX     == null) splashVFX     = BuildSplashVFX();
        if (normalBubbles == null) normalBubbles = BuildNormalBubbles();
        if (defectBubbles == null) defectBubbles = BuildDefectBubbles();
        if (passVFX       == null) passVFX       = BuildPassVFX();
        if (failVFX       == null) failVFX       = BuildFailVFX();
        if (dripVFX       == null) dripVFX       = BuildDripVFX();

        UnityEditor.Undo.RecordObject(this, "Assign VFX References");
        UnityEditor.Undo.CollapseUndoOperations(group);
        UnityEditor.EditorUtility.SetDirty(this);

        UnityEditor.AssetDatabase.SaveAssets();
        UnityEditor.AssetDatabase.Refresh();
        Debug.Log("[WaterTest] 임시 VFX 6개 생성 완료. Ctrl+Z로 되돌릴 수 있습니다.");
    }

    // ── 출수 후 물방울 ── 차량 바닥 면적(Box)에서 아래로 떨어지는 루프
    private ParticleSystem BuildDripVFX()
    {
        Transform parent = hangerGrabPoint != null ? hangerGrabPoint : transform;
        GameObject go = MakeChild("DripVFX", parent, new Vector3(0f, 0.2f, 0f));

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ConfigMain(ps,
            color:    new Color(0.65f, 0.88f, 1f, 0.8f),
            loop:     true,  duration: 2f,
            lifeMin:  0.6f,  lifeMax: 1.0f,
            speedMin: 0f,    speedMax: 0.2f,
            sizeMin:  0.04f, sizeMax: 0.09f,
            gravity:  1.2f,  maxPart: 120);

        var em = ps.emission;
        em.rateOverTime = 45f;

        // 차량 바닥 면적 (라인 진행 방향 X 기준 길이 4.2m, 폭 1.9m)
        var sh = ps.shape;
        sh.enabled   = true;
        sh.shapeType = ParticleSystemShapeType.Box;
        sh.scale     = new Vector3(4.2f, 0.1f, 1.9f);

        AssignMat(ps, "VFX_Splash", new Color(0.65f, 0.88f, 1f, 0.9f), false);
        return ps;
    }

    // ── 입수/출수 물 튀김 ── 수면 넓이(차 길이)의 Box에서 위로 Burst, 중력으로 다시 떨어짐
    private ParticleSystem BuildSplashVFX()
    {
        Transform parent = poolEntryPoint != null ? poolEntryPoint : transform;
        GameObject go = MakeChild("SplashVFX", parent, Vector3.zero);
        go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f); // 위로 열리도록

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ConfigMain(ps,
            color:    new Color(0.75f, 0.92f, 1f, 0.9f),
            loop:     false, duration: 1f,
            lifeMin:  0.45f, lifeMax: 0.9f,
            speedMin: 2.5f,  speedMax: 5f,
            sizeMin:  0.08f, sizeMax: 0.26f,
            gravity:  1.8f,  maxPart: 120);

        var em = ps.emission;
        em.rateOverTime = 0;
        em.SetBursts(new[] { new ParticleSystem.Burst(0f, 70) });

        var sh = ps.shape;
        sh.enabled    = true;
        sh.shapeType  = ParticleSystemShapeType.Cone;
        sh.angle      = 25f;
        sh.radius     = 1.6f;
        sh.scale      = new Vector3(1.4f, 0.65f, 1f); // 회전(-90,0,0) 기준: 로컬 X=월드 X(차 길이), 로컬 Y=월드 Z(폭)

        AssignMat(ps, "VFX_Splash", new Color(0.75f, 0.92f, 1f, 0.9f), false);
        return ps;
    }

    // ── 검사 중 일반 기포 ── 차량 바닥 면적 Box에서 위로 올라오는 루프
    private ParticleSystem BuildNormalBubbles()
    {
        Transform parent = poolBottomPoint != null ? poolBottomPoint : transform;
        GameObject go = MakeChild("NormalBubbles", parent, new Vector3(0f, 0.1f, 0f));

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ConfigMain(ps,
            color:    new Color(0.8f, 0.95f, 1f, 0.7f),
            loop:     true,  duration: 5f,
            lifeMin:  1.4f,  lifeMax: 2.4f,
            speedMin: 0f,    speedMax: 0f,
            sizeMin:  0.07f, sizeMax: 0.18f,
            gravity:  0f,    maxPart: 120);

        var em = ps.emission;
        em.rateOverTime = 20f;

        var sh = ps.shape;
        sh.enabled   = true;
        sh.shapeType = ParticleSystemShapeType.Box;
        sh.scale     = new Vector3(4.4f, 0.1f, 2.2f);

        // 위로 올라가는 속도 (X·Y·Z 모두 같은 TwoConstants 모드여야 에러 없음). 수면(약 2m 위)을 넘지 않게
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.x       = new ParticleSystem.MinMaxCurve(-0.05f, 0.05f);
        vel.y       = new ParticleSystem.MinMaxCurve(0.35f, 0.75f);
        vel.z       = new ParticleSystem.MinMaxCurve(-0.05f, 0.05f);

        AssignMat(ps, "VFX_NormalBubbles", new Color(0.8f, 0.95f, 1f, 0.7f), false);
        return ps;
    }

    // ── 불량 기포 ── 노랑-주황, 더 빠르고 많음
    private ParticleSystem BuildDefectBubbles()
    {
        Transform parent = poolBottomPoint != null ? poolBottomPoint : transform;
        GameObject go = MakeChild("DefectBubbles", parent, new Vector3(0.3f, 0.5f, 0f));

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ConfigMain(ps,
            color:    new Color(1f, 0.62f, 0.08f, 0.72f),
            loop:     true,  duration: 5f,
            lifeMin:  0.8f,  lifeMax: 1.4f,
            speedMin: 0f,    speedMax: 0f,
            sizeMin:  0.06f, sizeMax: 0.18f,
            gravity:  0f,    maxPart: 120);

        var em = ps.emission;
        em.rateOverTime = 24f;

        var sh = ps.shape;
        sh.enabled   = true;
        sh.shapeType = ParticleSystemShapeType.Box;
        sh.scale     = new Vector3(3.6f, 0.6f, 2f);

        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.x       = new ParticleSystem.MinMaxCurve(0f, 0f);
        vel.y       = new ParticleSystem.MinMaxCurve(0.6f, 1.1f);
        vel.z       = new ParticleSystem.MinMaxCurve(0f, 0f);

        AssignMat(ps, "VFX_DefectBubbles", new Color(1f, 0.62f, 0.08f, 0.72f), false);
        return ps;
    }

    // ── 통과 물보라 ── 출수 순간 수면에서 위로 솟았다 떨어지는 흰·하늘색 물방울
    private ParticleSystem BuildPassVFX()
    {
        Transform parent = poolEntryPoint != null ? poolEntryPoint : transform;
        GameObject go = MakeChild("PassVFX", parent, Vector3.zero);
        go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ConfigMain(ps,
            color:    new Color(0.85f, 0.97f, 1f, 0.95f),
            loop:     false, duration: 1.2f,
            lifeMin:  0.7f,  lifeMax: 1.3f,
            speedMin: 3f,    speedMax: 6.5f,
            sizeMin:  0.06f, sizeMax: 0.2f,
            gravity:  1.6f,  maxPart: 160);
        var main = ps.main;
        main.startColor = new ParticleSystem.MinMaxGradient(
            new Color(0.85f, 0.97f, 1f, 0.95f), new Color(0.45f, 0.8f, 1f, 0.9f));

        var em = ps.emission;
        em.rateOverTime = 0;
        em.SetBursts(new[] { new ParticleSystem.Burst(0f, 80), new ParticleSystem.Burst(0.15f, 40) });

        var sh = ps.shape;
        sh.enabled   = true;
        sh.shapeType = ParticleSystemShapeType.Cone;
        sh.angle     = 18f;
        sh.radius    = 1.5f;
        sh.scale     = new Vector3(1.4f, 0.65f, 1f);

        // 끝으로 갈수록 작아지며 사라짐
        var sz = ps.sizeOverLifetime;
        sz.enabled = true;
        sz.size    = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.3f));

        AssignMat(ps, "VFX_Pass", new Color(0.85f, 0.97f, 1f, 0.95f), false);
        return ps;
    }

    // ── 불량 수중 스파크 ── 주황-빨강 가산, 길게 늘인 입자가 짧게 튀는 루프 (검사 중 재생)
    private ParticleSystem BuildFailVFX()
    {
        Transform parent = poolBottomPoint != null ? poolBottomPoint : transform;
        GameObject go = MakeChild("FailVFX", parent, new Vector3(0f, 1f, 0f));

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ConfigMain(ps,
            color:    new Color(1f, 0.55f, 0.15f, 1f),
            loop:     true,  duration: 0.6f,
            lifeMin:  0.15f, lifeMax: 0.4f,
            speedMin: 2f,    speedMax: 5f,
            sizeMin:  0.06f, sizeMax: 0.14f,
            gravity:  0.6f,  maxPart: 120);

        var em = ps.emission;
        em.rateOverTime = 8f; // Burst 사이 잔불 느낌
        em.SetBursts(new[] { new ParticleSystem.Burst(0f, 30) });

        var sh = ps.shape;
        sh.enabled   = true;
        sh.shapeType = ParticleSystemShapeType.Box;
        sh.scale     = new Vector3(3.6f, 0.8f, 1.8f);

        AssignMat(ps, "VFX_Fail", new Color(1f, 0.55f, 0.15f, 1f), true);
        var r = ps.GetComponent<ParticleSystemRenderer>();
        r.renderMode    = ParticleSystemRenderMode.Stretch;
        r.velocityScale = 0.06f;
        r.lengthScale   = 3f;
        return ps;
    }

    // ── 공통: main 모듈 설정 ──────────────────────────────────────────────────────
    private static void ConfigMain(ParticleSystem ps, Color color,
        bool loop, float duration,
        float lifeMin, float lifeMax,
        float speedMin, float speedMax,
        float sizeMin, float sizeMax,
        float gravity, int maxPart)
    {
        var m = ps.main;
        m.playOnAwake     = false;
        m.loop            = loop;
        m.duration        = duration;
        m.startLifetime   = new ParticleSystem.MinMaxCurve(lifeMin, lifeMax);
        m.startSpeed      = new ParticleSystem.MinMaxCurve(speedMin, speedMax);
        m.startSize       = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
        m.startColor      = color;
        m.gravityModifier = gravity;
        m.simulationSpace = ParticleSystemSimulationSpace.World;
        m.maxParticles    = maxPart;
    }

    // ── 자식 오브젝트 생성 (Undo 지원) ──────────────────────────────────────────
    private static GameObject MakeChild(string name, Transform parent, Vector3 localPos)
    {
        var go = new GameObject(name);
        UnityEditor.Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        return go;
    }

    // ── 재질 생성·할당 ────────────────────────────────────────────────────────────
    /// <summary>ParticleSystemRenderer에 재질을 만들어 할당한다.</summary>
    private static void AssignMat(ParticleSystem ps, string matName, Color color, bool additive)
    {
        var mat = CreateOrLoadMat(matName, color, additive);
        if (mat == null) return;
        var r = ps.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.material   = mat;
    }

    // 원형 소프트 입자 텍스처 (없으면 텍스처 없이 생성 → 사각 입자)
    private const string SoftCircleTexPath = "Assets/@Resource/Textures/VFX/VFX_SoftCircle.png";

    /// <summary>
    /// Assets/@Resource/Materials/VFX/{matName}.mat 을 찾아 반환하거나 새로 생성한다.
    /// additive=true → 가산 합성(스파크), false → 알파 블렌드(물·기포).
    /// </summary>
    private static Material CreateOrLoadMat(string matName, Color color, bool additive)
    {
        const string dir = "Assets/@Resource/Materials/VFX";
        string path = $"{dir}/{matName}.mat";

        // 이미 저장된 에셋 재사용
        var existing = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        EnsureDir(dir);

        // URP 파티클 셰이더 탐색 (버전·패키지에 따라 이름이 다를 수 있음)
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
        if (shader == null) shader = Shader.Find("Particles/Alpha Blended");
        if (shader == null)
        {
            Debug.LogWarning($"[WaterTest] 파티클 셰이더를 찾지 못했습니다 — {matName} 재질 생성 실패.");
            return null;
        }

        var mat = new Material(shader) { name = matName };

        // 공통: 투명 렌더링
        mat.SetFloat("_Surface", 1f);   // 0=Opaque, 1=Transparent
        mat.SetFloat("_ZWrite",  0f);

        if (additive)
        {
            // 가산 합성 (스파크)
            mat.SetFloat("_Blend",    2f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            mat.EnableKeyword("_BLENDMODE_ADD");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 1;
        }
        else
        {
            // 알파 블렌드 (물·기포)
            mat.SetFloat("_Blend",    0f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        // 베이스 컬러 (셰이더 버전별 프로퍼티 이름이 다름)
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     color);
        if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", color);

        // 원형 소프트 텍스처 (사각 입자 방지)
        var soft = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(SoftCircleTexPath);
        if (soft != null)
        {
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", soft);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", soft);
        }

        UnityEditor.AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    /// <summary>경로의 각 폴더를 순서대로 생성한다 (이미 있으면 건너뜀).</summary>
    private static void EnsureDir(string path)
    {
        string[] parts = path.Split('/');
        string   cur   = parts[0]; // "Assets"
        for (int i = 1; i < parts.Length; i++)
        {
            string next = cur + "/" + parts[i];
            if (!UnityEditor.AssetDatabase.IsValidFolder(next))
                UnityEditor.AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }
#endif
}
