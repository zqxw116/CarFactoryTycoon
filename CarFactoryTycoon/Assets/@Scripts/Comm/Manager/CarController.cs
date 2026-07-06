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
        // 1. 이동 로직: 스플라인이 할당되어 있고, 이동 스위치가 켜져 있을 때만 작동
        if (isMoving && targetSpline != null && splineLength > 0f)
        {
            MoveAlongSpline();
        }
    }

    private void MoveAlongSpline()
    {
        // 속도를 실제 스플라인 길이로 나누어 진행도(0~1)를 증가시킴
        pathProgress += (moveSpeed / splineLength) * Time.deltaTime;

        // 라인 끝에 도달하면 출고 처리 후 풀로 반환 (파괴하지 않고 재사용)
        if (pathProgress >= 1f)
        {
            pathProgress = 1f;
            if (!_finished)
            {
                _finished = true;

                if (IsNotSuccessParts())
                {
                    Debug.LogWarning($"<color=red>[RESULT]</color> {gameObject.name} 불량품 출고! 조립 안 된 부품 있음.");
                }
                else
                {
                    Debug.Log($"<color=green>[RESULT]</color> {gameObject.name} 완제품 출고 성공!");

                    int finalPrice = sellPrice;
                    if (UpgradeManager.Instance != null)
                        finalPrice = Mathf.RoundToInt(sellPrice * UpgradeManager.Instance.carSellPriceMultiplier);
                    if (EconomyManager.Instance != null) EconomyManager.Instance.SellCar(finalPrice);
                }

                if (CarPool.Instance != null) CarPool.Instance.Return(this);
                else gameObject.SetActive(false);
            }
            return;
        }

        SnapToProgress(pathProgress);
    }

    public void SetPosition(Vector3 pos)
    {
        transform.position = pos;
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
                    else Debug.LogWarning($"[GetUnassembledPart] {targetType} 이미 체결완료(currentWork={part.currentWork:F2}) 로 인해 null 반환!");
                }
            }
        }
        return null;
    }


    public void ResetParts()
    { foreach (var listParts in dicListParts.Values) foreach (var part in listParts) part.Reset(); }

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