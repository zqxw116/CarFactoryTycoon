using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// AI 생성(Tripo + Mixamo 리깅) 작업자 캐릭터 FBX 2종을 게임용으로 설정하는 에디터 툴.
///
/// 대상: Worker_Helmet(NPC 작업자), Worker_Base(플레이어 기본형, 이후 커스터마이징 베이스)
///  - 텍스처: BaseColor = sRGB / 2048, Normal = NormalMap 타입으로 보관만 (재질에 연결하지 않음)
///  - 재질: URP Lit, BaseMap = BaseColor, Metallic 0, Smoothness 0 (ART_DIRECTION: 알베도만, 무광)
///  - 모델: Humanoid(Create From This Model), 애니메이션·카메라·라이트 임포트 끔,
///          재질 생성 None (FBX에 임베드된 재질·텍스처 14MB가 서브 에셋으로 중복 생성되지 않게 함.
///          재질은 프리팹의 렌더러에 직접 지정한다)
///          키가 TARGET_HEIGHT가 되도록 globalScale 자동 계산 (Mixamo 프리셋은 cm 단위)
///  - 프리팹: Prefabs/Characters/{이름}.prefab. 발바닥 중앙이 원점이 아니면 빈 루트 아래 자식으로 넣어 오프셋 보정
///  - 여러 번 실행해도 결과가 같다 (스케일은 현재 값 기준으로 재계산, 재질·프리팹은 같은 경로 덮어씀 → GUID 유지)
///  - 프리팹은 프리뷰 씬에서 조립하므로 열린 씬을 건드리지 않는다.
///
/// 사용법: Tools ▸ CarFactory ▸ Characters ▸ Setup Worker Models
/// batchmode: -executeMethod WorkerModelSetupTool.SetupAll
/// </summary>
public static class WorkerModelSetupTool
{
    private const string MENU_PATH = "Tools/CarFactory/Characters/Setup Worker Models";
    private const string LOG_TAG = "[작업자 모델]";

    private const string MODEL_DIR = "Assets/@Resource/Models/Characters/Worker";
    private const string TEXTURE_DIR = "Assets/@Resource/Textures/Characters/Worker";
    private const string MATERIAL_DIR = "Assets/@Resource/Materials/Characters/Worker";
    private const string PREFAB_DIR = "Assets/@Resource/Prefabs/Characters";

    private static readonly string[] MODEL_NAMES = { "Worker_Helmet", "Worker_Base" };

    // 목표 키(m). 사용자 지정 1.7~1.8m의 중간값.
    private const float TARGET_HEIGHT = 1.75f;
    private const int TEXTURE_MAX_SIZE = 2048;
    // 스케일 재계산 허용 오차(상대). 이 이내면 재임포트하지 않는다.
    private const float SCALE_TOLERANCE = 0.002f;
    // 피벗 판정 허용 오차(m)
    private const float PIVOT_TOLERANCE_Y = 0.01f;
    private const float PIVOT_TOLERANCE_XZ = 0.03f;

