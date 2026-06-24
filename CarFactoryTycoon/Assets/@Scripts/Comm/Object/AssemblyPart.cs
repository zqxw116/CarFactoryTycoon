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

    [Header("필요 작업량 (난이도, 10~100 / 평균 50)")]
    [Tooltip("이 부품 체결에 필요한 총 작업량. 옆면(문/바퀴)은 크게, 범퍼처럼 앞면은 작게.")]
    public float requiredWork = 50f;

    [Header("현재 체결 값 (0:분리 → requiredWork:체결완료)")]
    public float currentWork = 0f;

    /// <summary>포즈 보간용 정규화 값. 0:분리(pile) → 1:체결완료(assembled)</summary>
    public float Fill => requiredWork > 0f ? Mathf.Clamp01(currentWork / requiredWork) : (currentWork > 0f ? 1f : 0f);
    public bool IsAssembled => currentWork >= requiredWork;
    public bool IsDetached  => currentWork <= 0f;

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

    [Header("월드 조립 (로봇팔 pile → 차량 부착점)")]
    [Tooltip("부착점에서 바깥쪽으로 밀어낸 '진입점'까지의 거리. 마지막 구간은 진입점→부착점 직선이라 차체를 뚫지 않는다.")]
    public float entryDistance = 1.2f;
    [Tooltip("진입 방향 수동 지정(로컬). 0이면 차량중심→부착점 방향으로 자동 계산. 문/바퀴처럼 특정 방향 삽입이 필요할 때만 설정.")]
    public Vector3 approachDirLocalOverride = Vector3.zero;

    private Transform cachedParent;

    // 런타임 분리 위치 (스테이션 stationPilePos로부터 오버라이드)
    private bool hasRuntimeDetached = false;
    private Vector3 runtimeDetachedLocalPos;
    private Vector3 runtimeDetachedLocalRot; // Euler 각도 (로컬)

    // 월드 조립 상태: BeginWorldAssembly()로 시작. 시작점(pile)은 월드 고정,
    // 부착점은 매 프레임 차량을 따라 재계산되어 움직이는 차에 정확히 붙는다.
    private bool assembling = false;
    private Vector3 pileWorldPos;
    private Quaternion pileWorldRot = Quaternion.identity;
    private Transform frameCenter; // 진입 방향 자동계산용 차량 중심("Frame")

    public void SetActive(bool _isActive) => this.gameObject.SetActive(_isActive);

    private void Awake()
    {
        cachedParent = transform.parent;
        if (myType == PartType.None) AutoSetGroupAndType();
        LoadDataFromSO();

        // 인스펙터에 지정돼 있으면 그대로 두고, 없으면 자식 "ArmLookTarget"을 찾는다.
        // (이전 코드는 GetComponentInChildren<Transform>()가 자기 자신을 반환해 잘못 덮어썼음)
        if (armLookTarget == null)
        {
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "ArmLookTarget") { armLookTarget = t; break; }
            }
            if (armLookTarget == null) armLookTarget = transform;
        }
    }

    #region 외부 참조용 함수

    /// <summary>완전 분리 상태(work=0)로 되돌리고 오브젝트를 비활성화한다.</summary>
    public void Reset()
    {
        assembling = false; // 월드 보간 중단
        ClearRuntimeDetached();
        SetDetached(); // work=0 → SetActive(false)
    }

    /// <summary>
    /// 월드 조립을 시작한다. 부품은 pile(로봇팔 대기위치, 월드 고정)에서 출발해
    /// 진입점을 거쳐 차량 부착점(매 프레임 차량 추적)으로 이동한다.
    /// 호출 후 SetWork/AddWork로 진행도를 올리면 위치가 갱신된다.
    /// </summary>
    public void BeginWorldAssembly(Vector3 worldPilePos, Quaternion worldPileRot)
    {
        pileWorldPos = worldPilePos;
        pileWorldRot = worldPileRot;
        assembling = true;
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

    /// <summary>체결 값을 amount만큼 누적(증가)시킨다.</summary>
    public void AddWork(float amount) => SetWork(currentWork + amount);

    /// <summary>
    /// 체결 값을 직접 설정한다 (0 ~ requiredWork로 clamp).
    /// 위치는 4개 제어점(pile → mid2 → mid → assembled)을 지나는 큐빅 베지어로 보간.
    /// (mid/mid2는 통과점이 아니라 곡선을 휘게 하는 '제어 핸들')
    /// </summary>
    public void SetWork(float work)
    {
        currentWork = Mathf.Clamp(work, 0f, requiredWork);

        if (assembling) ApplyWorldPose(Fill); // pile(월드) → 진입점 → 차량 부착점
        else            ApplyLocalPose(Fill); // 차량 로컬 베지어 (에디터 프리뷰/비조립 상태)

        // 완전 분리(work=0)면 숨기고, 그 외에는 표시
        if (currentWork <= 0f) SetActive(false);
        else if (!gameObject.activeSelf) SetActive(true);
    }

    // 체결 완료: 월드 보간을 끝내고 차량 로컬 부착좌표에 고정한다.
    // (이후엔 차량 자식으로서 재계산 없이 차를 따라다닌다)
    public void SetAssembled()
    {
        assembling = false;
        SetWork(requiredWork); // assembling=false → ApplyLocalPose(1) = assembledPos에 정확히 고정
    }
    public void SetDetached()  => SetWork(0f);           // 완전 분리(숨김)

    /// <summary>위치·회전만 적용 (SetActive 없음). OnValidate에서 사용.</summary>
    private void ApplyLocalPose(float fill)
    {
        // fill: 0 → pile(분리), 1 → assembled(체결완료)
        Vector3 pileLocalPos = hasRuntimeDetached ? runtimeDetachedLocalPos : mid2Pos;
        Vector3 pileLocalRot = hasRuntimeDetached ? runtimeDetachedLocalRot : mid2Rot;

        Vector3 localPos = CubicBezier(pileLocalPos, mid2Pos, midPos, assembledPos, fill);
        Vector3 localRot = CubicBezier(pileLocalRot, mid2Rot, midRot, assembledRot, fill);

        transform.localPosition = localPos;
        transform.localRotation = Quaternion.Euler(localRot);
    }

    /// <summary>
    /// 월드 공간 보간: pile(월드 고정) → 진입점 → 부착점(차량 추적).
    /// 진입점은 부착점을 차량 바깥으로 밀어낸 점이라, 마지막 구간이 직선 삽입이 되어 차체를 뚫지 않는다.
    /// 부착점/진입점은 매 프레임 차량 트랜스폼으로 재계산되므로 움직이는 차에 정확히 붙는다.
    /// </summary>
    private void ApplyWorldPose(float fill)
    {
        if (cachedParent == null) cachedParent = transform.parent;
        if (cachedParent == null) { ApplyLocalPose(fill); return; } // 안전장치

        Vector3 entryLocal = assembledPos + GetApproachDirLocal() * entryDistance;

        Vector3 worldStart = pileWorldPos;
        Vector3 worldEntry = cachedParent.TransformPoint(entryLocal);
        Vector3 worldEnd   = cachedParent.TransformPoint(assembledPos);

        // p0=pile, p1=p2=entry → 곡선이 진입점 쪽으로 휜 뒤 entry→attach 직선으로 꽂힌다.
        Vector3 worldPos = CubicBezier(worldStart, worldEntry, worldEntry, worldEnd, fill);
        transform.position = worldPos;

        Quaternion worldEndRot = cachedParent.rotation * Quaternion.Euler(assembledRot);
        transform.rotation = Quaternion.Slerp(pileWorldRot, worldEndRot, fill);
    }

    /// <summary>진입 방향(로컬). override가 있으면 그것을, 없으면 차량중심→부착점 방향을 쓴다.</summary>
    private Vector3 GetApproachDirLocal()
    {
        if (approachDirLocalOverride != Vector3.zero) return approachDirLocalOverride.normalized;

        if (frameCenter == null) frameCenter = FindCenterObject("Frame");
        Vector3 centerLocal = Vector3.zero;
        if (frameCenter != null && cachedParent != null)
            centerLocal = cachedParent.InverseTransformPoint(frameCenter.position);

        Vector3 dir = assembledPos - centerLocal;
        return dir.sqrMagnitude < 1e-6f ? Vector3.up : dir.normalized;
    }

    /// <summary>큐빅 베지어: t=0 → p0, t=1 → p3. p1·p2는 제어 핸들.</summary>
    private static Vector3 CubicBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        return (u * u * u) * p0
             + (3f * u * u * t) * p1
             + (3f * u * t * t) * p2
             + (t * t * t) * p3;
    }

    public IEnumerator FixPartRoutine(float speed = 20f)
    {
        while (!IsAssembled)
        {
            AddWork(Time.deltaTime * speed);
            yield return null;
        }
        SetAssembled();
    }

    #region 에디터 도구 (Inspector 메뉴)

    private void OnValidate()
    {
        if (assembledPos == Vector3.zero && midPos == Vector3.zero) return;
        // OnValidate에서 SetActive 호출 금지 → 위치만 업데이트
        ApplyLocalPose(Fill);
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
        if (config.requiredWork > 0f) requiredWork = config.requiredWork;
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
            mid2Rot      = mid2Rot,
            requiredWork = requiredWork
        };

        if (index != -1) masterData.partConfigs[index] = config;
        else masterData.partConfigs.Add(config);

#if UNITY_EDITOR
        EditorUtility.SetDirty(masterData);
        AssetDatabase.SaveAssets();
        Debug.Log($"[{myType}] 설계도 저장 완료!");
#endif
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

        // 제어점만 표시. 실제 이동 경로(베지어 곡선)는 AssemblyPartEditor의 OnSceneGUI가 그린다.
        Gizmos.color = Color.green;   Gizmos.DrawWireSphere(p0, 0.05f);
        Gizmos.color = Color.yellow;  Gizmos.DrawWireSphere(p1, 0.05f);
        Gizmos.color = Color.magenta; Gizmos.DrawWireSphere(p2, 0.05f);
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
