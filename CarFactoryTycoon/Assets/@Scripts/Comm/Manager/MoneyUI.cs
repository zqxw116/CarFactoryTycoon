using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 자금(돈)을 화면에 표시하는 UI. EconomyManager.OnMoneyChanged를 구독해
/// 숫자를 카운트업/다운(주르륵)으로 갱신하고, 증감 시 코인(기본 이미지) 연출을 보여준다.
/// - 증가: 화면 하단에서 코인이 텍스트로 모여든다.
/// - 감소: 텍스트에서 코인이 위로 올라가며 빠져나간다.
/// Canvas/Text는 코드로 생성한다(씬 배치 불필요). 코인 이미지는 임시 기본 이미지(흰 박스+색).
/// </summary>
public class MoneyUI : MonoSingleton<MoneyUI>
{
    private TextMeshProUGUI moneyText;
    private EconomyManager eco;
    private long displayed;
    private Tween countTween;
    private bool built;

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

        var go = new GameObject("MoneyText");
        go.transform.SetParent(transform, false);
        moneyText = go.AddComponent<TextMeshProUGUI>();
        moneyText.fontSize = 64;
        moneyText.alignment = TextAlignmentOptions.Center;
        moneyText.color = Color.white;

        var rt = moneyText.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -60f);
        rt.sizeDelta = new Vector2(600f, 100f);
    }

    private void HandleMoneyChanged(int newAmount, int delta)
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
        if (delta != 0) SpawnCoins(delta > 0);
    }

    private void SpawnCoins(bool gain)
    {
        const int count = 6;
        Vector3 textPos = moneyText.rectTransform.position;
        for (int i = 0; i < count; i++)
            SpawnCoin(gain, textPos, i, count);
    }

    private void SpawnCoin(bool gain, Vector3 textPos, int idx, int count)
    {
        var go = new GameObject("Coin");
        go.transform.SetParent(transform, false);

        var img = go.AddComponent<Image>();         // sprite 미지정 → 기본 흰 박스
        img.color = new Color(1f, 0.82f, 0.2f, 1f); // 임시 코인색
        var rt = img.rectTransform;
        rt.sizeDelta = new Vector2(40f, 40f);

        float spread = (idx - (count - 1) * 0.5f) * 60f;
        Vector3 start, end;
        if (gain)
        {
            start = new Vector3(Screen.width * 0.5f + spread, Screen.height * 0.15f, 0f);
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

        seq.OnComplete(() => Destroy(go));
    }

    private static string Format(long v) => $"$ {v:N0}";
}
