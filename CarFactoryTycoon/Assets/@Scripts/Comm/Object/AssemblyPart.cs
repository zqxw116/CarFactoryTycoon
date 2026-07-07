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

    [Header("체결 완료 좌표 (차량 로컬, fill=1)")]
    public Vector3 assembledPos;
    public Vector3 assembledRot;

    [Header("베지어 제어 핸들 — 부착점 쪽 (통과점 아님, 차량 로컬)")]
    public Vector3 midPos;
    public Vector3 midRot;

    [Header("베지어 제어 핸들 — pile 쪽 (통과점 아님, 차량 로컬)")]
    [Tooltip("런타임 곡선은 스테이션 stationPilePos(월드)에서 출발: pile → mid2 → mid → 부착점." +
        " 에디터 프리뷰(ApplyLocalPose)에서는 mid2가 시작점을 겸한다. 둘 다 zero면 자동 진입점 사용.")]
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

    // 월드 조립 상태: BeginWorldAssembly()로 시작. 시작점(pile)은 월드 고정,
    // 부착점은 매 프레임 차량을 따라 재계산되어 움직이는 차에 정확히 붙는다.
    private bool assembling = false;
    private Vector3 pileWorldPos;
    // 시작 회전 = 부착(도착) 회전 × 이 오프셋 — 월드 절대값이 아니라 도착 회전 기준 상대값.
    // 차량/라인 진행 방향과 무관하게 "부착 방향에서 몇 도 꺾여서 시작"이라는 의미가 유지된다.
    // identity면 회전 연출 없음(처음부터 부착 방향).
    private Quaternion pileRotOffset = Quaternion.identity;
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
        SetDetached(); // work=0 → SetActive(false)
    }

    /// <summary>
    /// 월드 조립을 시작한다. 부품은 pile(로봇팔 대기위치, 월드 고정)에서 출발해
    /// 진입점을 거쳐 차량 부착점(매 프레임 차량 추적)으로 이동한다.
    /// startRotOffset = 부착(도착) 회전 기준 시작 회전 오프셋 (identity = 회전 연출 없음).
    /// 호출 후 SetWork/AddWork로 진행도를 올리면 위치가 갱신된다.
    /// </summary>
    public void BeginWorldAssembly(Vector3 worldPilePos, Quaternion startRotOffset)
    {
        pileWorldPos = worldPilePos;
        pileRotOffset = startRotOffset;
        assembling = true;
    }

    /// <summary>로봇팔이 바라봐야 할 월드 좌표. armLookTarget 미설정 시 오브젝트 중심 반환.</summary>
    public Vector3 GetArmLookWorldPos() => armLookTarget != null ? armLookTarget.position : transform.position;


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

    // 조립 중에는 매 프레임 포즈 재계산이 필요하다.
    // 파츠는 차량의 자식이라, 체결이 멈춘 동안(AddWork 미호출) 차량이 움직이면
    // 마지막으로 계산된 로컬 위치 그대로 딸려간다 → fill=0이어도 pile(월드 고정)에 붙잡아 둔다.
    // 차량 이동(Update) 이후에 계산되도록 LateUpdate 사용.
    private void LateUpdate()
    {
        if (assembling) ApplyWorldPose(Fill);
    }

    /// <summary>위치·회전만 적용 (SetActive 없음). OnValidate에서 사용.</summary>
    private void ApplyLocalPose(float fill)
    {
        // fill: 0 → mid2(시작점 겸 pile 쪽 핸들), 1 → assembled(체결완료)
        // 에디터 프리뷰/완료 고정용 로컬 곡선 — 런타임 곡선은 ApplyWorldPose(시작점=stationPilePos)가 담당.
        Vector3 localPos = CubicBezier(mid2Pos, mid2Pos, midPos, assembledPos, fill);
        Vector3 localRot = CubicBezier(mid2Rot, mid2Rot, midRot, assembledRot, fill);

        transform.localPosition = localPos;
        transform.localRotation = Quaternion.Euler(localRot);
    }

    /// <summary>
    /// 월드 공간 보간: pile(월드 고정) → 제어 핸들 → 부착점(차량 추적).
    /// 저작된 midPos/mid2Pos(차량 로컬)가 있으면 그대로 월드 제어 핸들로 사용해 저작 경로를 따라가고,
    /// 없으면 부착점을 차량 바깥으로 밀어낸 자동 진입점을 쓴다.
    /// 핸들/부착점은 매 프레임 차량 트랜스폼으로 재계산되므로 움직이는 차에 정확히 붙는다.
    /// </summary>
    private void ApplyWorldPose(float fill)
    {
        if (cachedParent == null) cachedParent = transform.parent;
        if (cachedParent == null) { ApplyLocalPose(fill); return; } // 안전장치

        Vector3 worldStart = pileWorldPos;
        Vector3 worldEnd   = cachedParent.TransformPoint(assembledPos);

        Vector3 p1, p2;
        if (midPos != Vector3.zero || mid2Pos != Vector3.zero)
        {
            p1 = cachedParent.TransformPoint(mid2Pos); // pile 쪽 핸들
            p2 = cachedParent.TransformPoint(midPos);  // 부착점 쪽 핸들
        }
        else
        {
            // 저작 핸들이 없는 부품: 자동 진입점(부착점에서 바깥 법선으로 entryDistance만큼)
            Vector3 entryLocal = assembledPos + GetApproachDirLocal() * entryDistance;
            p1 = p2 = cachedParent.TransformPoint(entryLocal);
        }

        transform.position = CubicBezier(worldStart, p1, p2, worldEnd, fill);

        // 회전: 시작 = 도착 회전 × 오프셋(도착 기준 상대값) → 차량이 어느 방향으로 달리든
        // "부착 방향에서 pileRotOffset만큼 꺾인 상태 → 부착 방향" 보간이 일관되게 유지된다.
        Quaternion worldEndRot = cachedParent.rotation * Quaternion.Euler(assembledRot);
        transform.rotation = Quaternion.Slerp(worldEndRot * pileRotOffset, worldEndRot, fill);
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
        Vector3 wAssembled = transform.parent.TransformPoint(assembledPos); // 체결 완료 (fill=1)
        Gizmos.color = Color.green; Gizmos.DrawWireSphere(wAssembled, 0.05f);

        if (assembling)
        {
            // 런타임 월드 곡선: pile(스테이션 stationPilePos 스냅샷) → mid2 → mid → 부착점.
            // ApplyWorldPose와 동일한 제어점을 그려 실제 이동 경로를 보여준다(차량 이동 따라 매 프레임 갱신).
            Vector3 p1, p2;
            if (midPos != Vector3.zero || mid2Pos != Vector3.zero)
            {
                p1 = transform.parent.TransformPoint(mid2Pos);
                p2 = transform.parent.TransformPoint(midPos);
            }
            else
            {
                Vector3 entryLocal = assembledPos + GetApproachDirLocal() * entryDistance;
                p1 = p2 = transform.parent.TransformPoint(entryLocal);
            }

            Gizmos.color = new Color(1f, 0.6f, 0.1f, 1f); // 주황: 시작점 = stationPilePos (스테이션 기즈모와 동일 색)
            Gizmos.DrawWireSphere(pileWorldPos, 0.05f);
            Gizmos.color = Color.magenta; Gizmos.DrawWireSphere(p1, 0.05f); // mid2 핸들
            Gizmos.color = Color.yellow;  Gizmos.DrawWireSphere(p2, 0.05f); // mid 핸들

            Gizmos.color = Color.cyan;
            DrawBezierGizmo(pileWorldPos, p1, p2, wAssembled);
        }
        else
        {
            // 에디터 저작 프리뷰: 차량 로컬 제어 핸들만 표시.
            // 저작 곡선(베지어)은 AssemblyPartEditor의 OnSceneGUI가 그린다.
            Gizmos.color = Color.yellow;  Gizmos.DrawWireSphere(transform.parent.TransformPoint(midPos), 0.05f);
            Gizmos.color = Color.magenta; Gizmos.DrawWireSphere(transform.parent.TransformPoint(mid2Pos), 0.05f);
        }

        if (armLookTarget != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(armLookTarget.position, 0.07f);
            Gizmos.DrawLine(transform.position, armLookTarget.position);
        }
    }

    /// <summary>기즈모용 베지어 폴리라인 (Gizmos.color 사용).</summary>
    private static void DrawBezierGizmo(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        const int SEGMENTS = 24;
        Vector3 prev = p0;
        for (int i = 1; i <= SEGMENTS; i++)
        {
            Vector3 next = CubicBezier(p0, p1, p2, p3, i / (float)SEGMENTS);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }

    #endregion
}
