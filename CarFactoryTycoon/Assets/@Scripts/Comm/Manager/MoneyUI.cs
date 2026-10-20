using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 자금(돈)을 화면에 표시하는 UI. EconomyManager.OnMoneyChanged를 구독해
/// 숫자를 카운트업/다운(주르륵)으로 갱신하고, 증감 시 코인(기본 이미지) 연출을 보여준다.
/// - 증가: 획득 발생 위치(로봇팔 등, 월드→스크린 변환)에서 코인이 텍스트로 모여든다. 위치 없으면 화면 하단 폴백.
/// - 감소: 텍스트에서 코인이 위로 올라가며 빠져나간다.
/// Canvas/Text는 코드로 생성한다(씬 배치 불필요). 코인은 Resources의 코인 스프라이트, 풀링 재사용.
/// 텍스트 왼쪽에 같은 코인 아이콘을 두고, 날아가는 코인은 이 아이콘으로 모인다(감소 시 여기서 출발).
/// 스프라이트 로드 실패 시: 아이콘 숨김 + 코인은 기존 임시 흰 박스(금색)로 폴백, 목표점은 텍스트.
/// </summary>
public class MoneyUI : MonoSingleton<MoneyUI>
{
    private const string CoinSpritePath = "Sprites/UI_Coin";
    private static readonly Color FallbackCoinColor = new Color(1f, 0.82f, 0.2f, 1f);

    private TextMeshProUGUI moneyText;
    private Image moneyIcon;
    private Sprite coinSprite;
    private EconomyManager eco;
    private long displayed;
    private Tween countTween;
    private bool built;
    private readonly System.Collections.Generic.Queue<Image> coinPool = new System.Collections.Generic.Queue<Image>();

    public override void Init()
    {
        if (built) return;
        built = true;

        BuildUI();

        eco = EconomyManager.Instance;
        displayed = eco.currentMoney;
        moneyText.text = Format(displayed);
        eco.OnMoneyChanged += HandleMoneyChanged;
    }

    private void OnDestroy()
    {
        if (eco != null) eco.OnMoneyChanged -= HandleMoneyChanged;
        countTween?.Kill();
    }

    private void BuildUI()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        gameObject.AddComponent<GraphicRaycaster>();

        coinSprite = Resources.Load<Sprite>(CoinSpritePath);

        // [아이콘][텍스트]를 가로로 묶어 화면 상단 중앙에 둔다. 숫자 길이가 바뀌어도 묶음 전체가 가운데 정렬.
        var group = new GameObject("MoneyGroup", typeof(RectTransform));
        group.transform.SetParent(transform, false);
        var grt = (RectTransform)group.transform;
        grt.anchorMin = grt.anchorMax = new Vector2(0.5f, 1f);
        grt.pivot = new Vector2(0.5f, 1f);
        grt.anchoredPosition = new Vector2(0f, -60f);
        grt.sizeDelta = new Vector2(600f, 100f);
        var layout = group.AddComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.spacing = 12f;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        group.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        var iconGo = new GameObject("MoneyIcon", typeof(RectTransform));
        iconGo.transform.SetParent(group.transform, false);
        moneyIcon = iconGo.AddComponent<Image>();
        moneyIcon.sprite = coinSprite;
        moneyIcon.preserveAspect = true;
        moneyIcon.raycastTarget = false;
        var le = iconGo.AddComponent<LayoutElement>();
        le.preferredWidth = le.preferredHeight = 60f;
        iconGo.SetActive(coinSprite != null); // 스프라이트 없으면 아이콘 생략 (흰 박스 노출 방지)

