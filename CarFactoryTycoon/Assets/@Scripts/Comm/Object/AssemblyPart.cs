using System.Collections;
using UnityEngine;


public class AssemblyPart : MonoBehaviour
{
    [Header("소속 및 식별")]
    public PartGroup myGroup;
    public PartType myType; // [추가] 세부 부품 식별자

    [Header("현재 체결 상태 (1: 분리 대기 -> 0: 체결 완료)")]
    [Range(0f, 1f)] public float assemblyProgress = 1f;

    [Header("위치 데이터 (수정 후 반드시 수동저장 누를 것!)")]
    public Vector3 assembledPos;
    public Vector3 assembledRot;
    public Vector3 detachedPos;
    public Vector3 detachedRot;


    [Header("자동 분리 세팅값")]
    public float explodeDistance = 3f;
    // [최적화] 매번 transform.parent를 호출하지 않도록 미리 저장할 변수
    private Transform cachedParent;

    private void Awake()
    {
        // 게임 시작 시 단 한 번만 부모를 찾아서 캐싱합니다.
        cachedParent = transform.parent;
    }

    // 로봇팔이 추적할 월드 좌표 반환 (캐싱된 부모 사용)
    public Vector3 GetWorldDetachedPos()
    {
        return cachedParent != null ? cachedParent.TransformPoint(detachedPos) : transform.position;
    }

    public Vector3 GetWorldAssembledPos()
    {
        return cachedParent != null ? cachedParent.TransformPoint(assembledPos) : transform.position;
    }
    // [추가 1] 외부(로봇팔/스테이션)에서 매 프레임 progress를 깎을 때 호출하는 함수
    public void UpdateProgress(float newProgress)
    {
        // progress 값은 0~1 사이를 벗어나지 않게 고정
        assemblyProgress = Mathf.Clamp01(newProgress);

        // OnValidate처럼 즉시 위치 갱신
        transform.localPosition = Vector3.Lerp(assembledPos, detachedPos, assemblyProgress);
        transform.localRotation = Quaternion.Euler(Vector3.Lerp(assembledRot, detachedRot, assemblyProgress));
    }

    // [추가 2] 유저가 클릭하거나 특정 이벤트 시 부드럽게 완전 체결(0)시키는 코루틴
    public IEnumerator FixPartRoutine(float speed = 2f)
    {
        // progress가 0보다 클 때까지 서서히 줄임
        while (assemblyProgress > 0f)
        {
            float newProgress = assemblyProgress - (Time.deltaTime * speed);
            UpdateProgress(newProgress);
            yield return null; // 다음 프레임까지 대기
        }

        // 확실하게 0으로 고정
        UpdateProgress(0f);
        Debug.Log($"[{gameObject.name}] 체결 완료!");
    }



    #region 에디터 함수


    private void Reset()
    {
        assembledPos = transform.localPosition;
        assembledRot = transform.localEulerAngles;
        //AutoSetDetachedPosition();
    }

    private void OnValidate()
    {
        if (assembledPos == Vector3.zero && detachedPos == Vector3.zero) return;
        transform.localPosition = Vector3.Lerp(assembledPos, detachedPos, assemblyProgress);
        transform.localRotation = Quaternion.Euler(Vector3.Lerp(assembledRot, detachedRot, assemblyProgress));
    }


    // =======================================================
    // [핵심 기능] 에디터에서 위치를 만진 후, 이 버튼을 눌러야 영구 저장됨!
    // =======================================================
    [ContextMenu("★ 1. [수동 저장] 현재 씬(Scene) 위치를 '분리 상태(1)'로 덮어쓰기")]
    private void SaveDetachedState()
    {
        detachedPos = transform.localPosition;
        detachedRot = transform.localEulerAngles;
        Debug.Log($"[{gameObject.name}] 분리 상태 수동 세팅 완료! (이제 슬라이더를 움직여도 이 위치가 유지됩니다)");
    }

    [ContextMenu("2. [수동 저장] 현재 씬(Scene) 위치를 '체결 완료(0)'로 덮어쓰기")]
    private void SaveAssembledState()
    {
        assembledPos = transform.localPosition;
        assembledRot = transform.localEulerAngles;
        Debug.Log($"[{gameObject.name}] 체결 상태 수동 세팅 완료!");
    }

    [ContextMenu("3. [자동화] 다시 방사형으로 밀어내기")]
    public void AutoSetDetachedPosition()
    {
        Transform centerObj = FindCenterObject("SM_detail27");
        if (centerObj == null) return;

        Vector3 direction = (transform.position - centerObj.position).normalized;
        if (direction == Vector3.zero) direction = Vector3.up;
        direction.y += 0.5f;

        transform.position = centerObj.position + (direction * explodeDistance);

        // 자동 계산된 값을 임시 저장
        detachedPos = transform.localPosition;
        detachedRot = assembledRot + new Vector3(Random.Range(-20f, 20f), Random.Range(-20f, 20f), Random.Range(-20f, 20f));

        assemblyProgress = 1f;
        OnValidate();
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
        // 1. 부모의 이름으로 PartGroup 자동 할당
        if (transform.parent != null)
        {
            if (System.Enum.TryParse(transform.parent.name, out PartGroup parsedGroup))
            {
                myGroup = parsedGroup;
            }
            else
            {
                Debug.LogWarning($"[{gameObject.name}]의 부모 이름({transform.parent.name})과 일치하는 PartGroup Enum이 없습니다.");
            }
        }

        // 2. 자신의 이름으로 PartType 자동 할당
        if (System.Enum.TryParse(gameObject.name, out PartType parsedType))
        {
            myType = parsedType;
        }
        else
        {
            Debug.LogWarning($"[{gameObject.name}] 이름과 일치하는 PartType Enum이 없습니다.");
        }
    }

    // =======================================================
    // [PM 추가] 에디터에서 이동 궤적을 선으로 보여주는 시각화 툴
    // =======================================================
    private void OnDrawGizmosSelected()
    {
        if (transform.parent == null) return;

        // 로컬 좌표를 월드 좌표로 변환해서 선을 그림
        Vector3 worldAssembled = transform.parent.TransformPoint(assembledPos);
        Vector3 worldDetached = transform.parent.TransformPoint(detachedPos);

        // 초록색 구체: 조립 완료 목적지
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(worldAssembled, 0.05f);

        // 빨간색 구체: 분리 대기 시작점
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(worldDetached, 0.05f);

        // 노란색 궤적: 이 선이 차체를 뚫고 지나가면 충돌하는 것!
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(worldAssembled, worldDetached);
    }
    #endregion
}