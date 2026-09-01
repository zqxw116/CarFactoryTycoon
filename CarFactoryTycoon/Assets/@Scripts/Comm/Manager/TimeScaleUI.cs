using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 플레이 검증용 게임 배속(Time.timeScale) 조절 UI.
///
/// 배속 스텝 규칙:
/// - 1배 초과 구간: 1단계당 1.0씩 증감 (1 → 2 → 3 …)
/// - 1배 이하 구간: 1단계당 0.1씩 증감 (1.0 → 0.9 → 0.8 …)
/// - 경계는 자연스럽게 이어진다 (0.9에서 + → 1.0, 1.0에서 + → 2.0, 1.5에서 - → 1.0)
///
/// 부동소수 누적 오차를 피하려고 내부값은 <b>0.1 단위 정수(tenths)</b>로만 들고 다니고,
/// 실제 적용 순간에만 /10f로 환산한다. 범위는 0.1x ~ 10x.
/// timeScale을 바꿀 때 fixedDeltaTime도 (시작 시 캐시한 기본값 × 배속)으로 같이 맞춰
/// 물리 스텝이 배속과 어긋나지 않게 한다.
///
/// 인스펙터 참조(버튼/텍스트)는 비어 있어도 동작한다 — 키보드 단축키(+ / = / -)만으로도 쓸 수 있다.
/// InputManager는 Pointer만 보므로 키 입력이 충돌하지 않는다.
/// </summary>
public class TimeScaleUI : MonoBehaviour
{
    [Header("UI 참조 (비어 있어도 됨)")]
    [Tooltip("배속을 올리는 버튼.")]
    public Button plusButton;

    [Tooltip("배속을 내리는 버튼.")]
    public Button minusButton;

    [Tooltip("현재 배속을 표시할 텍스트(TMP). 예: \"1.0x\"")]
    public TextMeshProUGUI scaleText;

    [Header("설정")]
    [Tooltip("시작 배속(x). 0.1 ~ 10 범위로 반올림·클램프된다.")]
    public float startScale = 1f;

    [Tooltip("+ / = / - 키보드 단축키 사용 여부.")]
    public bool enableHotkeys = true;

    private const int MinTenths = 1;    // 0.1x
    private const int MaxTenths = 100;  // 10x

    private int tenths = 10;            // 현재 배속 × 10
    private float defaultFixedDeltaTime;

    /// <summary>현재 배속(x). UI 표시/디버그용.</summary>
    public float CurrentScale => tenths / 10f;

    /// <summary>
    /// 씬에 배치된 TimeScaleUI가 하나도 없으면 플레이 시작 시 자동으로 만들어준다.
    /// 인스펙터로 수동 배치한 오브젝트가 있으면 그걸 우선하고 이 부트스트랩은 아무것도 하지 않는다.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<TimeScaleUI>() != null) return;

