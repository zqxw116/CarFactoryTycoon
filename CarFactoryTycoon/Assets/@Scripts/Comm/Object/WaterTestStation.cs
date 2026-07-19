using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 5라인 방수 테스트 공정. 집게형 행거로 차량을 들어올려 수영장에 침수시킨 뒤 방수 상태를 판정한다.
///
///   Idle → HangerDescending → Grabbing → Lifting → TravelToPool
///   → Submerging → Testing (스파크 연출) → Emerging → TravelToLine → Lowering → Releasing
///   → [Pass]   Cooldown: 행거가 차량을 releasePoint(게이트 이후)에 내려놓음 → 라인 정상 이동
///   → [Defect] Cooldown: 행거가 차량을 rejectPoint에 내려놓음 (TravelToLine 시작 시 문 열림) → VanishCar
///
/// - LineTrafficManager에 게이트로 등록: 차량이 게이트 앞에서 감속 정지 (WheelStation과 동일 방식).
/// - 잡힌 차량은 isMoving=false: 행거 GrabPoint가 차량 transform을 직접 제어하여 수영장으로 이송.
/// - 방출은 SetProgress(gateProgress)로 게이트 위로 올려 통과 처리 + isMoving=true.
///
/// [씬 셋업]
///  - 빈 오브젝트에 이 컴포넌트 추가, 게이트 위치에 배치.
///  - hangerRoot: 행거 루트 오브젝트 (씬에서 움직이는 전체 행거 메시의 부모).
///  - hangerGrabPoint: hangerRoot 자식, 차량이 매달리는 지점 (로컬 오프셋은 차량 중심 높이 기준).
///  - poolEntryPoint: 수면 위 위치 Transform (여기에 splashVFX를 자식으로 두면 됨).
///  - poolBottomPoint: 수중 최저 위치 Transform (여기에 normalBubbles/defectBubbles 자식 권장).
///  - passVFX / failVFX: 결과 이펙트 (수면 위 적당한 위치에 배치).
/// </summary>
public class WaterTestStation : MonoBehaviour, ILineGate
{
    public enum WaterTestState
    {
        Idle,
        HangerDescending,   // 행거가 차량 위치로 하강
        Grabbing,           // 집게 체결 연출 대기
        Lifting,            // 공중으로 상승
        TravelToPool,       // 수영장으로 수평 이동
        Submerging,         // 입수 (하강)
        Testing,            // 검사 중 (물속 체류)
        Emerging,           // 출수 (상승)
        TravelToLine,       // 라인으로 복귀 수평 이동
        Lowering,           // 라인 높이로 하강
        Releasing,          // 행거 해제 연출
        Rejecting,          // 불량 차량을 폐기 구멍으로 밀어냄
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

    // ─── 행거 연결 ───────────────────────────────────────────────────────────────
    [Header("행거 연결")]
    [Tooltip("행거 전체 루트 Transform. 이 오브젝트를 MoveTowards로 이동시켜 연출한다.")]
    public Transform hangerRoot;
    [Tooltip("hangerRoot 자식. 차량이 매달리는 기준점 — 이 Transform의 위치로 차량이 따라온다.")]
    public Transform hangerGrabPoint;

    // ─── 경로 포인트 ─────────────────────────────────────────────────────────────
    [Header("경로 포인트")]
    [Tooltip("차량 캡처 위치 Y + 이 값 = 공중 경유 높이. 수영장 위를 충분히 넘는 값으로 설정.")]
    public float liftHeight = 5f;
    [Tooltip("수면 위 진입 지점. 행거가 여기까지 수평 이동한 뒤 하강한다. splashVFX를 여기 자식에 두길 권장.")]
    public Transform poolEntryPoint;
    [Tooltip("수중 최저 지점. 차량이 완전히 잠기는 깊이. normalBubbles/defectBubbles를 여기 자식에 두길 권장.")]
    public Transform poolBottomPoint;
    [Tooltip("통과 차량을 라인에 내려놓는 위치. 게이트 이후 라인 위에 배치 (여기 위치의 스플라인 진행도로 SetProgress).")]
    public Transform releasePoint;
    [Range(0f, 1f)]
    [Tooltip("releasePoint의 스플라인 진행도 (0~1). autoProgressFromPosition이 켜져 있으면 자동 계산.")]
    public float releaseProgress = 0.55f;

