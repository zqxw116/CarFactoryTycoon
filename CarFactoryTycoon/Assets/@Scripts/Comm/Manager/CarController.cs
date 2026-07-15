using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Burst.CompilerServices;
using Unity.Mathematics;     // [추가] 수학 연산용 (float3 등)
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;   // [추가] 스플라인 네임스페이스

public class CarController : MonoBehaviour
{
    [Header("물류 설정")]
    public float moveSpeed = 1.0f;
    public bool isMoving = true;

    [Header("스플라인 추적 상태 (디버그용)")]
    public SplineContainer targetSpline;
    [Range(0f, 1f)] public float pathProgress = 0f; // 0=시작점, 1=도착점
    private float splineLength;

    [Header("판매")]
    [Tooltip("완제품 출고 성공 시 벌어들이는 금액")]
    public int sellPrice = 100;
    private bool _finished = false;

    // LineTrafficManager(정체 관리)가 있으면 이동을 매니저가 MoveStep으로 구동한다.
    // 없으면(TestPartsScene 등) 기존대로 Update에서 자율 이동.
    private bool _managed = false;

    /// <summary>스플라인 실제 길이(m). 트래픽 매니저가 간격(m)↔진행도 환산에 사용.</summary>
    public float SplineLength => splineLength;

    /// <summary>지금 스스로 전진해야 하는 상태인지 (리프트 캡처 등으로 멈추면 false — 장애물로만 남는다).</summary>
    public bool IsDriving => isMoving && !_finished && targetSpline != null;

    [SerializeField]private SerializedDictionary<PartGroup, List<AssemblyPart>> dicListParts = new SerializedDictionary<PartGroup, List<AssemblyPart>>();

    private void Awake()
    {
        InitializeCarParts();
    }

    private void InitializeCarParts()
    {
        foreach (PartGroup group in System.Enum.GetValues(typeof(PartGroup)))
        {
            dicListParts[group] = new List<AssemblyPart>();
        }

        AssemblyPart[] allParts = GetComponentsInChildren<AssemblyPart>(true);
        foreach (AssemblyPart part in allParts)
        {
            dicListParts[part.myGroup].Add(part);
            part.Reset();
        }

        Debug.Log($"[InitializeCarParts] {gameObject.name} (ID={GetInstanceID()}) → 파츠 {allParts.Length}개 등록");
    }

    // [신규] 스포너가 차량을 생성할 때 호출하여 경로를 주입해주는 함수
    // startProgress: 출발 지점(0~1). 스포너 위치에 가까운 지점에서 시작하고 싶을 때 사용.
    public void SetPath(SplineContainer spline, float speed, float startProgress = 0f)
    {
        targetSpline = spline;
        moveSpeed = speed;
        pathProgress = Mathf.Clamp01(startProgress);
        _finished = false;
        isMoving = true;

        if (targetSpline != null)
        {
            // 스플라인의 실제 물리적 길이(m)를 계산하여 저장
            splineLength = targetSpline.CalculateLength();

            ResetParts();                 // 재사용 시 이전 체결 상태 초기화
            SnapToProgress(pathProgress); // 출발 지점으로 즉시 배치 (원점에서 한 프레임 보이는 것 방지)
        }

        // 트래픽 매니저가 있으면 등록 — 이후 이동은 매니저가 선두→후미 순서로 구동한다
        _managed = LineTrafficManager.Instance != null;
        if (_managed) LineTrafficManager.Instance.Register(this);
    }

    // 풀 반환(SetActive(false))·파괴 시 트래픽 목록에서 해제
    private void OnDisable()
    {
        if (_managed && LineTrafficManager.Instance != null)
            LineTrafficManager.Instance.Unregister(this);
        _managed = false;
    }

    /// <summary>
    /// 라인 이동을 끝내고 트래픽 관리에서 벗어난다. 출고 공정(DepartureStation)처럼
    /// 외부가 transform을 직접 움직이기 전에 호출 — 라인 끝(progress 1.0)에 남은 차가
    /// 뒷차의 장애물로 계속 잡히는 것을 방지한다.
    /// </summary>
    public void LeaveLine()
    {
        if (_managed && LineTrafficManager.Instance != null)
            LineTrafficManager.Instance.Unregister(this);
        _managed = false;
        isMoving = false;
        targetSpline = null;
    }

