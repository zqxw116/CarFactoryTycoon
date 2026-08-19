using UnityEngine;

/// <summary>
/// 공정에 쌓여 있는 부품 재고. 실제 부품 프리팹을 쌓아 보여주고, 체결마다 눈에 보이게 줄어든다.
///
/// - 재고가 0이면 그 공정은 체결을 시작할 수 없다 → WorkerStation이 게이트를 유지해 라인이 멈춘다.
/// - 표시용 인스턴스는 capacity만큼 미리 생성해 SetActive로 개수만 토글한다(생성/파괴 없음 = GC 0).
///   프로젝트 전반의 풀링 원칙(CarPool, CashPopup 코인)과 동일.
/// - 부품 프리팹 경로는 PartResourceManager.GetPrefabPath(PartType)를 그대로 재사용한다.
/// </summary>
[ExecuteAlways] // 에디터 씬에서도 표시용 인스턴스를 만들어 보여주기 위함. 게임 로직은 아래에서 isPlaying으로 가드한다.
public class PartStack : MonoBehaviour
{
    [Header("재고 부품")]
    [Tooltip("쌓을 부품 타입. WorkerStation이 SetPartType으로 지정하기도 한다.")]
    public PartType partType = PartType.None;

    [Header("수량")]
    [Tooltip("이 파일에 쌓을 수 있는 최대 재고 수. 표시용 인스턴스도 이 수만큼 생성된다.")]
    public int capacity = 12;

    [Tooltip("시작 재고. -1이면 capacity로 가득 채운 상태로 시작.")]
    public int startCount = -1;

    [Header("쌓기 배치")]
    [Tooltip("한 줄 안에서 부품 하나가 늘 때마다 더해지는 로컬 오프셋. 위로 쌓으면 세로 기둥이 된다.")]
    public Vector3 stepOffset = new Vector3(0f, 0.35f, 0f);

    [Tooltip("한 줄(기둥)에 쌓는 개수. 이 수를 넘으면 rowOffset만큼 옆으로 새 줄을 시작한다. " +
             "stepOffset을 위쪽, rowOffset을 옆쪽으로 잡으면 부품이 직사각형 벽처럼 늘어선다.")]
    public int perRow = 4;

    [Tooltip("줄이 바뀔 때 더해지는 로컬 오프셋(보통 옆쪽 — 벽이 늘어나는 방향).")]
    public Vector3 rowOffset = new Vector3(0.8f, 0f, 0f);

    [Tooltip("표시용 부품 인스턴스에 적용할 스케일 배율.")]
    public float itemScale = 1f;

    [Tooltip("표시용 부품 인스턴스에 적용할 회전 오프셋(오일러). 범퍼·문짝처럼 납작한 부품을 눕히거나 세워서 " +
             "겹겹이 얇게 쌓기 위한 값. 0이면 프리팹 기본 회전 그대로.")]
    public Vector3 itemRotation = Vector3.zero;

    [Header("클릭 콜라이더")]
    [Tooltip("배치된 재고 전체를 감싸도록 BoxCollider를 자동으로 맞춘다(없으면 추가). 손으로 잡을 땐 끄면 된다.")]
    public bool autoFitCollider = true;

    [Tooltip("자동 맞춤 시 사방으로 더해줄 여유(클릭하기 쉽게).")]
    public float colliderPadding = 0.1f;

    [Tooltip("표시용 인스턴스가 가진 콜라이더를 끈다. 클릭은 이 오브젝트의 BoxCollider가 받는다(InputManager는 " +
             "GetComponentInParent로 찾으므로 자식에 맞아도 동작하지만, 물리 오브젝트가 늘어나는 걸 막는다).")]
    public bool disableItemColliders = true;

    [Header("현재 상태 (디버그)")]
    [SerializeField] private int count = 0;

    [Header("기즈모")]
    public bool drawGizmo = true;
    public Color gizmoColor = new Color(1f, 0.85f, 0.2f, 1f);

    private GameObject[] items;          // 표시용 인스턴스 (capacity개, SetActive로 개수 표현)
    private int[] fillOrder;             // 채워지는 순서 → fillOrder[k] = k번째로 채워질 슬롯 인덱스 (아래→위)
    private int[] rankOfSlot;            // 슬롯 인덱스 → 채움 순번. rank < count 이면 보인다 (소모는 그 역순 = 위→아래)
    private PartType builtType = PartType.None;
    private int builtCapacity = 0;

