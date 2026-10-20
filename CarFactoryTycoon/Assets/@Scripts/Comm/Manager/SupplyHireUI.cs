using TMPro;
using UnityEngine;
using UnityEngine.InputSystem.UI;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 보급 작업자 임시 고용 UI (9-15 ⑷). "보급 작업자 +1" 버튼 1개 + 현재 인원 표시.
/// 버튼을 누르면 SupplyManager.Hire()로 1명을 즉시 추가한다.
///
/// 임시 UI다: 비용 차감 · 인원 상한 · 업그레이드는 미구현(상점 UI 작업 시 추가 예정).
///
/// TimeScaleUI와 같은 방식 — 씬에 수동 배치한 것이 없으면 부트스트랩이 자동 생성하고,
/// 인스펙터 참조가 비어 있으면 Canvas/버튼/텍스트를 코드로 만든다.
/// 위치는 좌측 상단, 배속 UI(좌측 상단 y -30 ~ -90) 바로 아래. MoneyUI(상단 중앙)와 겹치지 않는다.
/// </summary>
public class SupplyHireUI : MonoBehaviour
{
    [Header("UI 참조 (비어 있어도 됨)")]
    [Tooltip("보급 작업자를 1명 추가하는 버튼.")]
    public Button hireButton;

    [Tooltip("현재 인원(또는 고용 불가 안내)을 표시할 텍스트(TMP).")]
    public TextMeshProUGUI countText;

    [Tooltip("인원 표시를 다시 확인하는 간격(초). 작업자가 외부에서 파괴·비활성화될 수 있어 주기적으로 본다.")]
    public float refreshInterval = 0.5f;

    private SupplyManager manager;
    private float refreshTimer = 0f;
    private int shownCount = -1;
    private bool shownCanHire = false;
    private bool shownHasManager = true;

    /// <summary>
    /// 씬에 SupplyManager가 있고 SupplyHireUI가 없으면 플레이 시작 시 자동으로 만든다.
    /// 보급 시스템이 없는 씬(TestPartsScene 등)에는 만들지 않는다.
    /// 씬 소속 시스템이라 TimeScaleUI와 달리 DontDestroyOnLoad로 남기지 않는다.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<SupplyHireUI>() != null) return;
        if (FindAnyObjectByType<SupplyManager>() == null) return;

        var go = new GameObject("SupplyHireUI (Auto)");
        go.AddComponent<SupplyHireUI>();
    }

    private void Awake()
    {
        if (hireButton == null || countText == null)
            BuildUI();
    }

    private void Start()
    {
        manager = SupplyManager.Instance;
        Refresh(true);
    }

    private void OnEnable()
    {
        if (hireButton != null) hireButton.onClick.AddListener(OnHireClicked);
    }

    private void OnDisable()
    {
        if (hireButton != null) hireButton.onClick.RemoveListener(OnHireClicked);
    }

    private void Update()
    {
        refreshTimer -= Time.unscaledDeltaTime; // 배속 0.1x에서도 표시가 늦지 않게 실시간 기준
        if (refreshTimer > 0f) return;
        refreshTimer = refreshInterval;
        Refresh(false);
    }

    private void OnHireClicked()
    {
        if (manager == null) manager = SupplyManager.Instance;
        if (manager == null) return;

        // TODO(상점 UI): 비용 차감(EconomyManager.TrySpend) · 인원 상한 판정은 여기서 Hire 전에 넣는다.
        manager.Hire();
        Refresh(true);
    }

    /// <summary>인원·고용 가능 여부가 바뀌었을 때만 텍스트를 갱신한다(매 갱신 문자열 생성 방지).</summary>
    private void Refresh(bool force)
    {
        if (manager == null) manager = SupplyManager.Instance;

        bool hasManager = manager != null;
        int count = hasManager ? manager.WorkerCount : 0;
        bool canHire = hasManager && manager.CanHire;

        if (!force && count == shownCount && canHire == shownCanHire && hasManager == shownHasManager) return;
        shownCount = count;
        shownCanHire = canHire;
        shownHasManager = hasManager;

        if (hireButton != null) hireButton.interactable = canHire;
        if (countText == null) return;

        if (!hasManager) countText.text = "보급 작업자: SupplyManager 없음";
        else if (!canHire) countText.text = $"보급 작업자: {count}명 (복제할 작업자 없음 — 씬에 1명 배치 필요)";
        else countText.text = $"보급 작업자: {count}명";
    }

    /// <summary>Canvas/버튼/텍스트를 코드로 생성한다(TimeScaleUI.BuildUI와 같은 구성).</summary>
    private void BuildUI()
    {
        EnsureEventSystem();

        var canvasGo = new GameObject("SupplyHireCanvas");
        canvasGo.transform.SetParent(transform, false);

        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 190; // MoneyUI(100)보다 위, TimeScaleUI(200)와 겹치지 않는 값.

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>();

        // 가로 배치 컨테이너: 좌측 상단, 배속 UI(높이 60, y -30) 아래.
        var rowGo = new GameObject("Row");
        rowGo.transform.SetParent(canvasGo.transform, false);
        var rowRt = rowGo.AddComponent<RectTransform>();
        rowRt.anchorMin = rowRt.anchorMax = new Vector2(0f, 1f);
        rowRt.pivot = new Vector2(0f, 1f);
        rowRt.anchoredPosition = new Vector2(30f, -100f);
        rowRt.sizeDelta = new Vector2(800f, 50f);

        var layout = rowGo.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 12f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        if (hireButton == null) hireButton = CreateButton(rowGo.transform, "보급 작업자 +1", 200f);

        if (countText == null)
        {
            var textGo = new GameObject("CountText");
            textGo.transform.SetParent(rowGo.transform, false);
            countText = textGo.AddComponent<TextMeshProUGUI>();
            countText.text = "보급 작업자: -";
            countText.fontSize = 26;
            countText.alignment = TextAlignmentOptions.MidlineLeft;
            countText.color = Color.white;
            var le = textGo.AddComponent<LayoutElement>();
            le.preferredWidth = 560f;
            le.preferredHeight = 50f;
        }
    }

    /// <summary>단색 배경 + TMP 라벨 버튼 (TimeScaleUI.CreateButton과 같은 구성, 폭만 지정).</summary>
    private static Button CreateButton(Transform parent, string label, float width)
    {
        var go = new GameObject($"Button_{label}");
        go.transform.SetParent(parent, false);

        var image = go.AddComponent<Image>();
        image.color = new Color(0f, 0f, 0f, 0.6f);

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = width;
        le.preferredHeight = 50f;

        var button = go.AddComponent<Button>();
        button.targetGraphic = image;

        var textGo = new GameObject("Label");
        textGo.transform.SetParent(go.transform, false);
        var text = textGo.AddComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 26;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        var textRt = text.rectTransform;
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;

        return button;
    }

    /// <summary>EventSystem이 씬에 없으면 새 Input System용 모듈로 자동 생성한다(TimeScaleUI와 동일).</summary>
    private static void EnsureEventSystem()
    {
        if (FindAnyObjectByType<EventSystem>() != null) return;

        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>();
    }
}
