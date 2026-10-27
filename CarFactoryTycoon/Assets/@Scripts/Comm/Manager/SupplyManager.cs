using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 재고 보급 시스템의 관리자 (GAME_DESIGN_PROGRESS.md 9-1, 9-15).
/// 보급 작업자(SupplyWorker)가 창고에서 "다음에 채울 재고 파일"을 고를 때 이 매니저에 묻는다.
///
/// - 보급 대상 = 씬의 WorkerStation이 가진 PartStack. Start(또는 첫 조회)에서 1회만 목록을 구성한다.
/// - 대상 선정은 <b>창고에서만</b> 한다(공정→공정 직행 없음 — 한 번에 한 부품 종류만 실을 수 있다).
/// - 우선순위 (9-15 ⑻-1 확정: 최소 잔량 우선 — 효율보다 '유저가 읽을 수 있는 규칙'이 우선)
///     1) 재고가 0이고 그 때문에 라인이 실제로 멈춘 공정 (WorkerStation.IsBlockedByStock)
///     2) 재고가 0인 공정 (아직 차량이 안 왔을 뿐 곧 멈춘다)
///     3) 나머지는 Fill(재고 비율) 최소 순
///     동률이면 창고 적재 지점에서 가까운 순.
/// - 예약: 재고 파일 하나에 보급 작업자 <b>1명만</b> 간다(단독 잠금). 여러 명을 배치해도 같은 파일로
///   몰려가지 않는다. 해제 경로는 하역 완료 / 대상 무효 / 작업자 비활성·파괴 세 가지다(SupplyWorker 쪽).
///
/// 씬에 이 매니저가 없으면 보급 작업자는 경고 1회 후 대기만 한다(자동 생성하지 않는다 —
/// 창고 적재 지점 같은 씬 설정이 필요하므로 GameObjectSingleton을 쓴다).
/// </summary>
public class SupplyManager : GameObjectSingleton<SupplyManager>
{
    /// <summary>보급 대상 하나 (재고 파일 + 그 파일을 쓰는 공정).</summary>
    public struct SupplyTarget
    {
        public WorkerStation station;
        public WheelStation wheelStation;
        public PartStack stack;
        public bool prototypeTarget;

        public bool IsValid => stack != null && (prototypeTarget || station != null || wheelStation != null);
    }

    [Header("창고")]
    [Tooltip("보급 작업자가 부품을 싣는 지점. 창고 건물 '앞' 바닥에 빈 오브젝트를 두고 꽂는다." +
        " 비어 있으면 InputManager.warehouse 위치로 폴백한다 — 단 창고 큐브의 중심이라" +
        " 작업자가 박스 안으로 파고들 수 있으므로 반드시 지정하는 것을 권장한다.")]
    public Transform warehouseLoadPoint;

    [Header("재고 파일 접근")]
    [Tooltip("재고 파일 앞에서 서는 지점 = 파일 위치 + 이 오프셋(파일 로컬 좌표, m)." +
        " 0이면 파일 원점(맨 아래 첫 슬롯)으로 바로 걸어간다. 파일이 라인 쪽을 보고 있으면" +
        " 통로 쪽으로 밀어 두면 된다.")]
    public Vector3 stackApproachOffset = Vector3.zero;

    [Header("보급 작업자")]
    [Tooltip("배치된 보급 작업자 목록. 비어 있으면 시작 시 씬에서 자동 수집한다." +
        " 다수 배치 대응: 여러 명을 배치해도 재고 파일 예약으로 같은 곳에 겹쳐 가지 않는다.")]
    public List<SupplyWorker> supplyWorkers = new List<SupplyWorker>();

    [Header("프로토타입 연결")]
    [Tooltip("켜면 씬의 실제 PartStack을 모두 보급 대상으로 사용하고, 구형 정지 긴급도를 무시한다.")]
    public bool prototypeMode;

    [Header("임시 고용 (SupplyHireUI)")]
    [Tooltip("고용 시 복제할 보급 작업자. 비어 있으면 목록의 첫 번째 작업자를 복제한다." +
        " 씬에 비활성 오브젝트로 두거나 프리팹을 꽂아도 된다(복제본은 활성화된다).")]
    public SupplyWorker hireTemplate;

    [Tooltip("고용된 작업자가 창고 적재 지점 주변에 생성될 때의 간격(m). 서로 겹쳐 생성되지 않게 원형으로 흩는다.")]
    public float hireSpacing = 0.8f;

    // TODO(9-15 ⑷ 증원): 비용 차감(EconomyManager.TrySpend, 누진) · 인원 상한 · 업그레이드는 미구현
    //   (상점 UI 작업 시 추가 예정). 상한은 WorkerCount로 판정하면 된다 —
    //   예약 구조는 인원 수와 무관하게 이미 동작한다(다수 배치 대응).