    // ─── 이동 속도 ───────────────────────────────────────────────────────────────
    [Header("이동 속도 (m/s)")]
    public float hangerDescendSpeed = 2.5f; // 차량 위치로 하강
    public float liftSpeed          = 2f;   // 공중 상승
    public float travelSpeed        = 3f;   // 수평 이동 (수영장/라인 왕복)
    public float submergeSpeed      = 0.8f; // 입수 하강
    public float emergeSpeed        = 0.8f; // 출수 상승
    public float lowerSpeed         = 1.5f; // 라인 복귀 하강
    public float hangerReturnSpeed  = 3f;   // 쿨다운 중 홈 복귀

    // ─── 타이밍 ──────────────────────────────────────────────────────────────────
    [Header("타이밍 (초)")]
    public float grabDuration    = 0.5f; // 집게 체결 연출
    public float testDuration    = 3f;   // 물속 체류(검사) 시간
    public float releaseDuration = 0.4f; // 행거 해제 연출
    public float cooldownDuration= 1f;

    // ─── 검사 판정 ────────────────────────────────────────────────────────────────
    [Header("검사 판정")]
    [Tooltip("미체결 파츠가 이 수 이상이면 심각한 불량 (보너스 없음). 미만이면 경미한 불량 (반액 보너스).")]
    public int majorDefectThreshold = 3;
    [Tooltip("정상 통과 보너스 ($).")]
    public int passBonus = 200;

    // ─── 불량 폐기 ───────────────────────────────────────────────────────────────
    [Header("불량 폐기")]
    [Tooltip("라인 밖 폐기 구멍/문 위치. 불량 차량이 이 지점으로 이동 후 사라진다.")]
    public Transform rejectPoint;
    [Tooltip("폐기 문 오브젝트 (없으면 생략). Rejecting 시작 시 슬라이드로 열리고, Cooldown에서 닫힌다.")]
    public Transform rejectDoor;
    [Tooltip("문이 열릴 때 이동하는 로컬 좌표 오프셋. 예: (2,0,0) = 오른쪽으로 2m 슬라이드.")]
    public Vector3   rejectDoorSlideOffset = new Vector3(2f, 0f, 0f);
    [Tooltip("문 슬라이드 속도 (m/s).")]
    public float     rejectDoorSlideSpeed = 1.5f;
    // ─── 이펙트 ──────────────────────────────────────────────────────────────────
    [Header("이펙트")]
    [Tooltip("입수/출수 시 물 튀김. poolEntryPoint 근처에 배치 권장.")]
    public ParticleSystem splashVFX;
    [Tooltip("검사 중 일반 공기방울. poolBottomPoint 근처에 배치 권장.")]
    public ParticleSystem normalBubbles;
    [Tooltip("불량 부위 공기방울. 미체결 파츠가 있을 때 normalBubbles와 함께 재생.")]
    public ParticleSystem defectBubbles;
    [Tooltip("정상/경미 통과 결과 이펙트.")]
    public ParticleSystem passVFX;
    [Tooltip("심각한 불량 결과 이펙트 (라이트 깜박임, 스파크 등).")]
    public ParticleSystem failVFX;

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

    // 행거 그랩포인트 → 루트 오프셋 (캐시). hangerRoot 이동 목표 = grabWorldTarget + grabToRoot.
    private Vector3    grabToRoot;
    // 캡처 시점 차량 월드 위치. 라인 복귀·공중 높이 계산의 기준.
    private Vector3    carCapturePos;
    // 공중 경유 Y (= carCapturePos.y + liftHeight)
    private float      airHeight;
    // 초기 hangerRoot 위치 → 쿨다운 후 복귀용
    private Vector3    hangerHomeRootPos;
    // 폐기 문 초기(닫힘) 로컬 위치 → Cooldown 복귀용
    private Vector3 rejectDoorClosedPos;
    // 폐기 문 열림 목표 로컬 위치 (= closedPos + slideOffset, EnsureInit에서 계산)
    private Vector3 rejectDoorOpenPos;

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

