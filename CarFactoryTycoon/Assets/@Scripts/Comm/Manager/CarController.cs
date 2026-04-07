using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;   // [추가] 스플라인 네임스페이스
using Unity.Mathematics;     // [추가] 수학 연산용 (float3 등)
using System.Collections;

public class CarController : MonoBehaviour
{
    [Header("물류 설정")]
    public float moveSpeed = 1.0f;
    public bool isMoving = true;

    [Header("스플라인 추적 상태 (디버그용)")]
    public SplineContainer targetSpline;
    [Range(0f, 1f)] public float pathProgress = 0f; // 0=시작점, 1=도착점
    private float splineLength;

    private Dictionary<PartGroup, List<AssemblyPart>> partsDictionary = new Dictionary<PartGroup, List<AssemblyPart>>();

    private void Awake()
    {
        InitializeCarParts();
    }

    // [신규] 스포너가 차량을 생성할 때 호출하여 경로를 주입해주는 함수
    public void InitializePath(SplineContainer spline, float speed)
    {
        targetSpline = spline;
        moveSpeed = speed;
        pathProgress = 0f;

        if (targetSpline != null)
        {
            // 스플라인의 실제 물리적 길이(m)를 계산하여 저장
            splineLength = targetSpline.CalculateLength();
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

        // 라인 끝에 도달하면 파괴 (CarDestructor가 없어도 스스로 파괴되도록 안전장치)
        if (pathProgress >= 1f)
        {
            pathProgress = 1f;
            CheckResultAndDestroy();
            return;
        }

        // 스플라인의 현재 진행도(pathProgress)에 해당하는 로컬 좌표와 방향을 가져옴
        SplineUtility.Evaluate(targetSpline.Spline, pathProgress, out float3 localPos, out float3 localTangent, out float3 localUp);

        // 로컬 좌표를 월드 좌표로 변환하여 차량 위치 적용
        transform.position = targetSpline.transform.TransformPoint(localPos);

        // 곡선 방향에 맞춰 차량의 머리를 회전시킴 (부드러운 코너링)
        if (math.length(localTangent) > 0.001f)
        {
            Vector3 worldDirection = targetSpline.transform.TransformDirection(localTangent);
            Vector3 worldUp = targetSpline.transform.TransformDirection(localUp);
            transform.rotation = Quaternion.LookRotation(worldDirection, worldUp);
        }
    }

    private void CheckResultAndDestroy()
    {
        if (HasUnassembledParts())
            Debug.LogWarning($"<color=red>[RESULT]</color> {gameObject.name} 불량품 출고! 조립 안 된 부품 있음.");
        else
            Debug.Log($"<color=green>[RESULT]</color> {gameObject.name} 완제품 출고 성공!");

        Destroy(gameObject);
    }

    private void InitializeCarParts()
    {
        foreach (PartGroup group in System.Enum.GetValues(typeof(PartGroup)))
        {
            partsDictionary[group] = new List<AssemblyPart>();
        }

        AssemblyPart[] allParts = GetComponentsInChildren<AssemblyPart>(true);
        foreach (AssemblyPart part in allParts)
        {
            partsDictionary[part.myGroup].Add(part);
            part.UpdateProgress(1f);
        }
    }

    public bool HasUnassembledParts()
    {
        AssemblyPart[] allParts = GetComponentsInChildren<AssemblyPart>(true);
        foreach (var part in allParts)
        {
            if (part.assemblyProgress > 0f) return true;
        }
        return false;
    }

    public AssemblyPart GetUnassembledPart(PartType targetType)
    {
        AssemblyPart[] allParts = GetComponentsInChildren<AssemblyPart>(true);
        foreach (AssemblyPart part in allParts)
        {
            if (part.myType == targetType && part.assemblyProgress > 0f) return part;
        }
        return null;
    }

    [ContextMenu("차량 체결 값 전체 리셋")]
    private void ResetAssemblyPart()
    {
        if (partsDictionary == null || partsDictionary.Count <= 0) return;

        foreach (List<AssemblyPart> lists in partsDictionary.Values)
        {
            foreach (AssemblyPart item in lists)
            {
                item.UpdateProgress(1f);
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