    [Header("디버그")]
    [Tooltip("대상 목록 구성 결과 등 매니저 로그를 남긴다(매 프레임 로그는 없다).")]
    public bool verboseLog = true;

    [Header("기즈모")]
    public bool drawGizmo = true;

    private const string LOG_TAG = "[Supply]";

    private readonly List<SupplyTarget> targets = new List<SupplyTarget>();

    // 재고 파일 단독 잠금: 파일 → 그 파일로 가는 보급 작업자
    private readonly Dictionary<PartStack, SupplyWorker> reservations = new Dictionary<PartStack, SupplyWorker>();

    private bool built = false;
    private Transform fallbackWarehouse;
    private bool warnedLoadPoint = false;

    /// <summary>보급 대상(재고 파일) 수 — 디버그/UI용.</summary>
    public int TargetCount { get { EnsureBuilt(); return targets.Count; } }

    private void Start()
    {
        EnsureBuilt();
    }

    /// <summary>
    /// 보급 대상 목록과 작업자 목록을 1회 구성한다. SupplyWorker.Start가 이 매니저의 Start보다 먼저
    /// 돌 수 있으므로 조회 시에도 호출해 실행 순서와 무관하게 만든다.
    /// </summary>
    private void EnsureBuilt()
    {
        if (built) return;
        built = true;

        // 비활성 공정도 포함해 수집한다 — 나중에 켜지는 공정(로봇↔사람 교체 등)도 대상이 되고,
        // 활성 여부는 선정 시점에 isActiveAndEnabled로 거른다.
        WorkerStation[] stations = FindObjectsByType<WorkerStation>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var seen = new HashSet<PartStack>();
        if (prototypeMode)
        {
            // M3 흐름형 공정은 WorkerStation을 끄며, 바퀴/로봇/플레이어 전용 재고도
            // 기존 작업자 공정에 속하지 않을 수 있다. 씬의 실재 PartStack을 직접 수집한다.
            WheelStation[] wheelStations = FindObjectsByType<WheelStation>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            PartStack[] allStacks = FindObjectsByType<PartStack>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < allStacks.Length; i++)
            {
                PartStack stack = allStacks[i];
                if (stack == null || !seen.Add(stack)) continue;

                WorkerStation ownerStation = null;
                for (int s = 0; s < stations.Length && ownerStation == null; s++)
                {
                    WorkerStation candidate = stations[s];
                    if (candidate == null || candidate.partStacks == null) continue;
                    for (int j = 0; j < candidate.partStacks.Length; j++)
                        if (candidate.partStacks[j] == stack) { ownerStation = candidate; break; }
                }
                WheelStation ownerWheel = null;
                for (int s = 0; s < wheelStations.Length && ownerWheel == null; s++)
                {
                    WheelStation candidate = wheelStations[s];
                    if (candidate == null || candidate.humanPartStacks == null) continue;
                    for (int j = 0; j < candidate.humanPartStacks.Length; j++)
                        if (candidate.humanPartStacks[j] == stack) { ownerWheel = candidate; break; }
                }
                targets.Add(new SupplyTarget
                {
                    station = ownerStation,
                    wheelStation = ownerWheel,
                    stack = stack,
                    prototypeTarget = true
                });
            }
        }
        else
        {
            for (int s = 0; s < stations.Length; s++)
            {
                WorkerStation station = stations[s];
                if (station == null || station.partStacks == null) continue;

                // partStacks가 targetPartTypes보다 짧으면 WorkerStation이 마지막 파일을 공용으로 쓴다.
                // 배열에 실제로 들어 있는 파일만 보므로 공용 파일도 한 번만 등록된다.
                for (int i = 0; i < station.partStacks.Length; i++)
                {
                    PartStack stack = station.partStacks[i];
                    if (stack == null || !seen.Add(stack)) continue;
                    targets.Add(new SupplyTarget { station = station, stack = stack });
                }
            }
        }

