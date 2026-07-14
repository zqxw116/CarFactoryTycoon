using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;

/// <summary>
/// 재화 획득 플로팅 텍스트("+$10")를 월드 공간에 띄우는 연출 매니저.
/// 체결 완료 위치에서 텍스트가 펑 튀어나와 위로 떠오르며 사라진다.
/// - 3D TextMeshPro를 풀링해 재사용 (GC/Instantiate 비용 없음)
/// - 카메라 빌보드: 떠오르는 동안 매 프레임 카메라 방향을 바라본다
/// MonoSingleton: 최초 Show 호출 시 오브젝트가 자동 생성된다.
/// </summary>
public class CashPopup : MonoSingleton<CashPopup>
{
    [Header("연출")]
    [Tooltip("떠오르는 높이(m)")]
    public float riseHeight = 1.2f;
    [Tooltip("전체 연출 시간(초). 후반 45% 구간에서 페이드아웃된다.")]
    public float duration = 0.8f;
    [Tooltip("텍스트 크기 (3D TMP fontSize)")]
    public float fontSize = 6f;
    public Color gainColor = new Color(0.35f, 1f, 0.45f, 1f);

    private readonly Queue<TextMeshPro> pool = new Queue<TextMeshPro>();

    /// <summary>월드 위치에 "+$amount" 플로팅 텍스트를 띄운다. scale로 임팩트 차등(차량 출고=크게).</summary>
    public static void Show(Vector3 worldPos, int amount, float scale = 1f)
        => Instance.Spawn(worldPos, amount, scale);

    private void Spawn(Vector3 worldPos, int amount, float scale)
    {
        TextMeshPro text = pool.Count > 0 ? pool.Dequeue() : CreateNew();
        Transform tf = text.transform;

        text.text = $"+${amount:N0}";
        text.color = gainColor; // 재사용 시 페이드로 빠진 알파도 함께 원복
        tf.position = worldPos;
        tf.localScale = Vector3.one * (0.6f * scale);
        text.gameObject.SetActive(true);

        Camera cam = Camera.main;
        if (cam != null) tf.rotation = cam.transform.rotation;

        // 펑 커지며 등장 → 위로 떠오름 → 후반 페이드아웃
        Sequence seq = DOTween.Sequence();
        seq.Append(tf.DOScale(scale, 0.15f).SetEase(Ease.OutBack));
        seq.Join(tf.DOMove(worldPos + Vector3.up * riseHeight, duration).SetEase(Ease.OutCubic));
        seq.Insert(duration * 0.55f,
            DOTween.To(() => text.color.a, a => { var c = text.color; c.a = a; text.color = c; }, 0f, duration * 0.45f));
        seq.OnUpdate(() => { if (cam != null) tf.rotation = cam.transform.rotation; });
        seq.OnComplete(() => { text.gameObject.SetActive(false); pool.Enqueue(text); });
    }

    private TextMeshPro CreateNew()
    {
        var go = new GameObject("CashText");
        go.transform.SetParent(transform, false);

        var text = go.AddComponent<TextMeshPro>();
        text.fontSize = fontSize;
        text.alignment = TextAlignmentOptions.Center;
        text.fontStyle = FontStyles.Bold;
        text.rectTransform.sizeDelta = new Vector2(6f, 2f); // 한 줄에 넉넉히 (줄바꿈 방지)
        return text;
    }
}
