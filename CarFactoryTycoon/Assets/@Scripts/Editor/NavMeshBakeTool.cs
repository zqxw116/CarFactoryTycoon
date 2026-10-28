using System.Collections.Generic;
using System.Text;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Splines;

/// <summary>
/// 사람 이동용 NavMesh를 굽는 에디터 툴. 씬 배치가 바뀌면(설비·수조·크레인 추가 등) 메뉴 한 번으로 다시 굽는다.
///
/// 메뉴
///   Tools ▸ CarFactory ▸ 내비메시 다시 굽기   — 아래 1~6을 전부 다시 하고 씬 저장
///   Tools ▸ CarFactory ▸ 횡단보도 추가        — Scene 뷰 중심 근처 라인에 LineCrossing을 하나 만든다(이후 다시 굽기)
///
/// 프로토타입은 벨트 차단/횡단 링크를 제거하고 오르기 0.5m로 일반 경로를 굽는다.
/// 아래 횡단보도 규칙은 구 모드에만 적용한다.
/// 하는 일
///   1) 에이전트 타입 Humanoid(id 0)를 사람 치수로 맞춤: 반경 0.35 / 높이 1.85 / 오르기 0.2 / 경사 30°
///      → 오르기 0.2라 바닥(-0.405)보다 0.2m 넘게 솟은 것(벨트 0.4·받침대 0.4·벽·기계)은 전부 장애물이 된다.
///   2) 'Navigation' 루트에 NavMeshSurface 1개: Default 레이어의 렌더 메시만 수집(차량 6·설비 트리거 7·로봇 8·부품 9·UI 5 제외).
///      → 새로 놓는 소품·설비는 Default 레이어 렌더러면 다시 굽기만으로 장애물이 된다.
///   3) 벨트(MainSpline) 전 구간을 따라 통행 불가 볼륨(NavMeshModifierVolume, Not Walkable)을 1m 간격으로 생성.
///      벨트 메시가 없거나 바뀌어도(구간 제외 등) 스플라인 기준으로 막힌다. 라인은 횡단보도(LineCrossing)로만 건넌다.
///   4) 로봇팔(RoboticArmIK) 베이스 주변을 통행 불가 볼륨으로 막음(받침대 + 팔 회전 여유).
///      방수 공정 바닥 해치(불량 낙하구)도 열리면 빠지므로 통행 불가 볼륨으로 막음.
///   5) 사람(SupplyWorker·Worker)과 횡단보도 표시는 굽기에서 제외(NavMeshModifier ignoreFromBuild) — 서 있는 자리에 구멍이 나지 않게.
///   6) 횡단보도(LineCrossing)마다 바닥 줄무늬 표시를 Decor/Crosswalks 아래에 다시 만든다(콜라이더 없음).
///   7) 굽고 NavMeshData를 씬 폴더(씬이름/NavMesh-Navigation.asset)에 저장, 씬 저장.
/// </summary>
public static class NavMeshBakeTool
{
    const string RootName = "Navigation";
    const string BlockersName = "Blockers";
    const string CrossingsName = "Crossings";
    const string CrosswalkRootPath = "Decor";
    const string CrosswalkGroupName = "Crosswalks";
    const string CrosswalkMatPath = "Assets/@Resource/Materials/MatCrosswalk.mat";

    // 에이전트(사람) 치수
    const float AgentRadius = 0.35f;
    const float AgentHeight = 1.85f;
    const float AgentClimb = 0.2f;
    const float AgentSlope = 30f;

    // 벨트 통행 불가 폭(m) — 벨트 2m + 브래킷·차체 여유
    const float BeltBlockWidth = 2.6f;
    const float BeltSampleStep = 1.0f;
    // 로봇팔 베이스 통행 불가 한 변(m) — 받침대 0.9 + 팔 회전 여유. 크게 잡으면 라인 사이 통로가 끊긴다(1.8에서 실측)
    const float ArmBlockSize = 1.2f;

    const int NotWalkableArea = 1;

    [MenuItem("Tools/CarFactory/내비메시 다시 굽기")]
    public static void RebakeMenu()
    {
        var log = new StringBuilder();
        Rebake(log, true);
        Debug.Log(log.ToString());
    }

