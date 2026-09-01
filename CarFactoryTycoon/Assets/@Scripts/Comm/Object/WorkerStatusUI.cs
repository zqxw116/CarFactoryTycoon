using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 작업자 머리 위 상태 표시. 관리형 게임에서 "한눈에 병목 파악"이 핵심 UX이므로,
/// 유저가 개입해야 할 곳만 눈에 띄게 만든다.
///
/// 표시 원칙:
/// - <b>정상 상태(대기·이동)는 아무것도 표시하지 않는다.</b> 표시가 보이는 곳 = 유저가 볼 곳.
/// - 상태는 3개만: <b>작업중 / 휴식중 / 부품없음</b>. (불량·피로 아이콘은 두지 않는다 —
///   피로는 컨디션 게이지의 색 변화가 대신한다)
/// - 컨디션은 게이지로 보여주고, 낮아질수록 색이 초록→노랑→빨강으로 변한다.
///
/// 게이지는 uGUI <see cref="Image"/>의 Filled 타입(fillAmount)으로 그린다.
/// 인스펙터에서 <see cref="conditionFill"/>에 Fill 이미지를 꽂아야 게이지가 보인다.
/// (게이지 이미지들은 Raycast Target을 꺼서 작업자 클릭을 가리지 않게 할 것)
/// </summary>
[RequireComponent(typeof(Worker))]
public class WorkerStatusUI : MonoBehaviour
{
    [Header("배치")]
    [Tooltip("작업자 기준 표시 높이(m).")]
    public float height = 2.2f;

    [Tooltip("텍스트 크기(3D TMP fontSize).")]
    public float fontSize = 4f;

    [Tooltip("전체 스케일.")]
    public float scale = 0.5f;

    [Header("게이지")]
    [Tooltip("컨디션 게이지 Fill 이미지. Image Type = Filled 로 설정할 것.")]
    [SerializeField] private Image conditionFill;

    [Tooltip("게이지 배경 이미지(선택). 게이지 표시/숨김에 함께 쓰인다.")]
    [SerializeField] private Image conditionBg;

    [Tooltip("퍼센트 숫자 텍스트(선택). 비워두면 표시하지 않는다.")]
    [SerializeField] private TMP_Text percentText;

    [Tooltip("컨디션이 100%일 때도 게이지를 보여줄지. 끄면 컨디션이 깎였을 때만 보인다.")]
    public bool alwaysShowGauge = false;

    [Header("색상")]
    public Color goodColor = new Color(0.35f, 1f, 0.45f, 1f);
    public Color warnColor = new Color(1f, 0.9f, 0.3f, 1f);
    public Color badColor = new Color(1f, 0.35f, 0.3f, 1f);
    [Tooltip("부품 없음(라인 정지) 강조색.")]
    public Color blockedColor = new Color(1f, 0.55f, 0.1f, 1f);

    [Tooltip("이 값(%)보다 높으면 초록.")]
    public float goodThreshold = 50f;
    [Tooltip("이 값(%)보다 높으면 노랑, 이하면 빨강.")]
    public float warnThreshold = 20f;

    private Worker worker;
    private TextMeshPro label;
    private Transform labelTf;

    // 갱신을 줄이기 위한 캐시 (매 프레임 string 만들면 GC가 계속 발생한다)
    private string lastStateText = null;
    private int lastPercent = -1;
    private bool lastVisible = true;
    private bool lastGaugeVisible = true;
    private bool fillWarned = false;   // fill 미할당 경고는 한 번만

    private void Awake()
    {
        worker = GetComponent<Worker>();
        BuildLabel();
    }

    private void BuildLabel()
    {
        var go = new GameObject("StatusLabel");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.up * height;

        label = go.AddComponent<TextMeshPro>();
        label.fontSize = fontSize;
        label.alignment = TextAlignmentOptions.Center;
        label.fontStyle = FontStyles.Bold;
        label.rectTransform.sizeDelta = new Vector2(10f, 4f); // 두 줄 + 줄바꿈 방지
        go.transform.localScale = Vector3.one * scale;

        labelTf = go.transform;
    }

    private void LateUpdate()
    {
        if (worker == null || label == null) return;

        string stateText = GetStateText();
        bool showGauge = alwaysShowGauge || worker.condition < 100f;
        bool visible = !string.IsNullOrEmpty(stateText) || showGauge;

        if (visible != lastVisible)
        {
            label.gameObject.SetActive(visible);
            lastVisible = visible;
        }

        if (showGauge != lastGaugeVisible)
        {
            SetGaugeVisible(showGauge);
            lastGaugeVisible = showGauge;
        }

        if (!visible) return;

        // 내용이 바뀔 때만 문자열을 다시 만든다
        if (stateText != lastStateText)
        {
            lastStateText = stateText;
            label.text = stateText ?? string.Empty;
        }

        UpdateGauge(showGauge);

        label.color = GetColor();

        // 카메라 빌보드
        Camera cam = Camera.main;
        if (cam != null) labelTf.rotation = cam.transform.rotation;
    }

    /// <summary>표시할 상태 문자열. 정상(대기·이동)이면 빈 문자열.</summary>
    private string GetStateText()
    {
        if (worker.IsResting) return "휴식중";

        switch (worker.currentState)
        {
            case Worker.WorkerState.Working:
                return "작업중";                 // 클릭하면 체결을 도울 수 있다는 힌트도 된다

            case Worker.WorkerState.Idle:
                return worker.stockBlocked ? "부품없음" : null;

            default:
                return null;                     // 출근·이동·퇴근은 표시하지 않는다
        }
    }

    private void SetGaugeVisible(bool on)
    {
        if (conditionBg != null) conditionBg.enabled = on;
        if (conditionFill != null) conditionFill.enabled = on;
        if (percentText != null) percentText.enabled = on;
    }

    private void UpdateGauge(bool showGauge)
    {
        if (conditionFill == null)
        {
            if (!fillWarned)
            {
                fillWarned = true;
                Debug.LogWarning($"[WorkerStatusUI] {name}: conditionFill(Image)이 비어 있어 컨디션 게이지를 표시하지 않는다.", this);
            }
            return;
        }
        if (!showGauge) return;

        conditionFill.fillAmount = Mathf.Clamp01(worker.ConditionFill);
        conditionFill.color = GetColor();

        if (percentText != null)
        {
            int percent = Mathf.RoundToInt(worker.condition);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                percentText.SetText("{0}%", percent);
            }
        }
    }

    private Color GetColor()
    {
        if (worker.stockBlocked && worker.currentState == Worker.WorkerState.Idle) return blockedColor;
        if (worker.condition > goodThreshold) return goodColor;
        if (worker.condition > warnThreshold) return warnColor;
        return badColor;
    }
}