    [MenuItem(MENU_PATH)]
    public static void SetupAll()
    {
        EnsureFolder(MATERIAL_DIR);
        EnsureFolder(PREFAB_DIR);

        var summary = new StringBuilder();
        summary.AppendLine($"{LOG_TAG} 설정 결과");
        bool allOk = true;
        foreach (string name in MODEL_NAMES)
        {
            bool ok = SetupModel(name, summary);
            allOk &= ok;
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (allOk) Debug.Log(summary.ToString());
        else Debug.LogWarning(summary.ToString());
    }

    private static bool SetupModel(string name, StringBuilder log)
    {
        string fbxPath = $"{MODEL_DIR}/{name}.fbx";
        string baseColorPath = $"{TEXTURE_DIR}/{name}_BaseColor.png";
        string normalPath = $"{TEXTURE_DIR}/{name}_Normal.png";
        string materialPath = $"{MATERIAL_DIR}/{name}.mat";
        string prefabPath = $"{PREFAB_DIR}/{name}.prefab";

        if (AssetImporter.GetAtPath(fbxPath) is not ModelImporter importer)
        {
            log.AppendLine($"- {name}: FBX 없음 ({fbxPath})");
            return false;
        }

        // 1) 텍스처
        bool baseOk = ConfigureTexture(baseColorPath, isNormal: false);
        ConfigureTexture(normalPath, isNormal: true);
        var baseColor = AssetDatabase.LoadAssetAtPath<Texture2D>(baseColorPath);
        if (!baseOk || baseColor == null)
        {
            log.AppendLine($"- {name}: BaseColor 텍스처 없음 ({baseColorPath})");
            return false;
        }

        // 2) 재질
        Material material = CreateOrUpdateMaterial(materialPath, baseColor);
        if (material == null)
        {
            log.AppendLine($"- {name}: URP Lit 셰이더를 찾지 못해 재질 생성 실패");
            return false;
        }

        // 3) 모델 임포트 설정 + 스케일
        ConfigureModelImporter(importer);
        Measure m = MeasureModel(fbxPath);
        if (m.valid && m.height > 0.0001f)
        {
            float wanted = importer.globalScale * TARGET_HEIGHT / m.height;
            wanted = Mathf.Round(wanted * 10000f) / 10000f;
            if (Mathf.Abs(wanted / importer.globalScale - 1f) > SCALE_TOLERANCE)
            {
                importer.globalScale = wanted;
                importer.SaveAndReimport();
                m = MeasureModel(fbxPath);
            }
        }
        // 아바타 뼈 위치는 처음 만든 스케일로 저장된다. 스케일이 바뀌어 어긋났으면 매핑을 비워 다시 자동 생성한다.
        if (!SkeletonMatchesModel(fbxPath, importer))
        {
            ResetHumanDescription(importer);
            m = MeasureModel(fbxPath);
        }
        if (!m.valid)
        {
            log.AppendLine($"- {name}: 모델 측정 실패 (SkinnedMeshRenderer 없음)");
            return false;
        }

        // 4) Humanoid 확인
        string humanoidResult = CheckHumanoid(fbxPath, importer, out bool humanoidOk);

        // 5) 임베드 재질·텍스처 중복 여부
        Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(fbxPath);
        int embeddedTex = subAssets.Count(a => a is Texture);
        int embeddedMat = subAssets.Count(a => a is Material);

        // 6) 프리팹
        bool pivotOk = Mathf.Abs(m.min.y) <= PIVOT_TOLERANCE_Y
                       && Mathf.Abs(m.centerXZ.x) <= PIVOT_TOLERANCE_XZ
                       && Mathf.Abs(m.centerXZ.y) <= PIVOT_TOLERANCE_XZ;
        bool prefabOk = BuildPrefab(name, fbxPath, prefabPath, material, pivotOk, m);

        log.AppendLine(
            $"- {name}: 키 {m.height:F3}m (bounds Y {m.min.y:F3}~{m.max.y:F3}, 폭 X {m.max.x - m.min.x:F3} / Z {m.max.z - m.min.z:F3}), " +
            $"삼각형 {m.triangles:N0}, globalScale {importer.globalScale:0.####} (useFileScale {importer.useFileScale}, fileScale {importer.fileScale:0.####}), " +
            $"피벗 {(pivotOk ? "발바닥 중앙=원점 OK" : $"보정함 (발 Y {m.min.y:F3}, 중심 XZ {m.centerXZ.x:F3},{m.centerXZ.y:F3})")}, " +
            $"Humanoid {humanoidResult}, 임베드 서브에셋 텍스처 {embeddedTex}/재질 {embeddedMat}, " +
            $"재질 {materialPath}, 프리팹 {(prefabOk ? prefabPath : "생성 실패")}");

        return humanoidOk && prefabOk && embeddedTex == 0 && embeddedMat == 0;
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

        // Rig
        mi.animationType = ModelImporterAnimationType.Human;
        mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        mi.optimizeGameObjects = false;      // IK·손 뼈 Transform을 코드에서 찾을 수 있게 유지

        // Animation (클립 없음)
        mi.importAnimation = false;

        // Materials: 임베드 재질·텍스처를 만들지 않는다. 재질은 프리팹 렌더러에 지정.
        mi.materialImportMode = ModelImporterMaterialImportMode.None;
        foreach (var kv in mi.GetExternalObjectMap().Where(kv => kv.Key.type == typeof(Material)).ToList())
            mi.RemoveRemap(kv.Key);

        mi.SaveAndReimport();
    }

    private struct Measure
    {
        public bool valid;
        public Vector3 min, max;
        public Vector2 centerXZ;
        public float height;
        public int triangles;
    }

    /// <summary>프리뷰 씬에 모델을 꺼내 바인드 포즈 기준 월드 bounds와 삼각형 수를 잰다.</summary>
    private static Measure MeasureModel(string fbxPath)
    {
        var result = new Measure();
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (model == null) return result;

        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model, preview);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one;

            var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) return result;

            bool first = true;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            int tris = 0;
            foreach (var smr in renderers)
            {
                Mesh mesh = smr.sharedMesh;
                if (mesh == null) continue;
                for (int s = 0; s < mesh.subMeshCount; s++)
                    tris += (int)(mesh.GetIndexCount(s) / 3);

                Bounds b = mesh.bounds;
                Matrix4x4 l2w = smr.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3(
                        (i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 w = l2w.MultiplyPoint3x4(corner);
                    if (first) { min = max = w; first = false; }
                    else { min = Vector3.Min(min, w); max = Vector3.Max(max, w); }
                }
            }
            if (first) return result;

            result.valid = true;
            result.min = min;
            result.max = max;
            result.height = max.y - min.y;
            result.centerXZ = new Vector2((min.x + max.x) * 0.5f, (min.z + max.z) * 0.5f);
            result.triangles = tris;
            return result;
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }

    // 아바타 스켈레톤 위치와 실제 모델 뼈 위치의 허용 오차(m)
    private const float SKELETON_TOLERANCE = 0.005f;

    /// <summary>humanDescription.skeleton의 로컬 위치가 현재 임포트된 뼈 위치와 맞는지 확인한다.</summary>
    private static bool SkeletonMatchesModel(string fbxPath, ModelImporter mi)
    {
        SkeletonBone[] skeleton = mi.humanDescription.skeleton;
        // 비어 있으면 임포트할 때마다 현재 스케일로 자동 생성되므로 어긋날 수 없다
        if (skeleton == null || skeleton.Length == 0) return true;

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (model == null) return true;
        var bones = model.GetComponentsInChildren<Transform>(true)
            .GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());

        foreach (SkeletonBone sb in skeleton)
        {
            if (!bones.TryGetValue(sb.name, out Transform t) || t == model.transform) continue;
            if ((sb.position - t.localPosition).magnitude > SKELETON_TOLERANCE) return false;
        }
        return true;
    }

    /// <summary>뼈 매핑과 스켈레톤을 비우고 재임포트해 현재 스케일 기준으로 아바타를 다시 만든다.</summary>
    private static void ResetHumanDescription(ModelImporter mi)
    {
        HumanDescription hd = mi.humanDescription;
        hd.human = new HumanBone[0];
        hd.skeleton = new SkeletonBone[0];
        mi.humanDescription = hd;
        mi.animationType = ModelImporterAnimationType.Human;
        mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        mi.SaveAndReimport();
    }

    private static string CheckHumanoid(string fbxPath, ModelImporter mi, out bool ok)
    {
        var avatar = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Avatar>().FirstOrDefault();
        if (avatar == null) { ok = false; return "실패 (Avatar 없음)"; }
        if (!avatar.isValid || !avatar.isHuman) { ok = false; return $"실패 (isValid {avatar.isValid}, isHuman {avatar.isHuman})"; }

        HumanBone[] human = mi.humanDescription.human ?? new HumanBone[0];
        var mapped = new HashSet<string>(human.Select(h => h.humanName));
        string[] all = HumanTrait.BoneName;
        var missingRequired = all.Where((n, i) => HumanTrait.RequiredBone(i) && !mapped.Contains(n)).ToList();

        // 체결 IK에서 쓰는 손·팔과 머리 매핑을 따로 표시
        string[] keyBones = { "Hips", "Head", "LeftHand", "RightHand", "LeftFoot", "RightFoot" };
        string keyInfo = string.Join(", ", keyBones.Select(k =>
        {
            var hb = human.FirstOrDefault(h => h.humanName == k);
            return $"{k}={(string.IsNullOrEmpty(hb.boneName) ? "없음" : hb.boneName)}";
        }));
        int fingers = human.Count(h => h.humanName.Contains("Index") || h.humanName.Contains("Middle")
                                       || h.humanName.Contains("Ring") || h.humanName.Contains("Little")
                                       || h.humanName.Contains("Thumb"));

        ok = missingRequired.Count == 0;
        return ok
            ? $"OK (매핑 {human.Length}/{all.Length}, 필수 누락 0, 손가락 {fingers}; {keyInfo})"
            : $"필수 뼈 누락: {string.Join(", ", missingRequired)}";
    }

    // ───────────────────────── 프리팹 ─────────────────────────

    private static bool BuildPrefab(string name, string fbxPath, string prefabPath, Material material, bool pivotOk, Measure m)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (model == null) return false;

        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, preview);
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int i = 0; i < mats.Length; i++) mats[i] = material;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
            }
            foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                // 화면 밖에서도 애니메이션 포즈가 튀지 않게 하는 옵션은 꺼둔다 (기본 성능 우선)
                smr.updateWhenOffscreen = false;
            }

            var animator = instance.GetComponent<Animator>();
            if (animator != null)
            {
                animator.applyRootMotion = false;
                animator.runtimeAnimatorController = null; // 클립 없음. 컨트롤러는 사용처에서 지정
            }

            GameObject root;
            if (pivotOk)
            {
                instance.name = name;
                root = instance;
            }
            else
            {
                root = new GameObject(name);
                SceneManager.MoveGameObjectToScene(root, preview);
                instance.name = "Model";
                instance.transform.SetParent(root.transform, false);
                instance.transform.localPosition = new Vector3(-m.centerXZ.x, -m.min.y, -m.centerXZ.y);
            }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool success);
            return success;
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }

    // ───────────────────────── 유틸 ─────────────────────────

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