    public int Count => count;
    public int Capacity => capacity;
    public bool IsEmpty => count <= 0;
    public bool IsFull => count >= capacity;
    /// <summary>재고 비율 0~1 (UI 게이지용).</summary>
    public float Fill => capacity > 0 ? (float)count / capacity : 0f;

    private void Start() => EnsureBuilt();

#if UNITY_EDITOR
    // 에디터(비플레이)에서 컴파일·씬 로드 후 표시용 인스턴스를 복구한다.
    // 인스턴스는 DontSave 플래그라 도메인 리로드 때 사라지므로 여기서 다시 만들어 준다.
    private void OnEnable()
    {
        if (Application.isPlaying) return;
        RequestEditorRebuild();
    }
#endif

    /// <summary>표시용 인스턴스를 준비한다(타입/용량이 바뀌면 재생성).</summary>
    private void EnsureBuilt()
    {
        if (items != null && builtType == partType && builtCapacity == capacity) return;

        // 기존 인스턴스 정리 (타입 변경 시에만 발생 — 런타임 상시 경로 아님)
        if (items != null)
        {
            for (int i = 0; i < items.Length; i++)
                if (items[i] != null) DestroyItem(items[i]);
        }
        // 에디터에서 이전 세션(도메인 리로드 등)에 남아 있을 수 있는 표시용 자식도 함께 정리한다.
        DestroyStrayEditorItems();

        items = new GameObject[Mathf.Max(0, capacity)];
        builtType = partType;
        builtCapacity = capacity;

        GameObject prefab = LoadPrefab();
        if (prefab == null)
        {
            Debug.LogWarning($"[PartStack] {name}: {partType} 부품 프리팹을 찾지 못해 재고를 시각화할 수 없습니다.");
        }
        else
        {
            for (int i = 0; i < items.Length; i++)
                items[i] = CreateItem(prefab, i);
        }

        count = startCount < 0 ? capacity : Mathf.Clamp(startCount, 0, capacity);
        BuildFillOrder();
        Refresh();
        FitCollider(true);   // 재생성 직후 1회만 — 재고 증감(Refresh)에서는 돌지 않는다
    }

    private GameObject LoadPrefab()
    {
        if (partType == PartType.None) return null;
        string path = PartResourceManager.GetPrefabPath(partType);
        return string.IsNullOrEmpty(path) ? null : Resources.Load<GameObject>(path);
    }

    private GameObject CreateItem(GameObject prefab, int index)
    {
        GameObject go = Instantiate(prefab, transform);
        go.name = $"Stack_{partType}_{index}";
        go.transform.localPosition = GetSlotLocalPos(index);
        go.transform.localRotation = Quaternion.Euler(itemRotation);
        go.transform.localScale = prefab.transform.localScale * itemScale;

        // 표시 전용이라 물리도 필요 없다. 클릭은 이 오브젝트의 BoxCollider가 받는다.
        if (disableItemColliders)
        {
            Collider[] cols = go.GetComponentsInChildren<Collider>(true);
            for (int c = 0; c < cols.Length; c++) cols[c].enabled = false;
        }

        // 표시 전용: 체결 로직이 이 인스턴스를 집어가지 않도록 AssemblyPart를 끈다.
        // (부품 프리팹에는 AssemblyPart가 붙어 있고, 켜져 있으면 LateUpdate 등이 함께 돈다)
        AssemblyPart part = go.GetComponent<AssemblyPart>();
        if (part != null) part.enabled = false;

        go.SetActive(false);

#if UNITY_EDITOR
        // 에디터 씬에서 만든 표시용 인스턴스는 씬 파일에 저장하지 않는다(유령 오브젝트 방지).
        if (!Application.isPlaying) go.hideFlags = HideFlags.HideAndDontSave;
#endif
        return go;
    }

    /// <summary>표시용 인스턴스 1개를 파괴한다(에디터 비플레이에서는 즉시 파괴해야 반영된다).</summary>
    private void DestroyItem(GameObject go)
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) { DestroyImmediate(go); return; }
#endif
        Destroy(go);
    }

    /// <summary>에디터에서 추적이 끊긴 채 남아 있는 표시용 자식(Stack_*)을 정리한다.</summary>
    private void DestroyStrayEditorItems()
    {
#if UNITY_EDITOR
        if (Application.isPlaying) return;
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (child != null && child.name.StartsWith("Stack_")) DestroyImmediate(child.gameObject);
        }
