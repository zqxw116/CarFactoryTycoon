using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>
/// Kenney Factory Kit 컨베이어 모듈을 MainSpline 위에 겹쳐 놓는 시각용 벨트(KenneyConveyor)를 씬에 배치하는 편집기 도구.
/// - 메뉴: Tools/Kenney 컨베이어/씬에 배치, Tools/Kenney 컨베이어/곡선 구간 스크린샷
/// - Library/KenneyConveyorSetup.trigger 파일이 있으면 스크립트 로드 직후 1회 자동 실행 (MCP 미연결 대비)
/// </summary>
[InitializeOnLoad]
public static class KenneyConveyorSetupTool
{
    const string RootName = "KenneyConveyor";
    const string ModelDir = "Assets/ThirdParty/Kenney/FactoryKit/Models/";
    const string MaterialPath = "Assets/ThirdParty/Kenney/FactoryKit/Materials/KenneyColormap_URP.mat";
    const string TriggerPath = "Library/KenneyConveyorSetup.trigger";
    const string LogPath = "Library/KenneyConveyorSetup.log";

    const string StripeTexturePath = "Assets/ThirdParty/Kenney/FactoryKit/Materials/KenneyBeltStripe.png";
    const string StripeMaterialPath = "Assets/ThirdParty/Kenney/FactoryKit/Materials/KenneyBeltStripe_URP.mat";

    // 윗면을 차량 바닥(스플라인 y=0, 바퀴 최저 -0.006)에 맞춰 5mm 아래로.
    // 바닥 Plane이 y=-0.1이라 보이는 두께는 약 0.1m → 모듈 0.4m × 0.25 로 바닥에 딱 얹힘 (더 키우면 브래킷이 바닥 아래로 묻힘)
    const float TopAboveSpline = -0.005f;
    const float ModuleHeight = 0.4f;
    const float BracketScale = 1.5f;
    static float s_HeightScale = 0.25f;
    static string s_Middle = "conveyor-long-stripe-sides-part-middle";
    static string s_End = "conveyor-long-stripe-sides-part-end";

    static KenneyConveyorSetupTool()
    {
        EditorApplication.delayCall += TryAutoRun;
    }

    static void TryAutoRun()
    {
        if (!File.Exists(TriggerPath)) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
        if (EditorSceneManager.GetActiveScene().name != "Car_Factory 3") return;

        // 트리거 파일: 1줄 스크린샷 폴더, (선택) 2줄 가운데 모듈 이름, 3줄 끝단 모듈 이름, 4줄 높이 배율
        var lines = File.ReadAllLines(TriggerPath);
        File.Delete(TriggerPath);
        string shotDir = lines.Length > 0 ? lines[0].Trim() : "";
        if (lines.Length > 1 && lines[1].Trim().Length > 0) s_Middle = lines[1].Trim();
        if (lines.Length > 2 && lines[2].Trim().Length > 0) s_End = lines[2].Trim();
        if (lines.Length > 3 && float.TryParse(lines[3].Trim(), out var hs)) s_HeightScale = hs;
        var log = new StringBuilder();
        try
        {
            Setup(log, true);
            if (!string.IsNullOrEmpty(shotDir)) CaptureCurves(shotDir, log);
        }
        catch (System.Exception e) { log.AppendLine("예외: " + e); }
        File.WriteAllText(LogPath, log.ToString());
    }

    [MenuItem("Tools/Kenney 컨베이어/씬에 배치")]
    static void SetupMenu()
    {
        var log = new StringBuilder();
        Setup(log, false);
        Debug.Log(log.ToString());
    }

    [MenuItem("Tools/Kenney 컨베이어/곡선 구간 스크린샷")]
    static void CaptureMenu()
    {
        var log = new StringBuilder();
        CaptureCurves(Path.GetFullPath("Library/KenneyConveyorShots"), log);
        Debug.Log(log.ToString());
    }

