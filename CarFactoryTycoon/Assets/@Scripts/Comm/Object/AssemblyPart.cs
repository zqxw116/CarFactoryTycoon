using System.Collections;
using UnityEngine;

public class AssemblyPart : MonoBehaviour
{
    [Header("소속 및 식별")]
    public PartGroup myGroup;
    public PartType myType;

    [Header("현재 체결 상태 (1: 분리 대기 -> 0: 체결 완료)")]
    [Range(0f, 1f)] public float assemblyProgress = 1f;

    [Header("1단계: 체결 완료 위치 (Progress: 0.0)")]
    public Vector3 assembledPos;
    public Vector3 assembledRot;

    [Header("2단계: 중간 경유지 (Progress: 0.5)")]
    [Tooltip("체결 직전 뚫림 방지를 위한 경유지 사용 여부")]
    public bool useIntermediate = true;
    public Vector3 intermediatePos;
    public Vector3 intermediateRot;

    [Header("3단계: 최종 분리 위치 (Progress: 1.0)")]
    public Vector3 detachedPos;
    public Vector3 detachedRot;

    [Header("자동 분리 설정")]
    public float explodeDistance = 3f;

    private Transform cachedParent;

    private void Awake()
    {
        // 게임 시작 시 부모 캐싱
        cachedParent = transform.parent;
        if (myType == PartType.None) AutoSetGroupAndType();
    }

    // 로봇팔이 추적할 월드 좌표 (분리 상태의 월드 위치)
    public Vector3 GetWorldDetachedPos() => GetWorldPosFromLocal(detachedPos);

    // 로봇팔이 최종적으로 가져다 놓아야 할 체결 월드 위치
    public Vector3 GetWorldAssembledPos() => GetWorldPosFromLocal(assembledPos);

    // 내부 좌표 계산용 편의 함수
    private Vector3 GetWorldPosFromLocal(Vector3 localPos)
    {
        if (cachedParent == null) cachedParent = transform.parent;
        return cachedParent != null ? cachedParent.TransformPoint(localPos) : transform.position;
    }

    // [핵심] 외부(로봇팔 등)에서 호출하여 파츠 위치를 실시간 갱신
    public void UpdateProgress(float newProgress)
    {
        assemblyProgress = Mathf.Clamp01(newProgress);

        if (useIntermediate)
        {
            // 구간 1: 0.0(체결) ~ 0.5(경유지)
            if (assemblyProgress <= 0.5f)
            {
                float t = assemblyProgress / 0.5f; // 0~0.5를 0~1로 환산
                transform.localPosition = Vector3.Lerp(assembledPos, intermediatePos, t);
                transform.localRotation = Quaternion.Euler(Vector3.Lerp(assembledRot, intermediateRot, t));
            }
            // 구간 2: 0.5(경유지) ~ 1.0(분리)
            else
            {
                float t = (assemblyProgress - 0.5f) / 0.5f; // 0.5~1을 0~1로 환산
                transform.localPosition = Vector3.Lerp(intermediatePos, detachedPos, t);
                transform.localRotation = Quaternion.Euler(Vector3.Lerp(intermediateRot, detachedRot, t));
            }
        }
        else
        {
            // 단순 직선 이동
            transform.localPosition = Vector3.Lerp(assembledPos, detachedPos, assemblyProgress);
            transform.localRotation = Quaternion.Euler(Vector3.Lerp(assembledRot, detachedRot, assemblyProgress));
        }
    }

    // 부드럽게 완전 체결(0)시키는 코루틴
    public IEnumerator FixPartRoutine(float speed = 2f)
    {
        while (assemblyProgress > 0f)
        {
            float newProgress = assemblyProgress - (Time.deltaTime * speed);
            UpdateProgress(newProgress);
            yield return null;
        }
        UpdateProgress(0f);
        Debug.Log($"<color=green>[{gameObject.name}]</color> 조립 완료!");
    }

    #region 에디터 도구 (Inspector 메뉴)

    private void OnValidate()
    {
        // 에디터 슬라이더 조작 시 즉시 반영
        if (assembledPos == Vector3.zero && detachedPos == Vector3.zero) return;
        UpdateProgress(assemblyProgress);
    }

    [ContextMenu("★ 1. [수동 저장] 현 위치를 '중간 경유지(0.5)'로")]
    private void SaveIntermediateState()
    {
        intermediatePos = transform.localPosition;
        intermediateRot = transform.localEulerAngles;
        Debug.Log($"[{gameObject.name}] 중간 위치 저장됨 (ㄱ자 꺾임점)");
    }

    [ContextMenu("2. [수동 저장] 현 위치를 '분리 대기(1.0)'로")]
    private void SaveDetachedState()
    {
        detachedPos = transform.localPosition;
        detachedRot = transform.localEulerAngles;
        Debug.Log($"[{gameObject.name}] 시작 위치(빨간 구슬) 저장됨");
    }

    [ContextMenu("3. [수동 저장] 현 위치를 '체결 완료(0.0)'로")]
    private void SaveAssembledState()
    {
        assembledPos = transform.localPosition;
        assembledRot = transform.localEulerAngles;
        Debug.Log($"[{gameObject.name}] 최종 조립 위치(초록 구슬) 저장됨");
    }

    [ContextMenu("4. [자동화] 방사형 분리 위치 계산")]
    public void AutoSetDetachedPosition()
    {
        Transform centerObj = FindCenterObject("SM_detail27");
        if (centerObj == null) return;

        Vector3 direction = (transform.position - centerObj.position).normalized;
        if (direction == Vector3.zero) direction = Vector3.up;
        direction.y += 0.5f;

        transform.position = centerObj.position + (direction * explodeDistance);
        detachedPos = transform.localPosition;
        detachedRot = assembledRot + new Vector3(Random.Range(-20f, 20f), Random.Range(-20f, 20f), Random.Range(-20f, 20f));

        assemblyProgress = 1f;
        UpdateProgress(assemblyProgress);
    }

    private Transform FindCenterObject(string targetName)
    {
        Transform[] allChildren = transform.root.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in allChildren)
        {
            if (child.name == targetName) return child;
        }
        return null;
    }

    [ContextMenu("이름 기반 Group 및 Type 자동 설정")]
    public void AutoSetGroupAndType()
    {
        if (transform.parent != null)
        {
            if (System.Enum.TryParse(transform.parent.name, out PartGroup parsedGroup))
                myGroup = parsedGroup;
        }

        if (System.Enum.TryParse(gameObject.name, out PartType parsedType))
            myType = parsedType;
    }

    // 씬 뷰 궤적 시각화
    private void OnDrawGizmosSelected()
    {
        if (transform.parent == null) return;

        Vector3 worldAssembled = transform.parent.TransformPoint(assembledPos);
        Vector3 worldInter = transform.parent.TransformPoint(intermediatePos);
        Vector3 worldDetached = transform.parent.TransformPoint(detachedPos);

        // 도착점(초록), 경유지(노랑), 시작점(빨강)
        Gizmos.color = Color.green; Gizmos.DrawWireSphere(worldAssembled, 0.05f);
        Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(worldInter, 0.05f);
        Gizmos.color = Color.red; Gizmos.DrawWireSphere(worldDetached, 0.05f);

        // 하늘색으로 꺾인 궤적 표시
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(worldAssembled, worldInter);
        Gizmos.DrawLine(worldInter, worldDetached);
    }
    #endregion
}