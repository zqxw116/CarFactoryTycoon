using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// AI 생성(Tripo) 차량 FBX를 게임용으로 설정하는 에디터 툴. WorkerModelSetupTool과 같은 패턴.
///
/// 대상: Car_Sedan (리깅 없는 정적 메쉬)
///  - 텍스처: BaseColor = sRGB / 2048, Normal = NormalMap 타입으로 보관만 (재질에 연결하지 않음)
///  - 재질: URP Lit, BaseMap = BaseColor, Metallic 0, Smoothness 0, Opaque (ART_DIRECTION: 알베도만, 무광)
///  - 모델: 애니메이션 타입 None, 애니메이션·카메라·라이트 임포트 끔, 재질 생성 None
///          (FBX 임베드 재질·텍스처가 서브 에셋으로 중복 생성되지 않게 함. 재질은 프리팹 렌더러에 직접 지정)
///          가장 긴 축(차 길이)이 TARGET_LENGTH가 되도록 globalScale 자동 계산 (Tripo FBX는 cm 단위)
///  - 방향: 정점 분포로 위(+Y)·정면(+Z) 축을 추정해 프리팹 자식 회전으로 보정.
///          정면 판정 근거 = 사이드미러 돌출(가장 넓은 단면) 위치 + 캐빈(지붕) 치우침(보닛이 트렁크보다 길다고 가정).
///          판정이 애매하면 경고 로그로 축별 bounds를 남긴다. 앞뒤가 반대로 나오면 FLIP_FRONT를 true로 바꾸고 다시 실행.
///  - 피벗: 바퀴 바닥(Y 최저) = 0, 차량 XZ 중심 = 원점. 빈 루트 아래 자식(Model) 오프셋으로 보정
///  - 콜라이더: 루트에 BoxCollider 1개 (보정 후 bounds에 맞춤, 컨베이어 시험 배치용). 레이어는 Default
///  - 여러 번 실행해도 결과가 같다 (스케일은 현재 값 기준 재계산, 재질·프리팹은 같은 경로 덮어씀 → GUID 유지)
///  - 프리팹은 프리뷰 씬에서 조립하므로 열린 씬을 건드리지 않는다.
///
/// 사용법: Tools ▸ CarFactory ▸ Vehicles ▸ Setup Car Models
/// batchmode: -executeMethod CarModelSetupTool.SetupAll
/// </summary>
public static class CarModelSetupTool
{
    private const string MENU_PATH = "Tools/CarFactory/Vehicles/Setup Car Models";
    private const string LOG_TAG = "[차량 모델]";

    private const string MODEL_DIR = "Assets/@Resource/Models/Vehicles";
    private const string TEXTURE_DIR = "Assets/@Resource/Textures/Vehicles";
    private const string MATERIAL_DIR = "Assets/@Resource/Materials/Vehicles";
    private const string PREFAB_DIR = "Assets/@Resource/Prefabs/Vehicles";

    private static readonly string[] MODEL_NAMES = { "Car_Sedan" };

    // 목표 차 길이(m). 실제 소형 세단 비율 기준. 기존 CarModel(약 4.2~4.5m 추정)은 참고만.
    private const float TARGET_LENGTH = 3.8f;
    // 정면 자동 판정 결과를 180° 뒤집는다. 프리팹에서 차가 뒤로 서 있으면 true로 바꾸고 다시 실행.
    private static readonly bool FLIP_FRONT = false;

    private const int TEXTURE_MAX_SIZE = 2048;
    // 스케일 재계산 허용 오차(상대). 이 이내면 재임포트하지 않는다.
    private const float SCALE_TOLERANCE = 0.002f;
    // 방향 판정용 길이 방향 단면 수
    private const int SLICE_COUNT = 20;

    [MenuItem(MENU_PATH)]
    public static void SetupAll()
    {
        EnsureFolder(MATERIAL_DIR);
        EnsureFolder(PREFAB_DIR);

        bool allOk = true;
        foreach (string name in MODEL_NAMES)
            allOk &= SetupModel(name);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (allOk) Debug.Log($"{LOG_TAG} 완료: {MODEL_NAMES.Length}개 모델 설정 성공");
        else Debug.LogWarning($"{LOG_TAG} 완료(경고 있음): 위 로그를 확인하세요");
    }