        // 행거 그랩포인트 → 루트 오프셋 (Move 함수에서 반복 계산 방지)
        if (hangerRoot != null && hangerGrabPoint != null)
            grabToRoot = hangerRoot.position - hangerGrabPoint.position;

        // 초기 행거 위치 저장 (쿨다운 복귀용)
        if (hangerRoot != null)
            hangerHomeRootPos = hangerRoot.position;

        // 폐기 문 초기 위치 저장 (Cooldown에서 원위치)
        if (rejectDoor != null)
        {
            rejectDoorClosedPos = rejectDoor.localPosition;
            rejectDoorOpenPos   = rejectDoorClosedPos + rejectDoorSlideOffset;
        }
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
                // 행거 그랩포인트가 차량 위치에 도달하면 집게 체결 시작
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
                // 수영장 연결 없으면 즉시 입수로 (fallback)
                if (poolEntryPoint == null) { BeginSubmerging(); break; }
                if (MoveHangerGrabTo(PoolAirPos(), travelSpeed))
                    BeginSubmerging();
                break;

            case WaterTestState.Submerging:
                // 수영장 바닥 연결 없으면 즉시 검사로 (fallback)
                if (poolBottomPoint == null) { BeginTesting(); break; }
                if (MoveHangerGrabTo(poolBottomPoint.position, submergeSpeed))
                    BeginTesting();
                break;

            case WaterTestState.Testing:
                timer -= Time.deltaTime;
                if (timer <= 0f) BeginEmerging();
                break;

            case WaterTestState.Emerging:
                if (MoveHangerGrabTo(PoolAirPos(), emergeSpeed))
                    BeginTravelToLine();
                break;

            case WaterTestState.TravelToLine:
                // 불량: 이동 중 문 열기 시작 (차량 도착 전에 문이 열려 있도록)
                if (lastResult != WaterTestResult.Pass && rejectDoor != null)
                    rejectDoor.localPosition = Vector3.MoveTowards(
                        rejectDoor.localPosition, rejectDoorOpenPos, rejectDoorSlideSpeed * Time.deltaTime);

                if (MoveHangerGrabTo(AirPosOf(GetDestPos()), travelSpeed))
                    BeginLowering();
                break;

            case WaterTestState.Lowering:
                // 불량: 계속 문 열기 (TravelToLine에서 다 못 열었을 경우 대비)
                if (lastResult != WaterTestResult.Pass && rejectDoor != null)
                    rejectDoor.localPosition = Vector3.MoveTowards(
                        rejectDoor.localPosition, rejectDoorOpenPos, rejectDoorSlideSpeed * Time.deltaTime);

                if (MoveHangerGrabTo(GetDestPos(), lowerSpeed))
                    BeginReleasing();
                break;

            case WaterTestState.Releasing:
                timer -= Time.deltaTime;
                if (timer <= 0f) ReleaseCar();
                break;

            case WaterTestState.Rejecting:
                // 사용하지 않는 Dead State — 즉시 Cooldown으로
                timer        = cooldownDuration;
                currentState = WaterTestState.Cooldown;
                break;

            case WaterTestState.Cooldown:
                timer -= Time.deltaTime;
                // 행거를 초기(홈) 위치로 복귀
                if (hangerRoot != null)
                    hangerRoot.position = Vector3.MoveTowards(
                        hangerRoot.position, hangerHomeRootPos, hangerReturnSpeed * Time.deltaTime);
                // 폐기 문 슬라이드 닫기
                if (rejectDoor != null)
                    rejectDoor.localPosition = Vector3.MoveTowards(
                        rejectDoor.localPosition, rejectDoorClosedPos, rejectDoorSlideSpeed * Time.deltaTime);
                if (timer <= 0f)
                    currentState = WaterTestState.Idle;
                break;
        }

