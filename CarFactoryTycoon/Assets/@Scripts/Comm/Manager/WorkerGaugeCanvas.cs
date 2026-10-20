using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 작업자 컨디션 게이지 전용 공용 월드 스페이스 캔버스(씬에 1개).
/// 작업자마다 캔버스를 만들지 않고, 게이지들을 이 캔버스 아래에 풀링해 각 작업자 머리 위에 배치한다.
///
/// ★ 배칭 유지 원칙
///   UGUI는 요소가 캔버스 로컬 z=0 평면에 정확히 있어야 같은 머티리얼끼리 배치가 합쳐진다
///   (z≠0이면 요소별로 배치가 갈린다). 그래서
///   - 캔버스 회전 = 카메라 회전 (매 LateUpdate 동기화)
///   - 게이지 위치 = 캔버스 로컬로 변환한 머리 위치에서 z만 0으로 투영
///   직교(orthographic) 카메라는 시선 방향 이동이 화면 위치를 바꾸지 않으므로 화면상 위치가 그대로다.
///   캔버스 평면은 카메라 near 바로 앞에 두므로 게이지는 항상 지오메트리 위에 그려진다.
///
/// ★ 원근(perspective) 카메라 폴백
///   원근에서는 z 투영 시 화면 위치가 어긋나므로 z를 유지한다(정확한 위치, 대신 배치가 갈릴 수 있음).
///
/// 싱글톤 자동 생성 없음 — 씬에 배치된 것만 쓴다. 없으면 게이지 비표시 + 경고 1회.
/// 작업자 쪽 LateUpdate보다 늦게 돌아야 같은 프레임 위치를 쓰므로 실행 순서를 뒤로 미룬다.
/// </summary>
[DefaultExecutionOrder(1000)]
[RequireComponent(typeof(Canvas))]
public class WorkerGaugeCanvas : MonoBehaviour
{
    [Header("게이지")]
    [Tooltip("게이지 프리팹(WorkerGauge 컴포넌트 포함).")]
    [SerializeField] private WorkerGauge gaugePrefab;

    [Tooltip("게이지 크기(m). 가로 x 세로.")]
    [SerializeField] private Vector2 gaugeSize = new Vector2(0.8f, 0.12f);

    [Tooltip("시작 시 미리 만들어 둘 게이지 수.")]
    [SerializeField] private int initialPoolSize = 16;

    [Header("캔버스")]
    [Tooltip("대상 카메라. 비우면 Camera.main.")]
    [SerializeField] private Camera targetCamera;

    [Tooltip("캔버스 1 로컬 단위의 월드 크기(m).")]
    [SerializeField] private float canvasScale = 0.01f;

    [Tooltip("직교 카메라일 때 캔버스 평면을 near 클립면에서 얼마나 앞에 둘지(m).")]
    [SerializeField] private float planeOffsetFromNear = 0.5f;

    private static WorkerGaugeCanvas instance;
    private static bool missingWarned;

    private readonly Queue<WorkerGauge> pool = new Queue<WorkerGauge>();
    private readonly List<WorkerGauge> active = new List<WorkerGauge>();
    private Transform canvasTf;
    private bool initialized;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        instance = null;
        missingWarned = false;
    }

    /// <summary>씬에 배치된 캔버스. 없으면 null(경고 1회). 자동 생성하지 않는다.</summary>
    public static WorkerGaugeCanvas Get()
    {
        if (instance != null) return instance;
        instance = FindFirstObjectByType<WorkerGaugeCanvas>();
        if (instance == null && !missingWarned)
        {
            missingWarned = true;
            Debug.LogWarning("[WorkerGaugeCanvas] 씬에 WorkerGaugeCanvas가 없어 작업자 컨디션 게이지를 표시하지 않는다.");
        }
        return instance;
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Debug.LogWarning("[WorkerGaugeCanvas] 씬에 2개 이상 있다. 첫 번째만 사용한다.", this);
            return;
        }
        instance = this;
        EnsureInit();
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private void EnsureInit()
    {
        if (initialized) return;
        initialized = true;

        canvasTf = transform;

        var canvas = GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        Camera cam = GetCamera();
        if (cam != null) canvas.worldCamera = cam;

        // 클릭 방해 방지: 레이캐스터는 쓰지 않는다
        if (TryGetComponent(out GraphicRaycaster raycaster)) raycaster.enabled = false;

        canvasTf.localScale = Vector3.one * canvasScale;

        if (gaugePrefab == null)
        {
            Debug.LogWarning("[WorkerGaugeCanvas] 게이지 프리팹이 비어 있어 게이지를 표시하지 않는다.", this);
            return;
        }
        for (int i = 0; i < initialPoolSize; i++)
        {
            WorkerGauge g = CreateNew();
            g.gameObject.SetActive(false);
            pool.Enqueue(g);
        }
    }

    private Camera GetCamera() => targetCamera != null ? targetCamera : Camera.main;

    private WorkerGauge CreateNew()
    {
        WorkerGauge g = Instantiate(gaugePrefab, canvasTf, false);
        var rt = (RectTransform)g.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = gaugeSize / Mathf.Max(canvasScale, 1e-5f);
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        g.owner = this;
        return g;
    }

    /// <summary>게이지 1개를 빌린다. 프리팹이 없으면 null.</summary>
    public WorkerGauge Rent()
    {
        EnsureInit();
        if (gaugePrefab == null) return null;

        WorkerGauge g = pool.Count > 0 ? pool.Dequeue() : CreateNew();
        g.activeIndex = active.Count;
        active.Add(g);
        g.SetVisible(true);
        g.gameObject.SetActive(true);
        return g;
    }

    /// <summary>빌린 게이지를 반납한다.</summary>
    public void Return(WorkerGauge g)
    {
        if (g == null || g.owner != this || g.activeIndex < 0) return;

        // 스왑 제거 (순서 무관)
        int idx = g.activeIndex;
        int last = active.Count - 1;
        if (idx != last)
        {
            WorkerGauge moved = active[last];
            active[idx] = moved;
            moved.activeIndex = idx;
        }
        active.RemoveAt(last);

        g.activeIndex = -1;
        g.Follow(null, 0f);
        g.gameObject.SetActive(false);
        pool.Enqueue(g);
    }

    private void LateUpdate()
    {
        if (active.Count == 0) return;
        Camera cam = GetCamera();
        if (cam == null) return;

        Transform camTf = cam.transform;
        bool ortho = cam.orthographic;

        canvasTf.rotation = camTf.rotation;
        if (ortho)
        {
            // near 바로 앞 평면: 모든 지오메트리보다 앞에 그려진다
            canvasTf.position = camTf.position + camTf.forward * (cam.nearClipPlane + planeOffsetFromNear);
        }

        float invScale = 1f / Mathf.Max(canvasScale, 1e-5f);
        for (int i = 0; i < active.Count; i++)
        {
            WorkerGauge g = active[i];
            Transform target = g.Target;
            if (target == null) continue;

            Vector3 lp = canvasTf.InverseTransformPoint(target.position);
            if (ortho) lp.z = 0f;           // 배칭 유지: z=0 평면으로 투영 (직교라 화면 위치 동일)
            // 원근이면 z 유지 (투영하면 위치가 어긋남 — 배칭은 갈릴 수 있음)
            lp.y -= g.DownOffset * invScale; // 캔버스가 카메라 정렬이므로 로컬 y = 화면 위쪽
            g.Rect.localPosition = lp;
        }
    }
}
