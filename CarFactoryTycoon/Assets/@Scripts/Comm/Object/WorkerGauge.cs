using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 작업자 컨디션 게이지 1개(배경 + Fill 이미지). <see cref="WorkerGaugeCanvas"/>가 풀링해 빌려준다.
/// 위치는 캔버스가 매 LateUpdate에 추적 대상(<see cref="Target"/>) 기준으로 갱신한다.
///
/// 프리팹 조건:
/// - 배경/채움 모두 같은 흰색 스프라이트 1장 사용 (텍스처가 다르면 배치가 갈린다)
/// - Fill: Image Type = Filled / Horizontal / Origin Left (sprite가 없으면 Filled가 동작하지 않는다)
/// - 모든 Image의 Raycast Target 끔 (작업자 클릭 방해 방지)
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class WorkerGauge : MonoBehaviour
{
    [SerializeField] private Image fill;
    [SerializeField] private Image background;

    public Image Fill => fill;
    public Image Background => background;
    public RectTransform Rect => rect != null ? rect : (rect = (RectTransform)transform);
    private RectTransform rect;

    /// <summary>추적할 월드 기준점(예: 작업자 상태 라벨).</summary>
    public Transform Target { get; private set; }

    /// <summary>기준점에서 화면 아래쪽으로 내릴 거리(m).</summary>
    public float DownOffset { get; private set; }

    /// <summary>풀 내부 인덱스(스왑 제거용). 캔버스만 사용.</summary>
    internal int activeIndex = -1;
    internal WorkerGaugeCanvas owner;

    public void Follow(Transform target, float downOffset)
    {
        Target = target;
        DownOffset = downOffset;
    }

    public void SetFill(float amount)
    {
        if (fill != null) fill.fillAmount = Mathf.Clamp01(amount);
    }

    public void SetColor(Color color)
    {
        if (fill != null) fill.color = color;
    }

    public void SetVisible(bool on)
    {
        if (background != null) background.enabled = on;
        if (fill != null) fill.enabled = on;
    }
}
