using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>ProductionProgression 확장 / 인력 관리 UI. 진행 매니저가 있는 씬에서만 자동 생성한다.</summary>
public sealed class ProductionGrowthUI : MonoBehaviour
{
    private const float RefreshInterval = 0.25f;

    private CanvasGroup canvasGroup;
    private ProductionProgression progression;
    private EconomyManager economy;
    private TextMeshProUGUI statusText;
    private TextMeshProUGUI hireLabel;
    private TextMeshProUGUI unlockLabel;
    private TextMeshProUGUI selectedLabel;
    private TextMeshProUGUI upgradeLabel;
    private TextMeshProUGUI automateLabel;
    private TextMeshProUGUI restLabel;
    private Button hireButton;
    private Button unlockButton;
    private Button previousButton;
    private Button nextButton;
    private Button upgradeButton;
    private Button automateButton;
    private Button restButton;
    private float refreshTimer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void RegisterSceneHook()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void BootstrapCurrentScene()
    {
        EnsureForScene(SceneManager.GetActiveScene());
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => EnsureForScene(scene);

    private static void EnsureForScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded || ProductionProgression.Current == null) return;

        ProductionGrowthUI[] existing = FindObjectsByType<ProductionGrowthUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < existing.Length; i++)
            if (existing[i] != null && existing[i].gameObject.scene == scene) return;

        GameObject go = new GameObject("ProductionGrowthUI (Auto)");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<ProductionGrowthUI>();
    }

    private void Awake()
    {
        progression = ProductionProgression.Current;
        if (progression != null) BuildUI();
    }

    private void Update()
    {
        if (progression == null)
        {
            progression = ProductionProgression.Current;
            if (progression != null) BuildUI();
            else return;
        }

        if (canvasGroup != null)
        {
            canvasGroup.alpha = Mathf.MoveTowards(canvasGroup.alpha, 1f, Time.unscaledDeltaTime * 4f);
            canvasGroup.interactable = canvasGroup.alpha > 0.98f;
            canvasGroup.blocksRaycasts = canvasGroup.interactable;
        }

        refreshTimer -= Time.unscaledDeltaTime;
        if (refreshTimer > 0f) return;
        refreshTimer = RefreshInterval;
        Refresh();
    }

    private void BuildUI()
    {
        EnsureEventSystem(gameObject.scene);

        GameObject canvasObject = new GameObject("ProductionGrowthCanvas", typeof(RectTransform));
        canvasObject.transform.SetParent(transform, false);
        Canvas canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 205;
        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasObject.AddComponent<GraphicRaycaster>();
        canvasGroup = canvasObject.AddComponent<CanvasGroup>();
        canvasGroup.alpha = 0f;

        GameObject panel = new GameObject("Panel", typeof(RectTransform));
        panel.transform.SetParent(canvasObject.transform, false);
        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = panelRect.anchorMax = Vector2.zero;
        panelRect.pivot = Vector2.zero;
        panelRect.anchoredPosition = new Vector2(28f, 28f);
        panelRect.sizeDelta = new Vector2(350f, 300f);
        Image panelImage = panel.AddComponent<Image>();
        panelImage.color = new Color(0.015f, 0.025f, 0.04f, 0.82f);

        TextMeshProUGUI title = CreateText(panel.transform, "공장 확장", 21f, TextAlignmentOptions.MidlineLeft);
        SetRect(title.rectTransform, new Vector2(12f, -8f), new Vector2(326f, 28f));
        statusText = CreateText(panel.transform, string.Empty, 14f, TextAlignmentOptions.TopLeft);
        statusText.enableWordWrapping = true;
        SetRect(statusText.rectTransform, new Vector2(12f, -37f), new Vector2(326f, 42f));

        hireButton = AddButton(panel.transform, "HireButton", new Vector2(12f, -82f), new Vector2(326f, 34f), out hireLabel);
        unlockButton = AddButton(panel.transform, "UnlockButton", new Vector2(12f, -120f), new Vector2(326f, 34f), out unlockLabel);

        previousButton = AddButton(panel.transform, "PreviousProcessButton", new Vector2(12f, -159f), new Vector2(42f, 32f), out _);
        previousButton.GetComponentInChildren<TextMeshProUGUI>().text = "이전";
        nextButton = AddButton(panel.transform, "NextProcessButton", new Vector2(296f, -159f), new Vector2(42f, 32f), out _);
        nextButton.GetComponentInChildren<TextMeshProUGUI>().text = "다음";
        selectedLabel = CreateText(panel.transform, "공정 없음", 14f, TextAlignmentOptions.Center);
        SetRect(selectedLabel.rectTransform, new Vector2(58f, -159f), new Vector2(232f, 32f));

        upgradeButton = AddButton(panel.transform, "WorkerUpgradeButton", new Vector2(12f, -197f), new Vector2(326f, 32f), out upgradeLabel);
        automateButton = AddButton(panel.transform, "AutomationButton", new Vector2(12f, -233f), new Vector2(326f, 32f), out automateLabel);
        restButton = AddButton(panel.transform, "RestButton", new Vector2(12f, -269f), new Vector2(326f, 28f), out restLabel);

        hireButton.onClick.AddListener(OnHireClicked);
        unlockButton.onClick.AddListener(OnUnlockClicked);
        previousButton.onClick.AddListener(() => MoveSelection(-1));
        nextButton.onClick.AddListener(() => MoveSelection(1));
        upgradeButton.onClick.AddListener(OnUpgradeClicked);
        automateButton.onClick.AddListener(OnAutomateClicked);
        restButton.onClick.AddListener(OnRestClicked);

        Refresh();
    }

    private void Refresh()
    {
        if (progression == null) return;
        if (economy == null) economy = EconomyManager.Instance;
        int money = economy != null ? economy.currentMoney : 0;

        if (statusText.text != progression.StatusText) statusText.text = progression.StatusText;
        hireLabel.text = $"다음 고용  {progression.NextHireName} / ${progression.NextHireCost:N0}";
        hireButton.interactable = !progression.IsTransitioning && progression.CanHireNext && money >= progression.NextHireCost;
        if (progression.CompleteVehicleProduction)
            unlockLabel.text = "모든 생산 구간 개방 완료";
        else if (progression.HiredCountInRow < progression.ProcessCountInRow)
            unlockLabel.text = $"다음 줄 / 현재 줄 고용 {progression.HiredCountInRow}/{progression.ProcessCountInRow} 필요";
        else
            unlockLabel.text = $"다음 줄 개방  ${progression.NextRowCost:N0}";
        unlockButton.interactable = !progression.CompleteVehicleProduction && !progression.IsTransitioning && progression.CanUnlockNextRow && money >= progression.NextRowCost;

        int selected = FindSelectedHiredIndex();
        if (selected >= 0 && progression.SelectedProcess != selected) progression.SelectedProcess = selected;
        bool hasSelected = selected >= 0;
        ProductionProgression.ProcessEntry entry = hasSelected ? progression.processes[selected] : null;
        if (hasSelected)
            selectedLabel.text = $"{entry.displayName}  Lv {entry.workerLevel} / {(entry.automated ? "자동" : "수동")}";
        else
            selectedLabel.text = "고용된 공정 없음";

        previousButton.interactable = !progression.IsTransitioning && HasMultipleHiredProcesses();
        nextButton.interactable = !progression.IsTransitioning && HasMultipleHiredProcesses();
        int upgradeCost = hasSelected ? progression.WorkerUpgradeCost(selected) : 0;
        int automationCost = hasSelected ? progression.AutomationCost(selected) : 0;
        bool canUpgrade = hasSelected && !progression.IsTransitioning && progression.CanUpgradeWorker(selected) && money >= upgradeCost;
        bool canAutomate = hasSelected && !progression.IsTransitioning && progression.CanAutomate(selected) && money >= automationCost;
        bool canRest = hasSelected && !progression.IsTransitioning && progression.CanRest(selected);
        upgradeLabel.text = !hasSelected ? "작업자 강화" : entry.automated ? "사람 강화 종료" : $"작업자 강화  ${upgradeCost:N0}";
        automateLabel.text = !hasSelected ? "로봇 구매" : entry.automated ? "로봇 자동화 완료" : !progression.CanAutomate(selected) ? $"로봇 구매  ${automationCost:N0} / 사람 작업 완료 후 구매" : $"로봇 구매  ${automationCost:N0}";
        restLabel.text = hasSelected ? "작업자 휴식" : "작업자 휴식";
        upgradeButton.interactable = canUpgrade;
        automateButton.interactable = canAutomate;
        restButton.interactable = canRest;
    }

    private int FindSelectedHiredIndex()
    {
        ProductionProgression.ProcessEntry[] entries = progression.processes;
        if (entries == null) return -1;
        int current = progression.SelectedProcess;
        if (IsHired(entries, current)) return current;
        for (int i = 0; i < entries.Length; i++)
            if (IsHired(entries, i)) return i;
        return -1;
    }

    private bool HasMultipleHiredProcesses()
    {
        ProductionProgression.ProcessEntry[] entries = progression.processes;
        if (entries == null) return false;
        int count = 0;
        for (int i = 0; i < entries.Length; i++) if (IsHired(entries, i) && ++count > 1) return true;
        return false;
    }

    private static bool IsHired(ProductionProgression.ProcessEntry[] entries, int index) =>
        entries != null && index >= 0 && index < entries.Length && entries[index] != null && entries[index].hired;

    private void MoveSelection(int direction)
    {
        ProductionProgression.ProcessEntry[] entries = progression != null ? progression.processes : null;
        if (entries == null || entries.Length == 0) return;
        int current = FindSelectedHiredIndex();
        if (current < 0) return;
        for (int step = 1; step <= entries.Length; step++)
        {
            int index = (current + direction * step + entries.Length) % entries.Length;
            if (!IsHired(entries, index)) continue;
            progression.SelectedProcess = index;
            Refresh();
            return;
        }
    }

    private void OnHireClicked() { progression?.TryHireNext(); Refresh(); }
    private void OnUnlockClicked() { progression?.TryUnlockNextRow(); Refresh(); }
    private void OnUpgradeClicked() { int i = FindSelectedHiredIndex(); if (i >= 0) progression.TryUpgradeWorker(i); Refresh(); }
    private void OnAutomateClicked() { int i = FindSelectedHiredIndex(); if (i >= 0) progression.TryAutomate(i); Refresh(); }
    private void OnRestClicked() { int i = FindSelectedHiredIndex(); if (i >= 0) progression.TryRest(i); Refresh(); }

    private static Button AddButton(Transform parent, string objectName, Vector2 topLeft, Vector2 size, out TextMeshProUGUI label)
    {
        GameObject buttonObject = new GameObject(objectName, typeof(RectTransform));
        buttonObject.transform.SetParent(parent, false);
        SetRect(buttonObject.GetComponent<RectTransform>(), topLeft, size);
        Image image = buttonObject.AddComponent<Image>();
        image.color = new Color(0.10f, 0.16f, 0.22f, 0.98f);
        Button button = buttonObject.AddComponent<Button>();
        button.targetGraphic = image;
        ColorBlock colors = button.colors;
        colors.normalColor = new Color(0.10f, 0.16f, 0.22f, 0.98f);
        colors.highlightedColor = new Color(0.16f, 0.28f, 0.36f, 1f);
        colors.pressedColor = new Color(0.05f, 0.10f, 0.15f, 1f);
        colors.disabledColor = new Color(0.22f, 0.24f, 0.26f, 0.72f);
        button.colors = colors;
        label = CreateText(buttonObject.transform, string.Empty, 15f, TextAlignmentOptions.Center);
        label.enableAutoSizing = true;
        label.fontSizeMin = 12f;
        label.fontSizeMax = 16f;
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = new Vector2(5f, 2f);
        label.rectTransform.offsetMax = new Vector2(-5f, -2f);
        return button;
    }

    private static TextMeshProUGUI CreateText(Transform parent, string content, float fontSize, TextAlignmentOptions alignment)
    {
        GameObject textObject = new GameObject("Text", typeof(RectTransform));
        textObject.transform.SetParent(parent, false);
        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.font = TMP_Settings.defaultFontAsset;
        text.text = content;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = Color.white;
        text.raycastTarget = false;
        return text;
    }

    private static void SetRect(RectTransform rect, Vector2 topLeftOffset, Vector2 size)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = topLeftOffset;
        rect.sizeDelta = size;
    }

    private static void EnsureEventSystem(Scene scene)
    {
        EventSystem[] systems = FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        EventSystem fallback = null;
        for (int i = 0; i < systems.Length; i++)
        {
            EventSystem eventSystem = systems[i];
            if (eventSystem == null) continue;
            if (eventSystem.gameObject.scene == scene) { EnsureInputModule(eventSystem); return; }
            if (fallback == null) fallback = eventSystem;
        }
        if (fallback != null) { EnsureInputModule(fallback); return; }
        GameObject eventSystemObject = new GameObject("EventSystem");
        SceneManager.MoveGameObjectToScene(eventSystemObject, scene);
        EnsureInputModule(eventSystemObject.AddComponent<EventSystem>());
    }

    private static void EnsureInputModule(EventSystem eventSystem)
    {
        if (eventSystem.GetComponent<InputSystemUIInputModule>() == null)
            eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
    }
}