        if (supplyWorkers.Count == 0)
            // 활성 작업자만 — 비활성으로 숨겨 둔 고용 템플릿이 인원으로 잡히지 않게 한다.
            supplyWorkers.AddRange(FindObjectsByType<SupplyWorker>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));

        InputManager input = FindFirstObjectByType<InputManager>();
        if (input != null) fallbackWarehouse = input.warehouse;

        if (verboseLog)
            Debug.Log($"{LOG_TAG} 보급 대상 재고 파일 {targets.Count}개 (공정 {stations.Length}개)," +
                $" 보급 작업자 {supplyWorkers.Count}명");
    }

    /// <summary>
    /// 창고 적재 지점(월드). warehouseLoadPoint → InputManager.warehouse → 이 오브젝트 순으로 폴백한다.
    /// 폴백 경고는 1회만 남긴다.
    /// </summary>
    public Vector3 LoadPoint
    {
        get
        {
            if (warehouseLoadPoint != null) return warehouseLoadPoint.position;

            EnsureBuilt();
            if (!warnedLoadPoint)
            {
                warnedLoadPoint = true;
                Debug.LogWarning(fallbackWarehouse != null
                    ? $"{LOG_TAG} warehouseLoadPoint가 비어 InputManager.warehouse('{fallbackWarehouse.name}') 위치로 폴백합니다." +
                      " 창고 큐브 중심이라 작업자가 박스 안으로 들어갈 수 있으니 적재 지점을 지정하세요."
                    : $"{LOG_TAG} warehouseLoadPoint와 InputManager.warehouse가 모두 없어 SupplyManager 위치를 창고로 씁니다.",
                    this);
            }
            return fallbackWarehouse != null ? fallbackWarehouse.position : transform.position;
        }
    }

    /// <summary>재고 파일 앞에서 보급 작업자가 설 지점(월드).</summary>
    public Vector3 GetApproachPoint(PartStack stack)
    {
        return stack.transform.TransformPoint(stackApproachOffset);
    }

    /// <summary>
    /// 대상이 아직 보급 가능한지 — 가는 도중 공정이 꺼지거나 파일이 사라지면 false.
    /// (가득 찼는지는 여기서 보지 않는다: 가는 동안 유저가 클릭으로 채웠어도 하역 시 Add가 넘치는 분을 버린다)
    /// </summary>
    public static bool IsTargetAlive(SupplyTarget t)
    {
        if (!t.IsValid || !t.stack.isActiveAndEnabled || t.stack.partType == PartType.None) return false;
        if (t.prototypeTarget) return true;
        return t.station != null && t.station.isActiveAndEnabled;
    }

    /// <summary>
    /// 창고에 있는 보급 작업자가 다음 대상을 고르고 <b>즉시 예약</b>한다. 후보가 없으면 false.
    /// 후보 = 예약 안 됨 && Count &lt; Capacity && 공정 활성 && partType != None.
    /// </summary>
    public bool TryReserveNext(SupplyWorker worker, out SupplyTarget result)
    {
        EnsureBuilt();
        result = default;
        if (worker == null) return false;

        Vector3 origin = LoadPoint;
        int bestIndex = -1;
        int bestTier = int.MaxValue;
        float bestFill = float.MaxValue;
        float bestDist = float.MaxValue;

        for (int i = 0; i < targets.Count; i++)
        {
            SupplyTarget t = targets[i];
            if (!IsTargetAlive(t) || t.stack.IsFull) continue;
            if (reservations.TryGetValue(t.stack, out SupplyWorker owner) && owner != null && owner != worker) continue;

            int tier = t.prototypeTarget ? 0 : GetUrgencyTier(t);
            float fill = t.stack.Fill;
            float dist = (GetApproachPoint(t.stack) - origin).sqrMagnitude;

            if (!IsBetter(tier, fill, dist, bestTier, bestFill, bestDist)) continue;
            bestIndex = i;
            bestTier = tier;
            bestFill = fill;
            bestDist = dist;
        }

        if (bestIndex < 0) return false;

        result = targets[bestIndex];
        reservations[result.stack] = worker;
        return true;
    }

    /// <summary>업무 재개 시 운반 중이던 화물의 기존 파일을 다시 예약한다.</summary>
    public bool TryReserveStack(SupplyWorker worker, PartStack stack, out SupplyTarget result)
    {
        EnsureBuilt();
        result = default;
        if (worker == null || stack == null) return false;

        for (int i = 0; i < targets.Count; i++)
        {
            SupplyTarget candidate = targets[i];
            if (candidate.stack != stack || !IsTargetAlive(candidate) || stack.IsFull) continue;
            if (reservations.TryGetValue(stack, out SupplyWorker owner) && owner != null && owner != worker) return false;
            reservations[stack] = worker;
            result = candidate;
            return true;
        }
        return false;
    }

    /// <summary>긴급도 단계. 작을수록 급하다: 0=재고0으로 라인 정지 / 1=재고0 / 2=일반.</summary>
    private static int GetUrgencyTier(SupplyTarget t)
    {
        if (!t.stack.IsEmpty) return 2;
        // IsBlockedByStock는 공정 단위 판정이라 '빈 파일'에만 적용한다
        // (같은 공정의 다른 파일이 남아 있는데 그 파일까지 최우선이 되지 않도록).
        return t.station.IsBlockedByStock() ? 0 : 1;
    }

    private const float FillEpsilon = 0.0001f;

    private static bool IsBetter(int tier, float fill, float dist, int bestTier, float bestFill, float bestDist)
    {
        if (tier != bestTier) return tier < bestTier;
        if (Mathf.Abs(fill - bestFill) > FillEpsilon) return fill < bestFill;
        return dist < bestDist;
    }

    /// <summary>예약을 해제한다. 예약 주인이 다르면 건드리지 않는다(다른 작업자의 잠금 보호).</summary>
    public void Release(PartStack stack, SupplyWorker worker)
    {
        if (stack == null) return;
        if (reservations.TryGetValue(stack, out SupplyWorker owner) && owner != worker && owner != null) return;
        reservations.Remove(stack);
    }

    /// <summary>현재 보급 작업자 수(파괴된 항목 제외) — UI용.</summary>
    public int WorkerCount
    {
        get
        {
            EnsureBuilt();
            int n = 0;
            for (int i = 0; i < supplyWorkers.Count; i++)
                if (supplyWorkers[i] != null && supplyWorkers[i].isActiveAndEnabled) n++;
            return n;
        }
    }

    /// <summary>복제할 템플릿: hireTemplate → 목록의 첫 번째 작업자. 없으면 null(고용 불가).</summary>
    private SupplyWorker GetHireTemplate()
    {
        if (hireTemplate != null) return hireTemplate;
        EnsureBuilt();
        // 비활성 씬 템플릿은 고용 인원 목록에 넣지 않으며, 복제할 때만 활성화한다.
        SupplyWorker[] all = FindObjectsByType<SupplyWorker>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && !all[i].gameObject.activeInHierarchy) return all[i];
        for (int i = 0; i < supplyWorkers.Count; i++)
            if (supplyWorkers[i] != null) return supplyWorkers[i];
        return null;
    }

    /// <summary>지금 고용할 수 있는지(복제할 템플릿이 있는지).</summary>
    public bool CanHire => GetHireTemplate() != null;

    /// <summary>
    /// 보급 작업자 1명을 고용한다(임시 — 비용·상한 없음). 템플릿을 복제해 창고 적재 지점 주변에 세우고
    /// 목록에 등록한다. 복제본은 Start에서 ToWarehouse로 시작하므로 바로 순회에 합류한다.
    /// 템플릿이 없으면 null.
    /// </summary>
    public SupplyWorker Hire()
    {
        SupplyWorker template = GetHireTemplate();
        if (template == null)
        {
            Debug.LogWarning($"{LOG_TAG} 복제할 보급 작업자(템플릿)가 없어 고용할 수 없습니다.", this);
            return null;
        }

        // 적재 지점 주변 원형 배치: 60°씩 돌고 6명마다 반경을 한 칸 늘린다(겹쳐 생성되지 않게).
        int n = WorkerCount;
        Vector3 offset = Quaternion.Euler(0f, 60f * n, 0f) * Vector3.forward * (hireSpacing * (1 + n / 6));
        Vector3 pos = LoadPoint + offset;
        pos.y = template.transform.position.y;

        GameObject go = Instantiate(template.gameObject, pos, template.transform.rotation);
        go.name = $"{template.gameObject.name.Replace("(Clone)", "").Trim()}_{n + 1}";
        if (!go.activeSelf) go.SetActive(true); // 비활성 템플릿에서 복제한 경우

        SupplyWorker hired = go.GetComponent<SupplyWorker>();
        if (hired != null && prototypeMode) hired.prototypeMode = true;
        supplyWorkers.Add(hired);

        if (verboseLog) Debug.Log($"{LOG_TAG} 보급 작업자 고용 '{go.name}' → 총 {WorkerCount}명", go);
        return hired;
    }

    /// <summary>이 재고 파일이 예약돼 있는지 — 디버그/UI용.</summary>
    public bool IsReserved(PartStack stack)
    {
        return stack != null && reservations.TryGetValue(stack, out SupplyWorker owner) && owner != null;
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmo) return;

        Vector3 load = warehouseLoadPoint != null ? warehouseLoadPoint.position : transform.position;
        Gizmos.color = new Color(0.4f, 1f, 0.4f, 1f);
        Gizmos.DrawWireSphere(load, 0.4f);
        Gizmos.DrawLine(load, load + Vector3.up * 2f);

#if UNITY_EDITOR
        UnityEditor.Handles.color = Gizmos.color;
        UnityEditor.Handles.Label(load + Vector3.up * 2.2f,
            warehouseLoadPoint != null ? "창고 적재 지점" : "창고 적재 지점(미지정 — 폴백 사용)");

        // 예약 현황: 파일 ← 작업자 선
        if (Application.isPlaying)
        {
            Gizmos.color = new Color(1f, 0.5f, 0.1f, 1f);
            foreach (KeyValuePair<PartStack, SupplyWorker> kv in reservations)
            {
                if (kv.Key == null || kv.Value == null) continue;
                Gizmos.DrawLine(kv.Value.transform.position + Vector3.up, GetApproachPoint(kv.Key) + Vector3.up);
            }
        }
#endif
    }
}
