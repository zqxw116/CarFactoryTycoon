using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 도색 부스 공정 — 라인 위에 세우는 '네모난 거울'. 차체(Frame_1)의 체결 공정 역할을 한다.
///
/// 차량은 검정 언더코트 상태로 스폰되고(CarPaintController), 이 사각형을 통과하는 동안
/// 지나간 부위만 픽셀 단위로 원본 색이 드러난다 (월드 평면 컷오프, CarPaintScan 셰이더).
///
///   진입(OnTriggerEnter) → 부스 중심을 지나는 스캔 평면 활성 (차량 진행 방향 기준)
///   통과 중              → 평면을 지난 앞부분 = 원본색 / 아직인 뒷부분 = 검정
///   이탈(OnTriggerExit)  → 전체 원본색 확정 + 최초 1회 보상(StationConfig.partReward) 지급
///                          + "+$" 플로팅 텍스트 → 차체가 '체결'된 느낌
///
/// 셋업: 빈 오브젝트 + BoxCollider(isTrigger, 라인을 가로막는 크기) + 이 컴포넌트.
/// 차량 쪽은 CarPool이 CarPaintController를 자동 부착하므로 손댈 것 없음.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class PaintBooth : MonoBehaviour
{
    [Header("경계 글로우")]
    [Tooltip("색이 갈라지는 경계에 표시되는 글로우 밴드 폭(m)")]
    public float edgeGlowWidth = 0.15f;
    [ColorUsage(false, true)]
    public Color edgeGlowColor = new Color(2f, 1.6f, 0.4f);

    [Header("기즈모")]
    public Color gizmoColor = new Color(0.2f, 0.9f, 1f, 1f);

    private void Awake()
    {
        // 이 프로젝트의 물리 매트릭스는 Car(6)↔Station(7)/Robot(8) 조합만 충돌 허용 —
        // Default 등 다른 레이어에 배치하면 차량 트리거 이벤트가 아예 오지 않는다.
        // 셋업 실수 방지를 위해 Car와 충돌하지 않는 레이어면 Station으로 자동 보정.
        int carLayer = LayerMask.NameToLayer("Car");
        if (carLayer < 0 || !Physics.GetIgnoreLayerCollision(gameObject.layer, carLayer)) return;

        int stationLayer = LayerMask.NameToLayer("Station");
        if (stationLayer < 0) return;

        Debug.LogWarning($"[{name}] 레이어 '{LayerMask.LayerToName(gameObject.layer)}'은 Car와 충돌하지 않아" +
            " 트리거가 동작하지 않습니다 → 'Station' 레이어로 자동 변경.");
        gameObject.layer = stationLayer;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.TryGetComponent<CarController>(out var car)) return;

        // CarPool 생성 차량엔 이미 붙어 있음 — 씬에 직접 배치한 차량 대비 폴백
        if (!car.TryGetComponent<CarPaintController>(out var paint))
            paint = car.gameObject.AddComponent<CarPaintController>();

        if (!paint.EnsureInit()) return;

        // 스캔 평면 = 부스 중심, 기준 방향 = 차량 진행 방향(차 앞머리) — 부스 회전 셋업 불필요
        paint.BeginReveal(transform.position, car.transform.forward, edgeGlowWidth, edgeGlowColor);
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.TryGetComponent<CarController>(out var car)) return;
        if (!car.TryGetComponent<CarPaintController>(out var paint)) return;

        // 최초 완료(검정이 완전히 사라진 순간)에만 보상 — 재통과/중복 지급 방지
        if (!paint.FinishReveal()) return;

        int reward = StationConfig.Instance.partReward;
        if (reward > 0)
        {
            EconomyManager.Instance.Earn(reward);
            CashPopup.Show(car.transform.position + Vector3.up * 1.2f, reward);
        }
        Debug.Log($"[{name}] ✅ {car.name} 차체 도색 완료! (+{reward})");
    }

    // 네모 거울 프레임 + 스캔 평면(반투명 판)을 씬 뷰에 표시
    private void OnDrawGizmos()
    {
        if (!TryGetComponent<BoxCollider>(out var box)) return;

        Matrix4x4 old = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;

        // 트리거 영역 (외곽 와이어)
        Gizmos.color = gizmoColor;
        Gizmos.DrawWireCube(box.center, box.size);

        // 스캔 평면 (부스 중앙의 얇은 반투명 판 = 거울)
        Color fill = gizmoColor;
        fill.a = 0.25f;
        Gizmos.color = fill;
        Gizmos.DrawCube(box.center, new Vector3(0.05f, box.size.y, box.size.z));

        Gizmos.matrix = old;

#if UNITY_EDITOR
        Handles.Label(transform.TransformPoint(box.center + Vector3.up * (box.size.y * 0.5f + 0.3f)),
            "도색 부스 (Frame_1)");
#endif
    }
}
