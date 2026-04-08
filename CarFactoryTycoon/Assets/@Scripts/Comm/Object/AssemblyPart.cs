using System.Collections;
using System.Collections.Generic;
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

    [Header("[0.0] 조립 완료 좌표 (Assembled)")]
    public Vector3 assembledPos;
    public Vector3 assembledRot;

    [Header("[0.5] 중간 경유지 좌표 (Intermediate)")]
    public bool useIntermediate = true;
    public Vector3 intermediatePos;
    public Vector3 intermediateRot;

    [Header("[1.0] 분리 시작 좌표 (Detached)")]
    public Vector3 detachedPos;
    public Vector3 detachedRot;

    [Header("자동 분리 설정")]
    public float explodeDistance = 3f;

    private Transform cachedParent;

    private void Awake()
    {
        cachedParent = transform.parent;
        if (myType == PartType.None) AutoSetGroupAndType();
        LoadDataFromSO(); // 시작 시 설계도에서 값 불러오기
    }

    #region 외부 참조용 함수 (로봇팔 등에서 사용)

    // 로봇팔이 추적할 '시작(분리)' 월드 좌표
    public Vector3 GetWorldDetachedPos() => GetWorldPosFromLocal(detachedPos);

    // 로봇팔이 가져다 놓아야 할 '최종(체결)' 월드 좌표
    public Vector3 GetWorldAssembledPos() => GetWorldPosFromLocal(assembledPos);

    private Vector3 GetWorldPosFromLocal(Vector3 localPos)
    {
        if (cachedParent == null) cachedParent = transform.parent;
        return cachedParent != null ? cachedParent.TransformPoint(localPos) : transform.position;
    }

    #endregion

    // [핵심] 진행도에 따른 실시간 위치 업데이트
    public void UpdateProgress(float newProgress)
    {
        assemblyProgress = Mathf.Clamp01(newProgress);

        if (useIntermediate)
        {
            if (assemblyProgress <= 0.5f)
            {
                float t = assemblyProgress / 0.5f;
                transform.localPosition = Vector3.Lerp(assembledPos, intermediatePos, t);
                transform.localRotation = Quaternion.Euler(Vector3.Lerp(assembledRot, intermediateRot, t));
            }
            else
            {
                float t = (assemblyProgress - 0.5f) / 0.5f;
                transform.localPosition = Vector3.Lerp(intermediatePos, detachedPos, t);
                transform.localRotation = Quaternion.Euler(Vector3.Lerp(intermediateRot, detachedRot, t));
            }
        }
        else
        {
            transform.localPosition = Vector3.Lerp(assembledPos, detachedPos, assemblyProgress);
            transform.localRotation = Quaternion.Euler(Vector3.Lerp(assembledRot, detachedRot, assemblyProgress));
        }
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
        if (assembledPos == Vector3.zero && detachedPos == Vector3.zero) return;
        UpdateProgress(assemblyProgress);
    }

    [ContextMenu("Step 0. 설계도(SO) 데이터 불러오기")]
    public void LoadDataFromSO()
    {
        if (masterData == null) return;
        PartConfig config = masterData.GetConfig(myType);
        if (config.type == PartType.None) return;

        assembledPos = config.assembledPos;
        assembledRot = config.assembledRot;
        useIntermediate = config.useIntermediate;
        intermediatePos = config.intermediatePos;
        intermediateRot = config.intermediateRot;
        detachedPos = config.detachedPos;
        detachedRot = config.detachedRot;
    }

    [ContextMenu("Step 1. 현재 위치를 [조립 완료(0.0)]로 저장")]
    public void SaveAsAssembled()
    {
        assembledPos = transform.localPosition;
        assembledRot = transform.localEulerAngles;
        ApplyToSO();
    }

    [ContextMenu("Step 2. 현재 위치를 [중간 경유(0.5)]로 저장")]
    public void SaveAsIntermediate()
    {
        intermediatePos = transform.localPosition;
        intermediateRot = transform.localEulerAngles;
        useIntermediate = true;
        ApplyToSO();
    }

    [ContextMenu("Step 3. 현재 위치를 [분리 시작(1.0)]로 저장")]
    public void SaveAsDetached()
    {
        detachedPos = transform.localPosition;
        detachedRot = transform.localEulerAngles;
        ApplyToSO();
    }

    [ContextMenu("Step 4. !!! [최종 저장] 인스펙터 값을 설계도(SO)에 굽기")]
    public void ApplyToSO()
    {
        if (masterData == null) { Debug.LogError("masterData(SO)가 없습니다!"); return; }

        int index = masterData.partConfigs.FindIndex(x => x.type == myType);
        PartConfig config = new PartConfig
        {
            type = myType,
            assembledPos = assembledPos,
            assembledRot = assembledRot,
            useIntermediate = useIntermediate,
            intermediatePos = intermediatePos,
            intermediateRot = intermediateRot,
            detachedPos = detachedPos,
            detachedRot = detachedRot
        };

        if (index != -1) masterData.partConfigs[index] = config;
        else masterData.partConfigs.Add(config);

#if UNITY_EDITOR
        EditorUtility.SetDirty(masterData);
        AssetDatabase.SaveAssets();
        Debug.Log($"[{myType}] 설계도 저장 완료!");
#endif
    }

    [ContextMenu("5. [자동화] 방사형 분리 위치 계산 및 저장")]
    public void AutoSetDetachedPosition()
    {
        Transform centerObj = FindCenterObject("Frame");
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
        Vector3 p0 = transform.parent.TransformPoint(assembledPos);
        Vector3 p1 = transform.parent.TransformPoint(intermediatePos);
        Vector3 p2 = transform.parent.TransformPoint(detachedPos);

        Gizmos.color = Color.green; Gizmos.DrawWireSphere(p0, 0.05f);
        Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(p1, 0.05f);
        Gizmos.color = Color.red; Gizmos.DrawWireSphere(p2, 0.05f);
        Gizmos.color = Color.cyan; Gizmos.DrawLine(p0, p1); Gizmos.DrawLine(p1, p2);
    }
    #endregion
}