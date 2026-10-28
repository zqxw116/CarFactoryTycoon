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

    [Header("도색 분사")]
    [Tooltip("기존 CFXR smoke atlas. 씬에서 참조하여 빌드에도 포함합니다.")]
    public Texture2D sprayTexture;
    readonly System.Collections.Generic.HashSet<CarController> paintingCars = new System.Collections.Generic.HashSet<CarController>();
    Transform portalRoot, leftPost, rightPost, topBeam;
    readonly Transform[] nozzles = new Transform[3];
    ParticleSystem spray;
    Material frameMaterial, beamMaterial, nozzleMaterial, sprayMaterial;
    BoxCollider boothBox;
    Bounds visualBounds;

    private void OnEnable()
    {
        if (portalRoot != null) { portalRoot.gameObject.SetActive(true); if (spray != null) spray.Play(); return; }
        boothBox = GetComponent<BoxCollider>();
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (shader == null) return;
        frameMaterial = CreatePortalMaterial(shader, "PaintBooth_Post_Runtime", new Color(.38f, .43f, .42f));
        beamMaterial = CreatePortalMaterial(shader, "PaintBooth_Beam_Runtime", new Color(.76f, .75f, .65f));
        nozzleMaterial = CreatePortalMaterial(shader, "PaintBooth_Nozzle_Runtime", new Color(.68f, .59f, .29f));
        portalRoot = new GameObject("PaintPortal").transform;
        portalRoot.SetParent(transform, false);
        leftPost = CreatePortalPiece("LeftPost", PrimitiveType.Cube, frameMaterial);
        rightPost = CreatePortalPiece("RightPost", PrimitiveType.Cube, frameMaterial);
        topBeam = CreatePortalPiece("TopBeam", PrimitiveType.Cube, beamMaterial);
        for (int i = 0; i < nozzles.Length; i++)
            nozzles[i] = CreatePortalPiece("SprayNozzle" + (i + 1), PrimitiveType.Cylinder, nozzleMaterial);
        BuildSpray();
        UpdatePortal();
    }

    Material CreatePortalMaterial(Shader shader, string materialName, Color color)
    {
        var material = new Material(shader) { name = materialName, enableInstancing = true };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", .22f);
        return material;
    }

    Transform CreatePortalPiece(string pieceName, PrimitiveType type, Material material)
    {
        var piece = GameObject.CreatePrimitive(type);
        piece.name = pieceName;
        piece.transform.SetParent(portalRoot, false);
        var collider = piece.GetComponent<Collider>();
        if (collider != null) { collider.enabled = false; Destroy(collider); }
        var renderer = piece.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return piece.transform;
    }
    void BuildSpray()
    {
#if UNITY_EDITOR
        if (sprayTexture == null)
            sprayTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath("44001ef5b09b59b44a589b1c0c0fd31f"));
