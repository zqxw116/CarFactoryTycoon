using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// AI 생성 통짜 세단 메쉬(Car_Sedan.fbx)를 체결용 부품(미러 2, 문 4, 후드, 트렁크, 범퍼 2, 앞/뒷유리, 헤드램프 2, 그릴, 테일램프 2)과
/// "패널이 빠진 빈 차체 껍데기(Car_Sedan_Frame)"로 나누는 에디터 툴. CarPartSplitPoC 의 평면 클리핑 방식을 확장했다.
///
///  - 부품 정의(평면/합집합 영역/제외 원기둥/두께 제외 상자/피벗/분리 위치)는 코드가 아니라
///    Assets/@Scripts/Editor/Data/SedanSplitDefinition.json 에 있다. 부품 추가·수정은 JSON만 고치면 된다.
///  - 절단: 걸친 삼각형을 평면으로 정확히 잘라(위치/법선/탄젠트/UV 보간) 차체와 부품이 같은 절단 정점을 공유 → 틈 없음.
///    잘린 뒤 생기는 T자 정점은 보정해서 닫힌 메쉬(열린 모서리 0)를 만든다.
///  - 차체: 바깥 면 + 안쪽 벽(바깥 면을 -법선으로 innerWallDepth 오프셋, 면 뒤집음, 내장 재질) + 모든 경계에 림 띠.
///    → 문/후드 등을 떼면 속이 비어 있고 구멍 너머로 반대편 바깥이 비치지 않는다. PoC의 속 채움(Cavity) 메쉬는 쓰지 않는다.
///  - 부품: panelThickness 두께의 닫힌 판재(바깥 면 + 안쪽 면 + 림). 안쪽 면은 내장 재질(서브메쉬 1).
///  - 얇은 곳(필러, 미러 받침 등)에서 오프셋이 반대편을 뚫지 않게 원본 메쉬로 레이를 쏴 깊이를 depthClampRatio 배로 제한.
///  - 절단 경계 정점의 오프셋 방향은 절단 평면 위로 투영 → 림이 절단면 안에 정확히 놓인다.
///
/// 산출물 (재실행 시 같은 경로 덮어씀 → GUID 유지, 기존 에셋은 수정하지 않음):
///  Models/Vehicles/SedanParts/*.asset, Car_Sedan_SplitParts.json(assembledPos·힌지축),
///  Materials/Vehicles/SedanParts/Car_Sedan_Interior.mat,
///  Prefabs/Vehicles/SedanParts/Car_Sedan_Assembled.prefab, Car_Sedan_Exploded.prefab
///
/// 메뉴: Tools ▸ CarFactory ▸ Vehicles ▸ Split Sedan Parts / Split Sedan Parts - Verify Screenshots
/// </summary>
public static class SedanPartSplitter
{
    private const string MENU_SPLIT = "Tools/CarFactory/Vehicles/Split Sedan Parts";
    private const string MENU_VERIFY = "Tools/CarFactory/Vehicles/Split Sedan Parts - Verify Screenshots";
    private const string MENU_STAGE3 = "Tools/CarFactory/Vehicles/Split Sedan Parts - Stage3 Feasibility Test";
    private const string LOG_TAG = "[세단 분할]";

    private const string DATA_PATH = "Assets/@Scripts/Editor/Data/SedanSplitDefinition.json";
    private const string DATA_PATH_STAGE3 = "Assets/@Scripts/Editor/Data/SedanSplitDefinition_Stage3Test.json";
    private const string SRC_MESH = "Assets/@Resource/Models/Vehicles/Car_Sedan.fbx";
    private const string SRC_MAT = "Assets/@Resource/Materials/Vehicles/Car_Sedan.mat";
    private const string SRC_PREFAB = "Assets/@Resource/Prefabs/Vehicles/Car_Sedan.prefab";
    private const string SRC_TEX = "Assets/@Resource/Textures/Vehicles/Car_Sedan_BaseColor.png";
    private const string MESH_DIR = "Assets/@Resource/Models/Vehicles/SedanParts";
    private const string MAT_DIR = "Assets/@Resource/Materials/Vehicles/SedanParts";
    private const string PREFAB_DIR = "Assets/@Resource/Prefabs/Vehicles/SedanParts";
    private const string SHOT_DIR = "Assets/Screenshots";
    private const string SHOT_TAG = "SplitV5"; // 검증 스크린샷 파일 접두어(작업 차수)

    private const float ON_PLANE_EPS = 2e-4f;

    /// <summary>자동화(MCP)용: 원본 대신 읽을 같은 내용의 읽기 가능 메쉬. 보통 null.</summary>
    public static Mesh SourceOverride;
    private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    // ───────────── JSON 데이터 ─────────────
    [Serializable] public class PlaneData
    {
        public string name; public Vector3 normal; public Vector3 point; public bool refine;
        public string sameAs; public bool flip;
        public bool usePoints; public Vector3 p1, p2, p3, inside;
    }
    [Serializable] public class RegionData { public string name; public List<PlaneData> planes = new List<PlaneData>(); }
    [Serializable] public class CylinderData { public string name; public string axis = "x"; public Vector3 center; public float radius; public int segments = 24; }
    [Serializable] public class BoxData { public Vector3 min, max; }
    /// <summary>
    /// 볼록 다각형 기둥(axis 방향, 현재 z 만): center 기준 (각도°, 반지름) 꼭짓점을 이은 변마다 평면 1개.
    /// 원기둥 근사와 달리 실제 메쉬의 림 꼭짓점(예: 헤드램프 12각 림의 바깥 링) 각도/반지름을 그대로 써서
    /// 림 바깥 주름선 바로 바깥을 지나게 한다 → 주변 그릴/펜더 조각이 부품에 딸려오지 않는다.
    /// </summary>
    [Serializable] public class PolygonData { public string name; public string axis = "z"; public Vector3 center; public List<float> anglesDeg = new List<float>(); public List<float> radii = new List<float>(); }
    [Serializable] public class PartData
    {
        public string name; public int stage = 1; public string pivot = "center"; public Vector3 hingeAxis;
        public List<PlaneData> planes = new List<PlaneData>();
        public List<RegionData> extraRegions = new List<RegionData>();
        public List<CylinderData> includeCylinders = new List<CylinderData>();
        public List<CylinderData> excludeCylinders = new List<CylinderData>();
        public List<PolygonData> includePolygons = new List<PolygonData>();
        /// <summary>
        /// 원본의 "지느러미" 삼각형(한 변은 열린 모서리, 다른 변은 3면 이상 공유 = 접힌 조각/떠 있는 판)을 이 부품이 차지할 때 버린다.
        /// 원본 비다양체가 부품의 열린 모서리/비다양체로 그대로 옮겨오는 것을 막는다. (기존 부품 메쉬 불변을 위해 부품별 선택)
        /// </summary>
        public bool removeFins;
        /// <summary>
        /// 0 이 아니면 안쪽 오프셋 껍데기 대신 "평평한 뒷판"으로 닫는다: 바깥 면 + 경계 → 뒷판 평면으로 내린 치마(림) + 뒷판 부채꼴 마개.
        /// 뒷판 평면 = 이 법선 방향으로 가장 먼 바깥 면 정점을 지나는 평면(= rearFace/frontFace 피벗 면). 그릴 슬랫·램프 림처럼
        /// 요철이 깊어 2cm 오프셋이 구겨지는 부품용. 경계 절단 평면이 이 법선과 평행해야 치마가 절단면 안에 놓인다.
        /// </summary>
        public Vector3 backCapNormal;
        public List<BoxData> noThickBoxes = new List<BoxData>();
        public Vector3 explodeOffset; public Vector3 explodeEuler;
    }
    /// <summary>
    /// 빈 차체 만들기: 원본 메쉬에서 "밖에서 안 보이는(가려진)" 삼각형 중 clear 상자 안에 있는 것을 Frame 에서 뺀다.
    /// 원본은 유리까지 불투명한 통짜라 시트/대시보드/엔진룸 덩어리는 밖에서 보이지 않는다 → 가시성으로 껍데기와 구분.
    /// </summary>
    [Serializable] public class ClearBox { public string name; public Vector3 min, max; }
    [Serializable] public class InteriorClearData
    {
        public bool enabled = false;
        public int rays = 160;                // 반구 방향 수(구 전체 2배 샘플 중 법선 쪽 절반)
        public List<ClearBox> boxes = new List<ClearBox>();
        /// <summary>
        /// 막음판: 내장 재질의 닫힌 상자를 Frame 에 더한다(엔진룸/실내 칸막이, 그릴·번호판 뒤 요철 가림판).
        /// 조립 상태에서 밖에서 보이지 않는 위치에만 둘 것(화소 비교로 확인).
        /// </summary>
        public List<ClearBox> fillBoxes = new List<ClearBox>();
        /// <summary>
        /// 휠하우스(아치) 가림: 바퀴 위를 덮는 X축 반원통 지붕(xIn~xOut) + 안쪽(xIn) 반원 마개. 양면, 내장 재질.
        /// 상자형 옆판+윗판 대신 벽에 붙은 둥근 혹으로 보이게 한다(실내에서 타이어가 비치지 않게).
        /// </summary>
        public List<WheelTubData> wheelTubs = new List<WheelTubData>();
    }
    [Serializable] public class WheelTubData
    {
        public string name;
        public float side = -1f;             // -1 = 왼쪽(-X), +1 = 오른쪽
        public float xIn = 0.575f, xOut = 0.89f; // |x| 범위(안쪽 마개 위치 ~ 차체 옆벽 속)
        public float cy = 0.36f, cz;         // 바퀴 축 높이/앞뒤 위치
        public float radius = 0.44f;         // 아치 반지름(타이어 반지름 + 여유)
        public float yMin = 0.36f;           // 지붕(호)은 이 높이 위만. 낮추면 아치 앞뒤 끝이 타이어 밖으로 나와 바깥에서 보인다
        public float capYMin = 0.27f;        // 안쪽 마개는 이 높이(바닥 아래)까지 내려 바닥과 아치 사이 틈을 막는다
        public float capBottomHalf = 0f;     // >0 이면 마개 아래 모서리의 앞뒤 반폭(cz±). 타이어 실루엣 안으로 좁혀 옆 문턱 틈으로 비치지 않게
        public int segments = 20;
    }
    /// <summary>Frame 안쪽 벽 다듬기: 바닥 평탄화 + 라플라시안 평활(바깥 디테일이 안쪽에 비치는 것 완화).</summary>
    [Serializable] public class InnerWallData
    {
        public int smoothIterations = 0;
        public float smoothLambda = 0.5f;
        public float minDepthRatio = 0.4f;   // 평활 후에도 안쪽 벽은 바깥 면에서 법선 방향으로 이 비율×깊이 이상 안쪽
        public bool inwardOnly = false;      // 평활 이동 중 바깥(법선) 쪽 성분은 버림 → 오목 디테일이 안쪽에 비치지 않음
        public float maxDepth = 0f;          // >0 이면 안쪽 벽이 바깥 면에서 이 거리(m) 이상 깊어지지 않게
        public bool smoothNormals = false;   // 안쪽 벽 법선을 평활된 형상에서 다시 계산(부드러운 음영)
        public bool flattenFloor = false;
        public Vector3 floorMin, floorMax;   // 바닥 외판(아래 향한 면) 정점 범위
        public float floorY = 0.285f;        // 안쪽 바닥 높이(이보다 낮은 안쪽 바닥 정점을 이 높이로 올림)
        public float floorNormalY = -0.7f;   // 법선 y 가 이 값 이하인 정점만 바닥 외판으로 본다
    }
    [Serializable] public class SplitDefinition
    {
        public string outputSubfolder = "";
        public InteriorClearData interiorClear = new InteriorClearData();
        public InnerWallData frameInnerWall = new InnerWallData();
        public InnerWallData partInnerWall = null; // 부품(문 등) 안쪽 면 평활. null 이면 이전과 동일(바깥 면은 어느 쪽이든 그대로)
        public float panelThickness = 0.02f;
        public float innerWallDepth = 0.025f;
        public float depthClampRatio = 0.45f;
        public Color interiorColor = new Color(0.22f, 0.19f, 0.165f, 1f);
        public float refineRange = 0.03f, refineStep = 0.002f, refineSide = 0.012f, refineMinContrast = -0.03f;
        public List<PartData> parts = new List<PartData>();
    }

    // ───────────── 런타임 구조 ─────────────
    private struct V
    {
        public Vector3 p; public Vector3 n; public Vector4 t; public Vector2 uv;
        public static V Lerp(V a, V b, float k)
        {
            return new V
            {
                p = Vector3.Lerp(a.p, b.p, k),
                n = Vector3.Lerp(a.n, b.n, k).normalized,
                t = Vector4.Lerp(a.t, b.t, k),
                uv = Vector2.Lerp(a.uv, b.uv, k),
            };
        }
    }

    /// <summary>안쪽 조건: dot(n, p) &lt;= d</summary>
    private class CutPlane
    {
        public string name; public Vector3 n; public float d; public bool refine;
        public float delta; public float contrast;
        public CutPlane(string name, Vector3 normal, Vector3 point, bool refine)
        {
            this.name = name; n = normal.normalized; d = Vector3.Dot(n, point); this.refine = refine;
        }
        public float Dist(Vector3 p) { return Vector3.Dot(n, p) - d; }
    }

    private class Part
    {
        public PartData data; public string name;
        public List<CutPlane> planes = new List<CutPlane>();
        public List<List<CutPlane>> extraRegions = new List<List<CutPlane>>();
        public List<List<CutPlane>> excludeRegions = new List<List<CutPlane>>();
        public Func<Vector3, bool> thickKeep = p => true;
        public Builder shell = new Builder();
        public Builder thick;
        public Vector3 pivot;
        public int tJunctionFixes;
        public IEnumerable<List<CutPlane>> IncludeRegions() { yield return planes; foreach (var r in extraRegions) yield return r; }
        public List<CutPlane> AllCutPlanes() { return IncludeRegions().Concat(excludeRegions).SelectMany(r => r).ToList(); }
    }

