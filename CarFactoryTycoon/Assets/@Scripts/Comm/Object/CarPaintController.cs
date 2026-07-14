using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 차량 차체(Frame_1)의 도색 상태를 관리한다. CarPool이 차량 생성 시 자동으로 붙인다.
///
/// 흐름 (도색 부스 = 차체의 '체결' 공정):
///   스폰       → 차체 전체가 언더코트(검정) 색     (_PaintMode 2 = 전체 _PaintColor)
///   부스 통과 중 → 스캔 평면을 지난 부위만 원본색     (_PaintMode 1, 법선 = 진행 방향의 반대 →
///                 아직 안 지난 뒤쪽이 검정, 지난 앞쪽이 원본)
///   통과 완료   → 전체 원본색 확정                  (_PaintMode 0)
///   풀 재사용   → OnEnable에서 다시 언더코트로 리셋
///
/// 대상 파츠의 머티리얼 인스턴스 셰이더를 CarPaintScan으로 교체한다 —
/// _BaseMap/_BaseColor/_EmissionMap 프로퍼티 이름이 URP Lit과 같아 텍스처/색이 그대로 승계된다.
/// 머티리얼 인스턴스는 차량별 1회 생성 후 계속 재사용 (풀링과 함께 GC 없음).
/// </summary>
public class CarPaintController : MonoBehaviour
{
    private static readonly int PaintModeId = Shader.PropertyToID("_PaintMode");
    private static readonly int PaintColorId = Shader.PropertyToID("_PaintColor");
    private static readonly int PlanePosId = Shader.PropertyToID("_PlanePos");
    private static readonly int PlaneNormalId = Shader.PropertyToID("_PlaneNormal");
    private static readonly int EdgeWidthId = Shader.PropertyToID("_EdgeWidth");
    private static readonly int EdgeColorId = Shader.PropertyToID("_EdgeColor");

    [Tooltip("도색 대상 파츠 그룹 (기본 Body = 차체). CarController.ResetParts의 체결 제외 기준과 동일.")]
    public PartGroup targetGroup = PartGroup.Body;

    [Tooltip("도색 전 언더코트 색 (대략 검정). 알파 = 덮는 강도 (1=완전 검정)")]
    public Color undercoatColor = new Color(0.06f, 0.06f, 0.07f, 1f);

    [Tooltip("CarPaintScan 셰이더. 비워두면 Shader.Find로 찾는다 (빌드 스트리핑 주의)")]
    public Shader paintShader;

    /// <summary>이번 라이프사이클에서 도색(드러내기)이 이미 완료됐는지 — 부스 중복 지급/재스캔 방지.</summary>
    public bool Revealed { get; private set; }

    private readonly List<Material> paintMats = new List<Material>();
    private bool initialized;

    private void OnEnable()
    {
        // 스폰/풀 재사용 시마다 새 차량 — 언더코트부터 다시 시작
        EnsureInit();
        ApplyUndercoat();
    }

    /// <summary>대상 파츠의 렌더러들을 찾아 페인트 셰이더로 교체한다 (1회).</summary>
    public bool EnsureInit()
    {
        if (initialized) return paintMats.Count > 0;

        if (paintShader == null) paintShader = Shader.Find("CarFactory/CarPaintScan");
        if (paintShader == null)
        {
            Debug.LogError($"[CarPaint] {name} CarPaintScan 셰이더를 찾지 못했습니다.");
            return false;
        }

        initialized = true;
        paintMats.Clear();

        AssemblyPart[] parts = GetComponentsInChildren<AssemblyPart>(true);
        foreach (AssemblyPart part in parts)
        {
            // 그룹으로 매칭 — 프리팹 재구성 등으로 myType이 유실(None)돼도 차체를 찾는다
            if (part.myGroup != targetGroup) continue;
            foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
            {
                // .materials = 이 차량 전용 인스턴스 (다른 차량/원본 에셋에 영향 없음)
                foreach (Material mat in r.materials)
                {
                    mat.shader = paintShader; // 동일 이름 프로퍼티(_BaseMap 등)는 자동 승계
                    paintMats.Add(mat);
                }
            }
        }

        if (paintMats.Count == 0)
            Debug.LogWarning($"[CarPaint] {name}에서 {targetGroup} 그룹 파츠의 렌더러를 찾지 못했습니다.");
        return paintMats.Count > 0;
    }

    /// <summary>차체 전체를 언더코트(검정) 상태로 되돌린다.</summary>
    public void ApplyUndercoat()
    {
        Revealed = false;
        for (int i = 0; i < paintMats.Count; i++)
        {
            Material mat = paintMats[i];
            mat.SetColor(PaintColorId, undercoatColor);
            mat.SetFloat(PaintModeId, 2f); // 전체 _PaintColor = 전체 검정
        }
    }

    /// <summary>
    /// 드러내기 스캔 시작: 평면을 지난(진행 방향 앞쪽) 부위가 원본색으로 갈라진다.
    /// 법선을 진행 방향의 반대로 걸어, '아직 안 지난' 쪽이 언더코트로 남는다.
    /// </summary>
    public void BeginReveal(Vector3 planePos, Vector3 travelDir, float edgeWidth, Color edgeColor)
    {
        if (Revealed) return; // 이미 원본색 — 재스캔하면 뒤쪽이 도로 검게 되므로 무시

        for (int i = 0; i < paintMats.Count; i++)
        {
            Material mat = paintMats[i];
            mat.SetVector(PlanePosId, planePos);
            mat.SetVector(PlaneNormalId, -travelDir); // 반전: 평면 뒤(미통과)가 _PaintColor
            mat.SetFloat(EdgeWidthId, edgeWidth);
            mat.SetColor(EdgeColorId, edgeColor);
            mat.SetFloat(PaintModeId, 1f);
        }
    }

    /// <summary>드러내기 완료: 전체 원본색 확정. 최초 완료면 true (부스가 보상 지급 판단에 사용).</summary>
    public bool FinishReveal()
    {
        if (Revealed) return false;
        Revealed = true;

        for (int i = 0; i < paintMats.Count; i++)
            paintMats[i].SetFloat(PaintModeId, 0f); // 페인트 없음 = 원본색
        return true;
    }
}