#endif
    }

    /// <summary>index번째 재고가 놓이는 로컬 좌표.</summary>
    /// <remarks>
    /// stepOffset/rowOffset은 itemRotation과 무관하게 **이 오브젝트의 로컬 축 그대로** 해석한다.
    /// 회전 후 축을 쓰면 부품을 눕히려고 itemRotation만 만졌을 때 쌓이는 방향까지 같이 돌아가
    /// 이미 맞춰 둔 배치가 무너진다. "파일(자재대)이 쌓이는 방향은 파일 기준, 회전은 부품 하나의 자세"로
    /// 역할을 분리하는 편이 위치 잡기가 훨씬 직관적이다.
    /// </remarks>
    private Vector3 GetSlotLocalPos(int index)
    {
        int row = perRow > 0 ? index / perRow : 0;
        int slot = perRow > 0 ? index % perRow : index;
        return stepOffset * slot + rowOffset * row;
    }

    /// <summary>이미 만들어 둔 인스턴스들의 위치/회전을 현재 배치 값으로 다시 맞춘다(값이 바뀔 때만 호출).</summary>
    private void ApplyLayout()
    {
        if (items == null) return;
        Quaternion rot = Quaternion.Euler(itemRotation);
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] == null) continue;
            items[i].transform.localPosition = GetSlotLocalPos(i);
            items[i].transform.localRotation = rot;
        }
        // 배치가 바뀌면 높이 순서도 달라지므로 소모 순서를 다시 계산하고 표시를 맞춘다.
        BuildFillOrder();
        Refresh();

        // OnValidate에서도 불리는 경로라 여기서는 컴포넌트를 추가하지 않는다(크기만 갱신).
        FitCollider(false);
    }

    /// <summary>
    /// 슬롯을 "채워지는 순서"로 정렬해 캐시한다. 아래(낮은 Y)부터 채우고 <b>위(높은 Y)부터 소모</b>한다.
    /// </summary>
    /// <remarks>
    /// 인덱스 순서를 그대로 쓰지 않는 이유: 직사각형 벽 배치(stepOffset=위, rowOffset=옆)에서는
    /// 인덱스가 "1번 기둥 아래→위, 2번 기둥 아래→위…" 순이라, 인덱스 역순으로 끄면 첫 기둥만 통째로
    /// 비고 옆 기둥은 가득 찬 이상한 그림이 된다.
    /// 그래서 <b>슬롯의 실제 높이(Y)</b>를 기준으로 삼아 전체 줄에서 <b>맨 윗 층부터 골고루</b> 빠지게 했다.
    /// 실제 자재대에서 위에 있는 걸 걷어가는 모습과 같고, 12개 중 6개를 쓰면 위 절반이 사라지고
    /// 아래 절반이 남는 — 유저가 말한 그림이 그대로 나온다.
    /// 정렬은 빌드/레이아웃 변경 시에만 돌고(런타임 재고 증감 경로 아님), 배열은 용량이 바뀔 때만 새로 잡는다.
    /// </remarks>
    private void BuildFillOrder()
    {
        int n = items != null ? items.Length : Mathf.Max(0, capacity);
        if (fillOrder == null || fillOrder.Length != n) fillOrder = new int[n];
        if (rankOfSlot == null || rankOfSlot.Length != n) rankOfSlot = new int[n];
        if (n == 0) return;

        for (int i = 0; i < n; i++) fillOrder[i] = i;

        // 삽입 정렬: 높이 오름차순, 같은 높이면 인덱스 순(줄 순서 유지). n이 작고 빌드 때만 도는 경로다.
        for (int i = 1; i < n; i++)
        {
            int cur = fillOrder[i];
            float curY = GetSlotLocalPos(cur).y;
            int j = i - 1;
            while (j >= 0 && GetSlotLocalPos(fillOrder[j]).y > curY + 0.0001f)
            {
                fillOrder[j + 1] = fillOrder[j];
                j--;
            }
            fillOrder[j + 1] = cur;
        }

        for (int k = 0; k < n; k++) rankOfSlot[fillOrder[k]] = k;
    }

    /// <summary>
    /// 배치된 재고 전체를 감싸도록 BoxCollider(응급 보충 클릭 대상)를 맞춘다.
    /// 부품 실제 크기는 메시 bounds에서 얻는다 — 표시용 인스턴스는 SetActive(false)일 수 있어
    /// Renderer.bounds가 신뢰할 수 없기 때문(비활성 오브젝트는 갱신되지 않는다).
    /// isTrigger는 건드리지 않는다: Physics.Raycast는 Physics.queriesHitTriggers(기본 true)만 만족하면
    /// 트리거도 맞으므로 유저가 잡아 둔 설정을 존중한다.
    /// </summary>
    /// <param name="allowAdd">BoxCollider가 없을 때 새로 붙여도 되는지(OnValidate 중에는 false).</param>
    private void FitCollider(bool allowAdd)
    {
        if (!autoFitCollider || items == null) return;

        bool has = false;
        Bounds local = new Bounds(Vector3.zero, Vector3.zero);
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] == null) continue;
            EncapsulateItemBounds(items[i], ref local, ref has);
        }
        if (!has) return;

        BoxCollider box = GetComponent<BoxCollider>();
        if (box == null)
        {
            if (!allowAdd) return;
            box = gameObject.AddComponent<BoxCollider>();
        }

        box.center = local.center;
        box.size = local.size + Vector3.one * Mathf.Max(0f, colliderPadding) * 2f;
    }

    /// <summary>표시용 인스턴스 하나의 메시 bounds를 이 오브젝트의 로컬 공간으로 옮겨 누적한다.</summary>
    private void EncapsulateItemBounds(GameObject go, ref Bounds local, ref bool has)
    {
        MeshFilter[] filters = go.GetComponentsInChildren<MeshFilter>(true);
        for (int f = 0; f < filters.Length; f++)
        {
            Mesh mesh = filters[f].sharedMesh;
            if (mesh == null) continue;

            Bounds mb = mesh.bounds;
            Transform t = filters[f].transform;
            // 메시 로컬 8코너 → 월드 → 스택 로컬. (재빌드/값 변경 시에만 도는 경로 — 매 프레임 아님)
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = mb.center + Vector3.Scale(mb.extents, kCorners[c]);
                Vector3 p = transform.InverseTransformPoint(t.TransformPoint(corner));
                if (!has) { local = new Bounds(p, Vector3.zero); has = true; }
                else local.Encapsulate(p);
            }
        }
    }

    private static readonly Vector3[] kCorners =
    {
        new Vector3(-1f, -1f, -1f), new Vector3(1f, -1f, -1f),
        new Vector3(-1f,  1f, -1f), new Vector3(1f,  1f, -1f),
        new Vector3(-1f, -1f,  1f), new Vector3(1f, -1f,  1f),
        new Vector3(-1f,  1f,  1f), new Vector3(1f,  1f,  1f),
    };