        var go = new GameObject("MoneyText");
        go.transform.SetParent(group.transform, false);
        moneyText = go.AddComponent<TextMeshProUGUI>();
        moneyText.fontSize = 64;
        moneyText.alignment = TextAlignmentOptions.Center;
        moneyText.color = Color.white;
        moneyText.raycastTarget = false;
    }

    private void HandleMoneyChanged(int newAmount, int delta, Vector3? sourceWorldPos)
    {
        // 1) 숫자 카운트업/다운 (주르륵)
        countTween?.Kill();
        long from = displayed;
        countTween = DOVirtual.Float(from, newAmount, 0.5f, v =>
        {
            displayed = (long)v;
            moneyText.text = Format(displayed);
        }).SetEase(Ease.OutCubic);

        // 살짝 펀치 스케일로 강조
        moneyText.rectTransform.DOKill();
        moneyText.rectTransform.localScale = Vector3.one;
        DOTween.To(() => moneyText.rectTransform.localScale,
                   s => moneyText.rectTransform.localScale = s,
                   Vector3.one * 1.15f, 0.1f)
               .SetLoops(2, LoopType.Yoyo);

        // 2) 코인 연출
        if (delta != 0) SpawnCoins(delta > 0, sourceWorldPos);
    }

    private void SpawnCoins(bool gain, Vector3? sourceWorldPos)
    {
        const int count = 6;
        // 코인이 모이는/출발하는 지점: 아이콘이 있으면 아이콘, 없으면 텍스트 (변수명은 기존 유지)
        Vector3 textPos = moneyIcon.gameObject.activeSelf
            ? moneyIcon.rectTransform.position
            : moneyText.rectTransform.position;

        // 획득 발생 월드좌표 → 스크린좌표 (카메라 뒤(z<=0)면 폴백)
        Vector3? sourceScreen = null;
        Camera cam = Camera.main;
        if (gain && sourceWorldPos.HasValue && cam != null)
        {
            Vector3 sp = cam.WorldToScreenPoint(sourceWorldPos.Value);
            if (sp.z > 0f) sourceScreen = new Vector3(sp.x, sp.y, 0f);
        }

        for (int i = 0; i < count; i++)
            SpawnCoin(gain, textPos, sourceScreen, i, count);
    }

    private void SpawnCoin(bool gain, Vector3 textPos, Vector3? sourceScreen, int idx, int count)
    {
        Image img = coinPool.Count > 0 ? coinPool.Dequeue() : CreateCoin();
        // 스프라이트면 원색 그대로, 폴백 흰 박스면 금색 (재사용 시 알파 원복)
        img.color = coinSprite != null ? Color.white : FallbackCoinColor;
        img.gameObject.SetActive(true);
        var rt = img.rectTransform;

        float spread = (idx - (count - 1) * 0.5f) * 60f;
        Vector3 start, end;
        if (gain)
        {
            // 발생 지점 주변에 흩뿌려 출발 → 텍스트로 수렴. 위치 없으면 기존 화면 하단 폴백.
            start = sourceScreen.HasValue
                ? sourceScreen.Value + (Vector3)(Random.insideUnitCircle * 45f)
                : new Vector3(Screen.width * 0.5f + spread, Screen.height * 0.15f, 0f);
            end = textPos;
        }
        else
        {
            start = textPos + new Vector3(spread * 0.4f, 0f, 0f);
            end = textPos + new Vector3(spread, 220f, 0f);
        }
        rt.position = start;

        float dur = 0.6f;
        var move = DOTween.To(() => rt.position, p => rt.position = p, end, dur)
                          .SetEase(gain ? Ease.InQuad : Ease.OutQuad);

        var seq = DOTween.Sequence();
        seq.AppendInterval(idx * 0.04f);
        seq.Append(move);

        Color clear = new Color(img.color.r, img.color.g, img.color.b, 0f);
        if (gain)
            // 텍스트 도착 후 사라짐
            seq.Append(DOTween.To(() => img.color, c => img.color = c, clear, 0.15f));
        else
            // 올라가며 동시에 사라짐
            seq.Join(DOTween.To(() => img.color, c => img.color = c, clear, dur));

        seq.OnComplete(() => { img.gameObject.SetActive(false); coinPool.Enqueue(img); });
    }

    private Image CreateCoin()
    {
        var go = new GameObject("Coin");
        go.transform.SetParent(transform, false);
        var img = go.AddComponent<Image>();
        img.sprite = coinSprite; // null이면 기본 흰 박스 폴백
        img.preserveAspect = true;
        img.raycastTarget = false;
        img.rectTransform.sizeDelta = new Vector2(44f, 44f);
        return img;
    }

    private static string Format(long v) => $"$ {v:N0}";
}
