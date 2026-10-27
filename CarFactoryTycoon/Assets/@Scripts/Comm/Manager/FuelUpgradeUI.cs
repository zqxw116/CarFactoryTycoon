using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>주유소 자동화·속도 업그레이드 구매 UI. 주유소가 있는 씬에서만 자동 생성한다.</summary>
public sealed class FuelUpgradeUI : MonoBehaviour
{
    private const float RefreshInterval = 0.25f;

    private FuelStation station;
    private EconomyManager economy;
    private CanvasGroup canvasGroup;
    private Button automationButton;
    private Button speedButton;
    private Button restButton;
    private TextMeshProUGUI statusText;
    private TextMeshProUGUI automationLabel;
    private TextMeshProUGUI speedLabel;
    private float refreshTimer;

    private bool hasShownState;
    private bool shownHasStation;
    private bool shownAutomated;
    private int shownMoney = int.MinValue;
    private int shownAutomationCost = int.MinValue;
    private int shownSpeedLevel = int.MinValue;
    private int shownMaxSpeedLevel = int.MinValue;
    private int shownSpeedCost = int.MinValue;

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

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureForScene(scene);
    }

    private static void EnsureForScene(Scene scene)
    {
        if (PrototypeLineController.Current != null || !scene.IsValid() || !scene.isLoaded) return;

        FuelStation[] stations = FindObjectsByType<FuelStation>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        bool hasStation = false;
        for (int i = 0; i < stations.Length; i++)
        {
            if (stations[i] != null && stations[i].gameObject.scene == scene)
            {
                hasStation = true;
                break;
            }
        }
        if (!hasStation) return;

        FuelUpgradeUI[] existing = FindObjectsByType<FuelUpgradeUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < existing.Length; i++)
            if (existing[i] != null && existing[i].gameObject.scene == scene) return;

        GameObject go = new GameObject("FuelUpgradeUI (Auto)");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<FuelUpgradeUI>();
    }

    private void Awake()
    {
        station = FindStationInOwnScene();
        BuildUI();
        Refresh(true);
    }

    private void OnEnable()
    {
        if (automationButton != null) automationButton.onClick.AddListener(OnAutomationClicked);
        if (speedButton != null) speedButton.onClick.AddListener(OnSpeedClicked);
        if (restButton != null) restButton.onClick.AddListener(OnRestClicked);
    }

    private void OnDisable()
    {
        RemoveButtonListeners();
    }

    private void OnDestroy()
    {
        RemoveButtonListeners();
    }

    private void RemoveButtonListeners()
    {
        if (automationButton != null) automationButton.onClick.RemoveListener(OnAutomationClicked);
        if (speedButton != null) speedButton.onClick.RemoveListener(OnSpeedClicked);
        if (restButton != null) restButton.onClick.RemoveListener(OnRestClicked);
    }

    private void Update()
    {
        bool shouldShow = station != null && station.isActiveAndEnabled;
        if (canvasGroup != null)
        {
            canvasGroup.alpha = Mathf.MoveTowards(canvasGroup.alpha, shouldShow ? 1f : 0f, Time.unscaledDeltaTime * 4f);
            canvasGroup.interactable = shouldShow && canvasGroup.alpha > 0.98f;
            canvasGroup.blocksRaycasts = canvasGroup.interactable;
        }

        refreshTimer -= Time.unscaledDeltaTime;
        if (refreshTimer > 0f) return;
        refreshTimer = RefreshInterval;
        Refresh(false);
    }

    private FuelStation FindStationInOwnScene()
    {
        FuelStation[] stations = FindObjectsByType<FuelStation>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        FuelStation inactiveFallback = null;
        for (int i = 0; i < stations.Length; i++)
        {
            FuelStation candidate = stations[i];
            if (candidate == null || candidate.gameObject.scene != gameObject.scene) continue;
            if (candidate.isActiveAndEnabled) return candidate;
            if (inactiveFallback == null) inactiveFallback = candidate;
        }
        return inactiveFallback;
    }

    private void OnAutomationClicked()
    {
        if (station == null || !station.isActiveAndEnabled) return;
        station.TryPurchaseAutomation();
        Refresh(true);
    }

    private void OnSpeedClicked()
    {
        if (station == null || !station.isActiveAndEnabled) return;
        station.TryUpgradeSpeed();
        Refresh(true);
    }

    private void OnRestClicked() { if(station!=null) station.TryRestAttendant(); Refresh(true); }

    private void Refresh(bool force)
    {
        bool hasStation = station != null && station.isActiveAndEnabled;
        if (economy == null) economy = EconomyManager.Instance;
        int money = economy != null ? economy.currentMoney : 0;
        bool automated = hasStation && station.IsAutomated;
        int automationCost = hasStation ? station.AutomationCost : 0;
        int speedLevel = hasStation ? station.SpeedLevel : 0;
        int maxSpeedLevel = hasStation ? station.MaxSpeedLevel : 0;
        int speedCost = hasStation ? station.SpeedUpgradeCost : 0;

        string status=hasStation?station.StatusText:"주유소 비활성";
        if(statusText!=null && statusText.text!=status)statusText.text=status;
        if(restButton!=null)restButton.interactable=hasStation && station.CanRest;

        if (!force && hasShownState
            && shownHasStation == hasStation
            && shownAutomated == automated
            && shownMoney == money
            && shownAutomationCost == automationCost
            && shownSpeedLevel == speedLevel
            && shownMaxSpeedLevel == maxSpeedLevel
            && shownSpeedCost == speedCost)
            return;

        hasShownState = true;
        shownHasStation = hasStation;
        shownAutomated = automated;
        shownMoney = money;
        shownAutomationCost = automationCost;
        shownSpeedLevel = speedLevel;
        shownMaxSpeedLevel = maxSpeedLevel;
        shownSpeedCost = speedCost;

        if (!hasStation)
        {
            automationLabel.text = "주유소 비활성";
            speedLabel.text = "주유소 비활성";
            automationButton.interactable = false;
            speedButton.interactable = false;
            return;
        }

        bool canBuyAutomation = !automated && money >= automationCost;
        automationButton.interactable = canBuyAutomation;
        if (automated)
            automationLabel.text = "자동화 완료";
        else if (!canBuyAutomation)
            automationLabel.text = $"자동화 / 자금 부족 (${automationCost:N0})";
        else
            automationLabel.text = $"주유 자동화 구매  ${automationCost:N0}";

        bool atMaxSpeed = speedLevel >= maxSpeedLevel;
        bool canBuySpeed = !atMaxSpeed && money >= speedCost;
        speedButton.interactable = canBuySpeed;
        if (atMaxSpeed)
            speedLabel.text = $"속도 최대  Lv {speedLevel}/{maxSpeedLevel}";
        else if (!canBuySpeed)
            speedLabel.text = $"속도 Lv {speedLevel}→{speedLevel + 1} / 자금 부족 (${speedCost:N0})";
        else
            speedLabel.text = $"속도 업그레이드  Lv {speedLevel}→{speedLevel + 1}  ${speedCost:N0}";
    }

    private void BuildUI()
    {
        EnsureEventSystem(gameObject.scene);

        GameObject canvasObject = new GameObject("FuelUpgradeCanvas", typeof(RectTransform));
        canvasObject.transform.SetParent(transform, false);

        Canvas canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 210;

        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasObject.AddComponent<GraphicRaycaster>();
        canvasGroup = canvasObject.AddComponent<CanvasGroup>();
        canvasGroup.alpha = 0f;

        GameObject panel = new GameObject("Panel", typeof(RectTransform));
        panel.transform.SetParent(canvasObject.transform, false);
        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = panelRect.anchorMax = new Vector2(1f, 0f);
        panelRect.pivot = new Vector2(1f, 0f);
        panelRect.anchoredPosition = new Vector2(-28f, 28f);
        panelRect.sizeDelta = new Vector2(320f, 222f);
        Image panelImage = panel.AddComponent<Image>();
        panelImage.color = new Color(0.015f, 0.025f, 0.04f, 0.78f);

        TextMeshProUGUI title = CreateText(panel.transform, "주유 업그레이드", 21f, TextAlignmentOptions.MidlineLeft);
        SetRect(title.rectTransform, new Vector2(12f, -8f), new Vector2(296f, 28f));

        statusText=CreateText(panel.transform,string.Empty,16f,TextAlignmentOptions.MidlineLeft);
        SetRect(statusText.rectTransform,new Vector2(12f,-36f),new Vector2(296f,24f));
        restButton=CreateButton(panel.transform,"RestButton");
        SetRect(restButton.GetComponent<RectTransform>(),new Vector2(12f,-170f),new Vector2(296f,34f));
        CreateButtonLabel(restButton.transform).text="작업자 선제 휴식";

        automationButton = CreateButton(panel.transform, "AutomationButton");
        SetRect(automationButton.GetComponent<RectTransform>(), new Vector2(12f, -66f), new Vector2(296f, 44f));
        automationLabel = CreateButtonLabel(automationButton.transform);

        speedButton = CreateButton(panel.transform, "SpeedButton");
        SetRect(speedButton.GetComponent<RectTransform>(), new Vector2(12f, -117f), new Vector2(296f, 44f));
        speedLabel = CreateButtonLabel(speedButton.transform);

        TMP_FontAsset defaultFont = TMP_Settings.defaultFontAsset;
        title.font = defaultFont;
        automationLabel.font = defaultFont;
        speedLabel.font = defaultFont;
    }

    private static Button CreateButton(Transform parent, string objectName)
    {
        GameObject buttonObject = new GameObject(objectName, typeof(RectTransform));
        buttonObject.transform.SetParent(parent, false);
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
        return button;
    }

    private static TextMeshProUGUI CreateButtonLabel(Transform parent)
    {
        TextMeshProUGUI label = CreateText(parent, string.Empty, 18f, TextAlignmentOptions.Center);
        label.enableAutoSizing = true;
        label.fontSizeMin = 14f;
        label.fontSizeMax = 18f;
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = new Vector2(8f, 3f);
        label.rectTransform.offsetMax = new Vector2(-8f, -3f);
        return label;
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
            if (eventSystem.gameObject.scene == scene)
            {
                EnsureInputModule(eventSystem);
                return;
            }
            if (fallback == null) fallback = eventSystem;
        }
        if (fallback != null)
        {
            EnsureInputModule(fallback);
            return;
        }

        GameObject eventSystemObject = new GameObject("EventSystem");
        SceneManager.MoveGameObjectToScene(eventSystemObject, scene);
        EventSystem created = eventSystemObject.AddComponent<EventSystem>();
        EnsureInputModule(created);
    }

    private static void EnsureInputModule(EventSystem eventSystem)
    {
        if (eventSystem.GetComponent<InputSystemUIInputModule>() == null)
            eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
    }
}