#endif
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null || sprayTexture == null) return;
        sprayMaterial = new Material(shader) { name = "PaintBooth_SoftSpray_Runtime", renderQueue = 3000 };
        sprayMaterial.SetFloat("_Surface", 1f);
        sprayMaterial.SetFloat("_ZWrite", 0f);
        sprayMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        sprayMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        sprayMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        sprayMaterial.SetTexture("_BaseMap", sprayTexture);
        sprayMaterial.mainTexture = sprayTexture;
        var go = new GameObject("PaintSprayCurtain");
        go.transform.SetParent(transform, false);
        spray = go.AddComponent<ParticleSystem>();
        spray.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = spray.main;
        main.loop = true;
        main.maxParticles = 64;
        main.startSpeed = 1.6f;
        main.startSize = new ParticleSystem.MinMaxCurve(.18f, .32f);
        main.startColor = new Color(.8f, .87f, .83f, .13f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        var emission = spray.emission; emission.rateOverTime = 0f;
        var shape = spray.shape; shape.shapeType = ParticleSystemShapeType.Box;
        var sheet = spray.textureSheetAnimation;
        sheet.enabled = true; sheet.numTilesX = 2; sheet.numTilesY = 2;
        sheet.startFrame = new ParticleSystem.MinMaxCurve(0f, 1f);
        sheet.frameOverTime = 0f;
        var color = spray.colorOverLifetime; color.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(.55f, .15f), new GradientAlphaKey(0f, 1f) });
        color.color = gradient;
        var renderer = spray.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = sprayMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        spray.Play();
    }

    void UpdatePortal()
    {
        if (boothBox == null || portalRoot == null) return;
        visualBounds = boothBox.bounds;
        var b = visualBounds;
        portalRoot.SetPositionAndRotation(b.center, Quaternion.identity);
        var parentScale = transform.lossyScale;
        portalRoot.localScale = new Vector3(1f / Mathf.Max(.001f, Mathf.Abs(parentScale.x)),
            1f / Mathf.Max(.001f, Mathf.Abs(parentScale.y)), 1f / Mathf.Max(.001f, Mathf.Abs(parentScale.z)));
        float height = Mathf.Max(.5f, b.size.y), halfWidth = b.extents.z + .13f;
        leftPost.localPosition = new Vector3(0f, 0f, -halfWidth);
        rightPost.localPosition = new Vector3(0f, 0f, halfWidth);
        leftPost.localScale = rightPost.localScale = new Vector3(.42f, height, .24f);
        topBeam.localPosition = new Vector3(0f, height * .5f + .1f, 0f);
        topBeam.localScale = new Vector3(.46f, .25f, halfWidth * 2f + .24f);
        for (int i = 0; i < nozzles.Length; i++)
        {
            nozzles[i].localPosition = new Vector3(0f, height * .5f - .09f, (i - 1) * b.size.z * .3f);
            nozzles[i].localScale = new Vector3(.16f, .09f, .16f);
        }
        if (spray == null) return;
        spray.transform.SetPositionAndRotation(new Vector3(b.center.x, b.max.y - .08f, b.center.z), Quaternion.Euler(90f, 0f, 0f));
        var shape = spray.shape; shape.scale = new Vector3(.18f, Mathf.Max(.1f, b.size.z - .2f), .02f);
        var main = spray.main; main.startLifetime = Mathf.Clamp(b.size.y / 1.6f, .4f, 2.2f);
    }

    private void Update()
    {
        if (boothBox == null) return;
        if (boothBox.bounds != visualBounds) UpdatePortal();
        if (spray == null) return;
        bool painting = false;
        foreach (var car in paintingCars)
            if (car != null && car.gameObject.activeInHierarchy) { painting = true; break; }
        var emission = spray.emission;
        emission.rateOverTime = painting ? 30f : 10f;
        var main = spray.main;
        main.startColor = new Color(.8f, .87f, .83f, painting ? .42f : .24f);
    }

    private void OnDisable()
    {
        paintingCars.Clear();
        if (portalRoot != null) portalRoot.gameObject.SetActive(false);
        if (spray != null) spray.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void OnDestroy()
    {
        if (frameMaterial != null) Destroy(frameMaterial);
        if (beamMaterial != null) Destroy(beamMaterial);
        if (nozzleMaterial != null) Destroy(nozzleMaterial);
        if (sprayMaterial != null) Destroy(sprayMaterial);
    }
    private void OnTriggerEnter(Collider other)
    {
        if (!other.TryGetComponent<CarController>(out var car)) return;

        // CarPool 생성 차량엔 이미 붙어 있음 — 씬에 직접 배치한 차량 대비 폴백
        if (!car.TryGetComponent<CarPaintController>(out var paint))
            paint = car.gameObject.AddComponent<CarPaintController>();

        if (!paint.EnsureInit()) return;

        // 스캔 평면 = 부스 중심, 기준 방향 = 차량 진행 방향(차 앞머리) — 부스 회전 셋업 불필요
        paintingCars.Add(car);
        paint.BeginReveal(transform.position, car.transform.forward, edgeGlowWidth, edgeGlowColor);
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.TryGetComponent<CarController>(out var car)) return;
        paintingCars.Remove(car);
        if (!car.TryGetComponent<CarPaintController>(out var paint)) return;

        // 최초 완료(검정이 완전히 사라진 순간)에만 보상 — 재통과/중복 지급 방지
        if (!paint.FinishReveal()) return;

        int reward = ProductionProgression.Current != null && ProductionProgression.Current.prototypeMode ? 0 : StationConfig.Instance.partReward;
        if (reward > 0)
        {
            Vector3 rewardPos = car.transform.position + Vector3.up * 1.2f;
            EconomyManager.Instance.Earn(reward, rewardPos);
            CashPopup.Show(rewardPos, reward);
        }
        Debug.Log($"[{name}] ✅ {car.name} 차체 도색 완료! (+{reward})");
    }

    // 네모 거울 프레임 + 스캔 평면(반투명 판)을 씬 뷰에 표시
    private void OnDrawGizmosSelected()
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