#if UNITY_EDITOR
    /// <summary>
    /// 인스펙터 값 변경을 즉시 반영한다.
    /// - partType/capacity가 바뀐 경우에만 프리팹을 다시 로드해 인스턴스를 재생성(EnsureBuilt).
    ///   OnValidate 중에는 오브젝트를 파괴/생성할 수 없으므로 delayCall로 한 프레임 미룬다.
    /// - 그 외(오프셋·회전·스케일)는 위치/회전만 갱신 — 재생성 없음(GC 0).
    /// </summary>
    private void OnValidate()
    {
        if (builtType != partType || builtCapacity != capacity)
        {
            RequestEditorRebuild();
            return;
        }
        ApplyLayout();
    }

    [System.NonSerialized] private bool pendingEditorRebuild;

    /// <summary>재빌드 예약(중복 예약 방지 — 값을 연속으로 바꿔도 한 번만 돈다).</summary>
    private void RequestEditorRebuild()
    {
        if (pendingEditorRebuild) return;
        pendingEditorRebuild = true;
        UnityEditor.EditorApplication.delayCall += EditorRebuild;
    }

    /// <summary>delayCall로 미뤄 실행되는 에디터 전용 재빌드. 도중에 상황이 바뀌었을 수 있어 가드가 필수.</summary>
    private void EditorRebuild()
    {
        UnityEditor.EditorApplication.delayCall -= EditorRebuild;
        if (this != null) pendingEditorRebuild = false;

        if (this == null) return;                                  // delayCall 사이에 삭제됨
        if (Application.isPlaying) return;                          // 플레이 진입 — 런타임 경로가 담당
        if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isUpdating) return;
        if (UnityEditor.PrefabUtility.IsPartOfPrefabAsset(gameObject)) return; // 프리팹 에셋 자체는 건드리지 않음
        if (!gameObject.scene.IsValid()) return;                    // 씬에 속하지 않은 인스턴스(프리뷰 등)

        // 아래 정리에서 자식을 통째로 지우므로, EnsureBuilt가 조기 반환하지 않도록 빌드 상태를 초기화한다.
        items = null;
        builtType = PartType.None;
        builtCapacity = -1;
        DestroyStrayEditorItems();
        EnsureBuilt();
    }