    private static bool SetupModel(string name)
    {
        string fbxPath = $"{MODEL_DIR}/{name}.fbx";
        string baseColorPath = $"{TEXTURE_DIR}/{name}_BaseColor.png";
        string normalPath = $"{TEXTURE_DIR}/{name}_Normal.png";
        string materialPath = $"{MATERIAL_DIR}/{name}.mat";
        string prefabPath = $"{PREFAB_DIR}/{name}.prefab";

        if (AssetImporter.GetAtPath(fbxPath) is not ModelImporter importer)
        {
            Debug.LogWarning($"{LOG_TAG} {name}: FBX 없음 ({fbxPath})");
            return false;
        }

        // 1) 텍스처
        bool baseOk = ConfigureTexture(baseColorPath, isNormal: false);
        bool normalOk = ConfigureTexture(normalPath, isNormal: true);
        var baseColor = AssetDatabase.LoadAssetAtPath<Texture2D>(baseColorPath);
        if (!baseOk || baseColor == null)
        {
            Debug.LogWarning($"{LOG_TAG} {name}: BaseColor 텍스처 없음 ({baseColorPath})");
            return false;
        }
        if (!normalOk) Debug.LogWarning($"{LOG_TAG} {name}: Normal 텍스처 없음 ({normalPath}) — 재질에는 쓰지 않으므로 계속 진행");

        // 2) 재질
        Material material = CreateOrUpdateMaterial(materialPath, baseColor);
        if (material == null)
        {
            Debug.LogWarning($"{LOG_TAG} {name}: URP Lit 셰이더를 찾지 못해 재질 생성 실패");
            return false;
        }

        // 3) 모델 임포트 설정 + 스케일 (길이 = 가장 긴 축, 회전과 무관)
        ConfigureModelImporter(importer);
        Analysis a = AnalyzeModel(fbxPath);
        if (a.valid && a.Length > 0.0001f)
        {
            float wanted = importer.globalScale * TARGET_LENGTH / a.Length;
            wanted = Mathf.Round(wanted * 10000f) / 10000f;
            if (Mathf.Abs(wanted / importer.globalScale - 1f) > SCALE_TOLERANCE)
            {
                importer.globalScale = wanted;
                importer.SaveAndReimport();
                a = AnalyzeModel(fbxPath);
            }
        }
        if (!a.valid)
        {
            Debug.LogWarning($"{LOG_TAG} {name}: 모델 측정 실패 (MeshFilter/SkinnedMeshRenderer 메쉬 없음)");
            return false;
        }

        // 4) 임베드 재질·텍스처 중복 여부
        Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(fbxPath);
        int embeddedTex = subAssets.Count(o => o is Texture);
        int embeddedMat = subAssets.Count(o => o is Material);
        int meshCount = subAssets.Count(o => o is Mesh);

        // 5) 프리팹
        bool prefabOk = BuildPrefab(name, fbxPath, prefabPath, material, a);

        // 6) 로그 (한 줄씩)
        Vector3 size = a.rotMax - a.rotMin;
        string[] axisNames = { "X", "Y", "Z" };
        Debug.Log($"{LOG_TAG} {name}: 크기 길이 {size.z:F3}m / 폭 {size.x:F3}m / 높이 {size.y:F3}m (목표 길이 {TARGET_LENGTH:F2}m)");
        Debug.Log($"{LOG_TAG} {name}: 삼각형 {a.triangles:N0}, 정점 {a.vertexCount:N0}, 메쉬 {meshCount}");
        Debug.Log($"{LOG_TAG} {name}: globalScale {importer.globalScale:0.####} (useFileScale {importer.useFileScale}, fileScale {importer.fileScale:0.####})");
        Debug.Log($"{LOG_TAG} {name}: 피벗 보정 오프셋 {Fmt(a.pivotOffset)} (바퀴 바닥 y=0, XZ 중심=원점), 보정 회전 {Fmt(a.correction.eulerAngles)}");
        Debug.Log($"{LOG_TAG} {name}: 임포트 원본 축별 bounds X {a.min.x:F3}~{a.max.x:F3} / Y {a.min.y:F3}~{a.max.y:F3} / Z {a.min.z:F3}~{a.max.z:F3}, " +
                  $"모델 루트 임포트 회전 {Fmt(a.nativeRotation.eulerAngles)}");
        string axisLine = $"{LOG_TAG} {name}: 축 방향 — 길이축 {axisNames[a.lengthAxis]}, " +
                          $"위 {(a.upSign > 0 ? "+" : "-")}{axisNames[a.upAxis]} ({(a.upCertain ? "확정" : "추정")}: {a.upReason}), " +
                          $"정면 {(a.frontSign > 0 ? "+" : "-")}{axisNames[a.lengthAxis]} ({(a.frontCertain ? "확정" : "추정")}: {a.frontReason})" +
                          (FLIP_FRONT ? ", FLIP_FRONT 적용(180° 뒤집음)" : "");
        if (a.upCertain && a.frontCertain) Debug.Log(axisLine);
        else Debug.LogWarning(axisLine + " → 판정이 확실하지 않습니다. 프리팹을 열어 앞(+Z)·위(+Y)를 눈으로 확인하고, 앞뒤가 반대면 FLIP_FRONT=true로 다시 실행하세요.");
        string embedLine = $"{LOG_TAG} {name}: 임베드 서브에셋 텍스처 {embeddedTex} / 재질 {embeddedMat} (총 서브에셋 {subAssets.Length})";
        if (embeddedTex == 0 && embeddedMat == 0) Debug.Log(embedLine);
        else Debug.LogWarning(embedLine + " → 0이어야 정상");
        Debug.Log($"{LOG_TAG} {name}: 재질 {materialPath}, 프리팹 {(prefabOk ? prefabPath : "생성 실패")}, " +
                  $"BoxCollider center {Fmt(new Vector3(0f, size.y * 0.5f, 0f))} size {Fmt(size)}");

        return prefabOk && embeddedTex == 0 && embeddedMat == 0;
    }