    static void Setup(StringBuilder log, bool save)
    {
        var mainSpline = GameObject.Find("MainSpline");
        if (mainSpline == null) { log.AppendLine("MainSpline 없음"); return; }
        var container = mainSpline.GetComponent<SplineContainer>();

        var middle = AssetDatabase.LoadAssetAtPath<GameObject>(ModelDir + s_Middle + ".fbx");
        var end = AssetDatabase.LoadAssetAtPath<GameObject>(ModelDir + s_End + ".fbx");
        log.AppendLine($"[모듈] 가운데 {s_Middle}, 끝단 {s_End}, 높이배율 {s_HeightScale}");
        if (middle == null) { log.AppendLine("모듈 FBX 로드 실패"); return; }

        var material = EnsureMaterial(log);

        var root = GameObject.Find(RootName);
        if (root == null)
        {
            root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Kenney 컨베이어 배치");
        }
        root.layer = 0;
        root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        root.transform.localScale = Vector3.one;

        var deformer = root.GetComponent<SplineConveyorDeformer>();
        if (deformer == null) deformer = root.AddComponent<SplineConveyorDeformer>();

        // 기존 벨트 폭: CreateSplineMesh 는 중심에서 ±(globalWidth × 폭데이터) 이므로 전체 폭 = 2배
        var loft = mainSpline.GetComponent<Unity.Splines.Examples.CreateSplineMesh>();
        var so = new SerializedObject(deformer);
        so.FindProperty("m_Spline").objectReferenceValue = container;
        so.FindProperty("m_MiddleModule").objectReferenceValue = middle;
        so.FindProperty("m_EndModule").objectReferenceValue = end;
        so.FindProperty("m_Material").objectReferenceValue = material;
        so.FindProperty("m_EndCapAtPositive").boolValue = false; // 임포트 시 X 반전 → 마감이 -X 쪽 (스크린샷으로 확인)
        so.FindProperty("m_SliceStep").floatValue = 0f; // 0.5m 모듈이라 자르지 않아도 곡선 오차 1cm 미만
        so.FindProperty("m_HeightScale").floatValue = s_HeightScale;
        so.FindProperty("m_HeightOffset").floatValue = TopAboveSpline - ModuleHeight * s_HeightScale;
        so.FindProperty("m_BracketScale").floatValue = BracketScale;
        so.FindProperty("m_SurfaceMaterial").objectReferenceValue = EnsureStripeMaterial(log);
        so.FindProperty("m_StripeSpacing").floatValue = 0.125f; // 홈 사이 0.125m (텍스처 한 장 8줄 = 1m)
        so.FindProperty("m_SyncLineSpeed").boolValue = true;
        so.FindProperty("m_LineSource").objectReferenceValue = Object.FindAnyObjectByType<CarSpawner>();
        so.FindProperty("m_OriginalBeltRenderer").objectReferenceValue = mainSpline.GetComponent<MeshRenderer>();
        so.FindProperty("m_HideOriginalBelt").boolValue = true;
        float baseWidth = loft != null ? loft.globalWidth * 2f : 2f;
        so.FindProperty("m_Width").floatValue = baseWidth;
        var widths = so.FindProperty("m_SplineWidths");
        widths.ClearArray();
        if (loft != null)
        {
            for (int i = 0; i < loft.Widths.Count; i++)
            {
                widths.InsertArrayElementAtIndex(i);
                widths.GetArrayElementAtIndex(i).floatValue = baseWidth * MaxWidthValue(loft.Widths[i]);
            }
        }
        so.ApplyModifiedPropertiesWithoutUndo();
        deformer.Rebuild();

        var mesh = root.GetComponent<MeshFilter>().sharedMesh;
        var rend = root.GetComponent<MeshRenderer>();
        log.AppendLine($"[배치] {RootName} 생성/갱신. 정점 {mesh?.vertexCount}, 삼각형 {(mesh != null ? mesh.triangles.Length / 3 : 0)}, bounds {rend.bounds}");
        log.AppendLine($"[배치] 머티리얼 {rend.sharedMaterial?.name} / 셰이더 {rend.sharedMaterial?.shader?.name}");
        log.AppendLine($"[배치] 기본폭 {baseWidth}, 스플라인별 폭 [{string.Join(",", Enumerable.Range(0, widths.arraySize).Select(i => widths.GetArrayElementAtIndex(i).floatValue))}]");

        // 스플라인 정보와 기존 벨트·차량 높이
        log.AppendLine($"[스플라인] MainSpline pos {mainSpline.transform.position} rot {mainSpline.transform.eulerAngles} scale {mainSpline.transform.lossyScale}");
        for (int i = 0; i < container.Splines.Count; i++)
        {
            var s = container.Splines[i];
            var p0 = container.EvaluatePosition(i, 0f); var p1 = container.EvaluatePosition(i, 1f);
            log.AppendLine($"  spline{i}: knots {s.Count}, 길이 {s.GetLength():F2}m, closed {s.Closed}, 시작 {p0}, 끝 {p1}");
        }
        var oldRend = mainSpline.GetComponent<MeshRenderer>();
        if (oldRend != null) log.AppendLine($"[기존벨트] bounds {oldRend.bounds}");
        foreach (var car in Object.FindObjectsByType<CarController>(FindObjectsInactive.Include, FindObjectsSortMode.None).Take(5))
        {
            var rs = car.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) continue;
            var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
            log.AppendLine($"[차량] {car.name} pos {car.transform.position} 렌더러 최저 y {b.min.y:F3}");
        }