#endif

    /// <summary>
    /// 현재 count만큼만 인스턴스를 보이게 한다.
    /// 채움 순번(rankOfSlot)이 count 미만인 슬롯만 켜므로 아래부터 차고 위부터 빠진다.
    /// 활성 개수는 항상 count와 정확히 일치하고, 소모/보충을 몇 번 반복해도 같은 규칙으로 다시 계산되므로
    /// 상태가 어긋나거나 중간에 구멍이 뚫리지 않는다(누적 상태 없이 count 하나로만 결정된다).
    /// </summary>
    private void Refresh()
    {
        if (items == null) return;
        bool useRank = rankOfSlot != null && rankOfSlot.Length == items.Length;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] == null) continue;
            bool visible = useRank ? rankOfSlot[i] < count : i < count;
            if (items[i].activeSelf != visible) items[i].SetActive(visible);
        }
    }

    /// <summary>다음에 소모될(= 남아 있는 재고 중 맨 위) 슬롯의 인덱스. 재고가 없으면 -1.</summary>
    public int TopSlotIndex
    {
        get
        {
            if (count <= 0) return -1;
            if (fillOrder == null || fillOrder.Length == 0) return Mathf.Min(count, Mathf.Max(0, capacity)) - 1;
            return fillOrder[Mathf.Clamp(count - 1, 0, fillOrder.Length - 1)];
        }
    }

    /// <summary>작업자·로봇팔이 집으러 올 지점 — 다음에 소모될 슬롯의 월드 좌표. 재고가 없으면 이 오브젝트 위치.</summary>
    public Vector3 GetTopSlotWorldPos()
    {
        int idx = TopSlotIndex;
        if (idx < 0) return transform.position;
        return transform.TransformPoint(GetSlotLocalPos(idx));
    }

    /// <summary>집어 든 부품이 이어받을 자세(쌓여 있던 회전 그대로).</summary>
    public Quaternion GetTopSlotWorldRot() => transform.rotation * Quaternion.Euler(itemRotation);

    /// <summary>재고 1개를 소모한다. 재고가 없으면 false (= 공정 진행 불가).</summary>
    public bool TryConsume()
    {
        EnsureBuilt();
        if (count <= 0) return false;
        count--;
        Refresh();
        return true;
    }

    /// <summary>재고를 추가한다. 용량을 넘는 분은 버리고, 실제로 추가된 개수를 반환한다.</summary>
    public int Add(int amount)
    {
        EnsureBuilt();
        if (amount <= 0) return 0;
        int added = Mathf.Min(amount, capacity - count);
        if (added <= 0) return 0;
        count += added;
        Refresh();
        return added;
    }

    /// <summary>가득 채운다. 실제로 추가된 개수를 반환한다.</summary>
    public int Fill_Full() => Add(capacity - count);

    /// <summary>부품 타입을 지정한다(WorkerStation이 공정 타입에 맞춰 호출).</summary>
    public void SetPartType(PartType type)
    {
        if (partType == type) return;
        partType = type;
        EnsureBuilt();
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmo) return;

        Gizmos.color = gizmoColor;
        // 재고 슬롯 위치를 작은 와이어큐브로 표시 — 배치/간격 조정용.
        // 큐브도 itemRotation을 적용해 그려서 실제 부품이 어떤 자세로 눕는지 바로 보이게 한다.
        Matrix4x4 prevMatrix = Gizmos.matrix;
        Quaternion rot = Quaternion.Euler(itemRotation);
        int n = Mathf.Max(0, capacity);
        for (int i = 0; i < n; i++)
        {
            Vector3 world = transform.TransformPoint(GetSlotLocalPos(i));
            Gizmos.matrix = Matrix4x4.TRS(world, transform.rotation * rot, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(0.15f, 0.05f, 0.15f));
        }
        Gizmos.matrix = prevMatrix;

#if UNITY_EDITOR
        UnityEditor.Handles.color = gizmoColor;
        UnityEditor.Handles.Label(transform.position + Vector3.up * 0.4f,
            $"{partType} 재고 {count}/{capacity}");
#endif
    }
}