    // ───────────────────────── 텍스처 ─────────────────────────

    private static bool ConfigureTexture(string path, bool isNormal)
    {
        if (AssetImporter.GetAtPath(path) is not TextureImporter ti) return false;

        bool changed = false;
        var wantedType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        if (ti.textureType != wantedType) { ti.textureType = wantedType; changed = true; }
        if (!isNormal && !ti.sRGBTexture) { ti.sRGBTexture = true; changed = true; }
        if (ti.maxTextureSize != TEXTURE_MAX_SIZE) { ti.maxTextureSize = TEXTURE_MAX_SIZE; changed = true; }
        if (!ti.mipmapEnabled) { ti.mipmapEnabled = true; changed = true; }
        if (ti.textureCompression != TextureImporterCompression.Compressed) { ti.textureCompression = TextureImporterCompression.Compressed; changed = true; }
        if (ti.alphaSource != TextureImporterAlphaSource.None && !isNormal) { ti.alphaSource = TextureImporterAlphaSource.None; changed = true; }

        if (changed) ti.SaveAndReimport();
        return true;
    }

    // ───────────────────────── 재질 ─────────────────────────

    private static Material CreateOrUpdateMaterial(string path, Texture2D baseColor)
    {
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null) return null;

        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(lit) { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(mat, path);
        }
        else if (mat.shader != lit)
        {
            mat.shader = lit;
        }

        mat.SetTexture("_BaseMap", baseColor);
        mat.SetTexture("_MainTex", baseColor);
        mat.SetColor("_BaseColor", Color.white);
        mat.SetColor("_Color", Color.white);
        mat.SetFloat("_WorkflowMode", 1f); // Metallic
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Smoothness", 0f);
        mat.SetTexture("_MetallicGlossMap", null);
        mat.SetTexture("_BumpMap", null);   // 노멀맵은 지금 연결하지 않는다 (ART_DIRECTION: 알베도만)
        mat.DisableKeyword("_NORMALMAP");
        mat.DisableKeyword("_METALLICSPECGLOSSMAP");
        mat.SetFloat("_Surface", 0f);       // Opaque
        mat.enableInstancing = true;