        // 차량 프리팹 바닥 높이 (차량 피벗 = 스플라인 위치)
        var carPrefab = Resources.Load<GameObject>("Prefabs/CarModel");
        if (carPrefab != null)
        {
            var tmp = Object.Instantiate(carPrefab);
            tmp.hideFlags = HideFlags.HideAndDontSave;
            tmp.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var rs = tmp.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
            if (rs.Length > 0)
            {
                var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                log.AppendLine($"[차량] CarModel 프리팹 렌더러 최저 y(피벗 기준) {b.min.y:F3}, 크기 {b.size}, 새 벨트 윗면 y {TopAboveSpline:F3}");
            }
            Object.DestroyImmediate(tmp);
        }

        EditorSceneManager.MarkSceneDirty(root.scene);
        if (save) log.AppendLine($"[저장] {EditorSceneManager.SaveScene(root.scene)}");
    }

    static float MaxWidthValue(SplineData<float> data)
    {
        if (data == null) return 1f;
        float v = data.Count > 0 ? 0f : data.DefaultValue;
        for (int i = 0; i < data.Count; i++) v = Mathf.Max(v, data[i].Value);
        return v > 0f ? v : 1f;
    }

    static Material EnsureMaterial(StringBuilder log)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat != null) return mat;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) { log.AppendLine("URP Lit 셰이더 없음"); return null; }
        Directory.CreateDirectory(Path.GetDirectoryName(MaterialPath));
        mat = new Material(shader) { name = "KenneyColormap_URP" };
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(ModelDir + "Textures/colormap.png");
        mat.SetTexture("_BaseMap", tex);
        mat.SetFloat("_Smoothness", 0.2f);
        AssetDatabase.CreateAsset(mat, MaterialPath);
        AssetDatabase.SaveAssets();
        log.AppendLine($"[머티리얼] 생성 {MaterialPath}, 텍스처 {(tex != null ? "OK" : "없음")}");
        return mat;
    }

    /// <summary>
    /// 벨트 윗면용 줄무늬 머티리얼. 텍스처가 없으면 코드로 생성해 PNG로 저장한다.
    /// - 어두운 고무 바탕 + 진행 방향(V)으로 반복되는 가로 홈, 밝기 그라데이션 없음 → 상하·좌우 이음 없음
    /// - 색은 Kenney colormap 팔레트의 푸른 회색 계열에 맞춤
    /// </summary>
    static Material EnsureStripeMaterial(StringBuilder log)
    {
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(StripeTexturePath);
        if (tex == null)
        {
            const int size = 256, period = 32; // 타일 1장에 홈 8줄
            var baseCol = new Color(0.27f, 0.27f, 0.34f);
            var grooveCol = new Color(0.15f, 0.15f, 0.20f);
            var edgeCol = new Color(0.36f, 0.36f, 0.45f);
            var pixels = new Color32[size * size];
            var rng = new System.Random(1234);
            for (int y = 0; y < size; y++)
            {
                int ph = y % period;
                // 홈 6px(양 끝 1px은 중간 톤) + 바로 위 1px 밝은 모서리
                Color row = ph < 6 ? (ph == 0 || ph == 5 ? Color.Lerp(baseCol, grooveCol, 0.5f) : grooveCol)
                          : ph == 6 ? edgeCol : baseCol;
                for (int x = 0; x < size; x++)
                {
                    float n = (float)(rng.NextDouble() - 0.5) * 0.03f; // 고르게 퍼진 잡티 (그라데이션 아님)
                    pixels[y * size + x] = new Color(row.r + n, row.g + n, row.b + n, 1f);
                }
            }
            var gen = new Texture2D(size, size, TextureFormat.RGBA32, false);
            gen.SetPixels32(pixels);
            gen.Apply();
            File.WriteAllBytes(StripeTexturePath, gen.EncodeToPNG());
            Object.DestroyImmediate(gen);
            AssetDatabase.ImportAsset(StripeTexturePath);

            var importer = (TextureImporter)AssetImporter.GetAtPath(StripeTexturePath);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.SaveAndReimport();
            tex = AssetDatabase.LoadAssetAtPath<Texture2D>(StripeTexturePath);
            log.AppendLine($"[줄무늬] 텍스처 생성 {StripeTexturePath}");
        }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(StripeMaterialPath);
        if (mat != null) return mat;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) { log.AppendLine("URP Lit 셰이더 없음"); return null; }
        mat = new Material(shader) { name = "KenneyBeltStripe_URP" };
        mat.SetTexture("_BaseMap", tex);
        mat.SetFloat("_Smoothness", 0.15f);
        AssetDatabase.CreateAsset(mat, StripeMaterialPath);
        AssetDatabase.SaveAssets();
        log.AppendLine($"[줄무늬] 머티리얼 생성 {StripeMaterialPath}");
        return mat;
    }

    /// <summary>스플라인마다 가장 많이 꺾인 지점을 찾아, KenneyConveyor 켬/끔 상태로 각각 촬영한다.</summary>
    static void CaptureCurves(string dir, StringBuilder log)
    {
        var mainSpline = GameObject.Find("MainSpline");
        var root = GameObject.Find(RootName);
        if (mainSpline == null || root == null) { log.AppendLine("[촬영] 대상 없음"); return; }
        var container = mainSpline.GetComponent<SplineContainer>();
        Directory.CreateDirectory(dir);

        var camGo = new GameObject("KenneyShotCam") { hideFlags = HideFlags.HideAndDontSave };
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 50f; cam.nearClipPlane = 0.1f; cam.farClipPlane = 500f;
        var rt = new RenderTexture(1280, 720, 24);
        cam.targetTexture = rt;

        try
        {
            // 전체 조감
            var all = root.GetComponent<MeshRenderer>().bounds;
            var overviewPos = all.center + new Vector3(0f, all.extents.magnitude * 1.1f, -all.extents.magnitude * 0.6f);
            Shoot(cam, rt, overviewPos, all.center, Path.Combine(dir, "overview_on.png"), root, true, log);

            for (int i = 0; i < container.Splines.Count; i++)
            {
                var s = container.Splines[i];
                float bestT = 0.5f, bestAngle = -1f;
                const int samples = 200;
                for (int k = 1; k < samples - 1; k++)
                {
                    float t = k / (float)samples;
                    Vector3 a = (Vector3)container.EvaluateTangent(i, t - 1f / samples);
                    Vector3 b = (Vector3)container.EvaluateTangent(i, t + 1f / samples);
                    float ang = Vector3.Angle(a, b);
                    if (ang > bestAngle) { bestAngle = ang; bestT = t; }
                }
                Vector3 p = container.EvaluatePosition(i, bestT);
                Vector3 tan = ((Vector3)container.EvaluateTangent(i, bestT)).normalized;
                var side = Vector3.Cross(Vector3.up, tan).normalized;
                var camPos = p + Vector3.up * 7f - tan * 6f + side * 5f;
                Shoot(cam, rt, camPos, p, Path.Combine(dir, $"spline{i}_curve_on.png"), root, true, log);
                Shoot(cam, rt, camPos, p, Path.Combine(dir, $"spline{i}_curve_off.png"), root, false, log);
                log.AppendLine($"[촬영] spline{i} 최대 꺾임 t={bestT:F3} 위치 {p} (샘플 간 {bestAngle:F1}도)");

                // 끝단 근접 + 차량 임시 배치 (저장 안 됨)
                foreach (var endT in new[] { 0f, 1f })
                {
                    Vector3 ep = container.EvaluatePosition(i, endT);
                    Vector3 et = ((Vector3)container.EvaluateTangent(i, endT)).normalized;
                    var es = Vector3.Cross(Vector3.up, et).normalized;
                    float outward = endT == 0f ? -1f : 1f;
                    Shoot(cam, rt, ep + Vector3.up * 2.5f + et * outward * 3.5f + es * 2.5f, ep,
                        Path.Combine(dir, $"spline{i}_end{(endT == 0f ? "start" : "finish")}.png"), root, true, log);
                }

                // 곡선 구간에 차량 임시 배치 후 근접 촬영 (HideAndDontSave, 촬영 뒤 삭제)
                var carPrefab = Resources.Load<GameObject>("Prefabs/CarModel");
                if (carPrefab != null)
                {
                    var car = Object.Instantiate(carPrefab);
                    car.hideFlags = HideFlags.HideAndDontSave;
                    car.transform.SetPositionAndRotation(p, Quaternion.LookRotation(tan, Vector3.up));
                    Shoot(cam, rt, p + Vector3.up * 2.2f - tan * 4f + side * 3.5f, p, Path.Combine(dir, $"spline{i}_curve_car.png"), root, true, log);
                    Shoot(cam, rt, p + Vector3.up * 0.6f + side * 4.5f, p + Vector3.up * 0.3f, Path.Combine(dir, $"spline{i}_curve_car_side.png"), root, true, log);
                    Object.DestroyImmediate(car);
                }
            }
        }
        finally
        {
            root.SetActive(true);
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(camGo);
        }
    }

    static void Shoot(Camera cam, RenderTexture rt, Vector3 pos, Vector3 target, string file, GameObject root, bool rootOn, StringBuilder log)
    {
        root.SetActive(rootOn);
        cam.transform.position = pos;
        cam.transform.LookAt(target);
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(file, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        log.AppendLine($"[촬영] {file}");
    }
}
