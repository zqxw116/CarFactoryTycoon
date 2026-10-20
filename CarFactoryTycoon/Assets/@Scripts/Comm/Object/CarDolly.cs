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
/// - 방수 공정(WaterTestStation): 크레인이 차를 들면 DetachInPlace()로 대차만 벨트 위에 남기고(부모 분리),
///   Vanish()로 사라진 뒤, 방출 지점에서 AppearAt()으로 '뿅' 나타나 Reattach()/Restore()로 차량에 다시 붙는다.
///   부모 분리·재부착은 이 클래스 안에서만 처리한다 (원래 부모·로컬 자세는 Awake에서 기억).
/// </summary>
public class CarDolly : MonoBehaviour
{
    [Tooltip("사라지기 전 살짝 부푸는 펀치 세기 (스케일 배율 증가량)")]
    public float punchAmount = 0.15f;
    [Tooltip("펀치 시간(초)")]
    public float punchDuration = 0.12f;
    [Tooltip("0으로 줄어드는 시간(초)")]
    public float shrinkDuration = 0.25f;

    [Header("나타나기 (Appear)")]
    [Tooltip("나타날 때 넘치는 스케일 배율 증가량 (0 → 1+이 값 → 1)")]
    public float appearOvershoot = 0.15f;
    [Tooltip("0에서 최대 크기까지 커지는 시간(초)")]
    public float appearGrowDuration = 0.2f;
    [Tooltip("최대 크기에서 원래 크기로 돌아오는 시간(초)")]
    public float appearSettleDuration = 0.12f;

    private Vector3 _baseScale = Vector3.one;
    private Sequence _seq;

    // 원래 자리(차량 자식) — 분리 후 재부착 기준
    private Transform  _homeParent;
    private Vector3    _homeLocalPos;
    private Quaternion _homeLocalRot;
    // 분리 상태(월드 루트)에서 쓰는 스케일 (분리 시점의 월드 스케일)
    private Vector3    _detachedScale = Vector3.one;
    private bool       _detached;

    /// <summary>차량에서 떨어져 월드에 남아 있는 상태인지.</summary>
    public bool IsDetached => _detached;

    // 현재 부모 기준 '원래 크기' (분리 중이면 월드 스케일, 아니면 프리팹 로컬 스케일)
    private Vector3 RestScale => _detached ? _detachedScale : _baseScale;

    private void Awake()
    {
        _baseScale    = transform.localScale;
        _homeParent   = transform.parent;
        _homeLocalPos = transform.localPosition;
        _homeLocalRot = transform.localRotation;
    }

    // 풀 반환 등으로 꺼지면 진행 중인 트윈을 끊는다 (재사용 시 Restore가 스케일을 원복)
    private void OnDisable() { _seq?.Kill(); _seq = null; }

    /// <summary>대차를 '뿅' 사라지게 한다. 끝나면 비활성.</summary>
    public void Vanish()
    {
        if (!gameObject.activeInHierarchy) return;

        _seq?.Kill();
        Vector3 rest = RestScale;
        transform.localScale = rest;

        _seq = DOTween.Sequence();
        _seq.Append(transform.DOScale(rest * (1f + punchAmount), punchDuration).SetEase(Ease.OutQuad));
        _seq.Append(transform.DOScale(Vector3.zero, shrinkDuration).SetEase(Ease.InBack));
        _seq.OnComplete(() => { _seq = null; gameObject.SetActive(false); });
    }

    /// <summary>
    /// 지금 월드 위치·회전 그대로 차량에서 떼어 낸다 (월드 루트로 부모 분리).
    /// 차량이 들려 올라가도 대차는 벨트 위에 남는다. 이미 분리됐거나 원래 부모가 없으면 무시.
    /// </summary>
    public void DetachInPlace()
    {
        if (_detached || _homeParent == null) return;

        _seq?.Kill();
        _seq = null;
        transform.localScale = _baseScale; // 트윈 도중이었어도 원래 크기 기준으로 분리
        transform.SetParent(null, true);
        _detachedScale = transform.localScale;
        _detached      = true;
    }

    /// <summary>
    /// 차량이 carPos/carRot 자세로 놓일 때 대차가 있어야 할 자리에 '뿅' 나타난다:
    /// 0 → (1+appearOvershoot) → 1 스케일 팝. 분리 상태가 아니면 먼저 분리한다 (차량이 아직 공중에 있으므로).
    /// </summary>
    public void AppearAt(Vector3 carPos, Quaternion carRot)
    {
        if (_homeParent == null) return;
        if (!_detached)
        {
            // 차량 자식인 채로 꺼져 있던 경우: 같은 월드 스케일로 분리 상태를 만든다
            _detachedScale = Vector3.Scale(_homeParent.lossyScale, _baseScale);
            transform.SetParent(null, false);
            _detached = true;
        }

        _seq?.Kill();
        Vector3 carScale = _homeParent.lossyScale;
        transform.SetPositionAndRotation(carPos + carRot * Vector3.Scale(carScale, _homeLocalPos),
                                         carRot * _homeLocalRot);
        transform.localScale = Vector3.zero;
        if (!gameObject.activeSelf) gameObject.SetActive(true);

        Vector3 rest = _detachedScale;
        _seq = DOTween.Sequence();
        _seq.Append(transform.DOScale(rest * (1f + appearOvershoot), appearGrowDuration).SetEase(Ease.OutBack));
        _seq.Append(transform.DOScale(rest, appearSettleDuration).SetEase(Ease.InOutQuad));
        _seq.OnComplete(() => _seq = null);
    }

    /// <summary>
    /// 분리돼 있으면 원래 부모(차량) 밑 원래 로컬 자세로 되돌린다. 활성 상태·스케일은 건드리지 않는다
    /// (사라진 채로 차량에 붙여 두기 — 불량 폐기 후 풀 재사용 시 SetPath의 Restore가 다시 보이게 한다).
    /// 원래 부모(차량)가 파괴됐으면 남은 대차도 파괴한다.
    /// </summary>
    public void Reattach()
    {
        if (!_detached) return;
        if (_homeParent == null) { Destroy(gameObject); return; }

        // 분리 중 트윈(월드 스케일 기준)은 부모가 바뀌면 의미가 없으므로 끊는다
        _seq?.Kill();
        _seq = null;
        transform.SetParent(_homeParent, false);
        transform.localPosition = _homeLocalPos;
        transform.localRotation = _homeLocalRot;
        _detached = false;
        // 보이는 대차는 원래 크기로, 꺼진 대차는 그대로 (재사용 시 Restore가 원복)
        if (gameObject.activeSelf) transform.localScale = _baseScale;
    }

    /// <summary>재사용·사이클 중단 시 원복: 트윈 정리 + 차량에 재부착 + 스케일 1 + 활성.</summary>
    public void Restore()
    {
        _seq?.Kill();
        _seq = null;
        Reattach();
        if (_detached) return; // 원래 부모(차량)가 파괴돼 대차도 파괴 예약됨
        transform.localScale = _baseScale;
        if (!gameObject.activeSelf) gameObject.SetActive(true);
    }
}