    /// <summary>주어진 진행도(0~1) 지점으로 위치·회전을 즉시 적용한다.</summary>
    private void SnapToProgress(float p)
    {
        if (targetSpline == null) return;

        SplineUtility.Evaluate(targetSpline.Spline, p, out float3 localPos, out float3 localTangent, out float3 localUp);
        transform.position = targetSpline.transform.TransformPoint(localPos);

        if (math.length(localTangent) > 0.001f)
        {
            Vector3 worldDirection = targetSpline.transform.TransformDirection(localTangent);
            Vector3 worldUp = targetSpline.transform.TransformDirection(localUp);
            transform.rotation = Quaternion.LookRotation(worldDirection, worldUp);
        }
    }

    private void Update()
    {
        if (_managed) return; // LineTrafficManager가 MoveStep으로 구동 (차간 간격/게이트 클램프 포함)

        // 자율 이동 폴백: 스플라인이 할당되어 있고, 이동 스위치가 켜져 있을 때만 작동
        if (isMoving && targetSpline != null && splineLength > 0f)
        {
            MoveStep(1f, 1f);
        }
    }

    /// <summary>
    /// 한 프레임 이동. LineTrafficManager가 선두→후미 순서로 호출한다.
    /// speedFactor: 감속 배율(0~1, 앞차/게이트 접근 시), maxProgress: 넘을 수 없는 진행도(앞차−minGap, 게이트 등).
    /// </summary>
    public void MoveStep(float speedFactor, float maxProgress)
    {
        if (_finished || !isMoving || targetSpline == null || splineLength <= 0f) return;

        // 속도를 실제 스플라인 길이로 나누어 진행도(0~1)를 증가시킴
        float next = pathProgress + (moveSpeed * speedFactor / splineLength) * Time.deltaTime;

        // 제약(앞차 꽁무니/게이트)을 넘지 못하게 클램프. 이미 제약보다 앞서 있으면 후진하지 않고 정지.
        pathProgress = Mathf.Min(next, Mathf.Max(pathProgress, maxProgress));

        // 라인 끝에 도달하면 출고 처리 — 출고 공정이 있으면 부릉부릉 주행 후, 없으면 즉시 풀 반환
        if (pathProgress >= 1f)
        {
            pathProgress = 1f;
            if (!_finished)
            {
                _finished = true;

                if (IsNotSuccessParts())
                {
                    // 미체결 부품이 있으면 재화 없음 (파츠 보상도 체결 완료분만 이미 지급된 상태)
                    Debug.LogWarning($"<color=red>[RESULT]</color> {gameObject.name} 불량품 출고! 조립 안 된 부품 있음.");
                }
                else
                {
                    Debug.Log($"<color=green>[RESULT]</color> {gameObject.name} 완제품 출고 성공!");

                    int finalPrice = sellPrice;
                    if (UpgradeManager.Instance != null)
                        finalPrice = Mathf.RoundToInt(sellPrice * UpgradeManager.Instance.carSellPriceMultiplier);
                    Vector3 rewardPos = transform.position + Vector3.up * 1.8f;
                    if (EconomyManager.Instance != null) EconomyManager.Instance.SellCar(finalPrice, rewardPos);
                    CashPopup.Show(rewardPos, finalPrice, 1.6f);
                }

                // 출고 공정에 인계: 부릉부릉 → 도착 지점까지 가속 주행 → 풀 반환.
                // 씬에 공정이 없으면 기존대로 즉시 풀 반환.
                if (!DepartureStation.TryDepart(this))
                {
                    if (CarPool.Instance != null) CarPool.Instance.Return(this);
                    else gameObject.SetActive(false);
                }
            }
            return;
        }

        SnapToProgress(pathProgress);
    }

    public void SetPosition(Vector3 pos)
    {
        transform.position = pos;
    }