        // Grabbing 이후 Releasing까지: 차량이 행거 그랩포인트 위치를 따른다
        if (currentCar != null && IsCarAttached() && hangerGrabPoint != null)
            currentCar.transform.position = hangerGrabPoint.position;
    }

    // Grabbing(집게 체결 시작) ~ Releasing(해제) 구간 = 차량이 행거에 붙어 있는 상태
    private bool IsCarAttached() =>
        currentState >= WaterTestState.Grabbing &&
        currentState <= WaterTestState.Releasing;

    // 라인 위 공중 경유 위치 (X/Z=캡처 지점, Y=airHeight)
    private Vector3 LiftPos() => new Vector3(carCapturePos.x, airHeight, carCapturePos.z);

    // 수영장 위 공중 위치 (X/Z=poolEntryPoint, Y=airHeight). poolEntryPoint 없으면 LiftPos 폴백.
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
        PlayVFX(splashVFX); // 입수 스플래시
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
            // 스파크 연출: 물속에서 불량 감지 (loop=false이므로 자동 종료)
            PlayVFX(failVFX);
        }

        timer        = testDuration;
        currentState = WaterTestState.Testing;
    }

    private void BeginEmerging()
    {
        StopVFX(normalBubbles);
        StopVFX(defectBubbles);

        // 통과 시에만 passVFX (failVFX는 Testing에서 이미 재생됨)
        if (lastResult == WaterTestResult.Pass) PlayVFX(passVFX);

        currentState = WaterTestState.Emerging;
    }

    private void BeginTravelToLine()
    {
        PlayVFX(splashVFX); // 출수 스플래시

        // 불량 차량: 라인으로 돌아가지 않으므로 트래픽 목록에서 즉시 제거
        // → 뒷차들이 게이트 클램프에서 풀려 진행 가능
        if (lastResult != WaterTestResult.Pass && currentCar != null)
            currentCar.LeaveLine();

        currentState = WaterTestState.TravelToLine;
    }

    private void BeginLowering()
    {
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
            // releaseProgress 위치로 SetProgress → 게이트 이후 지점에서 재출발
            currentCar.SetProgress(releaseProgress);
            currentCar.isMoving = true;
            currentCar          = null;

            timer        = cooldownDuration;
            currentState = WaterTestState.Cooldown;
        }
        else
        {
            // ── 불량: 행거가 rejectPoint에 내려놓은 차량을 폐기 ──────────────────
            Debug.LogWarning($"<color=red>[WaterTest] ✗ {currentCar.name} 방수 실패! " +
                             $"(누락 {missingPartCount}개) → 폐기 처리</color>");
            VanishCar();
        }
    }

    // ─── 유틸 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// hangerGrabPoint가 grabWorldTarget에 도달하도록 hangerRoot를 MoveTowards로 이동.
    /// 도달(0.05m 이내) 시 true. 행거 미연결 시 즉시 true (애니메이션 없이 상태 진행 → 로직은 정상 동작).
    /// </summary>
    private bool MoveHangerGrabTo(Vector3 grabWorldTarget, float speed)
    {
        if (hangerRoot == null || hangerGrabPoint == null) return true;
        hangerRoot.position = Vector3.MoveTowards(
            hangerRoot.position, grabWorldTarget + grabToRoot, speed * Time.deltaTime);
        return Vector3.Distance(hangerGrabPoint.position, grabWorldTarget) < 0.05f;
    }

    /// <summary>진행 중인 사이클을 취소한다. 차량이 있으면 상태에 따라 라인 복귀 또는 풀 반환.</summary>
    private void CancelCycle()
    {
        if (currentState == WaterTestState.Idle || currentState == WaterTestState.Cooldown) return;
        StopVFX(normalBubbles);
        StopVFX(defectBubbles);
        if (currentCar != null && currentCar.gameObject.activeInHierarchy)
        {
            if (currentCar.targetSpline != null)
            {
                // 아직 스플라인에 연결 → 게이트 이전으로 복귀 후 재출발
                currentCar.SetProgress(gateProgress);
                currentCar.isMoving = true;
            }
            else
            {
                // LeaveLine() 이미 호출됨 (불량 이송 중 취소) → 풀 반환
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
        currentCar   = null;
        lastResult   = WaterTestResult.None;
        timer        = cooldownDuration;
        currentState = WaterTestState.Cooldown;
    }

    /// <summary>불량 차량을 풀에 반환하고 Cooldown으로 진입한다.</summary>
    private void VanishCar()
    {
        if (currentCar != null)
        {
            if (CarPool.Instance != null) CarPool.Instance.Return(currentCar);
            else currentCar.gameObject.SetActive(false);
            currentCar = null;
        }
        timer        = cooldownDuration;
        currentState = WaterTestState.Cooldown;
    }

    // 결과에 따른 목적지 위치
    // Pass  → releasePoint (게이트 이후 라인 위)
    // Defect→ rejectPoint  (폐기 구멍 위치)
    private Vector3 GetDestPos() => lastResult == WaterTestResult.Pass ? GetReleasePos() : GetRejectPos();
    private Vector3 GetReleasePos() => releasePoint != null ? releasePoint.position : carCapturePos;
    private Vector3 GetRejectPos()  => rejectPoint  != null ? rejectPoint.position  : carCapturePos;

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

        // 경로 미리보기: 게이트 → 공중 → 수영장 위 → 수중
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
    //   passVFX       → poolEntryPoint 자식, Y +1.5
    //   failVFX       → poolEntryPoint 자식, Y +1.5
    //
    // 이미 슬롯이 연결된 VFX는 건너뜀 (중복 생성 없음).

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

        UnityEditor.Undo.RecordObject(this, "Assign VFX References");
        UnityEditor.Undo.CollapseUndoOperations(group);
        UnityEditor.EditorUtility.SetDirty(this);

        UnityEditor.AssetDatabase.SaveAssets();
        UnityEditor.AssetDatabase.Refresh();
        Debug.Log("[WaterTest] 임시 VFX 5개 생성 완료. Ctrl+Z로 되돌릴 수 있습니다.");
    }

    // ── 입수/출수 물 튀김 ── Hemisphere Burst, 중력 있음, 위로 퍼짐
    private ParticleSystem BuildSplashVFX()
    {
        Transform parent = poolEntryPoint != null ? poolEntryPoint : transform;
        GameObject go = MakeChild("SplashVFX", parent, Vector3.zero);
        go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f); // 위로 열리도록

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ConfigMain(ps,
            color:    new Color(0.65f, 0.88f, 1f, 0.9f),
            loop:     false, duration: 1f,
            lifeMin:  0.35f, lifeMax: 0.75f,
            speedMin: 2f,    speedMax: 5.5f,
            sizeMin:  0.08f, sizeMax: 0.28f,
            gravity:  1.8f,  maxPart: 60);

        var em = ps.emission;
        em.rateOverTime = 0;
        em.SetBursts(new[] { new ParticleSystem.Burst(0f, 28) });

        var sh = ps.shape;
        sh.enabled    = true;
        sh.shapeType  = ParticleSystemShapeType.Hemisphere;
        sh.radius     = 0.75f;

        AssignMat(ps, "VFX_Splash", new Color(0.65f, 0.88f, 1f, 0.9f), false);
        return ps;
    }

    // ── 검사 중 일반 기포 ── Box에서 위로 올라오는 루프
    private ParticleSystem BuildNormalBubbles()
    {
        Transform parent = poolBottomPoint != null ? poolBottomPoint : transform;
        GameObject go = MakeChild("NormalBubbles", parent, Vector3.zero);

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ConfigMain(ps,
            color:    new Color(0.55f, 0.85f, 1f, 0.55f),
            loop:     true,  duration: 5f,
            lifeMin:  1.6f,  lifeMax: 2.8f,
            speedMin: 0f,    speedMax: 0f,
            sizeMin:  0.03f, sizeMax: 0.11f,
            gravity:  0f,    maxPart: 80);

        var em = ps.emission;
        em.rateOverTime = 8f;

        var sh = ps.shape;
        sh.enabled   = true;
        sh.shapeType = ParticleSystemShapeType.Box;
        sh.scale     = new Vector3(1.8f, 0.1f, 1.8f);

        // 위로 올라가는 속도 (X·Y·Z 모두 같은 TwoConstants 모드여야 에러 없음)
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.x       = new ParticleSystem.MinMaxCurve(0f, 0f);
        vel.y       = new ParticleSystem.MinMaxCurve(0.25f, 0.75f);
        vel.z       = new ParticleSystem.MinMaxCurve(0f, 0f);

        AssignMat(ps, "VFX_NormalBubbles", new Color(0.55f, 0.85f, 1f, 0.55f), false);
        return ps;
    }

    // ── 불량 기포 ── 노랑-주황, 더 빠르고 많음
    private ParticleSystem BuildDefectBubbles()
    {
        Transform parent = poolBottomPoint != null ? poolBottomPoint : transform;
        GameObject go = MakeChild("DefectBubbles", parent, new Vector3(0.3f, 0f, 0f));

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ConfigMain(ps,
            color:    new Color(1f, 0.62f, 0.08f, 0.72f),
            loop:     true,  duration: 5f,
            lifeMin:  1.0f,  lifeMax: 2.0f,
            speedMin: 0f,    speedMax: 0f,
            sizeMin:  0.05f, sizeMax: 0.17f,
            gravity:  0f,    maxPart: 120);

        var em = ps.emission;
        em.rateOverTime = 18f;

        var sh = ps.shape;
        sh.enabled   = true;
        sh.shapeType = ParticleSystemShapeType.Box;
        sh.scale     = new Vector3(1.8f, 0.1f, 1.8f);

        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.x       = new ParticleSystem.MinMaxCurve(0f, 0f);
        vel.y       = new ParticleSystem.MinMaxCurve(0.55f, 1.6f);
        vel.z       = new ParticleSystem.MinMaxCurve(0f, 0f);

        AssignMat(ps, "VFX_DefectBubbles", new Color(1f, 0.62f, 0.08f, 0.72f), false);
        return ps;
    }

    // ── 정상 통과 이펙트 ── 초록/금색 Sphere Burst
    private ParticleSystem BuildPassVFX()
    {
        Transform parent = poolEntryPoint != null ? poolEntryPoint : transform;
        GameObject go = MakeChild("PassVFX", parent, new Vector3(0f, 1.5f, 0f));

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ConfigMain(ps,
            color:    new Color(0.15f, 1f, 0.3f, 1f),
            loop:     false, duration: 1.5f,
            lifeMin:  0.7f,  lifeMax: 1.5f,
            speedMin: 2f,    speedMax: 6f,
            sizeMin:  0.07f, sizeMax: 0.22f,
            gravity:  0.4f,  maxPart: 80);

        var em = ps.emission;
        em.rateOverTime = 0;
        em.SetBursts(new[] { new ParticleSystem.Burst(0f, 45) });

        var sh = ps.shape;
        sh.enabled   = true;
        sh.shapeType = ParticleSystemShapeType.Sphere;
        sh.radius    = 0.3f;

        AssignMat(ps, "VFX_Pass", new Color(0.15f, 1f, 0.3f, 1f), true);
        return ps;
    }

    // ── 불량 이펙트 ── 빨강 Cone Burst + 소량 지속 (스파크 느낌)
    private ParticleSystem BuildFailVFX()
    {
        Transform parent = poolEntryPoint != null ? poolEntryPoint : transform;
        GameObject go = MakeChild("FailVFX", parent, new Vector3(0f, 1.5f, 0f));

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ConfigMain(ps,
            color:    new Color(1f, 0.12f, 0.05f, 1f),
            loop:     false, duration: 2f,
            lifeMin:  0.4f,  lifeMax: 1.0f,
            speedMin: 1.5f,  speedMax: 5f,
            sizeMin:  0.06f, sizeMax: 0.2f,
            gravity:  0.8f,  maxPart: 80);

        var em = ps.emission;
        em.rateOverTime = 5f; // Burst 이후 잔불 느낌
        em.SetBursts(new[] { new ParticleSystem.Burst(0f, 30) });

        var sh = ps.shape;
        sh.enabled   = true;
        sh.shapeType = ParticleSystemShapeType.Cone;
        sh.angle     = 30f;
        sh.radius    = 0.3f;

        AssignMat(ps, "VFX_Fail", new Color(1f, 0.12f, 0.05f, 1f), true);
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

    /// <summary>
    /// Assets/@Resource/Materials/VFX/{matName}.mat 을 찾아 반환하거나 새로 생성한다.
    /// additive=true → 가산 합성(Pass/Fail), false → 알파 블렌드(물·기포).
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
            // 가산 합성 (Pass/Fail 이펙트)
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