    private class Builder
    {
        public readonly List<V> verts = new List<V>();
        public readonly List<int>[] subs = { new List<int>(), new List<int>() };
        private readonly Dictionary<(Vector3, Vector2, Vector3), int> map = new Dictionary<(Vector3, Vector2, Vector3), int>();
        public List<int> tris => subs[0];
        public int Add(V v)
        {
            var key = (v.p, v.uv, v.n);
            if (map.TryGetValue(key, out int i)) return i;
            i = verts.Count; verts.Add(v); map[key] = i; return i;
        }
        public void AddPoly(List<V> poly, int sub = 0)
        {
            if (poly.Count < 3) return;
            int i0 = Add(poly[0]);
            for (int k = 1; k + 1 < poly.Count; k++)
            {
                if (Vector3.Cross(poly[k].p - poly[0].p, poly[k + 1].p - poly[0].p).sqrMagnitude < 1e-20f) continue;
                subs[sub].Add(i0); subs[sub].Add(Add(poly[k])); subs[sub].Add(Add(poly[k + 1]));
            }
        }
        public void AddTri(V a, V b, V c, int sub)
        {
            if (Vector3.Cross(b.p - a.p, c.p - a.p).sqrMagnitude < 1e-20f) return;
            subs[sub].Add(Add(a)); subs[sub].Add(Add(b)); subs[sub].Add(Add(c));
        }
        /// <summary>
        /// 면적 0(일직선)이어도 추가하고, 같은 위치(용접 키) 정점이 겹친 삼각형만 버린다. 뒷판 치마/마개처럼
        /// 경계 변이 투영 방향과 평행해 일직선 삼각형이 생겨도 모서리 짝(위상)을 유지해야 할 때 쓴다.
        /// </summary>
        public void AddTriKeepFlat(V a, V b, V c, int sub)
        {
            Vector3Int ka = WKey(a.p), kb = WKey(b.p), kc = WKey(c.p);
            if (ka == kb || kb == kc || ka == kc) return;
            subs[sub].Add(Add(a)); subs[sub].Add(Add(b)); subs[sub].Add(Add(c));
        }
        public int TriCount => (subs[0].Count + subs[1].Count) / 3;
    }

    private class PartReport
    {
        public string name; public int tris; public int openEdges; public int nonManifold; public Vector3 size; public Vector3 assembledPos; public Vector3 hingeAxis; public string pivotMode;
    }

    // ───────────── 메뉴 ─────────────
    [MenuItem(MENU_SPLIT)]
    public static void RunMenu() { Debug.Log(Run(DATA_PATH)); }

    [MenuItem(MENU_VERIFY)]
    public static void VerifyMenu() { Debug.Log(Verify("")); }

    [MenuItem(MENU_STAGE3)]
    public static void RunStage3Menu()
    {
        if (!File.Exists(DATA_PATH_STAGE3)) { Debug.LogWarning($"{LOG_TAG} {DATA_PATH_STAGE3} 없음"); return; }
        Debug.Log(Run(DATA_PATH_STAGE3));
        Debug.Log(Verify("Stage3Test"));
    }

    // ───────────── 진입점 ─────────────
    /// <summary>분할 실행. 요약 문자열을 반환한다(MCP execute_code 에서 직접 호출 가능).</summary>
    public static string Run(string dataPath)
    {
        var log = new StringBuilder();
        // JsonUtility 대신 Newtonsoft(com.unity.nuget.newtonsoft-json, manifest 명시 패키지): 주석 필드 허용 + 동적 로드 환경에서도 동작
        var def = Newtonsoft.Json.JsonConvert.DeserializeObject<SplitDefinition>(File.ReadAllText(dataPath));
        var src = AssetDatabase.LoadAssetAtPath<Mesh>(SRC_MESH);
        var mat = AssetDatabase.LoadAssetAtPath<Material>(SRC_MAT);
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(SRC_TEX);
        if (src == null || mat == null || tex == null) return $"{LOG_TAG} 원본 에셋을 찾지 못함";

        string sub = string.IsNullOrEmpty(def.outputSubfolder) ? "" : "/" + def.outputSubfolder;
        string meshDir = MESH_DIR + sub, matDir = MAT_DIR, prefabDir = PREFAB_DIR + sub;
        EnsureFolder(meshDir); EnsureFolder(matDir); EnsureFolder(prefabDir);

        // isReadable=false 메쉬는 플레이 모드 중 vertices/triangles 가 빈 배열로 올 수 있어 ReadOnlyMeshData 로 읽는다
        ReadMesh(SourceOverride != null ? SourceOverride : src, out var sv, out var sn, out var st, out var suv, out var stri);
        if (sv.Length == 0 || stri.Length == 0)
            return $"{LOG_TAG} 원본 메쉬 데이터를 읽지 못함 (Read/Write 꺼진 메쉬는 플레이 모드 중 읽기가 막힐 수 있음 → 플레이 모드를 끄고 다시 실행)";
        s_grid = new RayGrid(sv, stri, 0.1f);
        var verts = new V[sv.Length];
        for (int i = 0; i < sv.Length; i++)
            verts[i] = new V { p = sv[i], n = sn[i], t = st[i], uv = suv[i] };

        var texReader = new TexReader(tex);
        log.AppendLine($"{LOG_TAG} 원본 삼각형 {stri.Length / 3}, 정점 {sv.Length}, 부품 {def.parts.Count}개 ({dataPath})");

        // 1) 부품 정의 → 평면 생성 + 텍스처 선 보정 (정의 순서대로: sameAs 는 앞 부품의 보정 결과를 공유)
        var parts = new List<Part>();
        foreach (var pd in def.parts)
        {
            var part = BuildPart(pd, parts);
            for (int k = 0; k < part.planes.Count; k++)
                if (part.planes[k].refine) RefinePlane(part, k, verts, stri, texReader, def);
            parts.Add(part);
        }

        // 1.5) 빈 차체: 밖에서 안 보이는 원본 삼각형 중 clear 상자 안의 것은 Frame 에서 뺀다(부품 몫은 그대로)
        var removeFromBody = new bool[stri.Length / 3];
        var ic = def.interiorClear;
        if (ic != null && ic.enabled)
        {
            var hidden = ClassifyHidden(sv, stri, s_grid, ic.rays);
            int nh = 0, nr = 0;
            for (int t = 0; t < hidden.Length; t++)
            {
                if (!hidden[t]) continue; nh++;
                var c = (sv[stri[t * 3]] + sv[stri[t * 3 + 1]] + sv[stri[t * 3 + 2]]) / 3f;
                if (InAnyBox(c, ic.boxes)) { removeFromBody[t] = true; nr++; }
            }
            log.AppendLine($"내부 정리: 가려진 원본 삼각형 {nh}개 중 clear 상자 안 {nr}개를 Frame 에서 제외");
        }

        // 1.6) 원본 지느러미 삼각형 표시(removeFins 부품만 사용)
        var fin = FindFinTriangles(sv, stri);
        int finDropped = 0;

        // 2) 클리핑 (정의 순서대로 먼저 정의된 부품이 우선 차지)
        var body = new Builder();
        int splitTris = 0, removedPieces = 0;
        for (int t = 0; t < stri.Length; t += 3)
        {
            var remaining = new List<List<V>> { new List<V> { verts[stri[t]], verts[stri[t + 1]], verts[stri[t + 2]] } };
            bool wasSplit = false;
            foreach (var part in parts)
            {
                foreach (var region in part.IncludeRegions())
                {
                    var next = new List<List<V>>();
                    foreach (var piece in remaining)
                    {
                        ClipConvex(piece, region, out var inside, out var outs);
                        if (inside == null) { next.Add(piece); continue; }
                        if (outs.Count > 0) wasSplit = true;
                        next.AddRange(outs);
                        // 제외 영역(휠 아치 등): 안쪽 조각은 다시 남은 조각으로 돌려 차체/뒤 부품이 갖게 한다
                        var keep = new List<List<V>> { inside };
                        foreach (var ex in part.excludeRegions)
                        {
                            var nextKeep = new List<List<V>>();
                            foreach (var k in keep)
                            {
                                ClipConvex(k, ex, out var exIn, out var exOut);
                                if (exIn == null) { nextKeep.Add(k); continue; }
                                next.Add(exIn); nextKeep.AddRange(exOut); wasSplit = true;
                            }
                            keep = nextKeep;
                        }
                        if (part.data.removeFins && fin[t / 3]) { finDropped += keep.Count; continue; }
                        foreach (var k in keep) part.shell.AddPoly(k);
                    }
                    remaining = next;
                }
            }
            if (removeFromBody[t / 3]) removedPieces += remaining.Count;
            else foreach (var piece in remaining) body.AddPoly(piece);
            if (wasSplit) splitTris++;
        }
        log.AppendLine($"평면에 걸려 잘린 원본 삼각형 {splitTris}개, Frame 에서 뺀 내부 조각 {removedPieces}개, 원본 지느러미 {fin.Count(f => f)}개 중 부품에서 버린 조각 {finDropped}개");

        // 3) T자 정점 보정 → 두께/안쪽 벽
        body = FixTJunctions(body, out int bodyFix);
        foreach (var part in parts) part.shell = FixTJunctions(part.shell, out part.tJunctionFixes);
        var allPlanes = parts.SelectMany(p => p.AllCutPlanes()).ToList();
        var frame = MakeThick(body, def.innerWallDepth, p => true, allPlanes, true, def.depthClampRatio, out int frameFans, def.frameInnerWall);
        if (ic != null && ic.enabled && ic.fillBoxes != null)
        {
            foreach (var fb0 in ic.fillBoxes) AddBox(frame, fb0.min, fb0.max, 1);
            if (ic.fillBoxes.Count > 0) log.AppendLine($"막음판 {ic.fillBoxes.Count}개: {string.Join(", ", ic.fillBoxes.Select(b => b.name))}");
        }
        if (ic != null && ic.enabled && ic.wheelTubs != null && ic.wheelTubs.Count > 0)
        {
            foreach (var wt in ic.wheelTubs) AddWheelTub(frame, wt, 1);
            log.AppendLine($"휠하우스 아치 {ic.wheelTubs.Count}개: {string.Join(", ", ic.wheelTubs.Select(w => w.name))}");
        }
        foreach (var part in parts)
            part.thick = part.data.backCapNormal != Vector3.zero
                ? MakeCapped(part.shell, part.data.backCapNormal.normalized)
                : MakeThick(part.shell, def.panelThickness, part.thickKeep, allPlanes, false, def.depthClampRatio, out _, def.partInnerWall); // 다른 부품 평면에 닿은 경계에도 림

        // 4) 피벗 + 메쉬 저장
        var srcPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SRC_PREFAB);
        var srcModel = srcPrefab != null ? srcPrefab.transform.Find("Model") : null;
        Vector3 modelOffset = srcModel != null ? srcModel.localPosition : Vector3.zero;

        var reports = new List<PartReport>();
        var meshes = new Dictionary<string, Mesh>();
        foreach (var part in parts)
        {
            if (part.shell.tris.Count == 0) { log.AppendLine($"[경고] {part.name}: 영역 안 삼각형 없음"); continue; }
            var b = BoundsOf(part.shell.verts);
            part.pivot = ComputePivot(part, b);
            meshes[part.name] = SaveMesh(BuildMesh(part.thick, -part.pivot, $"Car_Sedan_{part.name}"), $"{meshDir}/Car_Sedan_{part.name}.asset");
            CountEdges(part.thick, out int open, out int nonMan);
            reports.Add(new PartReport
            {
                name = part.name, tris = part.thick.TriCount, openEdges = open, nonManifold = nonMan, size = b.size,
                assembledPos = part.pivot + modelOffset, hingeAxis = part.data.hingeAxis, pivotMode = part.data.pivot
            });
        }
        var frameMesh = SaveMesh(BuildMesh(frame, Vector3.zero, "Car_Sedan_Frame"), $"{meshDir}/Car_Sedan_Frame.asset");
        CountEdges(frame, out int fOpen, out int fNon);
        var fb = BoundsOf(body.verts);
        reports.Insert(0, new PartReport { name = "Frame", tris = frame.TriCount, openEdges = fOpen, nonManifold = fNon, size = fb.size, assembledPos = modelOffset, pivotMode = "-" });

