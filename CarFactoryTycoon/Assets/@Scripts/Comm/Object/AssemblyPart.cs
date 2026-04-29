using System.Collections;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class AssemblyPart : MonoBehaviour
{
    [Header("마스터 설계도 (Scriptable Object)")]
    public AssemblyPartDataSO masterData;

    [Header("소속 및 식별")]
    public PartGroup myGroup;
    public PartType myType;

    [Header("현재 체결 상태 (0:완료 <-> 1:분리)")]
    [Range(0f, 1f)] public float assemblyProgress = 1f;

    [Header("[0.0] 체결 완료 좌표")]
    public Vector3 assembledPos;
    public Vector3 assembledRot;

    [Header("[0.3] 첫 번째 중간 꺾임 좌표 (mid2 → 여기 → 체결완료)")]
    public Vector3 midPos;
    public Vector3 midRot;

    [Header("[0.6] 두 번째 중간 꺾임 좌표 (stationPilePos → 여기 → mid)")]
    public Vector3 mid2Pos;
    public Vector3 mid2Rot;

    [Header("로봇팔 바라보기 타겟")]
    [Tooltip("미설정 시 오브젝트 중심을 바라봄. 자식 오브젝트로 배치해 파츠 회전에 함께 따라가도록 설정.")]
    public Transform armLookTarget;

    [Header("자동 분리 설정")]
    public float explodeDistance = 3f;

    private Transform cachedParent;

    // 런타임 분리 위치 (스테이션 stationPilePos로부터 오버라이드)
    private bool hasRuntimeDetached = false;
    private Vector3 runtimeDetachedLocalPos;
    private Vector3 runtimeDetachedLocalRot; // Euler 각도 (로컬)

    public void SetActive(bool _isActive) => this.gameObject.SetActive(_isActive);

    private void Awake()
    {
        cachedParent = transform.parent;
        if (myType == PartType.None) AutoSetGroupAndType();
        LoadDataFromSO();
        armLookTarget = this.GetComponentInChildren<Transform>();
    }

    #region 외부 참조용 함수

    /// <summary>분리 상태(progress=1)로 되돌리고 오브젝트를 비활성화한다.</summary>
    public void Reset()
    {
        ClearRuntimeDetached();
        UpdateProgress(1f); // 내부에서 assemblyProgress==1 → SetActive(false) 호출
    }

    /// <summary>스테이션의 stationPilePos 월드 좌표를 분리 시작 위치로 오버라이드한다.</summary>
    public void SetRuntimeDetachedPose(Vector3 worldPos, Quaternion worldRot)
    {
        if (cachedParent == null) cachedParent = transform.parent;
        if (cachedParent != null)
        {
            runtimeDetachedLocalPos = cachedParent.InverseTransformPoint(worldPos);
            runtimeDetachedLocalRot = (Quaternion.Inverse(cachedParent.rotation) * worldRot).eulerAngles;
        }
        else
        {
            runtimeDetachedLocalPos = worldPos;
            runtimeDetachedLocalRot = worldRot.eulerAngles;
        }
        hasRuntimeDetached = true;
    }

    /// <summary>런타임 오버라이드를 해제하고 SO 기본값을 사용하도록 되돌린다.</summary>
    public void ClearRuntimeDetached() => hasRuntimeDetached = false;


    /// <summary>로봇팔이 가져다 놓아야 할 '최종(체결)' 월드 좌표</summary>
    public Vector3 GetWorldAssembledPos() => GetWorldPosFromLocal(assembledPos);

    /// <summary>로봇팔이 바라봐야 할 월드 좌표. armLookTarget 미설정 시 오브젝트 중심 반환.</summary>
    public Vector3 GetArmLookWorldPos() => armLookTarget != null ? armLookTarget.position : transform.position;

    private Vector3 GetWorldPosFromLocal(Vector3 localPos)
    {
        if (cachedParent == null) cachedParent = transform.parent;
        return cachedParent != null ? cachedParent.TransformPoint(localPos) : transform.position;
    }

    #endregion

    /// <summary>
    /// 진행도에 따른 실시간 위치 업데이트.
    /// 구간별 보간:
    ///   1.0 ~ 0.6 : stationPilePos(런타임) → mid2Pos
    ///   0.6 ~ 0.3 : mid2Pos → midPos
    ///   0.3 ~ 0.0 : midPos → assembledPos
    /// </summary>
    public void UpdateProgress(float newProgress)
    {
        assemblyProgress = Mathf.Clamp01(newProgress);
        ApplyLocalPose(assemblyProgress);

        if (assemblyProgress == 1f)
            SetActive(false);
        else if (!gameObject.activeSelf)
            SetActive(true);
    }

    /// <summary>위치·회전만 적용 (SetActive 없음). OnValidate에서 사용.</summary>
    private void ApplyLocalPose(float progress)
    {
        Vector3 localPos, localRot;

        if (progress >= 0.6f)
        {
            // 1.0 → 0.6 구간: stationPilePos → mid2Pos
            float t = (progress - 0.6f) / 0.4f;
            Vector3 pileLocalPos = hasRuntimeDetached ? runtimeDetachedLocalPos : mid2Pos;
            Vector3 pileLocalRot = hasRuntimeDetached ? runtimeDetachedLocalRot : mid2Rot;
            localPos = Vector3.Lerp(mid2Pos, pileLocalPos, t);
            localRot = Vector3.Lerp(mid2Rot, pileLocalRot, t);
        }
        else if (progress >= 0.3f)
        {
            // 0.6 → 0.3 구간: mid2Pos → midPos
            float t = (progress - 0.3f) / 0.3f;
            localPos = Vector3.Lerp(midPos, mid2Pos, t);
            localRot = Vector3.Lerp(midRot, mid2Rot, t);
        }
        else
        {
            // 0.3 → 0.0 구간: midPos → assembledPos
            float t = progress / 0.3f;
            localPos = Vector3.Lerp(assembledPos, midPos, t);
            localRot = Vector3.Lerp(assembledRot, midRot, t);
        }

        transform.localPosition = localPos;
        transform.localRotation = Quaternion.Euler(localRot);
    }

    public IEnumerator FixPartRoutine(float speed = 2f)
    {
        while (assemblyProgress > 0f)
        {
            float newProgress = assemblyProgress - (Time.deltaTime * speed);
            UpdateProgress(newProgress);
            yield return null;
        }
        UpdateProgress(0f);
    }

    #region 에디터 도구 (Inspector 메뉴)

    private void OnValidate()
    {
        if (assembledPos == Vector3.zero && midPos == Vector3.zero) return;
        // OnValidate에서 SetActive 호출 금지 → 위치만 업데이트
        ApplyLocalPose(Mathf.Clamp01(assemblyProgress));
    }

    [ContextMenu("Step 0. 설계도(SO) 데이터 불러오기")]
    public void LoadDataFromSO()
    {
        if (masterData == null) return;
        PartConfig config = masterData.GetConfig(myType);
        if (config.type == PartType.None) return;

        assembledPos = config.assembledPos;
        assembledRot = config.assembledRot;
        midPos       = config.midPos;
        midRot       = config.midRot;
        mid2Pos      = config.mid2Pos;
        mid2Rot      = config.mid2Rot;
    }

    [ContextMenu("Step 1. 현재 위치를 [체결 완료(0.0)]로 저장")]
    public void SaveAsAssembled()
    {
        assembledPos = transform.localPosition;
        assembledRot = transform.localEulerAngles;
        ApplyToSO();
    }

    [ContextMenu("Step 2. 현재 위치를 [첫 번째 중간 꺾임(0.3)]로 저장")]
    public void SaveAsMid()
    {
        midPos = transform.localPosition;
        midRot = transform.localEulerAngles;
        ApplyToSO();
    }

    [ContextMenu("Step 2-2. 현재 위치를 [두 번째 중간 꺾임(0.6)]로 저장")]
    public void SaveAsMid2()
    {
        mid2Pos = transform.localPosition;
        mid2Rot = transform.localEulerAngles;
        ApplyToSO();
    }

    [ContextMenu("Step 3. !!! [최종 저장] 인스펙터 값을 설계도(SO)에 굽기")]
    public void ApplyToSO()
    {
        if (masterData == null) { Debug.LogError("masterData(SO)가 없습니다!"); return; }

        int index = masterData.partConfigs.FindIndex(x => x.type == myType);
        PartConfig config = new PartConfig
        {
            type         = myType,
            assembledPos = assembledPos,
            assembledRot = assembledRot,
            midPos       = midPos,
            midRot       = midRot,
            mid2Pos      = mid2Pos,
            mid2Rot      = mid2Rot
        };

        if (index != -1) masterData.partConfigs[index] = config;
        else masterData.partConfigs.Add(config);

#if UNITY_EDITOR
        EditorUtility.SetDirty(masterData);
        AssetDatabase.SaveAssets();
        Debug.Log($"[{myType}] 설계도 저장 완료!");
#endif
    }

    [ContextMenu("4. [자동화] 방사형 중간 위치 계산 및 저장")]
    public void AutoSetMidPosition()
    {
        Transform centerObj = FindCenterObject("Frame");
        if (centerObj == null) return;

        Vector3 direction = (transform.position - centerObj.position).normalized;
        if (direction == Vector3.zero) direction = Vector3.up;
        direction.y += 0.5f;

        transform.position = centerObj.position + (direction * explodeDistance);
        midPos = transform.localPosition;
        midRot = assembledRot + new Vector3(Random.Range(-20f, 20f), Random.Range(-20f, 20f), Random.Range(-20f, 20f));

        assemblyProgress = 0.5f;
        UpdateProgress(assemblyProgress);
    }

    private Transform FindCenterObject(string targetName)
    {
        Transform[] allChildren = transform.root.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in allChildren) if (child.name == targetName) return child;
        return null;
    }

    [ContextMenu("이름 기반 Group 및 Type 자동 설정")]
    public void AutoSetGroupAndType()
    {
        if (transform.parent != null)
            if (System.Enum.TryParse(transform.parent.name, out PartGroup parsedGroup)) myGroup = parsedGroup;

        if (System.Enum.TryParse(gameObject.name, out PartType parsedType)) myType = parsedType;
        LoadDataFromSO();
    }

    private void OnDrawGizmosSelected()
    {
        if (transform.parent == null) return;
        Vector3 p0 = transform.parent.TransformPoint(assembledPos); // 체결 완료 (0.0)
        Vector3 p1 = transform.parent.TransformPoint(midPos);       // 첫 번째 중간 꺾임 (0.333)
        Vector3 p2 = transform.parent.TransformPoint(mid2Pos);      // 두 번째 중간 꺾임 (0.667)

        Gizmos.color = Color.green;   Gizmos.DrawWireSphere(p0, 0.05f);
        Gizmos.color = Color.yellow;  Gizmos.DrawWireSphere(p1, 0.05f);
        Gizmos.color = Color.magenta; Gizmos.DrawWireSphere(p2, 0.05f);
        Gizmos.color = Color.cyan;    Gizmos.DrawLine(p0, p1);
        Gizmos.color = Color.cyan;    Gizmos.DrawLine(p1, p2);
        // p2 → stationPilePos 구간은 런타임에 결정되므로 에디터에서 표시 불가

        if (armLookTarget != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(armLookTarget.position, 0.07f);
            Gizmos.DrawLine(transform.position, armLookTarget.position);
        }
    }

    #endregion
}