        EditorUtility.SetDirty(mat);
        return mat;
    }

    // ───────────────────────── 모델 ─────────────────────────

    private static void ConfigureModelImporter(ModelImporter mi)
    {
        // Model
        mi.useFileScale = true;              // FBX cm → m (fileScale 0.01)
        mi.importBlendShapes = false;
        mi.importVisibility = false;
        mi.importCameras = false;
        mi.importLights = false;
        mi.meshCompression = ModelImporterMeshCompression.Off;
        mi.isReadable = false;
        mi.importNormals = ModelImporterNormals.Import;

        // Rig: 뼈대 없는 정적 메쉬
        mi.animationType = ModelImporterAnimationType.None;

        // Animation
        mi.importAnimation = false;

        // Materials: 임베드 재질·텍스처를 만들지 않는다. 재질은 프리팹 렌더러에 지정.
        mi.materialImportMode = ModelImporterMaterialImportMode.None;
        foreach (var kv in mi.GetExternalObjectMap().Where(kv => kv.Key.type == typeof(Material)).ToList())
            mi.RemoveRemap(kv.Key);

        mi.SaveAndReimport();
    }

    private struct Analysis
    {
        public bool valid;
        public int triangles, vertexCount;
        public Vector3 min, max;              // 임포트 원본(루트 위치 0, 임포트 회전 유지) 월드 bounds
        public Quaternion nativeRotation;     // 모델 루트의 임포트 회전
        public int lengthAxis, upAxis, lateralAxis;
        public int upSign, frontSign;
        public bool upCertain, frontCertain;
        public string upReason, frontReason;
        public Quaternion correction;         // 원본 → (앞 +Z, 위 +Y)
        public Vector3 rotMin, rotMax;        // 보정 회전 후 bounds
        public Vector3 pivotOffset;           // 보정 회전 후 적용할 위치 오프셋

        public float Length => max[lengthAxis] - min[lengthAxis];
    }

    /// <summary>프리뷰 씬에 모델을 꺼내 정점 분포로 크기·축 방향·피벗을 계산한다.</summary>
    private static Analysis AnalyzeModel(string fbxPath)
    {
        var a = new Analysis();
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (model == null) return a;

        var pts = new List<Vector3>();
        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model, preview);
            go.transform.position = Vector3.zero; // 임포트 회전·스케일은 그대로 둔다 (프리팹에서도 유지)
            a.nativeRotation = go.transform.localRotation;

            var meshes = new List<(Mesh mesh, Matrix4x4 l2w)>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null) meshes.Add((mf.sharedMesh, mf.transform.localToWorldMatrix));
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.sharedMesh != null) meshes.Add((smr.sharedMesh, smr.transform.localToWorldMatrix));
            if (meshes.Count == 0) return a;

            foreach (var (mesh, l2w) in meshes)
            {
                for (int s = 0; s < mesh.subMeshCount; s++)
                    a.triangles += (int)(mesh.GetIndexCount(s) / 3);

                // 에디터에서는 isReadable=false 메쉬도 CPU 사본을 읽을 수 있다. 실패하면 bounds 꼭짓점으로 대체.
                Vector3[] verts = mesh.vertices;
                if (verts != null && verts.Length > 0)
                {
                    a.vertexCount += verts.Length;
                    foreach (var v in verts) pts.Add(l2w.MultiplyPoint3x4(v));
                }
                else
                {
                    Bounds b = mesh.bounds;
                    for (int i = 0; i < 8; i++)
                        pts.Add(l2w.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3(
                            (i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
                }
            }
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(preview);
        }
        if (pts.Count == 0) return a;

        Vector3 min = pts[0], max = pts[0];
        foreach (var p in pts) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        a.min = min;
        a.max = max;
        a.valid = true;
        Vector3 ext = max - min;
        Vector3 mid = (min + max) * 0.5f;
        bool hasVertices = a.vertexCount > 0;

        // 길이축 = 가장 긴 축
        a.lengthAxis = ext.x >= ext.y && ext.x >= ext.z ? 0 : (ext.y >= ext.z ? 1 : 2);
        int o1 = (a.lengthAxis + 1) % 3, o2 = (a.lengthAxis + 2) % 3;

        // ── 위 축: 좌우는 대칭(평균≈중앙), 위아래는 비대칭(아래쪽에 정점이 몰림) ──
        Vector3 mean = Vector3.zero;
        foreach (var p in pts) mean += p;
        mean /= pts.Count;
        float Asym(int ax) => ext[ax] > 1e-6f ? Mathf.Abs(mean[ax] - mid[ax]) / ext[ax] : 0f;
        float as1 = Asym(o1), as2 = Asym(o2);

        if (hasVertices && Mathf.Abs(as1 - as2) >= 0.02f)
        {
            a.upAxis = as1 > as2 ? o1 : o2;
            a.lateralAxis = a.upAxis == o1 ? o2 : o1;
            a.upSign = mean[a.upAxis] < mid[a.upAxis] ? 1 : -1; // 정점이 몰린 쪽이 아래
            // 교차 확인: 지붕(위 15%) 단면이 바닥(아래 15%) 단면보다 좁아야 한다
            float topW = LateralHalfWidth(pts, a.upAxis, a.lateralAxis, mid[a.lateralAxis], a.upSign, min, max, top: true);
            float botW = LateralHalfWidth(pts, a.upAxis, a.lateralAxis, mid[a.lateralAxis], a.upSign, min, max, top: false);
            a.upCertain = topW < botW;
            a.upReason = $"비대칭 {as1:F3}/{as2:F3}, 지붕폭 {topW:F3} {(topW < botW ? "<" : ">=")} 바닥폭 {botW:F3}";
        }
        else
        {
            // 판단 불가 → Unity가 이미 Y-up으로 변환했다고 보고 +Y (길이축이 Y면 남은 축 중 하나)
            a.upAxis = a.lengthAxis != 1 ? 1 : o1;
            a.lateralAxis = a.upAxis == o1 ? o2 : o1;
            a.upSign = 1;
            a.upCertain = false;
            a.upReason = hasVertices ? $"비대칭 차이 작음 {as1:F3}/{as2:F3}, 기본값 사용" : "정점 읽기 실패, 기본값 사용";
        }

        // ── 정면: 사이드미러 돌출 위치 + 캐빈(지붕) 치우침 ──
        int mirrorVote = 0, cabinVote = 0;
        string mirrorInfo = "정점 없음", cabinInfo = "정점 없음";
        if (hasVertices)
        {
            int L = a.lengthAxis, U = a.upAxis, W = a.lateralAxis;
            float lMin = min[L], lLen = ext[L];
            var sliceUp = new float[SLICE_COUNT];
            var sliceW = new float[SLICE_COUNT];
            var sliceN = new int[SLICE_COUNT];
            for (int i = 0; i < SLICE_COUNT; i++) sliceUp[i] = float.MinValue;
            foreach (var p in pts)
            {
                int i = Mathf.Clamp((int)((p[L] - lMin) / lLen * SLICE_COUNT), 0, SLICE_COUNT - 1);
                sliceN[i]++;
                sliceUp[i] = Mathf.Max(sliceUp[i], p[U] * a.upSign);
                sliceW[i] = Mathf.Max(sliceW[i], Mathf.Abs(p[W] - mid[W]));
            }
            float SliceCenter(int i) => (i + 0.5f) / SLICE_COUNT - 0.5f; // 길이 비율, 중앙 0

            // 미러: 양 끝 15%(바퀴 아치)를 뺀 단면 중 가장 넓은 곳이 중앙값보다 확실히 넓으면 미러로 본다
            int s0 = Mathf.CeilToInt(SLICE_COUNT * 0.15f), s1 = SLICE_COUNT - s0;
            var widths = new List<float>();
            int peak = -1;
            for (int i = s0; i < s1; i++)
            {
                if (sliceN[i] == 0) continue;
                widths.Add(sliceW[i]);
                if (peak < 0 || sliceW[i] > sliceW[peak]) peak = i;
            }
            if (peak >= 0)
            {
                widths.Sort();
                float median = widths[widths.Count / 2];
                float ratio = median > 1e-6f ? sliceW[peak] / median : 1f;
                float off = SliceCenter(peak);
                if (ratio >= 1.06f && Mathf.Abs(off) >= 0.05f) mirrorVote = off > 0 ? 1 : -1;
                mirrorInfo = $"미러 후보 위치 {off:+0.00;-0.00} 폭비 {ratio:F2}";
            }

            // 캐빈: 지붕 높이(85% 이상) 단면의 중심. 보닛이 트렁크보다 길다고 보고 캐빈 반대쪽을 앞으로
            float upLo = a.upSign > 0 ? min[U] : -max[U];
            float upTh = upLo + ext[U] * 0.85f;
            float sum = 0f; int cnt = 0;
            for (int i = 0; i < SLICE_COUNT; i++)
                if (sliceN[i] > 0 && sliceUp[i] >= upTh) { sum += SliceCenter(i); cnt++; }
            if (cnt > 0)
            {
                float cabin = sum / cnt;
                if (Mathf.Abs(cabin) >= 0.03f) cabinVote = cabin > 0 ? -1 : 1;
                cabinInfo = $"캐빈 중심 {cabin:+0.00;-0.00}";
            }
        }

        int votes = mirrorVote + cabinVote;
        a.frontSign = votes < 0 ? -1 : 1;
        a.frontCertain = mirrorVote != 0 && mirrorVote == cabinVote;
        string verdict = mirrorVote != 0 && cabinVote != 0 && mirrorVote != cabinVote ? "근거 충돌, 기본 + 사용"
                       : votes == 0 ? "근거 없음, 기본 + 사용"
                       : a.frontCertain ? "근거 2개 일치" : "근거 1개";
        a.frontReason = $"{mirrorInfo}, {cabinInfo}, {verdict}";

        // ── 보정 회전: front → +Z, up → +Y ──
        Vector3 front = Axis(a.lengthAxis) * a.frontSign;
        if (FLIP_FRONT) front = -front;
        Vector3 up = Axis(a.upAxis) * a.upSign;
        a.correction = Quaternion.Inverse(Quaternion.LookRotation(front, up));

        // 보정 후 bounds → 피벗 오프셋 (바닥 y=0, XZ 중심 원점)
        Vector3 rmin = a.correction * pts[0], rmax = rmin;
        foreach (var p in pts)
        {
            Vector3 r = a.correction * p;
            rmin = Vector3.Min(rmin, r);
            rmax = Vector3.Max(rmax, r);
        }
        a.pivotOffset = new Vector3(-(rmin.x + rmax.x) * 0.5f, -rmin.y, -(rmin.z + rmax.z) * 0.5f);
        a.rotMin = rmin + a.pivotOffset;
        a.rotMax = rmax + a.pivotOffset;
        return a;
    }

    /// <summary>위 축 기준 상단(top) 또는 하단 15% 구간 정점들의 좌우 반폭.</summary>
    private static float LateralHalfWidth(List<Vector3> pts, int upAxis, int latAxis, float latMid, int upSign,
                                          Vector3 min, Vector3 max, bool top)
    {
        float h = max[upAxis] - min[upAxis];
        float lo = upSign > 0 ? min[upAxis] : max[upAxis];
        float w = 0f;
        foreach (var p in pts)
        {
            float t = (p[upAxis] - lo) * upSign / h; // 0 = 바닥, 1 = 지붕
            if (top ? t >= 0.85f : t <= 0.15f)
                w = Mathf.Max(w, Mathf.Abs(p[latAxis] - latMid));
        }
        return w;
    }

    private static Vector3 Axis(int i) => i == 0 ? Vector3.right : (i == 1 ? Vector3.up : Vector3.forward);

    // ───────────────────────── 프리팹 ─────────────────────────

    private static bool BuildPrefab(string name, string fbxPath, string prefabPath, Material material, Analysis a)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (model == null) return false;

        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            var root = new GameObject(name);
            SceneManager.MoveGameObjectToScene(root, preview);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, preview);
            instance.name = "Model";
            instance.transform.SetParent(root.transform, false);
            // 측정 때와 같은 기준(위치 0, 임포트 회전 유지) 위에 보정 회전·오프셋을 얹는다
            instance.transform.localRotation = a.correction * a.nativeRotation;
            instance.transform.localPosition = a.pivotOffset;

            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int i = 0; i < mats.Length; i++) mats[i] = material;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
            }

            Vector3 size = a.rotMax - a.rotMin;
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, size.y * 0.5f, 0f);
            box.size = size;

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool success);
            return success;
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }

    // ───────────────────────── 유틸 ─────────────────────────

    private static string Fmt(Vector3 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
