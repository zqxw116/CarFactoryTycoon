using DG.Tweening;
using UnityEngine;

/// <summary>
/// 차량 아래 대차(Dolly) 연출. 출고 공정(DepartureStation)이 차량을 넘겨받는 순간
/// 대차가 '뿅' 하고 사라진다: 살짝 부풀었다(펀치) → 0으로 쏙 줄어듦(InBack) → 비활성.
///
/// - 차량 프리팹의 Dolly 오브젝트에 붙인다. CarController가 Awake에서 찾아 두고
///   SetPath(스폰/재사용) 때 Restore()로 원복한다.
/// - 대차 오브젝트 자신이 꺼진 상태라 OnEnable로는 원복할 수 없다 → 원복은 차량 쪽(SetPath)에서 호출.
/// - 트윈 도중 풀 반환(차량 비활성)되면 OnDisable에서 트윈을 끊는다.
/// </summary>
public class CarDolly : MonoBehaviour
{
    [Tooltip("사라지기 전 살짝 부푸는 펀치 세기 (스케일 배율 증가량)")]
    public float punchAmount = 0.15f;
    [Tooltip("펀치 시간(초)")]
    public float punchDuration = 0.12f;
    [Tooltip("0으로 줄어드는 시간(초)")]
    public float shrinkDuration = 0.25f;

    private Vector3 _baseScale = Vector3.one;
    private Sequence _seq;

    private void Awake() { _baseScale = transform.localScale; }

    // 풀 반환 등으로 꺼지면 진행 중인 트윈을 끊는다 (재사용 시 Restore가 스케일을 원복)
    private void OnDisable() { _seq?.Kill(); _seq = null; }

    /// <summary>대차를 '뿅' 사라지게 한다. 끝나면 비활성.</summary>
    public void Vanish()
    {
        if (!gameObject.activeInHierarchy) return;

        _seq?.Kill();
        transform.localScale = _baseScale;

        _seq = DOTween.Sequence();
        _seq.Append(transform.DOScale(_baseScale * (1f + punchAmount), punchDuration).SetEase(Ease.OutQuad));
        _seq.Append(transform.DOScale(Vector3.zero, shrinkDuration).SetEase(Ease.InBack));
        _seq.OnComplete(() => { _seq = null; gameObject.SetActive(false); });
    }

    /// <summary>재사용 시 원복: 트윈 정리 + 스케일 1 + 활성.</summary>
    public void Restore()
    {
        _seq?.Kill();
        _seq = null;
        transform.localScale = _baseScale;
        if (!gameObject.activeSelf) gameObject.SetActive(true);
    }
}