        var go = new GameObject("TimeScaleUI (Auto)");
        DontDestroyOnLoad(go);
        go.AddComponent<TimeScaleUI>();
    }

    private void Awake()
    {
        // 다른 스크립트가 이미 timeScale을 건드린 뒤라면 기본값이 오염되므로 Awake에서 캐시한다.
        defaultFixedDeltaTime = Time.fixedDeltaTime / Mathf.Max(0.0001f, Time.timeScale);

        tenths = Mathf.Clamp(Mathf.RoundToInt(startScale * 10f), MinTenths, MaxTenths);

        // 인스펙터 참조가 비어 있으면(씬에 UI를 안 만들어놨으면) 코드로 자동 생성한다.
        if (plusButton == null || minusButton == null || scaleText == null)
            BuildUI();
    }

    /// <summary>
    /// Canvas/버튼/텍스트를 런타임에 코드로 생성한다 (MoneyUI/CashPopup 패턴 참고).
    /// 화면 좌측 상단(MoneyUI는 상단 중앙을 쓰므로 겹치지 않음)에 "- 1.0x +" 형태로 배치한다.
    /// </summary>
    private void BuildUI()
    {
        EnsureEventSystem();

        var canvasGo = new GameObject("TimeScaleCanvas");
        canvasGo.transform.SetParent(transform, false);

        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200; // MoneyUI(100)보다 위에 그려지도록.

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>();

        // 가로 배치 컨테이너: 좌측 상단 앵커.
        var rowGo = new GameObject("Row");
        rowGo.transform.SetParent(canvasGo.transform, false);
        var rowRt = rowGo.AddComponent<RectTransform>();
        rowRt.anchorMin = rowRt.anchorMax = new Vector2(0f, 1f);
        rowRt.pivot = new Vector2(0f, 1f);
        rowRt.anchoredPosition = new Vector2(30f, -30f);
        rowRt.sizeDelta = new Vector2(260f, 60f);

        var layout = rowGo.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 10f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        if (minusButton == null) minusButton = CreateButton(rowGo.transform, "-");

        if (scaleText == null)
        {
            var textGo = new GameObject("ScaleText");
            textGo.transform.SetParent(rowGo.transform, false);
            scaleText = textGo.AddComponent<TextMeshProUGUI>();
            scaleText.text = "1.0x";
            scaleText.fontSize = 32;
            scaleText.alignment = TextAlignmentOptions.Center;
            scaleText.color = Color.white;
            var le = textGo.AddComponent<LayoutElement>();
            le.preferredWidth = 100f;
            le.preferredHeight = 50f;
        }

        if (plusButton == null) plusButton = CreateButton(rowGo.transform, "+");
    }

    /// <summary>단색 배경 + TMP 라벨로 구성된 최소 버튼 하나를 만든다.</summary>
    private static Button CreateButton(Transform parent, string label)
    {
        var go = new GameObject($"Button_{label}");
        go.transform.SetParent(parent, false);

        var image = go.AddComponent<Image>();
        image.color = new Color(0f, 0f, 0f, 0.6f);

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = 50f;
        le.preferredHeight = 50f;

        var button = go.AddComponent<Button>();
        button.targetGraphic = image;

        var textGo = new GameObject("Label");
        textGo.transform.SetParent(go.transform, false);
        var text = textGo.AddComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 32;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        var textRt = text.rectTransform;
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;

        return button;
    }

    /// <summary>EventSystem이 씬에 없으면 새 Input System용 모듈로 자동 생성한다(클릭 입력에 필수).</summary>
    private static void EnsureEventSystem()
    {
        if (FindAnyObjectByType<EventSystem>() != null) return;

        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>();
    }

    private void OnEnable()
    {
        if (plusButton != null) plusButton.onClick.AddListener(StepUp);
        if (minusButton != null) minusButton.onClick.AddListener(StepDown);
        Apply();
    }

    private void OnDisable()
    {
        if (plusButton != null) plusButton.onClick.RemoveListener(StepUp);
        if (minusButton != null) minusButton.onClick.RemoveListener(StepDown);
    }

    private void Update()
    {
        if (!enableHotkeys) return;

        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        // '+'는 Shift+'='이라 equalsKey로 함께 잡힌다. 넘패드도 지원.
        if (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame)
            StepUp();
        else if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame)
            StepDown();
    }

    /// <summary>한 단계 올린다 (1 이하는 0.1씩, 1 초과는 1.0씩).</summary>
    public void StepUp()
    {
        // 1.0 미만이면 0.1 스텝, 그 외에는 다음 정수 배속으로.
        tenths = tenths < 10 ? tenths + 1 : (tenths / 10 + 1) * 10;
        ClampAndApply();
    }

    /// <summary>한 단계 내린다 (1 이하는 0.1씩, 1 초과는 1.0씩).</summary>
    public void StepDown()
    {
        // 1.0 이하면 0.1 스텝, 그 외에는 아래쪽 정수 배속으로(1.5 → 1.0).
        tenths = tenths <= 10 ? tenths - 1 : (tenths - 1) / 10 * 10;
        ClampAndApply();
    }

    /// <summary>배속을 직접 지정한다(0.1 단위로 반올림·클램프).</summary>
    public void SetScale(float scale)
    {
        tenths = Mathf.RoundToInt(scale * 10f);
        ClampAndApply();
    }

    private void ClampAndApply()
    {
        tenths = Mathf.Clamp(tenths, MinTenths, MaxTenths);
        Apply();
    }

    private void Apply()
    {
        float scale = CurrentScale;
        Time.timeScale = scale;
        Time.fixedDeltaTime = defaultFixedDeltaTime * scale;

        if (scaleText != null) scaleText.text = $"{scale:0.0}x";
        if (plusButton != null) plusButton.interactable = tenths < MaxTenths;
        if (minusButton != null) minusButton.interactable = tenths > MinTenths;
    }

    private void OnDestroy()
    {
        // 씬을 벗어나도 배속이 남아 있으면 다음 플레이가 헷갈린다 — 원복.
        Time.timeScale = 1f;
        if (defaultFixedDeltaTime > 0f) Time.fixedDeltaTime = defaultFixedDeltaTime;
    }
}