        // 5) 재질 (원본 재질은 그대로 참조, 내장 재질만 새로 만든다)
        var interior = SaveMaterial($"{matDir}/Car_Sedan_Interior.mat", () =>
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Car_Sedan_Interior" };
            m.SetColor("_BaseColor", def.interiorColor); m.SetColor("_Color", def.interiorColor);
            m.SetFloat("_Metallic", 0f); m.SetFloat("_Smoothness", 0f); m.SetFloat("_Glossiness", 0f);
            m.SetFloat("_SpecularHighlights", 0f); m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            m.SetFloat("_EnvironmentReflections", 0f); m.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            return m;
        });

        // 6) 프리팹
        var mats = new[] { mat, interior };
        BuildPrefab($"{prefabDir}/Car_Sedan_Assembled.prefab", "Car_Sedan_Assembled", frameMesh, parts, meshes, mats, modelOffset, false);
        BuildPrefab($"{prefabDir}/Car_Sedan_Exploded.prefab", "Car_Sedan_Exploded", frameMesh, parts, meshes, mats, modelOffset, true);

        // 7) 정보 JSON
        File.WriteAllText($"{meshDir}/Car_Sedan_SplitParts.json", BuildInfoJson(parts, reports, modelOffset, def));
        AssetDatabase.ImportAsset($"{meshDir}/Car_Sedan_SplitParts.json");
        AssetDatabase.SaveAssets();

        // 8) 요약
        log.AppendLine($"T자 정점 보정: Frame {bodyFix}, 부품 {string.Join(", ", parts.Select(p => p.name + " " + p.tJunctionFixes))}; Frame 부채꼴 메움 {frameFans}");
        log.AppendLine("부품 | 삼각형 | 열린모서리 | 비다양체 | 크기(m) | assembledPos | 힌지축");
        foreach (var r in reports)
            log.AppendLine($"{r.name} | {r.tris} | {r.openEdges} | {r.nonManifold} | {V3(r.size, "F3")} | {V3(r.assembledPos, "F4")} | {(r.hingeAxis == Vector3.zero ? "-" : V3(r.hingeAxis, "F0"))} ({r.pivotMode})");
        foreach (var part in parts)
            log.AppendLine($"  [{part.name}] 평면: " + string.Join("; ", part.planes.Select(pl =>
                $"{pl.name} n{V3(pl.n, "F3")} d={pl.d.ToString("F4", IC)}" + (pl.refine ? $" 보정{(pl.delta * 1000f).ToString("+0.0;-0.0;0", IC)}mm(대비{pl.contrast.ToString("F3", IC)})" : ""))));
        return log.ToString();
    }

    /// <summary>
    /// 원본 메쉬 데이터 읽기. Read/Write 꺼진 메쉬는 (특히 플레이 모드 중) 게터가 빈 배열을 줄 때가 있어,
    /// 비어 있으면 복제본에서 다시 읽고, 그래도 비면 빈 배열을 돌려준다(호출부에서 오류 처리).
    /// </summary>
    private static void ReadMesh(Mesh m, out Vector3[] v, out Vector3[] n, out Vector4[] t, out Vector2[] uv, out int[] tri)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var src = attempt == 0 ? m : Object.Instantiate(m);
            try
            {
                v = src.vertices; n = src.normals; t = src.tangents; uv = src.uv; tri = src.GetTriangles(0);
                for (int s = 1; s < src.subMeshCount; s++) tri = tri.Concat(src.GetTriangles(s)).ToArray();
                int vc = v.Length;
                if (vc > 0 && tri.Length > 0 && n.Length == vc && uv.Length == vc)
                {
                    if (t.Length != vc) t = Enumerable.Repeat(new Vector4(1, 0, 0, 1), vc).ToArray();
                    return;
                }
            }
            finally { if (src != m) Object.DestroyImmediate(src); }
        }
        v = new Vector3[0]; n = new Vector3[0]; t = new Vector4[0]; uv = new Vector2[0]; tri = new int[0];
    }

    // ───────────── 내부 덩어리(가려진 삼각형) 분류 ─────────────
    /// <summary>
    /// 원본 삼각형마다 표본점 4개(무게중심 + 꼭짓점 쪽 3점)에서 법선 쪽 반구로 레이를 쏴,
    /// 하나라도 차량 경계 상자 밖으로 빠져나가면 "밖에서 보임(껍데기)", 전부 막히면 "가려짐(내부 덩어리)".
    /// 반환: hidden[t/3]
    /// </summary>
    private static bool[] ClassifyHidden(Vector3[] sv, int[] stri, RayGrid grid, int rays)
    {
        int n = Mathf.Max(16, rays) * 2;
        var sphere = new Vector3[n];
        float ga = Mathf.PI * (3f - Mathf.Sqrt(5f));
        for (int i = 0; i < n; i++)
        {
            float y = 1f - 2f * (i + 0.5f) / n, r = Mathf.Sqrt(1f - y * y);
            sphere[i] = new Vector3(Mathf.Cos(ga * i) * r, y, Mathf.Sin(ga * i) * r);
        }
        var hidden = new bool[stri.Length / 3];
        var samples = new Vector3[4];
        for (int t = 0; t < stri.Length; t += 3)
        {
            Vector3 a = sv[stri[t]], b = sv[stri[t + 1]], c = sv[stri[t + 2]];
            var fn = Vector3.Cross(b - a, c - a);
            if (fn.sqrMagnitude < 1e-16f) continue; // 퇴화 삼각형은 보존
            fn.Normalize();
            var cen = (a + b + c) / 3f;
            samples[0] = cen; samples[1] = Vector3.Lerp(cen, a, 0.6f); samples[2] = Vector3.Lerp(cen, b, 0.6f); samples[3] = Vector3.Lerp(cen, c, 0.6f);
            bool visible = false;
            for (int s = 0; s < 4 && !visible; s++)
            {
                var o = samples[s] + fn * 0.002f;
                if (grid.Escapes(o, fn)) { visible = true; break; }
                for (int i = 0; i < n && !visible; i++)
                    if (Vector3.Dot(sphere[i], fn) > 0.03f && grid.Escapes(o, sphere[i])) visible = true;
            }
            hidden[t / 3] = !visible;
        }
        return hidden;
    }

    /// <summary>축 정렬 닫힌 상자(면마다 정점 분리 → 평면 음영, 바깥 향함)를 서브메쉬 sub 에 추가</summary>
    private static void AddBox(Builder b, Vector3 mn, Vector3 mx, int sub)
    {
        var c = new Vector3[8];
        for (int i = 0; i < 8; i++) c[i] = new Vector3((i & 1) != 0 ? mx.x : mn.x, (i & 2) != 0 ? mx.y : mn.y, (i & 4) != 0 ? mx.z : mn.z);
        // 각 면: 바깥에서 봤을 때 시계방향(Unity 앞면) 네 꼭짓점
        int[][] faces =
        {
            new[] { 1, 3, 7, 5 }, new[] { 0, 4, 6, 2 }, // +x, -x
            new[] { 2, 6, 7, 3 }, new[] { 0, 1, 5, 4 }, // +y, -y
            new[] { 4, 5, 7, 6 }, new[] { 0, 2, 3, 1 }, // +z, -z
        };
        Vector3[] ns = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        for (int f = 0; f < 6; f++)
        {
            var q = faces[f].Select(i => new V { p = c[i], n = ns[f], t = new Vector4(1, 0, 0, 1), uv = Vector2.zero }).ToArray();
            b.AddTri(q[0], q[1], q[2], sub);
            b.AddTri(q[0], q[2], q[3], sub);
        }
    }

    /// <summary>
    /// 휠하우스 아치: X축 반원통 지붕(반지름 radius, yMin 위쪽 호) + xIn 위치의 반원 마개. 양면(앞/뒤 면 모두 생성)으로
    /// 어느 쪽에서 봐도 비치지 않게 한다. 지붕 법선은 반지름 방향(부드러운 음영), 마개 법선은 차 중심 쪽.
    /// </summary>
    private static void AddWheelTub(Builder b, WheelTubData w, int sub)
    {
        float sgn = w.side < 0 ? -1f : 1f, xi = sgn * w.xIn, xo = sgn * w.xOut, r = w.radius;
        float s0 = Mathf.Clamp((w.yMin - w.cy) / r, -1f, 1f);
        float th0 = Mathf.Asin(s0), th1 = Mathf.PI - th0;
        int seg = Mathf.Max(6, w.segments);
        var arc = new Vector3[seg + 1]; var nrm = new Vector3[seg + 1];
        for (int k = 0; k <= seg; k++)
        {
            float th = Mathf.Lerp(th0, th1, (float)k / seg);
            nrm[k] = new Vector3(0f, Mathf.Sin(th), Mathf.Cos(th));
            arc[k] = new Vector3(0f, w.cy, w.cz) + nrm[k] * r;
        }
        V Mk(Vector3 p, Vector3 n) => new V { p = p, n = n, t = new Vector4(1, 0, 0, 1), uv = Vector2.zero };
        void Tri2(V a, V c, V d, Vector3 face)
        {
            // 앞면: cross(b-a, c-a) 가 face 쪽(AddBox 와 같은 규약). 뒷면: 법선 뒤집고 감김 반대
            if (Vector3.Dot(Vector3.Cross(c.p - a.p, d.p - a.p), face) < 0f) { var t = c; c = d; d = t; }
            b.AddTri(a, c, d, sub);
            V fa = a, fc = c, fd = d; fa.n = -a.n; fc.n = -c.n; fd.n = -d.n;
            b.AddTri(fa, fd, fc, sub);
        }
        for (int k = 0; k < seg; k++)
        {
            var na = nrm[k]; var nb = nrm[k + 1]; var face = (na + nb).normalized;
            V a0 = Mk(new Vector3(xi, arc[k].y, arc[k].z), na), a1 = Mk(new Vector3(xo, arc[k].y, arc[k].z), na);
            V b0 = Mk(new Vector3(xi, arc[k + 1].y, arc[k + 1].z), nb), b1 = Mk(new Vector3(xo, arc[k + 1].y, arc[k + 1].z), nb);
            Tri2(a0, a1, b1, face); Tri2(a0, b1, b0, face);
        }
        var capN = new Vector3(-sgn, 0f, 0f);
        float capY = Mathf.Min(w.capYMin, w.yMin);
        float bh = w.capBottomHalf > 0f ? w.capBottomHalf : r;
        var capPts = new List<Vector3> { new Vector3(xi, capY, Mathf.Min(arc[0].z, w.cz + bh)) };
        foreach (var q in arc) capPts.Add(new Vector3(xi, q.y, q.z));
        capPts.Add(new Vector3(xi, capY, Mathf.Max(arc[seg].z, w.cz - bh)));
        var cen = Mk(new Vector3(xi, capY, w.cz), capN);
        for (int k = 0; k + 1 < capPts.Count; k++)
            Tri2(cen, Mk(capPts[k], capN), Mk(capPts[k + 1], capN), capN);
    }

    private static bool InAnyBox(Vector3 p, List<ClearBox> boxes)
    {
        foreach (var b in boxes)
            if (p.x >= b.min.x && p.x <= b.max.x && p.y >= b.min.y && p.y <= b.max.y && p.z >= b.min.z && p.z <= b.max.z) return true;
        return false;
    }

    /// <summary>
    /// 분석용(MCP execute_code 에서 호출): 가려진 삼각형을 연결 성분으로 묶어 영역/개수를 보고한다.
    /// </summary>
    public static string AnalyzeInterior(string dataPath, int rays)
    {
        var def = Newtonsoft.Json.JsonConvert.DeserializeObject<SplitDefinition>(File.ReadAllText(dataPath));
        var src = AssetDatabase.LoadAssetAtPath<Mesh>(SRC_MESH);
        ReadMesh(src, out var sv, out _, out _, out _, out var stri);
        var grid = new RayGrid(sv, stri, 0.1f);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hidden = ClassifyHidden(sv, stri, grid, rays);
        var sb = new StringBuilder();
        int nh = hidden.Count(h => h);
        sb.AppendLine($"원본 삼각형 {hidden.Length}, 가려짐 {nh}, 보임 {hidden.Length - nh} ({sw.ElapsedMilliseconds}ms, rays {rays})");
        // 위치 용접 → 가려진 삼각형 연결 성분
        var wid = new int[sv.Length]; var map = new Dictionary<Vector3Int, int>();
        for (int i = 0; i < sv.Length; i++) { var k = WKey(sv[i]); if (!map.TryGetValue(k, out int id)) { id = map.Count; map[k] = id; } wid[i] = id; }
        var parent = Enumerable.Range(0, hidden.Length).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var firstTriOfVert = new Dictionary<int, int>();
        for (int t = 0; t < stri.Length; t += 3)
        {
            if (!hidden[t / 3]) continue;
            for (int j = 0; j < 3; j++)
            {
                int v = wid[stri[t + j]];
                if (firstTriOfVert.TryGetValue(v, out int o)) parent[Find(o)] = Find(t / 3); else firstTriOfVert[v] = t / 3;
            }
        }
        var comps = new Dictionary<int, List<int>>();
        for (int t = 0; t < hidden.Length; t++) if (hidden[t]) { int r = Find(t); if (!comps.TryGetValue(r, out var l)) comps[r] = l = new List<int>(); l.Add(t); }
        var box = def.interiorClear?.boxes ?? new List<ClearBox>();
        int inBoxes = 0;
        for (int t = 0; t < hidden.Length; t++)
            if (hidden[t] && InAnyBox((sv[stri[t * 3]] + sv[stri[t * 3 + 1]] + sv[stri[t * 3 + 2]]) / 3f, box)) inBoxes++;
        sb.AppendLine($"연결 성분 {comps.Count}개, clear 상자 안 가려진 삼각형 {inBoxes}");
        foreach (var kv in comps.OrderByDescending(c => c.Value.Count).Take(25))
        {
            var bb = new Bounds(sv[stri[kv.Value[0] * 3]], Vector3.zero); float area = 0;
            foreach (int t in kv.Value)
            {
                Vector3 a = sv[stri[t * 3]], b = sv[stri[t * 3 + 1]], c = sv[stri[t * 3 + 2]];
                bb.Encapsulate(a); bb.Encapsulate(b); bb.Encapsulate(c);
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            sb.AppendLine($"  성분 tris {kv.Value.Count} area {area:F3}m² min {V3(bb.min, "F2")} max {V3(bb.max, "F2")}");
        }
        // 높이/길이 분포 (z 구간별 가려진 삼각형 수)
        var hist = new int[20];
        for (int t = 0; t < hidden.Length; t++)
            if (hidden[t]) { float z = (sv[stri[t * 3]].z + 1.9f) / 3.8f; hist[Mathf.Clamp((int)(z * 20), 0, 19)]++; }
        sb.AppendLine("z 분포(-1.9→+1.9, 0.19m 구간): " + string.Join(" ", hist));
        s_lastHidden = hidden;
        return sb.ToString();
    }
    public static bool[] s_lastHidden;

    private static string V3(Vector3 v, string f) => $"({v.x.ToString(f, IC)}, {v.y.ToString(f, IC)}, {v.z.ToString(f, IC)})";

    // ───────────── 부품 정의 → 평면 ─────────────
    private static Part BuildPart(PartData pd, List<Part> done)
    {
        var part = new Part { data = pd, name = pd.name };
        foreach (var pl in pd.planes) part.planes.Add(MakePlane(pl, done, pd.name));
        foreach (var r in pd.extraRegions ?? new List<RegionData>())
            part.extraRegions.Add(r.planes.Select(pl => MakePlane(pl, done, pd.name)).ToList());
        foreach (var c in pd.includeCylinders ?? new List<CylinderData>()) part.planes.AddRange(CylinderPlanes(c));
        foreach (var g in pd.includePolygons ?? new List<PolygonData>()) part.planes.AddRange(PolygonPlanes(g, pd.name));
        foreach (var c in pd.excludeCylinders ?? new List<CylinderData>()) part.excludeRegions.Add(CylinderPlanes(c));
        var boxes = pd.noThickBoxes ?? new List<BoxData>();
        if (boxes.Count > 0)
            part.thickKeep = p => !boxes.Any(b => p.x > b.min.x && p.x < b.max.x && p.y > b.min.y && p.y < b.max.y && p.z > b.min.z && p.z < b.max.z);
        return part;
    }

    /// <summary>축(x|y|z) 방향 원기둥을 외접 다각형(segments 면)으로 근사한 볼록 영역의 평면들</summary>
    private static List<CutPlane> CylinderPlanes(CylinderData c)
    {
        var region = new List<CutPlane>();
        int seg = Mathf.Max(6, c.segments);
        for (int k = 0; k < seg; k++)
        {
            float a = (k + 0.5f) * Mathf.PI * 2f / seg;
            float sn = Mathf.Sin(a), cs = Mathf.Cos(a);
            var n = c.axis == "z" ? new Vector3(cs, sn, 0f) : c.axis == "y" ? new Vector3(cs, 0f, sn) : new Vector3(0f, sn, cs);
            region.Add(new CutPlane($"{c.name}#{k}", n, c.center + n * c.radius, false));
        }
        return region;
    }

    /// <summary>z축 볼록 다각형 기둥의 변 평면들(바깥 법선). 꼭짓점은 각도 순으로 정렬, 볼록하지 않으면 예외.</summary>
    private static List<CutPlane> PolygonPlanes(PolygonData g, string owner)
    {
        if (g.axis != "z") throw new Exception($"{owner}.{g.name}: includePolygons 는 axis z 만 지원");
        if (g.anglesDeg == null || g.radii == null || g.anglesDeg.Count != g.radii.Count || g.anglesDeg.Count < 3)
            throw new Exception($"{owner}.{g.name}: anglesDeg/radii 개수가 맞지 않음");
        var pts = g.anglesDeg.Select((a, i) => (a: Mathf.Repeat(a, 360f), r: g.radii[i])).OrderBy(t => t.a)
            .Select(t => new Vector2(g.center.x + Mathf.Cos(t.a * Mathf.Deg2Rad) * t.r, g.center.y + Mathf.Sin(t.a * Mathf.Deg2Rad) * t.r)).ToList();
        var region = new List<CutPlane>();
        for (int k = 0; k < pts.Count; k++)
        {
            Vector2 a = pts[k], b = pts[(k + 1) % pts.Count], c = pts[(k + 2) % pts.Count];
            Vector2 e = b - a, f = c - b;
            if (e.x * f.y - e.y * f.x <= 0f) throw new Exception($"{owner}.{g.name}: 다각형이 볼록하지 않음(꼭짓점 {(k + 1) % pts.Count})");
            var n = new Vector3(e.y, -e.x, 0f).normalized; // 반시계 다각형의 바깥 법선
            region.Add(new CutPlane($"{g.name}#{k}", n, new Vector3(a.x, a.y, 0f), false));
        }
        return region;
    }

    private static CutPlane MakePlane(PlaneData pl, List<Part> done, string owner)
    {
        if (!string.IsNullOrEmpty(pl.sameAs))
        {
            var s = pl.sameAs.Split('/');
            var refPart = done.FirstOrDefault(p => p.name == s[0]);
            var refPl = refPart?.planes.FirstOrDefault(p => p.name == s[1]);
            if (refPl == null) throw new Exception($"{owner}.{pl.name}: sameAs '{pl.sameAs}' 를 찾지 못함(앞에 정의돼야 함)");
            float f = pl.flip ? -1f : 1f;
            return new CutPlane(pl.name, refPl.n * f, Vector3.zero, false) { d = refPl.d * f };
        }
        if (pl.usePoints)
        {
            var n = Vector3.Cross(pl.p2 - pl.p1, pl.p3 - pl.p1).normalized;
            if (Vector3.Dot(n, pl.inside - pl.p1) > 0f) n = -n;
            return new CutPlane(pl.name, n, pl.p1, pl.refine);
        }
        return new CutPlane(pl.name, pl.normal, pl.point, pl.refine);
    }

    private static Vector3 ComputePivot(Part part, Bounds b)
    {
        var vs = part.shell.verts;
        switch (part.data.pivot)
        {
            case "frontEdge":
            {
                // 문: 앞쪽 끝(힌지 라인) 5cm 띠의 평균 x, 높이 중앙, z 최대
                var band = vs.Where(v => v.p.z > b.max.z - 0.05f).ToList();
                return new Vector3(band.Count > 0 ? band.Average(v => v.p.x) : b.center.x, b.center.y, b.max.z);
            }
            case "rearEdgeTop":
            {
                // 후드: 뒤쪽 끝(힌지) 5cm 띠의 평균 y, x 중앙, z 최소
                var band = vs.Where(v => v.p.z < b.min.z + 0.05f).ToList();
                return new Vector3(b.center.x, band.Count > 0 ? band.Average(v => v.p.y) : b.center.y, b.min.z);
            }
            case "frontEdgeTop":
            {
                // 트렁크: 앞쪽 끝(힌지) 5cm 띠의 평균 y, x 중앙, z 최대
                var band = vs.Where(v => v.p.z > b.max.z - 0.05f).ToList();
                return new Vector3(b.center.x, band.Count > 0 ? band.Average(v => v.p.y) : b.center.y, b.max.z);
            }
            case "rearFace": return new Vector3(b.center.x, b.center.y, b.min.z);   // 앞범퍼: 차체에 닿는 뒷면 중앙
            case "frontFace": return new Vector3(b.center.x, b.center.y, b.max.z);  // 뒷범퍼: 차체에 닿는 앞면 중앙
            case "cutCenter":
            {
                // 미러 등 돌출 부품: 자기 절단 평면 위에 놓인(=차체에 붙는 뿌리) 정점들의 경계 상자 중심
                var own = part.AllCutPlanes();
                var root = vs.Where(v => own.Any(pl => Mathf.Abs(pl.Dist(v.p)) < ON_PLANE_EPS)).Select(v => v.p).ToList();
                if (root.Count == 0) return b.center;
                var rb = new Bounds(root[0], Vector3.zero);
                foreach (var p in root) rb.Encapsulate(p);
                return rb.center;
            }
            default: return b.center;
        }
    }

    // ───────────── 텍스처 선 보정 (PoC 와 동일 알고리즘) ─────────────
    private class TexReader
    {
        private readonly Color[] px; private readonly int w, h;
        public TexReader(Texture2D tex)
        {
            w = tex.width; h = tex.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0); copy.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            px = copy.GetPixels(); Object.DestroyImmediate(copy);
        }
        public float Lum(Vector2 uv)
        {
            float x = Mathf.Repeat(uv.x, 1f) * w - 0.5f, y = Mathf.Repeat(uv.y, 1f) * h - 0.5f;
            int x0 = Mathf.Clamp((int)Mathf.Floor(x), 0, w - 1), y0 = Mathf.Clamp((int)Mathf.Floor(y), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            float fx = x - Mathf.Floor(x), fy = y - Mathf.Floor(y);
            float a = L(px[y0 * w + x0]), b = L(px[y0 * w + x1]), c = L(px[y1 * w + x0]), d = L(px[y1 * w + x1]);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }
        private static float L(Color c) { return 0.299f * c.r + 0.587f * c.g + 0.114f * c.b; }
    }

    /// <summary>평면을 δ만큼 옮겨가며 절단선을 따라 휘도를 샘플, "선 중앙 − 양옆 평균" 대비가 가장 낮은 δ 채택.</summary>
    private static void RefinePlane(Part part, int k, V[] verts, int[] tri, TexReader tex, SplitDefinition def)
    {
        var pl = part.planes[k];
        float range = def.refineRange, side = def.refineSide;
        var cand = new List<int>();
        for (int t = 0; t < tri.Length; t += 3)
        {
            var c = (verts[tri[t]].p + verts[tri[t + 1]].p + verts[tri[t + 2]].p) / 3f;
            bool ok = true;
            for (int j = 0; j < part.planes.Count && ok; j++)
                if (j != k && part.planes[j].Dist(c) > 0f) ok = false;
            if (!ok) continue;
            float d0 = pl.Dist(verts[tri[t]].p), d1 = pl.Dist(verts[tri[t + 1]].p), d2 = pl.Dist(verts[tri[t + 2]].p);
            float mn = Mathf.Min(d0, Mathf.Min(d1, d2)), mx = Mathf.Max(d0, Mathf.Max(d1, d2));
            if (mx < -range - side || mn > range + side) continue;
            cand.Add(t);
        }
        float LineLum(float delta)
        {
            float sum = 0, wsum = 0;
            var pts = new List<V>(3);
            foreach (int t in cand)
            {
                var a = verts[tri[t]]; var b = verts[tri[t + 1]]; var c = verts[tri[t + 2]];
                pts.Clear();
                Cross(a, b, pl, delta, pts); Cross(b, c, pl, delta, pts); Cross(c, a, pl, delta, pts);
                if (pts.Count < 2) continue;
                float len = Vector3.Distance(pts[0].p, pts[1].p);
                if (len < 1e-5f) continue;
                for (int s = 0; s < 6; s++)
                {
                    float f = (s + 0.5f) / 6f;
                    sum += tex.Lum(Vector2.Lerp(pts[0].uv, pts[1].uv, f)) * len; wsum += len;
                }
            }
            return wsum > 0 ? sum / wsum : 1f;
        }
        float best = 0f, bestScore = float.MaxValue;
        for (float delta = -range; delta <= range + 1e-6f; delta += def.refineStep)
        {
            float score = LineLum(delta) - 0.5f * (LineLum(delta - side) + LineLum(delta + side));
            if (score < bestScore) { bestScore = score; best = delta; }
        }
        pl.contrast = bestScore;
        if (bestScore <= def.refineMinContrast) { pl.delta = best; pl.d += best; }
    }

    private static void Cross(V a, V b, CutPlane pl, float delta, List<V> outPts)
    {
        float da = pl.Dist(a.p) - delta, db = pl.Dist(b.p) - delta;
        if ((da < 0 && db >= 0) || (da >= 0 && db < 0)) outPts.Add(V.Lerp(a, b, da / (da - db)));
    }

    // ───────────── 기하 연산 ─────────────
    private static void Split(List<V> poly, CutPlane pl, out List<V> inside, out List<V> outside)
    {
        inside = new List<V>(poly.Count + 2); outside = new List<V>(poly.Count + 2);
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Count];
            float da = pl.Dist(a.p), db = pl.Dist(b.p);
            if (da <= 0) inside.Add(a);
            if (da >= 0) outside.Add(a);
            if ((da < 0 && db > 0) || (da > 0 && db < 0))
            {
                var m = V.Lerp(a, b, da / (da - db));
                inside.Add(m); outside.Add(m);
            }
        }
    }

    /// <summary>볼록 영역으로 자르기. inside=null 이면 영역 안에 남는 조각 없음(outs 무시).</summary>
    private static void ClipConvex(List<V> piece, List<CutPlane> region, out List<V> inside, out List<List<V>> outs)
    {
        outs = new List<List<V>>();
        var poly = piece;
        foreach (var pl in region)
        {
            Split(poly, pl, out var ins, out var outside);
            if (outside.Count >= 3 && HasArea(outside)) outs.Add(outside);
            poly = ins;
            if (poly.Count < 3) break;
        }
        inside = poly.Count >= 3 && HasArea(poly) ? poly : null;
    }

    private static bool HasArea(List<V> poly)
    {
        var acc = Vector3.zero;
        for (int k = 1; k + 1 < poly.Count; k++) acc += Vector3.Cross(poly[k].p - poly[0].p, poly[k + 1].p - poly[0].p);
        return acc.sqrMagnitude > 1e-20f;
    }

    private static Vector3Int WKey(Vector3 p) => Vector3Int.RoundToInt(p * 20000f); // 0.05mm 용접

    /// <summary>
    /// 잘린 조각들 사이의 T자 정점(한 삼각형 변 위에 이웃 삼각형의 정점이 놓인 경우)을 찾아 그 변을 쪼갠다.
    /// 경계 변(한 삼각형만 쓰는 변) 위에 놓인 다른 경계 정점만 검사한다.
    /// </summary>
    private static Builder FixTJunctions(Builder b, out int inserted)
    {
        inserted = 0;
        for (int pass = 0; pass < 6; pass++)
        {
            var tris = b.tris; var vs = b.verts;
            var wid = new int[vs.Count]; var keyToId = new Dictionary<Vector3Int, int>(); var wpos = new List<Vector3>();
            for (int i = 0; i < vs.Count; i++)
            {
                var key = WKey(vs[i].p);
                if (!keyToId.TryGetValue(key, out int id)) { id = wpos.Count; keyToId[key] = id; wpos.Add(vs[i].p); }
                wid[i] = id;
            }
            var edgeCount = new Dictionary<long, int>();
            for (int t = 0; t < tris.Count; t += 3)
                for (int j = 0; j < 3; j++)
                {
                    int a = wid[tris[t + j]], c = wid[tris[t + (j + 1) % 3]];
                    if (a == c) continue;
                    long k = EKey(a, c); edgeCount.TryGetValue(k, out int n); edgeCount[k] = n + 1;
                }
            var bEdges = new List<(int t, int j)>(); var bVerts = new HashSet<int>();
            for (int t = 0; t < tris.Count; t += 3)
                for (int j = 0; j < 3; j++)
                {
                    int a = wid[tris[t + j]], c = wid[tris[t + (j + 1) % 3]];
                    if (a == c || edgeCount[EKey(a, c)] != 1) continue;
                    bEdges.Add((t, j)); bVerts.Add(a); bVerts.Add(c);
                }
            const float CELL = 0.02f;
            var grid = new Dictionary<Vector3Int, List<int>>();
            foreach (int id in bVerts)
            {
                var c = Vector3Int.FloorToInt(wpos[id] / CELL);
                if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>();
                l.Add(id);
            }
            var inserts = new Dictionary<(int t, int j), List<(float s, int id)>>();
            foreach (var (t, j) in bEdges)
            {
                int ia = wid[tris[t + j]], ib = wid[tris[t + (j + 1) % 3]];
                Vector3 pa = wpos[ia], pb = wpos[ib], ab = pb - pa;
                float len2 = ab.sqrMagnitude;
                if (len2 < 1e-12f) continue;
                var mn = Vector3Int.FloorToInt((Vector3.Min(pa, pb) - Vector3.one * 1e-4f) / CELL);
                var mx = Vector3Int.FloorToInt((Vector3.Max(pa, pb) + Vector3.one * 1e-4f) / CELL);
                for (int x = mn.x; x <= mx.x; x++)
                    for (int y = mn.y; y <= mx.y; y++)
                        for (int z = mn.z; z <= mx.z; z++)
                        {
                            if (!grid.TryGetValue(new Vector3Int(x, y, z), out var l)) continue;
                            foreach (int id in l)
                            {
                                if (id == ia || id == ib) continue;
                                var p = wpos[id];
                                float s = Vector3.Dot(p - pa, ab) / len2;
                                if (s <= 1e-4f || s >= 1f - 1e-4f) continue;
                                if ((pa + ab * s - p).sqrMagnitude > 4e-10f) continue; // 0.02mm
                                if (!inserts.TryGetValue((t, j), out var il)) inserts[(t, j)] = il = new List<(float, int)>();
                                il.Add((s, id));
                            }
                        }
            }
            if (inserts.Count == 0) break;
            inserted += inserts.Values.Sum(l => l.Count);

            var nb = new Builder();
            for (int t = 0; t < tris.Count; t += 3)
            {
                var c = new[] { vs[tris[t]], vs[tris[t + 1]], vs[tris[t + 2]] };
                int edgesWith = 0, lastJ = -1;
                for (int j = 0; j < 3; j++) if (inserts.ContainsKey((t, j))) { edgesWith++; lastJ = j; }
                if (edgesWith == 0) { nb.AddTri(c[0], c[1], c[2], 0); continue; }
                var poly = new List<V>();
                for (int j = 0; j < 3; j++)
                {
                    poly.Add(c[j]);
                    if (!inserts.TryGetValue((t, j), out var il)) continue;
                    foreach (var (s, id) in il.OrderBy(e => e.s))
                    {
                        var v = V.Lerp(c[j], c[(j + 1) % 3], s); v.p = wpos[id];
                        poly.Add(v);
                    }
                }
                if (edgesWith == 1)
                {
                    // 쪼갠 변의 맞은편 꼭짓점에서 부채꼴
                    int opp = (lastJ + 2) % 3;
                    int start = poly.FindIndex(v => v.p == c[opp].p && v.uv == c[opp].uv);
                    var rot = new List<V>();
                    for (int k = 0; k < poly.Count; k++) rot.Add(poly[(start + k) % poly.Count]);
                    nb.AddPoly(rot);
                }
                else
                {
                    var cv = new V
                    {
                        p = (c[0].p + c[1].p + c[2].p) / 3f, n = (c[0].n + c[1].n + c[2].n).normalized,
                        t = (c[0].t + c[1].t + c[2].t) / 3f, uv = (c[0].uv + c[1].uv + c[2].uv) / 3f,
                    };
                    for (int k = 0; k < poly.Count; k++) nb.AddTri(cv, poly[k], poly[(k + 1) % poly.Count], 0);
                }
            }
            b = nb;
        }
        return b;
    }

    /// <summary>원본 삼각형 중 "열린 변 1개 이상 + 3면 이상 공유 변 1개 이상"인 것(접힌 지느러미/떠 있는 판)</summary>
    private static bool[] FindFinTriangles(Vector3[] sv, int[] stri)
    {
        var wid = new int[sv.Length]; var keyToId = new Dictionary<Vector3Int, int>();
        for (int i = 0; i < sv.Length; i++)
        {
            var k = WKey(sv[i]);
            if (!keyToId.TryGetValue(k, out int id)) { id = keyToId.Count; keyToId[k] = id; }
            wid[i] = id;
        }
        var ec = new Dictionary<long, int>();
        for (int t = 0; t < stri.Length; t += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = wid[stri[t + j]], b = wid[stri[t + (j + 1) % 3]];
                if (a == b) continue;
                long k = EKey(a, b); ec.TryGetValue(k, out int n); ec[k] = n + 1;
            }
        var fin = new bool[stri.Length / 3];
        for (int t = 0; t < stri.Length; t += 3)
        {
            bool open = false, multi = false;
            for (int j = 0; j < 3; j++)
            {
                int a = wid[stri[t + j]], b = wid[stri[t + (j + 1) % 3]];
                if (a == b) continue;
                int n = ec[EKey(a, b)];
                if (n == 1) open = true; else if (n > 2) multi = true;
            }
            fin[t / 3] = open && multi;
        }
        return fin;
    }

    private static long EKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>
    /// 두께 부여: 서브메쉬0 = 바깥 면 그대로, 서브메쉬1 = 안쪽 껍데기(-법선 오프셋, 뒤집음) + 경계 림 (+ 비절단 경계 부채꼴 메움).
    /// rimAll=true: 모든 경계 변에 림(차체용). false: 절단 평면 위 경계에만 림, 나머지 경계 루프는 안쪽 높이에서 부채꼴로 메움(PoC 부품 방식).
    /// 경계 정점의 오프셋 방향은 그 정점이 놓인 절단 평면 위로 투영해서 림이 절단면 안에 놓이게 한다.
    /// </summary>
    private static Builder MakeThick(Builder src, float depth, Func<Vector3, bool> keep, List<CutPlane> cutPlanes, bool rimAll, float clampRatio, out int fanLoops, InnerWallData wall = null)
    {
        fanLoops = 0;
        var outB = new Builder();
        for (int t = 0; t < src.tris.Count; t += 3)
            outB.AddTri(src.verts[src.tris[t]], src.verts[src.tris[t + 1]], src.verts[src.tris[t + 2]], 0);

        var f = new Builder();
        for (int t = 0; t < src.tris.Count; t += 3)
        {
            var a = src.verts[src.tris[t]]; var c1 = src.verts[src.tris[t + 1]]; var c2 = src.verts[src.tris[t + 2]];
            if (keep(a.p) && keep(c1.p) && keep(c2.p)) f.AddTri(a, c1, c2, 0);
        }
        var vs = f.verts; var ts = f.tris;
        var weldId = new int[vs.Count]; var keyToId = new Dictionary<Vector3Int, int>(); var nSum = new List<Vector3>(); var wpos = new List<Vector3>();
        for (int i = 0; i < vs.Count; i++)
        {
            var key = WKey(vs[i].p);
            if (!keyToId.TryGetValue(key, out int id)) { id = nSum.Count; keyToId[key] = id; nSum.Add(Vector3.zero); wpos.Add(vs[i].p); }
            weldId[i] = id; nSum[id] += vs[i].n;
        }
        var edgeCount = new Dictionary<long, int>();
        for (int t = 0; t < ts.Count; t += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = weldId[ts[t + j]], b = weldId[ts[t + (j + 1) % 3]];
                if (a == b) continue;
                long k = EKey(a, b); edgeCount.TryGetValue(k, out int c); edgeCount[k] = c + 1;
            }
        var boundary = new HashSet<int>();
        for (int t = 0; t < ts.Count; t += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = weldId[ts[t + j]], b = weldId[ts[t + (j + 1) % 3]];
                if (a != b && edgeCount[EKey(a, b)] == 1) { boundary.Add(a); boundary.Add(b); }
            }

        // 오프셋 방향 (경계 정점은 절단 평면 위로 투영)
        var dir = new Vector3[nSum.Count];
        for (int id = 0; id < dir.Length; id++)
        {
            var n = nSum[id].sqrMagnitude > 1e-12f ? nSum[id].normalized : Vector3.up;
            if (boundary.Contains(id))
            {
                var on = cutPlanes.Where(pl => Mathf.Abs(pl.Dist(wpos[id])) < ON_PLANE_EPS).ToList();
                if (on.Count > 0)
                {
                    var d = n;
                    for (int it = 0; it < 4; it++) foreach (var pl in on) d -= Vector3.Dot(d, pl.n) * pl.n;
                    if (d.magnitude > 0.3f) n = d.normalized;
                }
            }
            dir[id] = n;
        }
        var depthOf = new float[nSum.Count];
        for (int id = 0; id < depthOf.Length; id++)
        {
            float hit = s_grid.Hit(wpos[id], -dir[id], depth / clampRatio);
            depthOf[id] = Mathf.Min(depth, hit * clampRatio);
        }
        var inner = new Vector3[nSum.Count];
        for (int id = 0; id < inner.Length; id++) inner[id] = wpos[id] - dir[id] * depthOf[id];
        Vector3[] innerN = null;
        if (wall != null) innerN = ShapeInnerWall(wall, ts, weldId, wpos, nSum, dir, depthOf, boundary, inner, clampRatio);
        Vector3 Inner(int i) => inner[weldId[i]];

        // 안쪽 껍데기 (감김 반대 + 법선 반대)
        for (int t = 0; t < ts.Count; t += 3)
        {
            var tri = new V[3];
            for (int j = 0; j < 3; j++)
            {
                int i = ts[t + j]; var v = vs[i]; v.p = Inner(i); v.n = innerN != null ? -innerN[weldId[i]] : -v.n; v.t.w = -v.t.w;
                tri[2 - j] = v;
            }
            outB.AddTri(tri[0], tri[1], tri[2], 1);
        }

        bool OnSamePlane(Vector3 p, Vector3 q) => cutPlanes.Any(pl => Mathf.Abs(pl.Dist(p)) < ON_PLANE_EPS && Mathf.Abs(pl.Dist(q)) < ON_PLANE_EPS);
        var holeNext = new Dictionary<int, (int ia, int ib)>();
        for (int t = 0; t < ts.Count; t += 3)
            for (int j = 0; j < 3; j++)
            {
                int ia = ts[t + j], ib = ts[t + (j + 1) % 3];
                int a = weldId[ia], b = weldId[ib];
                if (a == b || edgeCount[EKey(a, b)] != 1) continue;
                if (!rimAll && !OnSamePlane(vs[ia].p, vs[ib].p)) { holeNext[a] = (ia, ib); continue; }
                // 림: b → a → a' → b' (경계 바깥, 즉 구멍/절단면 쪽을 향함)
                var pa = vs[ia].p; var pb = vs[ib].p; var qa = Inner(ia); var qb = Inner(ib);
                var quad = new[] { pb, pa, qa, qb };
                var fn = Vector3.Cross(quad[1] - quad[0], quad[2] - quad[0]);
                if (fn.sqrMagnitude < 1e-20f) fn = Vector3.Cross(quad[2] - quad[0], quad[3] - quad[0]);
                fn = fn.normalized;
                var tan = (Vector4)(quad[1] - quad[0]).normalized; tan.w = 1f;
                var uvA = vs[ia].uv; var uvB = vs[ib].uv;
                var uvs = new[] { uvB, uvA, uvA, uvB };
                var q = new V[4];
                for (int k = 0; k < 4; k++) q[k] = new V { p = quad[k], n = fn, t = tan, uv = uvs[k] };
                outB.AddTri(q[0], q[1], q[2], 1);
                outB.AddTri(q[0], q[2], q[3], 1);
            }

        // 비절단 경계 루프 메우기 (부채꼴, 중심 = 안쪽 정점 평균)
        var used = new HashSet<int>();
        foreach (var start in holeNext.Keys.ToList())
        {
            if (used.Contains(start)) continue;
            var loop = new List<(int ia, int ib)>(); int cur = start;
            while (holeNext.TryGetValue(cur, out var e) && used.Add(cur)) { loop.Add(e); cur = weldId[e.ib]; }
            if (loop.Count < 3) continue;
            fanLoops++;
            var center = Vector3.zero; var nAvg = Vector3.zero;
            foreach (var e in loop) { center += Inner(e.ia); nAvg += nSum[weldId[e.ia]]; }
            center /= loop.Count; nAvg = -nAvg.normalized;
            var cv = new V { p = center, n = nAvg, t = new Vector4(1, 0, 0, 1), uv = vs[loop[0].ia].uv };
            foreach (var e in loop)
            {
                var va = vs[e.ia]; va.p = Inner(e.ia); va.n = nAvg;
                var vb = vs[e.ib]; vb.p = Inner(e.ib); vb.n = nAvg;
                outB.AddTri(va, vb, cv, 1);
            }
        }
        return outB;
    }

    /// <summary>
    /// 평평한 뒷판으로 닫기: 서브메쉬0 = 바깥 면, 서브메쉬1 = 경계 변마다 뒷판 평면으로 내린 치마 + 경계 루프별 부채꼴 마개.
    /// 뒷판 평면은 n 방향으로 가장 먼 정점을 지난다. 안쪽 오프셋 껍데기를 만들지 않으므로 깊은 요철에서도 구겨짐/튀어나옴이 없다.
    /// 감김: 바깥 경계 변 a→b 에 대해 치마는 (b,a,a',b'), 마개는 b'→a' 방향 → 모든 변이 반대 방향으로 짝지어 닫힌다.
    /// </summary>
    private static Builder MakeCapped(Builder src, Vector3 n)
    {
        var outB = new Builder();
        for (int t = 0; t < src.tris.Count; t += 3)
            outB.AddTri(src.verts[src.tris[t]], src.verts[src.tris[t + 1]], src.verts[src.tris[t + 2]], 0);
        var vs = src.verts; var ts = src.tris;
        float d = vs.Max(v => Vector3.Dot(n, v.p));
        Vector3 Proj(Vector3 p) => p - n * (Vector3.Dot(n, p) - d);
        var weldId = new int[vs.Count]; var keyToId = new Dictionary<Vector3Int, int>();
        for (int i = 0; i < vs.Count; i++)
        {
            var key = WKey(vs[i].p);
            if (!keyToId.TryGetValue(key, out int id)) { id = keyToId.Count; keyToId[key] = id; }
            weldId[i] = id;
        }
        var edgeCount = new Dictionary<long, int>();
        for (int t = 0; t < ts.Count; t += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = weldId[ts[t + j]], b = weldId[ts[t + (j + 1) % 3]];
                if (a == b) continue;
                long k = EKey(a, b); edgeCount.TryGetValue(k, out int c); edgeCount[k] = c + 1;
            }
        var next = new Dictionary<int, (int ia, int ib)>();
        for (int t = 0; t < ts.Count; t += 3)
            for (int j = 0; j < 3; j++)
            {
                int ia = ts[t + j], ib = ts[t + (j + 1) % 3];
                int a = weldId[ia], b = weldId[ib];
                if (a == b || edgeCount[EKey(a, b)] != 1) continue;
                next[a] = (ia, ib);
                // 치마: b → a → a' → b'
                var pa = vs[ia].p; var pb = vs[ib].p; var qa = Proj(pa); var qb = Proj(pb);
                var quad = new[] { pb, pa, qa, qb };
                var fn = Vector3.Cross(quad[1] - quad[0], quad[2] - quad[0]);
                if (fn.sqrMagnitude < 1e-20f) fn = Vector3.Cross(quad[2] - quad[0], quad[3] - quad[0]);
                fn = fn.normalized;
                var tan = (Vector4)(quad[1] - quad[0]).normalized; tan.w = 1f;
                var uvs = new[] { vs[ib].uv, vs[ia].uv, vs[ia].uv, vs[ib].uv };
                var q = new V[4];
                for (int k = 0; k < 4; k++) q[k] = new V { p = quad[k], n = fn, t = tan, uv = uvs[k] };
                outB.AddTriKeepFlat(q[0], q[1], q[2], 1);
                outB.AddTriKeepFlat(q[0], q[2], q[3], 1);
            }
        var used = new HashSet<int>();
        foreach (var start in next.Keys.ToList())
        {
            if (used.Contains(start)) continue;
            var loop = new List<(int ia, int ib)>(); int cur = start;
            while (next.TryGetValue(cur, out var e) && used.Add(cur)) { loop.Add(e); cur = weldId[e.ib]; }
            if (loop.Count < 3) continue;
            var center = Vector3.zero;
            foreach (var e in loop) center += Proj(vs[e.ia].p);
            center /= loop.Count;
            var cv = new V { p = center, n = n, t = new Vector4(1, 0, 0, 1), uv = vs[loop[0].ia].uv };
            foreach (var e in loop)
            {
                var va = vs[e.ia]; va.p = Proj(va.p); va.n = n;
                var vb = vs[e.ib]; vb.p = Proj(vb.p); vb.n = n;
                outB.AddTriKeepFlat(vb, va, cv, 1);
            }
        }
        return outB;
    }

    /// <summary>
    /// Frame 안쪽 벽 다듬기(용접 정점 단위, inner 를 제자리 수정).
    ///  1) 바닥 평탄화: 바닥 외판(아래 향함, floorMin~floorMax 안) 정점의 안쪽 높이를 floorY 이상으로 올림 → 바닥 요철 제거.
    ///  2) 라플라시안 평활: 경계 정점(림/절단면)은 고정. 매 회 "바깥 면에서 법선 방향 minDepthRatio×깊이 이상 안쪽" 조건으로 되밀어
    ///     안쪽 벽이 바깥 면을 뚫고 나오지 않게 한다.
    ///  3) smoothNormals: 평활된 형상에서 법선을 다시 계산(바깥 면 방향 기준)해 반환. 아니면 null.
    /// </summary>
    private static Vector3[] ShapeInnerWall(InnerWallData w, List<int> ts, int[] weldId, List<Vector3> wpos, List<Vector3> nSum,
        Vector3[] dir, float[] depthOf, HashSet<int> boundary, Vector3[] inner, float clampRatio)
    {
        int n = inner.Length;
        var isFloor = new bool[n];
        if (w.flattenFloor)
            for (int id = 0; id < n; id++)
            {
                var p = wpos[id]; var nn = nSum[id].normalized;
                isFloor[id] = nn.y <= w.floorNormalY && p.x >= w.floorMin.x && p.x <= w.floorMax.x && p.y >= w.floorMin.y && p.y <= w.floorMax.y
                              && p.z >= w.floorMin.z && p.z <= w.floorMax.z && !boundary.Contains(id);
            }
        void Floor() { for (int id = 0; id < n; id++) if (isFloor[id] && inner[id].y < w.floorY) inner[id].y = w.floorY; }
        Floor();

        if (w.smoothIterations > 0)
        {
            var nb = new List<int>[n];
            for (int id = 0; id < n; id++) nb[id] = new List<int>(6);
            for (int t = 0; t < ts.Count; t += 3)
                for (int j = 0; j < 3; j++)
                {
                    int a = weldId[ts[t + j]], b = weldId[ts[t + (j + 1) % 3]];
                    if (a == b) continue;
                    if (!nb[a].Contains(b)) nb[a].Add(b);
                    if (!nb[b].Contains(a)) nb[b].Add(a);
                }
            // 최대 깊이: maxDepth 와 "원본 메쉬 반대편 면까지 거리 × clampRatio" 중 작은 값 (얇은 필러 등에서 반대편 뚫림 방지)
            var limit = new float[n];
            float maxDepth = w.maxDepth > 0f ? w.maxDepth : 0f;
            for (int id = 0; id < n; id++)
                limit[id] = isFloor[id] ? float.MaxValue : maxDepth > 0f ? Mathf.Min(maxDepth, s_grid.Hit(wpos[id], -dir[id], maxDepth / clampRatio) * clampRatio) : float.MaxValue;
            var next = new Vector3[n];
            for (int it = 0; it < w.smoothIterations; it++)
            {
                for (int id = 0; id < n; id++)
                {
                    if (boundary.Contains(id) || nb[id].Count == 0) { next[id] = inner[id]; continue; }
                    var avg = Vector3.zero; foreach (int k in nb[id]) avg += inner[k]; avg /= nb[id].Count;
                    var disp = (avg - inner[id]) * w.smoothLambda;
                    if (w.inwardOnly)
                    {
                        // 바깥(법선) 쪽으로 나오는 성분은 버림 → 오목한 바깥 디테일(그릴 홈, 손잡이 컵)이 안쪽으로 비치지 않고
                        // 안쪽 벽이 가장 깊은 면 쪽으로 평평해진다
                        float outward = Vector3.Dot(disp, dir[id]);
                        if (outward > 0f) disp -= dir[id] * outward;
                    }
                    var q = inner[id] + disp;
                    float minD = w.minDepthRatio * depthOf[id];
                    float dd = Vector3.Dot(wpos[id] - q, dir[id]);
                    if (dd < minD) q -= dir[id] * (minD - dd);
                    float maxD = Mathf.Max(minD, limit[id]);
                    if (dd > maxD) q += dir[id] * (dd - maxD);
                    next[id] = q;
                }
                Array.Copy(next, inner, n);
                Floor();
            }
        }
        if (!w.smoothNormals) return null;
        var acc = new Vector3[n];
        for (int t = 0; t < ts.Count; t += 3)
        {
            int a = weldId[ts[t]], b = weldId[ts[t + 1]], c = weldId[ts[t + 2]];
            var fn = Vector3.Cross(inner[b] - inner[a], inner[c] - inner[a]); // 바깥 면과 같은 감김 → 바깥 쪽 법선(면적 가중)
            acc[a] += fn; acc[b] += fn; acc[c] += fn;
        }
        for (int id = 0; id < n; id++)
        {
            var o = nSum[id].sqrMagnitude > 1e-12f ? nSum[id].normalized : Vector3.up;
            acc[id] = acc[id].sqrMagnitude > 1e-16f ? acc[id].normalized : o;
            if (Vector3.Dot(acc[id], o) < 0f) acc[id] = o; // 뒤집힌 곳은 원래 법선
        }
        return acc;
    }

    /// <summary>열린 모서리(용접 기준 한 면만 쓰는 변) / 비다양체 모서리(3면 이상) 수</summary>
    private static void CountEdges(Builder b, out int open, out int nonManifold)
    {
        var wid = new int[b.verts.Count]; var keyToId = new Dictionary<Vector3Int, int>();
        for (int i = 0; i < b.verts.Count; i++)
        {
            var key = WKey(b.verts[i].p);
            if (!keyToId.TryGetValue(key, out int id)) { id = keyToId.Count; keyToId[key] = id; }
            wid[i] = id;
        }
        var edgeCount = new Dictionary<long, int>();
        foreach (var sub in b.subs)
            for (int t = 0; t < sub.Count; t += 3)
                for (int j = 0; j < 3; j++)
                {
                    int a = wid[sub[t + j]], c = wid[sub[t + (j + 1) % 3]];
                    if (a == c) continue;
                    long k = EKey(a, c); edgeCount.TryGetValue(k, out int n); edgeCount[k] = n + 1;
                }
        open = edgeCount.Values.Count(v => v == 1);
        nonManifold = edgeCount.Values.Count(v => v > 2);
    }

    // ───────────── 레이 (깊이 제한용, 균일 격자 가속) ─────────────
    private static RayGrid s_grid;
    private class RayGrid
    {
        private readonly Vector3[] P; private readonly int[] T; private readonly float cell;
        private readonly Dictionary<Vector3Int, List<int>> cells = new Dictionary<Vector3Int, List<int>>();
        private readonly int[] stamp; private int stampId;
        public RayGrid(Vector3[] p, int[] t, float cellSize)
        {
            P = p; T = t; cell = cellSize; stamp = new int[t.Length / 3];
            bmin = Vector3.one * float.MaxValue; bmax = Vector3.one * float.MinValue;
            foreach (var q in p) { bmin = Vector3.Min(bmin, q); bmax = Vector3.Max(bmax, q); }
            bmin -= Vector3.one * 0.02f; bmax += Vector3.one * 0.02f;
            for (int i = 0; i < t.Length; i += 3)
            {
                var mn = Vector3.Min(p[t[i]], Vector3.Min(p[t[i + 1]], p[t[i + 2]]));
                var mx = Vector3.Max(p[t[i]], Vector3.Max(p[t[i + 1]], p[t[i + 2]]));
                var a = Vector3Int.FloorToInt(mn / cell); var b = Vector3Int.FloorToInt(mx / cell);
                for (int x = a.x; x <= b.x; x++) for (int y = a.y; y <= b.y; y++) for (int z = a.z; z <= b.z; z++)
                {
                    var k = new Vector3Int(x, y, z);
                    if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<int>();
                    l.Add(i);
                }
            }
        }
        public Vector3 bmin, bmax;
        /// <summary>
        /// 3D DDA 로 격자를 따라가며 경계 상자 밖으로 빠져나가는지 검사(어떤 삼각형이든 1mm 이후에 맞으면 막힘).
        /// </summary>
        public bool Escapes(Vector3 o, Vector3 dir)
        {
            float tExit = float.MaxValue;
            for (int k = 0; k < 3; k++)
            {
                if (Mathf.Abs(dir[k]) < 1e-9f) continue;
                float tk = dir[k] > 0 ? (bmax[k] - o[k]) / dir[k] : (bmin[k] - o[k]) / dir[k];
                tExit = Mathf.Min(tExit, tk);
            }
            if (tExit <= 0f) return true;
            stampId++;
            var c = Vector3Int.FloorToInt(o / cell);
            var step = new Vector3Int(dir.x > 0 ? 1 : -1, dir.y > 0 ? 1 : -1, dir.z > 0 ? 1 : -1);
            var tMax = Vector3.zero; var tDelta = Vector3.zero;
            for (int k = 0; k < 3; k++)
            {
                if (Mathf.Abs(dir[k]) < 1e-9f) { tMax[k] = float.MaxValue; tDelta[k] = float.MaxValue; continue; }
                float next = (c[k] + (dir[k] > 0 ? 1 : 0)) * cell;
                tMax[k] = (next - o[k]) / dir[k];
                tDelta[k] = cell / Mathf.Abs(dir[k]);
            }
            float t = 0f;
            int guard = 0;
            while (t <= tExit && guard++ < 4000)
            {
                if (cells.TryGetValue(c, out var l))
                    foreach (int ti in l)
                    {
                        if (stamp[ti / 3] == stampId) continue;
                        stamp[ti / 3] = stampId;
                        if (RayTri(o, dir, ti, out float dist) && dist > 0.001f) return false;
                    }
                if (tMax.x <= tMax.y && tMax.x <= tMax.z) { t = tMax.x; tMax.x += tDelta.x; c.x += step.x; }
                else if (tMax.y <= tMax.z) { t = tMax.y; tMax.y += tDelta.y; c.y += step.y; }
                else { t = tMax.z; tMax.z += tDelta.z; c.z += step.z; }
            }
            return true;
        }
        private bool RayTri(Vector3 o, Vector3 dir, int t, out float dist)
        {
            dist = 0f;
            Vector3 p0 = P[T[t]], p1 = P[T[t + 1]], p2 = P[T[t + 2]];
            Vector3 e1 = p1 - p0, e2 = p2 - p0, pv = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, pv);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float inv = 1f / det; Vector3 tv = o - p0;
            float u = Vector3.Dot(tv, pv) * inv; if (u < 0f || u > 1f) return false;
            Vector3 qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(dir, qv) * inv; if (v < 0f || u + v > 1f) return false;
            dist = Vector3.Dot(e2, qv) * inv;
            return true;
        }

        /// <summary>최근접 교차 거리(시작점 1mm 이내 무시). 없으면 maxDist.</summary>
        public float Hit(Vector3 o, Vector3 dir, float maxDist)
        {
            float best = maxDist; stampId++;
            var e = o + dir * maxDist;
            var a = Vector3Int.FloorToInt(Vector3.Min(o, e) / cell); var b = Vector3Int.FloorToInt(Vector3.Max(o, e) / cell);
            for (int x = a.x; x <= b.x; x++) for (int y = a.y; y <= b.y; y++) for (int z = a.z; z <= b.z; z++)
            {
                if (!cells.TryGetValue(new Vector3Int(x, y, z), out var l)) continue;
                foreach (int t in l)
                {
                    if (stamp[t / 3] == stampId) continue;
                    stamp[t / 3] = stampId;
                    Vector3 p0 = P[T[t]], p1 = P[T[t + 1]], p2 = P[T[t + 2]];
                    Vector3 e1 = p1 - p0, e2 = p2 - p0, pv = Vector3.Cross(dir, e2);
                    float det = Vector3.Dot(e1, pv);
                    if (Mathf.Abs(det) < 1e-12f) continue;
                    float inv = 1f / det; Vector3 tv = o - p0;
                    float u = Vector3.Dot(tv, pv) * inv; if (u < 0f || u > 1f) continue;
                    Vector3 qv = Vector3.Cross(tv, e1);
                    float v = Vector3.Dot(dir, qv) * inv; if (v < 0f || u + v > 1f) continue;
                    float dist = Vector3.Dot(e2, qv) * inv;
                    if (dist > 0.001f && dist < best) best = dist;
                }
            }
            return best;
        }
    }

    private static Bounds BoundsOf(List<V> vs)
    {
        var b = new Bounds(vs[0].p, Vector3.zero);
        foreach (var v in vs) b.Encapsulate(v.p);
        return b;
    }

    private static Mesh BuildMesh(Builder b, Vector3 offset, string name)
    {
        var m = new Mesh { name = name };
        if (b.verts.Count > 65000) m.indexFormat = IndexFormat.UInt32;
        m.SetVertices(b.verts.Select(v => v.p + offset).ToList());
        m.SetNormals(b.verts.Select(v => v.n).ToList());
        m.SetTangents(b.verts.Select(v => v.t).ToList());
        m.SetUVs(0, b.verts.Select(v => v.uv).ToList());
        m.subMeshCount = 2;
        m.SetTriangles(b.subs[0], 0);
        m.SetTriangles(b.subs[1], 1);
        m.RecalculateBounds();
        return m;
    }

    // ───────────── 에셋 저장 ─────────────
    private static Mesh SaveMesh(Mesh mesh, string path)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null)
        {
            existing.Clear();
            EditorUtility.CopySerialized(mesh, existing);
            existing.name = Path.GetFileNameWithoutExtension(path);
            Object.DestroyImmediate(mesh);
            EditorUtility.SetDirty(existing);
            return existing;
        }
        mesh.name = Path.GetFileNameWithoutExtension(path);
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    private static Material SaveMaterial(string path, Func<Material> make)
    {
        var fresh = make();
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null)
        {
            existing.shader = fresh.shader;
            existing.CopyPropertiesFromMaterial(fresh);
            existing.shaderKeywords = fresh.shaderKeywords;
            Object.DestroyImmediate(fresh);
            EditorUtility.SetDirty(existing);
            return existing;
        }
        AssetDatabase.CreateAsset(fresh, path);
        return fresh;
    }

    private static void BuildPrefab(string path, string rootName, Mesh frame, List<Part> parts, Dictionary<string, Mesh> meshes,
        Material[] mats, Vector3 modelOffset, bool exploded)
    {
        var root = new GameObject(rootName);
        try
        {
            // 원본 프리팹(Car_Sedan.prefab)의 Model 자식 오프셋(약 1mm)을 Frame 에 그대로 적용 → 루트 기준 위치가 원본과 일치.
            // 부품은 루트 직속: localPosition = assembledPos (루트 로컬)
            var frameGo = MakeRenderer("Frame", root.transform, frame, mats);
            frameGo.transform.localPosition = modelOffset;
            foreach (var part in parts)
            {
                if (!meshes.TryGetValue(part.name, out var mesh)) continue;
                var go = MakeRenderer(part.name, root.transform, mesh, mats);
                go.transform.localPosition = part.pivot + modelOffset;
                if (!exploded) continue;
                go.transform.localPosition += part.data.explodeOffset;
                go.transform.localRotation = Quaternion.Euler(part.data.explodeEuler);
            }
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { Object.DestroyImmediate(root); }
    }

    private static GameObject MakeRenderer(string name, Transform parent, Mesh mesh, Material[] mats)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = mats;
        return go;
    }

    private static string BuildInfoJson(List<Part> parts, List<PartReport> reports, Vector3 modelOffset, SplitDefinition def)
    {
        string F(float v) => v.ToString("0.####", IC);
        string A(Vector3 v) => $"[{F(v.x)}, {F(v.y)}, {F(v.z)}]";
        var sb = new StringBuilder();
        sb.Append("{\n  \"source\": \"" + SRC_MESH + "\",\n");
        sb.Append("  \"coordinateSpace\": \"차량 프리팹 루트 로컬 (m). assembledPos = 부품 메쉬 원점(힌지/체결 기준점)의 조립 위치. 메쉬 공간 = assembledPos - frameOffset\",\n");
        sb.Append($"  \"frameOffset\": {A(modelOffset)},\n  \"panelThickness\": {F(def.panelThickness)},\n  \"innerWallDepth\": {F(def.innerWallDepth)},\n");
        sb.Append("  \"parts\": [\n");
        for (int i = 0; i < reports.Count; i++)
        {
            var r = reports[i];
            var part = parts.FirstOrDefault(p => p.name == r.name);
            sb.Append($"    {{ \"name\": \"{r.name}\", \"mesh\": \"Car_Sedan_{r.name}.asset\", \"assembledPos\": {A(r.assembledPos)}, ");
            sb.Append(r.hingeAxis == Vector3.zero ? "\"hingeAxis\": null, " : $"\"hingeAxis\": {A(r.hingeAxis)}, ");
            sb.Append($"\"pivot\": \"{r.pivotMode}\", \"tris\": {r.tris}, \"openEdges\": {r.openEdges}, \"size\": {A(r.size)}");
            if (part != null)
            {
                sb.Append($", \"explodeOffset\": {A(part.data.explodeOffset)}, \"planes\": [");
                sb.Append(string.Join(", ", part.planes.Select(pl => $"{{\"name\":\"{pl.name}\",\"n\":{A(pl.n)},\"d\":{F(pl.d)},\"refineMm\":{(pl.delta * 1000f).ToString("0.0", IC)}}}")));
                sb.Append("]");
            }
            sb.Append(i < reports.Count - 1 ? " },\n" : " }\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    // ═════════════ 검증: 프리뷰 씬 렌더 (열린 씬은 건드리지 않음) ═════════════
    private class Shot
    {
        public string name; public bool ortho; public Vector3 pos; public Vector3 look; public Vector3 up = Vector3.up; public float size = 1.25f; public float fov = 30f;
    }

    /// <summary>
    /// 조립 프리팹 vs 원본 프리팹 화소 비교(정사영 앞/뒤/좌/우/상 + 3/4 두 방향) + 분리 상태 스크린샷.
    /// subfolder: 결과 프리팹 하위 폴더("" = 기본).
    /// </summary>
    public static string Verify(string subfolder)
    {
        string sub = string.IsNullOrEmpty(subfolder) ? "" : "/" + subfolder;
        string tag = string.IsNullOrEmpty(subfolder) ? SHOT_TAG : SHOT_TAG + "_" + subfolder;
        var original = AssetDatabase.LoadAssetAtPath<GameObject>(SRC_PREFAB);
        var assembled = AssetDatabase.LoadAssetAtPath<GameObject>($"{PREFAB_DIR}{sub}/Car_Sedan_Assembled.prefab");
        var explodedPf = AssetDatabase.LoadAssetAtPath<GameObject>($"{PREFAB_DIR}{sub}/Car_Sedan_Exploded.prefab");
        if (original == null || assembled == null || explodedPf == null) return $"{LOG_TAG} 검증용 프리팹 없음";
        EnsureFolder(SHOT_DIR);
        var log = new StringBuilder($"{LOG_TAG} 검증 ({tag})\n");
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var sunGo = new GameObject("Sun"); SceneManager.MoveGameObjectToScene(sunGo, scene);
            var sun = sunGo.AddComponent<Light>(); sun.type = LightType.Directional; sun.intensity = 1.3f; sun.shadows = LightShadows.None;
            sunGo.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
            var camGo = new GameObject("Cam"); SceneManager.MoveGameObjectToScene(camGo, scene);
            var cam = camGo.AddComponent<Camera>(); cam.scene = scene; cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.70f, 0.80f); cam.nearClipPlane = 0.05f; cam.farClipPlane = 60f;

            var o = (GameObject)PrefabUtility.InstantiatePrefab(original, scene);
            var a = (GameObject)PrefabUtility.InstantiatePrefab(assembled, scene);

            // ── 1) 조립 vs 원본 화소 비교 ──
            var cmp = new List<Shot>
            {
                new Shot { name = "Front", ortho = true, pos = new Vector3(0, 1.0f, 8), look = Vector3.back },
                new Shot { name = "Rear", ortho = true, pos = new Vector3(0, 1.0f, -8), look = Vector3.forward },
                new Shot { name = "Left", ortho = true, pos = new Vector3(-8, 1.0f, 0), look = Vector3.right, size = 1.3f },
                new Shot { name = "Right", ortho = true, pos = new Vector3(8, 1.0f, 0), look = Vector3.left, size = 1.3f },
                new Shot { name = "Top", ortho = true, pos = new Vector3(0, 8, 0), look = Vector3.down, up = Vector3.forward, size = 2.1f },
                new Shot { name = "34FL", pos = new Vector3(-4.3f, 2.4f, 4.6f), look = new Vector3(0, 0.85f, 0) - new Vector3(-4.3f, 2.4f, 4.6f) },
                new Shot { name = "34RR", pos = new Vector3(4.3f, 2.4f, -4.6f), look = new Vector3(0, 0.85f, 0) - new Vector3(4.3f, 2.4f, -4.6f) },
            };
            const int W = 1600, H = 1000;
            int totalBad = 0, totalCar = 0;
            foreach (var s in cmp)
            {
                Place(cam, s, (float)W / H);
                o.SetActive(true); a.SetActive(false);
                var t0 = RenderToTexture(cam, W, H, 1);
                o.SetActive(false); a.SetActive(true);
                var t1 = RenderToTexture(cam, W, H, 1);
                var p0 = t0.GetPixels32(); var p1 = t1.GetPixels32();
                var bgPx = p0[0];
                int bad = 0, car = 0; int maxD = 0;
                var diff = new Color32[p0.Length];
                for (int i = 0; i < p0.Length; i++)
                {
                    int d = Mathf.Max(Mathf.Abs(p0[i].r - p1[i].r), Mathf.Max(Mathf.Abs(p0[i].g - p1[i].g), Mathf.Abs(p0[i].b - p1[i].b)));
                    bool isCar = !Same(p0[i], bgPx) || !Same(p1[i], bgPx);
                    if (isCar) car++;
                    if (d > 30) bad++;
                    maxD = Mathf.Max(maxD, d);
                    byte g = (byte)(p0[i].r / 4 + p0[i].g / 4 + p0[i].b / 8);
                    diff[i] = d > 30 ? new Color32(255, 0, 0, 255) : d > 8 ? new Color32(255, 200, 0, 255) : new Color32(g, g, g, 255);
                }
                totalBad += bad; totalCar += car;
                log.AppendLine($"  비교 {s.name}: d>30 {bad}px / 차량 {car}px = {(car > 0 ? 100.0 * bad / car : 0):F4}% (전체 대비 {100.0 * bad / p0.Length:F4}%), 최대차 {maxD}");
                SavePng(t1, $"{SHOT_DIR}/{tag}_A_{s.name}_Assembled.png");
                t0.SetPixels32(diff); t0.Apply();
                SavePng(t0, $"{SHOT_DIR}/{tag}_A_{s.name}_Diff.png");
                Object.DestroyImmediate(t0); Object.DestroyImmediate(t1);
            }
            log.AppendLine($"  비교 합계: d>30 {totalBad}px / 차량 {totalCar}px = {(totalCar > 0 ? 100.0 * totalBad / totalCar : 0):F4}%");

            // ── 2) 분리 상태 스크린샷 (그림자 켬, MSAA 4) ──
            o.SetActive(false);
            sun.shadows = LightShadows.Soft;
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane); SceneManager.MoveGameObjectToScene(ground, scene);
            ground.transform.localScale = new Vector3(3, 1, 3);
            var gm = new Material(Shader.Find("Universal Render Pipeline/Lit")); gm.SetColor("_BaseColor", new Color(0.55f, 0.56f, 0.58f)); gm.SetFloat("_Smoothness", 0.1f);
            ground.GetComponent<MeshRenderer>().sharedMaterial = gm;
            var e = (GameObject)PrefabUtility.InstantiatePrefab(explodedPf, scene);

            void Shoot(string file, Vector3 pos, Vector3 target, float fov, GameObject show, params string[] hide) => ShootN(file, pos, target, fov, 0.05f, show, hide);
            void ShootN(string file, Vector3 pos, Vector3 target, float fov, float near, GameObject show, params string[] hide)
            {
                a.SetActive(show == a); e.SetActive(show == e);
                foreach (Transform c in show.transform) c.gameObject.SetActive(!hide.Contains(c.name) && !(hide.Contains("*parts") && c.name != "Frame"));
                Place(cam, new Shot { pos = pos, look = target - pos, fov = fov }, (float)W / H);
                cam.nearClipPlane = near;
                var t = RenderToTexture(cam, W, H, 4);
                cam.nearClipPlane = 0.05f;
                SavePng(t, $"{SHOT_DIR}/{tag}_{file}.png"); Object.DestroyImmediate(t);
                foreach (Transform c in show.transform) c.gameObject.SetActive(true);
                log.AppendLine($"  스크린샷 {SHOT_DIR}/{tag}_{file}.png");
            }
            Shoot("E1_Exploded_34FL", new Vector3(-6.2f, 3.6f, 6.4f), new Vector3(0, 0.8f, 0), 38f, e);
            Shoot("E2_Exploded_34RR", new Vector3(6.2f, 3.6f, -6.4f), new Vector3(0, 0.8f, 0), 38f, e);
            Shoot("E3_FrameOnly_34FL", new Vector3(-4.4f, 2.6f, 4.4f), new Vector3(0, 0.85f, 0), 32f, a, "*parts");
            Shoot("E4_FrameOnly_34RR", new Vector3(4.4f, 2.6f, -4.4f), new Vector3(0, 0.85f, 0), 32f, a, "*parts");
            Shoot("E5_LeftDoorsOff_Close", new Vector3(-2.9f, 1.55f, 0.9f), new Vector3(0, 1.0f, -0.25f), 42f, a, "Door_FL", "Door_RL");
            Shoot("E6_FrontLeftDoorOff_Close", new Vector3(-2.2f, 1.75f, 2.0f), new Vector3(-0.3f, 1.0f, 0.2f), 42f, a, "Door_FL");
            Shoot("E7_HoodOff_Close", new Vector3(-1.2f, 2.7f, 3.4f), new Vector3(0, 0.9f, 1.4f), 40f, a, "Hood");
            Shoot("E8_TrunkOff_Close", new Vector3(1.0f, 2.6f, -3.6f), new Vector3(0, 0.95f, -1.5f), 40f, a, "Trunk");
            Shoot("E9_BumpersGlassOff_34FL", new Vector3(-4.4f, 2.6f, 4.4f), new Vector3(0, 0.85f, 0), 32f, a, "Bumper_F", "Bumper_R", "Windshield", "RearWindow");
            Shoot("E10_BumperFOff_Corner", new Vector3(-1.9f, 0.9f, 2.9f), new Vector3(-0.4f, 0.45f, 1.6f), 40f, a, "Bumper_F");
            // 목표 이미지(빈 차체 컨셉)와 같은 시점: 앞-왼쪽 위 3/4, Frame 만
            Shoot("T1_FrameOnly_TargetView", new Vector3(-3.5f, 3.9f, 4.3f), new Vector3(0, 0.75f, 0.05f), 33f, a, "*parts");
            // 클로즈업: 실내(위에서, 지붕을 near 평면으로 잘라냄) / 엔진룸 / 트렁크 / 그릴 윗부분(후드만 뗌)
            sun.shadows = LightShadows.None; // 잘라낸 지붕 그림자가 실내를 덮지 않게
            ShootN("I1_Interior_TopCut", new Vector3(-0.6f, 6.0f, -0.1f), new Vector3(0, 0.4f, -0.1f), 34f, 4.3f, a, "*parts");
            sun.shadows = LightShadows.Soft;
            Shoot("I2_EngineBay_Close", new Vector3(-0.9f, 2.5f, 2.9f), new Vector3(0, 0.7f, 1.35f), 40f, a, "*parts");
            Shoot("I3_Trunk_Close", new Vector3(0.9f, 2.5f, -3.2f), new Vector3(0, 0.75f, -1.45f), 40f, a, "*parts");
            Shoot("I4_GrilleTop_Close", new Vector3(0f, 1.6f, 2.6f), new Vector3(0, 0.92f, 1.72f), 30f, a, "Hood");
            // 3단계 부품(헤드램프/그릴/미러/테일램프) 분리 상태
            Shoot("F1_Front_LampsGrilleOff_34", new Vector3(-1.5f, 1.35f, 3.7f), new Vector3(-0.1f, 0.8f, 1.7f), 38f, a, "Headlight_L", "Headlight_R", "Grille");
            Shoot("F2_Front_LampsGrilleOff_Straight", new Vector3(0f, 1.05f, 4.6f), new Vector3(0f, 0.82f, 1.7f), 30f, a, "Headlight_L", "Headlight_R", "Grille");
            Shoot("F3_Front_FrameOnly_Straight", new Vector3(0f, 1.05f, 4.6f), new Vector3(0f, 0.82f, 1.7f), 30f, a, "*parts");
            Shoot("M1_MirrorL_Off_Side", new Vector3(-2.4f, 1.65f, 1.6f), new Vector3(-0.85f, 1.2f, 0.65f), 34f, a, "Mirror_L");
            Shoot("M2_MirrorR_Off_Side", new Vector3(2.4f, 1.65f, 1.6f), new Vector3(0.85f, 1.2f, 0.65f), 34f, a, "Mirror_R");
            Shoot("M3_MirrorL_DoorOff_Side", new Vector3(-2.4f, 1.65f, 1.6f), new Vector3(-0.85f, 1.2f, 0.65f), 34f, a, "Mirror_L", "Door_FL");
            Shoot("R1_Rear_TaillightsOff_34", new Vector3(1.4f, 1.45f, -4.0f), new Vector3(0.1f, 0.85f, -1.75f), 38f, a, "Taillight_L", "Taillight_R");
            Shoot("R2_Rear_TaillightsOff_Straight", new Vector3(0f, 1.1f, -4.6f), new Vector3(0f, 0.85f, -1.75f), 30f, a, "Taillight_L", "Taillight_R");
            Object.DestroyImmediate(gm);
            ground.SetActive(false);

            // ── 3) 부품 갤러리: 부품마다 [바깥 | 안쪽] 타일 (헤드라이트 조명, 그림자 끔) ──
            sun.shadows = LightShadows.None;
            a.SetActive(true); e.SetActive(false);
            var partTs = a.transform.Cast<Transform>().Where(c => c.name != "Frame").ToList();
            const int TW = 480, TH = 360, COLS = 4;
            int rows = (partTs.Count * 2 + COLS - 1) / COLS;
            var gallery = new Texture2D(TW * COLS, TH * rows, TextureFormat.RGB24, false);
            var fill = new Color32[TW * COLS * TH * rows];
            for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(40, 40, 46, 255);
            gallery.SetPixels32(fill);
            var order = new List<string>();
            for (int pi = 0; pi < partTs.Count; pi++)
            {
                foreach (Transform c in a.transform) c.gameObject.SetActive(c == partTs[pi]);
                var r = partTs[pi].GetComponent<MeshRenderer>();
                var bc = r.bounds.center; float ext = r.bounds.extents.magnitude;
                var outward = Vector3.Scale(bc - new Vector3(0, 0.9f, 0), new Vector3(1f, 1f, 1f / 1.9f));
                outward = outward.sqrMagnitude < 1e-4f ? Vector3.up : outward.normalized;
                float dist = ext / Mathf.Tan(22f * Mathf.Deg2Rad) * 1.05f;
                for (int side = 0; side < 2; side++)
                {
                    var dirv = side == 0 ? outward : -outward;
                    var camPos = bc + dirv * dist + Vector3.up * 0.15f * dist * (side == 0 ? 1 : -1);
                    var up = Mathf.Abs(Vector3.Dot((bc - camPos).normalized, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
                    Place(cam, new Shot { pos = camPos, look = bc - camPos, fov = 44f, up = up }, (float)TW / TH);
                    sunGo.transform.rotation = cam.transform.rotation * Quaternion.Euler(15f, 20f, 0f);
                    var t = RenderToTexture(cam, TW, TH, 4);
                    int tile = pi * 2 + side, tx = tile % COLS, ty = rows - 1 - tile / COLS;
                    gallery.SetPixels(tx * TW, ty * TH, TW, TH, t.GetPixels());
                    Object.DestroyImmediate(t);
                }
                order.Add(partTs[pi].name);
            }
            foreach (Transform c in a.transform) c.gameObject.SetActive(true);
            gallery.Apply();
            SavePng(gallery, $"{SHOT_DIR}/{tag}_G_PartsGallery.png"); Object.DestroyImmediate(gallery);
            log.AppendLine($"  부품 갤러리 {SHOT_DIR}/{tag}_G_PartsGallery.png (행 우선, 부품당 [바깥|안쪽]): {string.Join(", ", order)}");
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
        AssetDatabase.Refresh();
        return log.ToString();
    }

    /// <summary>
    /// 분석용 단발 렌더(MCP 호출용). orthoSize&gt;0 이면 정사영. near 로 단면(카메라 쪽 잘라냄) 렌더 가능.
    /// hide: 숨길 자식 이름("*parts" = Frame 외 전부). 결과는 Assets/Screenshots/{file}.png
    /// </summary>
    public static string DebugRender(string file, Vector3 pos, Vector3 target, float fov, float orthoSize, float near, string hideCsv, bool shadows)
    {
        var assembled = AssetDatabase.LoadAssetAtPath<GameObject>($"{PREFAB_DIR}/Car_Sedan_Assembled.prefab");
        var hide = (hideCsv ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var sunGo = new GameObject("Sun"); SceneManager.MoveGameObjectToScene(sunGo, scene);
            var sun = sunGo.AddComponent<Light>(); sun.type = LightType.Directional; sun.intensity = 1.3f;
            sun.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            sunGo.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
            var camGo = new GameObject("Cam"); SceneManager.MoveGameObjectToScene(camGo, scene);
            var cam = camGo.AddComponent<Camera>(); cam.scene = scene; cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.70f, 0.80f); cam.farClipPlane = 60f;
            if (shadows)
            {
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane); SceneManager.MoveGameObjectToScene(ground, scene);
                ground.transform.localScale = new Vector3(3, 1, 3);
            }
            var a = (GameObject)PrefabUtility.InstantiatePrefab(assembled, scene);
            foreach (Transform c in a.transform) c.gameObject.SetActive(!hide.Contains(c.name) && !(hide.Contains("*parts") && c.name != "Frame"));
            Place(cam, new Shot { pos = pos, look = target - pos, fov = fov, ortho = orthoSize > 0, size = orthoSize }, 1.6f);
            cam.nearClipPlane = near;
            var t = RenderToTexture(cam, 1600, 1000, 4);
            SavePng(t, $"{SHOT_DIR}/{file}.png"); Object.DestroyImmediate(t);
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
        return $"{SHOT_DIR}/{file}.png";
    }

    // ───────────── 분석용: 원본 메쉬 정사영 와이어 / 단면 (MCP 호출용, 결과 PNG 는 Assets/Screenshots) ─────────────
    private static Texture2D ReadableBaseColor()
    {
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(SRC_TEX);
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(tex, rt); var prev = RenderTexture.active; RenderTexture.active = rt;
        var cp = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
        cp.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0); cp.Apply();
        RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
        return cp;
    }

    /// <summary>
    /// 원본 메쉬를 axis 방향 정사영으로 소프트 래스터(텍스처 색) + 삼각형 모서리(노랑, 깊이 단차 큰 곳 빨강) + 5cm 격자(청록).
    /// 화면: u = (axis x→z, y→x, z→x), v = (axis y→z, 그 외 y), 아래쪽이 v0. 깊이 [dMin,dMax] 밖은 잘라낸다.
    /// nearIsMax=true 면 axis 값이 큰 쪽이 카메라 쪽(예: z 축 앞에서 보기), false 면 작은 쪽(뒤/왼쪽에서 보기).
    /// 림·꼭짓점 링·격자 양자화를 눈으로 읽어 절단 평면/다각형 꼭짓점을 잡을 때 쓴다.
    /// </summary>
    public static string DebugWire(string file, string axis, float u0, float u1, float v0, float v1, float dMin, float dMax, float ppm, bool nearIsMax)
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(SRC_MESH);
        ReadMesh(mesh, out var V, out _, out _, out var UV, out var T);
        var cp = ReadableBaseColor();
        float sgn = nearIsMax ? 1f : -1f;
        int ai = axis == "x" ? 0 : axis == "y" ? 1 : 2, ui = axis == "x" ? 2 : 0, vi = axis == "y" ? 2 : 1;
        int W = Mathf.CeilToInt((u1 - u0) * ppm), H = Mathf.CeilToInt((v1 - v0) * ppm);
        var px = new Color32[W * H]; var zb = new float[W * H]; var tid = new int[W * H];
        for (int i = 0; i < px.Length; i++) { px[i] = new Color32(60, 80, 110, 255); zb[i] = float.MinValue; tid[i] = -1; }
        for (int t = 0; t < T.Length; t += 3)
        {
            Vector3 p0 = V[T[t]], p1 = V[T[t + 1]], p2 = V[T[t + 2]];
            if (Mathf.Max(p0[ai], Mathf.Max(p1[ai], p2[ai])) < dMin || Mathf.Min(p0[ai], Mathf.Min(p1[ai], p2[ai])) > dMax) continue;
            float d0 = p0[ai] * sgn, d1 = p1[ai] * sgn, d2 = p2[ai] * sgn;
            Vector2 s0 = new Vector2((p0[ui] - u0) * ppm, (p0[vi] - v0) * ppm), s1 = new Vector2((p1[ui] - u0) * ppm, (p1[vi] - v0) * ppm), s2 = new Vector2((p2[ui] - u0) * ppm, (p2[vi] - v0) * ppm);
            float area = (s1.x - s0.x) * (s2.y - s0.y) - (s2.x - s0.x) * (s1.y - s0.y);
            if (Mathf.Abs(area) < 1e-9f) continue;
            int xa = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(s0.x, Mathf.Min(s1.x, s2.x)))), xb = Mathf.Min(W - 1, Mathf.CeilToInt(Mathf.Max(s0.x, Mathf.Max(s1.x, s2.x))));
            int ya = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(s0.y, Mathf.Min(s1.y, s2.y)))), yb = Mathf.Min(H - 1, Mathf.CeilToInt(Mathf.Max(s0.y, Mathf.Max(s1.y, s2.y))));
            for (int y = ya; y <= yb; y++) for (int x = xa; x <= xb; x++)
            {
                float qx = x + 0.5f, qy = y + 0.5f;
                float w0 = ((s1.x - qx) * (s2.y - qy) - (s2.x - qx) * (s1.y - qy)) / area;
                float w1 = ((s2.x - qx) * (s0.y - qy) - (s0.x - qx) * (s2.y - qy)) / area;
                float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float dd = w0 * d0 + w1 * d1 + w2 * d2, real = dd * sgn;
                if (real < dMin || real > dMax) continue;
                int k = y * W + x; if (dd <= zb[k]) continue;
                zb[k] = dd; tid[k] = t;
                var uv = UV[T[t]] * w0 + UV[T[t + 1]] * w1 + UV[T[t + 2]] * w2;
                px[k] = (Color32)cp.GetPixelBilinear(uv.x, uv.y);
            }
        }
        var outPx = (Color32[])px.Clone();
        for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++)
        {
            int k = y * W + x; if (tid[k] < 0) continue;
            if (tid[k + 1] != tid[k] || tid[k + W] != tid[k])
            {
                float dz = Mathf.Max(Mathf.Abs(zb[k + 1] - zb[k]), Mathf.Abs(zb[k + W] - zb[k]));
                outPx[k] = dz > 0.004f ? new Color32(255, 40, 40, 255) : new Color32(255, 230, 0, 255);
            }
        }
        DrawGrid(outPx, W, H, u0, v0, ppm);
        SaveAnalysis(outPx, W, H, file); Object.DestroyImmediate(cp);
        return $"{SHOT_DIR}/{file}.png {W}x{H} u={"xyz"[ui]}[{u0},{u1}] v={"xyz"[vi]}[{v0},{v1}] (아래=v0, 5cm 격자: 밝은 청록=10cm)";
    }

    /// <summary>원본 메쉬를 axis=c 평면으로 자른 단면선(텍스처 색). 화면 축 규칙은 DebugWire 와 같다.</summary>
    public static string DebugSection(string file, string axis, float c, float u0, float u1, float v0, float v1, float ppm)
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(SRC_MESH);
        ReadMesh(mesh, out var V, out _, out _, out var UV, out var T);
        var cp = ReadableBaseColor();
        int ai = axis == "x" ? 0 : axis == "y" ? 1 : 2, ui = axis == "x" ? 2 : 0, vi = axis == "y" ? 2 : 1;
        int W = Mathf.CeilToInt((u1 - u0) * ppm), H = Mathf.CeilToInt((v1 - v0) * ppm);
        var px = new Color32[W * H];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(70, 90, 120, 255);
        DrawGrid(px, W, H, u0, v0, ppm);
        for (int t = 0; t < T.Length; t += 3)
        {
            var pts = new List<Vector3>(2); var uvs = new List<Vector2>(2);
            for (int j = 0; j < 3; j++)
            {
                var p = V[T[t + j]]; var q = V[T[t + (j + 1) % 3]];
                float dp = p[ai] - c, dq = q[ai] - c;
                if ((dp < 0 && dq >= 0) || (dp >= 0 && dq < 0)) { float k = dp / (dp - dq); pts.Add(Vector3.Lerp(p, q, k)); uvs.Add(Vector2.Lerp(UV[T[t + j]], UV[T[t + (j + 1) % 3]], k)); }
            }
            if (pts.Count < 2) continue;
            for (int s = 0; s <= 60; s++)
            {
                var p = Vector3.Lerp(pts[0], pts[1], s / 60f); var uvp = Vector2.Lerp(uvs[0], uvs[1], s / 60f);
                var col = (Color32)cp.GetPixelBilinear(uvp.x, uvp.y);
                int x = Mathf.RoundToInt((p[ui] - u0) * ppm), y = Mathf.RoundToInt((p[vi] - v0) * ppm);
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                {
                    int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                    px[yy * W + xx] = col;
                }
            }
        }
        SaveAnalysis(px, W, H, file); Object.DestroyImmediate(cp);
        return $"{SHOT_DIR}/{file}.png {W}x{H} u={"xyz"[ui]}[{u0},{u1}] v={"xyz"[vi]}[{v0},{v1}] (아래=v0)";
    }

    private static void DrawGrid(Color32[] px, int W, int H, float u0, float v0, float ppm)
    {
        for (int x = 0; x < W; x++)
        {
            float u = u0 + x / ppm; if (Mathf.Abs(u * 20f - Mathf.Round(u * 20f)) >= 0.5f / ppm * 20f) continue;
            var c = Mathf.RoundToInt(u * 20f) % 2 == 0 ? new Color32(0, 255, 255, 255) : new Color32(0, 140, 255, 255);
            for (int y = 0; y < H; y += 3) px[y * W + x] = c;
        }
        for (int y = 0; y < H; y++)
        {
            float v = v0 + y / ppm; if (Mathf.Abs(v * 20f - Mathf.Round(v * 20f)) >= 0.5f / ppm * 20f) continue;
            var c = Mathf.RoundToInt(v * 20f) % 2 == 0 ? new Color32(0, 255, 255, 255) : new Color32(0, 140, 255, 255);
            for (int x = 0; x < W; x += 3) px[y * W + x] = c;
        }
    }

    private static void SaveAnalysis(Color32[] px, int W, int H, string file)
    {
        var img = new Texture2D(W, H, TextureFormat.RGB24, false);
        img.SetPixels32(px); img.Apply();
        SavePng(img, $"{SHOT_DIR}/{file}.png"); Object.DestroyImmediate(img);
    }

    private static bool Same(Color32 a, Color32 b) => Mathf.Abs(a.r - b.r) <= 2 && Mathf.Abs(a.g - b.g) <= 2 && Mathf.Abs(a.b - b.b) <= 2;

    private static void Place(Camera cam, Shot s, float aspect)
    {
        cam.orthographic = s.ortho; cam.orthographicSize = s.size; cam.fieldOfView = s.fov; cam.aspect = aspect;
        cam.transform.SetPositionAndRotation(s.pos, Quaternion.LookRotation(s.look.normalized, s.up));
    }

    private static Texture2D RenderToTexture(Camera cam, int w, int h, int msaa)
    {
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = msaa };
        cam.targetTexture = rt; cam.aspect = (float)w / h;
        cam.Render();
        var prev = RenderTexture.active; RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
        RenderTexture.active = prev; cam.targetTexture = null;
        rt.Release(); Object.DestroyImmediate(rt);
        return tex;
    }

    private static void SavePng(Texture2D t, string assetPath)
    {
        File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), assetPath), t.EncodeToPNG());
    }
}