    /// <summary>
    /// 진행도를 외부에서 직접 지정하고 그 지점으로 스냅한다.
    /// WheelStation이 방출 시 차량을 게이트 진행도 위로 올려(통과 처리) 게이트 클램프에서 풀 때 사용.
    /// </summary>
    public void SetProgress(float progress)
    {
        pathProgress = Mathf.Clamp01(progress);
        SnapToProgress(pathProgress);
    }

    public bool IsNotSuccessParts()
    {
        foreach (var listParts in dicListParts.Values)
        { foreach (var part in listParts) if (!part.IsAssembled) return true; }
        return false;
    }


    public AssemblyPart GetUnassembledPart(PartType targetType)
    {
        foreach (var listParts in dicListParts.Values)
        {
            foreach (var part in listParts)
            {
                if (part.myType == targetType)
                {
                    Debug.Log($"[GetUnassembledPart] {targetType} 발견 → currentWork={part.currentWork:F2}/{part.requiredWork:F0}, active={part.gameObject.activeSelf}");
                    if (!part.IsAssembled) return part;
                    //else Debug.LogWarning($"[GetUnassembledPart] {targetType} 이미 체결완료(currentWork={part.currentWork:F2}) 로 인해 null 반환!");
                }
            }
        }
        return null;
    }


    public void ResetParts()
    {
        foreach (var listParts in dicListParts.Values)
        {
            foreach (var part in listParts)
            {
                // 차체(Body)는 로봇팔 체결 공정이 아니라 도색 부스(PaintBooth) 담당 —
                // 스폰 직후부터 보여야 하므로 숨기지 않고 체결 완료 상태로 둔다.
                // (색은 CarPaintController가 언더코트 → 부스 통과 시 원본색으로 처리)
                if (part.myGroup == PartGroup.Body)
                {
                    part.SetActive(true);
                    part.SetAssembled();
                }
                else part.Reset();
            }
        }
    }

    public void SetCurretParts(PartType targetType)
    {
        int targetIdx = Constants.GetPartTypeIndex(targetType);
        int totalParts = 0;
        foreach (var l in dicListParts.Values) totalParts += l.Count;
        Debug.Log($"[SetCurretParts] {gameObject.name} (ID={GetInstanceID()}) targetType={targetType} (idx={targetIdx}), 등록된 파츠 수={totalParts}");
        foreach (var listParts in dicListParts.Values)
        {
            foreach (var part in listParts)
            {
                // 차체(Body)는 도색 부스 담당 — 테스트 대상과 무관하게 항상 보이는 체결 상태 유지
                if (part.myGroup == PartGroup.Body)
                {
                    part.SetActive(true);
                    part.SetAssembled();
                    continue;
                }

                int partIdx = Constants.GetPartTypeIndex(part.myType);

                if (partIdx >= 0 && partIdx < targetIdx)
                {
                    // 이전 공정: 체결 완료(work=requiredWork), 활성화
                    part.SetActive(true);
                    part.SetAssembled();
                }
                else
                {
                    // 현재/미래 공정 또는 None: 분리 상태로 숨김
                    Debug.Log($"[SetCurretParts] {part.myType}(idx={partIdx}) → Reset() 호출");
                    part.Reset();
                    Debug.Log($"[SetCurretParts] {part.myType} Reset 후 currentWork={part.currentWork:F2}");
                }
            }
        }
    }


    [ContextMenu("차량 체결 값 전체 리셋")]
    private void ResetAssemblyPart()
    {
        if (dicListParts == null || dicListParts.Count <= 0) return;

        foreach (List<AssemblyPart> lists in dicListParts.Values)
        {
            foreach (AssemblyPart item in lists)
            {
                item.SetDetached();
            }
        }
        StartCoroutine(coReset());
        IEnumerator coReset()
        {
            var pos = this.transform.position;
            this.transform.position = new Vector3(999, 999, 999);
            yield return new WaitForSeconds(0.5f);
            this.transform.position = pos;
        }
        Debug.Log($"[{gameObject.name}] 파츠 전체 리셋");
    }
}