    [MenuItem("Tools/CarFactory/횡단보도 추가")]
    static void AddCrossingMenu()
    {
        Vector3 pivot = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
        LineCrossing lc = CreateCrossing("LineCrossing_" + System.DateTime.Now.ToString("HHmmss"), pivot);
        Selection.activeGameObject = lc.gameObject;
        Debug.Log($"[NavBake] 횡단보도 '{lc.name}'를 {lc.transform.position}에 추가했습니다. 위치 조정 후 '내비메시 다시 굽기'를 실행하세요.");
    }

    /// <summary>라인 위 가장 가까운 지점에 횡단보도를 만든다(Navigation/Crossings 아래).</summary>
    public static LineCrossing CreateCrossing(string name, Vector3 nearPoint)
    {
        Transform parent = GetOrCreateChild(GetOrCreateRoot().transform, CrossingsName);
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "횡단보도 추가");
        go.transform.SetParent(parent, false);
        go.transform.position = nearPoint;
        go.AddComponent<NavMeshLink>();
        LineCrossing lc = go.AddComponent<LineCrossing>();
        if (lc.spline == null)
        {
            GameObject sp = GameObject.Find("MainSpline");
            if (sp != null) lc.spline = sp.GetComponent<SplineContainer>();
        }
        lc.SnapToSpline();
        lc.ApplyLink();
        EditorSceneManager.MarkSceneDirty(go.scene);
        return lc;
    }

    public static void Rebake(StringBuilder log, bool save)
    {
        var progression = Object.FindFirstObjectByType<ProductionProgression>();
        bool dynamicTraffic = progression != null && progression.prototypeMode;
        EnsureAgentSettings(log, dynamicTraffic ? .5f : AgentClimb);

        GameObject root = GetOrCreateRoot();
        NavMeshSurface surface = root.GetComponent<NavMeshSurface>();
        if (surface == null) surface = root.AddComponent<NavMeshSurface>();
        surface.agentTypeID = 0;
        surface.collectObjects = CollectObjects.All;
        surface.useGeometry = NavMeshCollectGeometry.RenderMeshes;
        surface.layerMask = 1 << 0; // Default만
        surface.defaultArea = 0;
        surface.minRegionArea = 2f;
        surface.overrideVoxelSize = true;
        surface.voxelSize = 0.1f;   // 기본(반경/3≈0.117)보다 살짝 촘촘히 — 좁은 통로 보존

        // 루트 자체(볼륨·링크뿐)는 굽기 대상이 아니지만 하위에 렌더러가 생겨도 무시되게
        EnsureIgnore(root);

        if (dynamicTraffic)
        {
            var belt = root.transform.Find(BlockersName + "/Belt");
            if (belt != null) Undo.DestroyObjectImmediate(belt.gameObject);
            foreach (var crossing in Object.FindObjectsByType<LineCrossing>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Undo.DestroyObjectImmediate(crossing.gameObject);
            var marks = GameObject.Find(CrosswalkRootPath + "/" + CrosswalkGroupName);
            if (marks != null) Undo.DestroyObjectImmediate(marks);
            log.AppendLine("[NavBake] Prototype: walkable belt, dynamic vehicle avoidance, no crossing links.");
        }
        else BuildBeltBlockers(root.transform, log);
        BuildArmBlockers(root.transform, log);
        BuildHatchBlockers(root.transform, log);
        IgnorePeople(log);

        var crossings = Object.FindObjectsByType<LineCrossing>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var lc in crossings)
        {
            if (lc.snapToSpline) lc.SnapToSpline();
            lc.ApplyLink();
            EditorUtility.SetDirty(lc);
        }
        if (!dynamicTraffic) BuildCrosswalkVisuals(crossings, log);

        // 굽기 + 저장
        surface.RemoveData();
        surface.BuildNavMesh();
        SaveNavMeshData(surface, log);

        var tri = NavMesh.CalculateTriangulation();
        log.AppendLine($"[NavBake] 굽기 완료 — 삼각형 {tri.indices.Length / 3}개, 정점 {tri.vertices.Length}개, 횡단보도 {crossings.Length}개");

        EditorSceneManager.MarkSceneDirty(root.scene);
        if (save) EditorSceneManager.SaveScene(root.scene);
    }

    // ─────────────────────────────────────────────────────────────────────

    static void EnsureAgentSettings(StringBuilder log, float climb)
    {
        Object[] objs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset");
        if (objs == null || objs.Length == 0) { log.AppendLine("[NavBake] NavMeshAreas.asset을 찾지 못함 — 에이전트 설정 생략"); return; }
        var so = new SerializedObject(objs[0]);
        SerializedProperty arr = so.FindProperty("m_Settings");
        for (int i = 0; i < arr.arraySize; i++)
        {
            SerializedProperty s = arr.GetArrayElementAtIndex(i);
            if (s.FindPropertyRelative("agentTypeID").intValue != 0) continue;
            s.FindPropertyRelative("agentRadius").floatValue = AgentRadius;
            s.FindPropertyRelative("agentHeight").floatValue = AgentHeight;
            s.FindPropertyRelative("agentClimb").floatValue = climb;
            s.FindPropertyRelative("agentSlope").floatValue = AgentSlope;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
        log.AppendLine($"[NavBake] 에이전트 Humanoid: 반경 {AgentRadius} / 높이 {AgentHeight} / 오르기 {climb} / 경사 {AgentSlope}");
    }

    static void BuildBeltBlockers(Transform root, StringBuilder log)
    {
        Transform group = GetOrCreateChild(GetOrCreateChild(root, BlockersName), "Belt");
        ClearChildren(group);

        GameObject sp = GameObject.Find("MainSpline");
        SplineContainer sc = sp != null ? sp.GetComponent<SplineContainer>() : null;
        if (sc == null) { log.AppendLine("[NavBake] MainSpline 없음 — 벨트 볼륨 생략"); return; }

        int count = 0;
        for (int si = 0; si < sc.Splines.Count; si++)
        {
            float len = sc.CalculateLength(si);
            int n = Mathf.Max(1, Mathf.CeilToInt(len / BeltSampleStep));
            for (int i = 0; i < n; i++)
            {
                float t0 = (float)i / n, t1 = (float)(i + 1) / n;
                Vector3 a = sc.EvaluatePosition(si, t0);
                Vector3 b = sc.EvaluatePosition(si, t1);
                Vector3 d = b - a; d.y = 0f;
                if (d.sqrMagnitude < 1e-6f) continue;

                var go = new GameObject($"Belt_{si}_{i:000}");
                go.transform.SetParent(group, false);
                go.transform.position = (a + b) * 0.5f;
                go.transform.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
                var v = go.AddComponent<NavMeshModifierVolume>();
                // 곡선 구간 틈이 생기지 않게 길이를 조금 겹친다
                v.size = new Vector3(BeltBlockWidth, 3f, d.magnitude + 0.3f);
                v.center = Vector3.zero;
                v.area = NotWalkableArea;
                count++;
            }
        }
        log.AppendLine($"[NavBake] 벨트 통행 불가 볼륨 {count}개 (폭 {BeltBlockWidth}m)");
    }

    static void BuildArmBlockers(Transform root, StringBuilder log)
    {
        Transform group = GetOrCreateChild(GetOrCreateChild(root, BlockersName), "RobotArms");
        ClearChildren(group);

        var arms = Object.FindObjectsByType<RoboticArmIK>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var arm in arms)
        {
            var go = new GameObject("Arm_" + (arm.transform.parent != null ? arm.transform.parent.name : arm.name));
            go.transform.SetParent(group, false);
            Vector3 p = arm.transform.position;
            go.transform.position = new Vector3(p.x, -0.405f, p.z);
            var v = go.AddComponent<NavMeshModifierVolume>();
            v.size = new Vector3(ArmBlockSize, 3f, ArmBlockSize);
            v.center = new Vector3(0f, 1.2f, 0f);
            v.area = NotWalkableArea;
        }
        log.AppendLine($"[NavBake] 로봇팔 베이스 통행 불가 볼륨 {arms.Length}개 ({ArmBlockSize}m 사각)");
    }

    /// <summary>방수 공정 바닥 해치(불량 차량 낙하구) — 평평해서 걸어갈 수 있지만 열리면 빠지므로 통행 불가로 막는다.</summary>
    static void BuildHatchBlockers(Transform root, StringBuilder log)
    {
        Transform group = GetOrCreateChild(GetOrCreateChild(root, BlockersName), "Hatch");
        ClearChildren(group);

        int n = 0;
        foreach (var st in Object.FindObjectsByType<WaterTestStation>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (st.rejectHatchLeaves == null || st.rejectHatchLeaves.Length == 0 || st.rejectHatchLeaves[0] == null) continue;
            Transform hatchRoot = st.rejectHatchLeaves[0].parent != null ? st.rejectHatchLeaves[0].parent : st.rejectHatchLeaves[0];
            var rs = hatchRoot.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) continue;
            Bounds b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);

            var go = new GameObject("Hatch_" + st.name);
            go.transform.SetParent(group, false);
            go.transform.position = new Vector3(b.center.x, -0.405f, b.center.z);
            var v = go.AddComponent<NavMeshModifierVolume>();
            v.size = new Vector3(b.size.x + 0.2f, 3f, b.size.z + 0.2f);
            v.center = new Vector3(0f, 1.2f, 0f);
            v.area = NotWalkableArea;
            n++;
        }
        log.AppendLine($"[NavBake] 방수 해치 통행 불가 볼륨 {n}개");
    }

    static void IgnorePeople(StringBuilder log)
    {
        int n = 0;
        foreach (var w in Object.FindObjectsByType<SupplyWorker>(FindObjectsInactive.Include, FindObjectsSortMode.None)) { EnsureIgnore(w.gameObject); n++; }
        foreach (var w in Object.FindObjectsByType<Worker>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            // 루트가 로봇 레이어(8)여도 자식 메시는 Default일 수 있어 레이어와 무관하게 제외한다
            EnsureIgnore(w.gameObject);
            n++;
            // 체결 작업자는 아직 NavMesh로 움직이지 않는다 → 비카빙 장애물로 두어 보급 작업자가 국소 회피로 비켜 가게
            var obs = w.GetComponent<NavMeshObstacle>();
            if (obs == null) obs = Undo.AddComponent<NavMeshObstacle>(w.gameObject);
            obs.shape = NavMeshObstacleShape.Capsule;
            obs.radius = 0.35f;
            obs.height = 1.8f;
            obs.center = new Vector3(0f, 0.9f, 0f);
            obs.carving = false;
            EditorUtility.SetDirty(obs);
        }
        log.AppendLine($"[NavBake] 굽기 제외(사람) {n}명 — 체결 작업자는 비카빙 NavMeshObstacle 부착");
    }

    static void EnsureIgnore(GameObject go)
    {
        var m = go.GetComponent<NavMeshModifier>();
        if (m == null) m = Undo.AddComponent<NavMeshModifier>(go);
        m.ignoreFromBuild = true;
        m.applyToChildren = true;
        EditorUtility.SetDirty(m);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 횡단보도 바닥 표시 — 진행 방향 줄무늬(Zebra). 바닥 양쪽 + 벨트 윗면.
    // ─────────────────────────────────────────────────────────────────────

    static void BuildCrosswalkVisuals(LineCrossing[] crossings, StringBuilder log)
    {
        GameObject decor = GameObject.Find(CrosswalkRootPath);
        Transform parent = decor != null ? decor.transform : null;
        Transform group = parent != null ? parent.Find(CrosswalkGroupName) : null;
        if (group == null)
        {
            var g = new GameObject(CrosswalkGroupName);
            if (parent != null) g.transform.SetParent(parent, false);
            group = g.transform;
        }
        group.localPosition = Vector3.zero;
        ClearChildren(group);
        EnsureIgnore(group.gameObject);

        Material mat = GetCrosswalkMaterial();
        int stripes = 0;
        foreach (var lc in crossings)
        {
            if (lc == null || !lc.isActiveAndEnabled) continue;
            var cw = new GameObject("Crosswalk_" + lc.name);
            cw.transform.SetParent(group, false);
            cw.transform.SetPositionAndRotation(lc.transform.position, lc.transform.rotation);

            float span = lc.halfSpan * 2f + 0.6f;   // 링크 끝보다 조금 더 길게
            float width = lc.zoneHalfWidth * 2f;
            const float stripeW = 0.3f, gap = 0.3f;
            int count = Mathf.Max(2, Mathf.FloorToInt((width + gap) / (stripeW + gap)));
            float total = count * stripeW + (count - 1) * gap;
            float floorY = lc.floorY + 0.012f;
            float beltHalf = BeltBlockWidth * 0.5f;

            for (int i = 0; i < count; i++)
            {
                float x = -total * 0.5f + stripeW * 0.5f + i * (stripeW + gap);
                // 바닥 양쪽(벨트 바깥) + 벨트 윗면(y≈0)
                stripes += AddStripe(cw.transform, mat, x, -span * 0.5f, -beltHalf, floorY, stripeW);
                stripes += AddStripe(cw.transform, mat, x, beltHalf, span * 0.5f, floorY, stripeW);
                stripes += AddStripe(cw.transform, mat, x, -beltHalf, beltHalf, 0.012f, stripeW);
            }
        }
        log.AppendLine($"[NavBake] 횡단보도 표시 {crossings.Length}곳 (줄무늬 {stripes}개, Decor/{CrosswalkGroupName})");
    }

    static int AddStripe(Transform parent, Material mat, float x, float z0, float z1, float worldY, float stripeW)
    {
        if (z1 - z0 < 0.05f) return 0;
        var s = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(s.GetComponent<Collider>()); // 클릭 레이캐스트를 가로채지 않게 콜라이더 제거
        s.name = "Stripe";
        s.transform.SetParent(parent, false);
        s.transform.localPosition = new Vector3(x, 0f, (z0 + z1) * 0.5f);
        s.transform.localRotation = Quaternion.identity;
        s.transform.localScale = new Vector3(stripeW, 0.01f, z1 - z0);
        Vector3 wp = s.transform.position;
        s.transform.position = new Vector3(wp.x, worldY, wp.z);
        var r = s.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return 1;
    }

    static Material GetCrosswalkMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(CrosswalkMatPath);
        if (mat != null) return mat;
        mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.name = "MatCrosswalk";
        Color c = new Color(0.95f, 0.85f, 0.3f, 1f); // 바닥 안전 표시 노랑
        mat.SetColor("_BaseColor", c);
        mat.color = c;
        mat.SetFloat("_Smoothness", 0.2f);
        AssetDatabase.CreateAsset(mat, CrosswalkMatPath);
        return mat;
    }

    // ─────────────────────────────────────────────────────────────────────

    static void SaveNavMeshData(NavMeshSurface surface, StringBuilder log)
    {
        var data = surface.navMeshData;
        if (data == null) { log.AppendLine("[NavBake] 굽기 결과 없음"); return; }

        var scene = surface.gameObject.scene;
        string dir = System.IO.Path.GetDirectoryName(scene.path).Replace('\\', '/') + "/" + scene.name;
        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(dir).Replace('\\', '/'), scene.name);
        string path = dir + "/NavMesh-" + surface.name + ".asset";

        if (AssetDatabase.LoadAssetAtPath<NavMeshData>(path) != null) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(data, path);
        AssetDatabase.SaveAssets();

        // 저장한 에셋을 표면에 다시 연결(씬에 참조가 직렬화되도록)
        var so = new SerializedObject(surface);
        so.FindProperty("m_NavMeshData").objectReferenceValue = data;
        so.ApplyModifiedPropertiesWithoutUndo();
        surface.RemoveData();
        surface.AddData();
        log.AppendLine($"[NavBake] NavMeshData 저장: {path}");
    }

    static GameObject GetOrCreateRoot()
    {
        GameObject root = GameObject.Find(RootName);
        if (root == null)
        {
            root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Navigation 생성");
        }
        return root;
    }

    static Transform GetOrCreateChild(Transform parent, string name)
    {
        Transform t = parent.Find(name);
        if (t != null) return t;
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    static void ClearChildren(Transform t)
    {
        var list = new List<GameObject>();
        foreach (Transform c in t) list.Add(c.gameObject);
        foreach (var go in list) Object.DestroyImmediate(go);
    }
}
