using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// [PoC] AI 생성 통짜 세단 메쉬(Car_Sedan.fbx)에서 체결용 부품(왼쪽 앞 도어, 후드)을 메쉬 분할로 떼어내는 실험 툴.
///
/// 방식: "평면 클리핑" — 부품마다 볼록 영역(반공간 평면 N개, 차량 로컬 좌표)을 정의하고
///  - 영역 안 삼각형 → 부품, 밖 → 차체, 걸친 삼각형 → 평면으로 정확히 잘라(정점 속성 보간) 양쪽에 나눔.
///  - 경계가 계단식(삼각형 단위)이 아니라 직선이 되고, 양쪽이 같은 절단 정점을 공유하므로 틈·색 불연속이 없다.
///  - 평면 위치는 측면/상면 직교 렌더에서 텍스처 윤곽선을 읽어 잡은 초기값 + BaseColor 텍스처 선(어두운 선)
///    자동 보정(±3cm 범위에서 절단선 따라 샘플한 휘도의 국소 최소 = 선 중앙)으로 미세 조정한다.
///  - 두께: 패널을 (용접된 평균 법선) 반대로 T만큼 오프셋한 안쪽 껍데기 + 테두리 띠를 붙인 "Thick" 메쉬 생성.
///  - 차체 구멍: 부품의 음각(오프셋 4cm 안쪽 껍데기 + 테두리, 바깥을 향하게 뒤집음)을 어두운 재질로 차체에 붙여 막는다.
///
/// 산출물(전부 Experiments 폴더, 기존 에셋 수정 없음, 재실행 시 같은 경로 덮어씀 → GUID 유지):
///  Models/Vehicles/Experiments/*.asset, Materials/Vehicles/Experiments/*.mat,
///  Prefabs/Vehicles/Experiments/Car_Sedan_SplitTest_Assembled/Exploded.prefab, SplitPoC_Info.json
///
/// 사용법: Tools ▸ CarFactory ▸ Vehicles ▸ Split PoC (Door+Hood)
/// </summary>
public static class CarPartSplitPoC
{
    private const string MENU_PATH = "Tools/CarFactory/Vehicles/Split PoC (Door+Hood)";
    private const string LOG_TAG = "[분할 PoC]";

    private const string SRC_MESH = "Assets/@Resource/Models/Vehicles/Car_Sedan.fbx";
    private const string SRC_MAT = "Assets/@Resource/Materials/Vehicles/Car_Sedan.mat";
    private const string SRC_PREFAB = "Assets/@Resource/Prefabs/Vehicles/Car_Sedan.prefab";
    private const string SRC_TEX = "Assets/@Resource/Textures/Vehicles/Car_Sedan_BaseColor.png";
    private const string MESH_DIR = "Assets/@Resource/Models/Vehicles/Experiments";
    private const string MAT_DIR = "Assets/@Resource/Materials/Vehicles/Experiments";
    private const string PREFAB_DIR = "Assets/@Resource/Prefabs/Vehicles/Experiments";

    private const float PANEL_THICKNESS = 0.02f;   // Thick 메쉬 두께
    private const float CAVITY_DEPTH = 0.04f;      // 차체 구멍 음각 깊이 (부품 두께보다 깊어야 조립 시 안 보임)
    private const float REFINE_RANGE = 0.03f;      // 텍스처 선 자동 보정 탐색 범위 ±
    private const float REFINE_STEP = 0.002f;
    private const float REFINE_SIDE = 0.012f;      // 국소 대비 비교 거리
    private const float REFINE_MIN_CONTRAST = -0.03f; // 이보다 대비가 약하면 보정하지 않음

    // ───────────── 데이터 구조 ─────────────
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

    private class PartDef
    {
        public string name; public List<CutPlane> planes = new List<CutPlane>();
        public bool hingeAtFront; // true=도어(앞쪽 끝 힌지), false=후드(뒤쪽 끝 힌지)
        // 추가 볼록 영역(합집합). 주 영역 평면으로 못 잡는 돌출물(사이드미러)을 통째로 부품에 넣을 때 쓴다. 텍스처 보정 안 함
        public List<List<CutPlane>> extraRegions = new List<List<CutPlane>>();
        // 두께(안쪽 껍데기)·음각 생성에 쓸 삼각형 필터(세 정점 모두 통과해야 포함) — 이미 닫힌 입체인 돌출물(미러)은 제외
        public Func<Vector3, bool> thickFilter = c => true;
        public IEnumerable<List<CutPlane>> AllRegions() { yield return planes; foreach (var r in extraRegions) yield return r; }
        public Builder shell = new Builder();
        public Vector3 pivot;
    }

    private class Builder
    {
        public readonly List<V> verts = new List<V>();
        public readonly List<int> tris = new List<int>();
        private readonly Dictionary<(Vector3, Vector2, Vector3), int> map = new Dictionary<(Vector3, Vector2, Vector3), int>();
        public int Add(V v)
        {
            var key = (v.p, v.uv, v.n);
            if (map.TryGetValue(key, out int i)) return i;
            i = verts.Count; verts.Add(v); map[key] = i; return i;
        }
        public void AddPoly(List<V> poly)
        {
            if (poly.Count < 3) return;
            int i0 = Add(poly[0]);
            for (int k = 1; k + 1 < poly.Count; k++)
            {
                if (Vector3.Cross(poly[k].p - poly[0].p, poly[k + 1].p - poly[0].p).sqrMagnitude < 1e-14f) continue;
                tris.Add(i0); tris.Add(Add(poly[k])); tris.Add(Add(poly[k + 1]));
            }
        }
    }

    // ───────────── 진입점 ─────────────
    [MenuItem(MENU_PATH)]
    public static void Run()
    {
        var src = AssetDatabase.LoadAssetAtPath<Mesh>(SRC_MESH);
        var mat = AssetDatabase.LoadAssetAtPath<Material>(SRC_MAT);
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(SRC_TEX);
        if (src == null || mat == null || tex == null) { Debug.LogError($"{LOG_TAG} 원본 에셋을 찾지 못함"); return; }
        EnsureFolder(MESH_DIR); EnsureFolder(MAT_DIR); EnsureFolder(PREFAB_DIR);

        var sv = src.vertices; var sn = src.normals; var st = src.tangents; var suv = src.uv; var stri = src.triangles;
        s_srcPos = sv; s_srcTri = stri;
        var verts = new V[sv.Length];
        for (int i = 0; i < sv.Length; i++)
            verts[i] = new V { p = sv[i], n = sn[i], t = st.Length > 0 ? st[i] : Vector4.zero, uv = suv[i] };

        var texReader = new TexReader(tex);
        var parts = DefineParts();
        var log = new StringBuilder();
        log.AppendLine($"{LOG_TAG} 원본 삼각형 {stri.Length / 3}, 정점 {sv.Length}");

        // 1) 텍스처 선 기반 평면 미세 보정
        foreach (var part in parts)
            for (int k = 0; k < part.planes.Count; k++)
                if (part.planes[k].refine) RefinePlane(part, k, verts, stri, texReader);

        // 2) 클리핑
        var body = new Builder();
        int splitTris = 0;
        for (int t = 0; t < stri.Length; t += 3)
        {
            var remaining = new List<List<V>> { new List<V> { verts[stri[t]], verts[stri[t + 1]], verts[stri[t + 2]] } };
            bool wasSplit = false;
            foreach (var part in parts)
            {
                foreach (var region in part.AllRegions())
                {
                    var next = new List<List<V>>();
                    foreach (var piece in remaining)
                    {
                        // 영역 안에 남는 조각이 있을 때만 분할을 확정 (영역 밖 삼각형을 불필요하게 쪼개지 않음)
                        var poly = piece; var outs = new List<List<V>>();
                        foreach (var pl in region)
                        {
                            Split(poly, pl, out var inside, out var outside);
                            if (outside.Count >= 3) outs.Add(outside);
                            poly = inside;
                            if (poly.Count < 3) break;
                        }
                        if (poly.Count >= 3)
                        {
                            part.shell.AddPoly(poly); next.AddRange(outs);
                            if (outs.Count > 0) wasSplit = true;
                        }
                        else next.Add(piece);
                    }
                    remaining = next;
                }
            }
            foreach (var piece in remaining) body.AddPoly(piece);
            if (wasSplit) splitTris++;
        }
        log.AppendLine($"평면에 걸려 잘린 원본 삼각형 {splitTris}개");

        // 3) 메쉬 생성/저장
        var cavityBuilders = new List<(string, Builder)>();
        var info = new StringBuilder("{\n  \"source\": \"" + SRC_MESH + "\",\n  \"parts\": [\n");
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var part = parts[pi];
            var b = BoundsOf(part.shell.verts);
            part.pivot = ComputePivot(part, b);
            log.AppendLine($"[{part.name}] 삼각형 {part.shell.tris.Count / 3}, 정점 {part.shell.verts.Count}, bounds min{b.min:F3} max{b.max:F3}, assembledPos {part.pivot:F3}");
            foreach (var pl in part.planes)
                log.AppendLine($"   평면 {pl.name}: n{pl.n:F3} d={pl.d:F4} (보정 {pl.delta * 1000f:+0.0;-0.0;0}mm, 대비 {pl.contrast:F3})");

            SaveMesh(BuildMesh(part.shell, -part.pivot, $"Car_Sedan_{part.name}_Shell"), $"{MESH_DIR}/Car_Sedan_{part.name}_Shell.asset");
            SaveMesh(BuildMesh(MakeThick(part.shell, Filter(part.shell, part.thickFilter), PANEL_THICKNESS, false, part.AllRegions().SelectMany(r => r).ToList()), -part.pivot, $"Car_Sedan_{part.name}_Thick"), $"{MESH_DIR}/Car_Sedan_{part.name}_Thick.asset");
            cavityBuilders.Add((part.name, MakeThick(null, Filter(part.shell, part.thickFilter), CAVITY_DEPTH, true, part.AllRegions().SelectMany(r => r).ToList())));

            info.Append($"    {{ \"name\": \"{part.name}\", \"assembledPos\": [{part.pivot.x:F4}, {part.pivot.y:F4}, {part.pivot.z:F4}], \"hinge\": \"{(part.hingeAtFront ? "front edge" : "rear edge")}\", \"tris\": {part.shell.tris.Count / 3}, \"planes\": [");
            info.Append(string.Join(", ", part.planes.Select(pl => $"{{\"name\":\"{pl.name}\",\"n\":[{pl.n.x:F4},{pl.n.y:F4},{pl.n.z:F4}],\"d\":{pl.d:F4},\"refineMm\":" + (pl.delta * 1000f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}")));
            info.Append(pi < parts.Count - 1 ? "] },\n" : "] }\n");
        }
        info.Append("  ],\n  \"bodyTris\": " + body.tris.Count / 3 + "\n}\n");
        log.AppendLine($"[Body] 삼각형 {body.tris.Count / 3}, 정점 {body.verts.Count}");

        var bodyMesh = SaveMesh(BuildMesh(body, Vector3.zero, "Car_Sedan_Body_NoDoorFL_NoHood"), $"{MESH_DIR}/Car_Sedan_Body_NoDoorFL_NoHood.asset");
        var cavityMeshes = new Dictionary<string, Mesh>();
        foreach (var (name, cb) in cavityBuilders)
            cavityMeshes[name] = SaveMesh(BuildMesh(cb, Vector3.zero, $"Car_Sedan_Cavity_{name}"), $"{MESH_DIR}/Car_Sedan_Cavity_{name}.asset");
        File.WriteAllText($"{MESH_DIR}/Car_Sedan_SplitPoC_Info.json", info.ToString());
        AssetDatabase.ImportAsset($"{MESH_DIR}/Car_Sedan_SplitPoC_Info.json");

        // 4) 실험 재질
        var matDouble = SaveMaterial($"{MAT_DIR}/Car_Sedan_DoubleSided.mat", () =>
        {
            var m = new Material(mat); m.SetFloat("_Cull", 0f); m.doubleSidedGI = true; return m;
        });
        var matCavity = SaveMaterial($"{MAT_DIR}/Car_Sedan_CavityDark.mat", () =>
        {
            var m = new Material(mat); m.SetTexture("_BaseMap", null); m.SetTexture("_MainTex", null);
            var c = new Color(0.10f, 0.11f, 0.13f); m.SetColor("_BaseColor", c); m.SetColor("_Color", c); return m;
        });

        // 5) 프리팹
        var door = parts[0]; var hood = parts[1];
        var meshes = parts.ToDictionary(p => p.name, p => AssetDatabase.LoadAssetAtPath<Mesh>($"{MESH_DIR}/Car_Sedan_{p.name}_Thick.asset"));
        BuildPrefab($"{PREFAB_DIR}/Car_Sedan_SplitTest_Assembled.prefab", "Car_Sedan_SplitTest_Assembled", bodyMesh, cavityMeshes, meshes, parts, mat, matCavity, false);
        BuildPrefab($"{PREFAB_DIR}/Car_Sedan_SplitTest_Exploded.prefab", "Car_Sedan_SplitTest_Exploded", bodyMesh, cavityMeshes, meshes, parts, mat, matCavity, true);

        AssetDatabase.SaveAssets();
        Debug.Log(log.ToString());
    }

    // ───────────── 부품 정의 (차량 로컬, m) ─────────────
    // 초기값 근거: 직교 측면 렌더(500px/m)와 상면 렌더(1000px/m)에서 읽은 텍스처 윤곽선 좌표.
    private static List<PartDef> DefineParts()
    {
        // 미러와 미러 받침(미러 박스 안 x<-0.895)은 두께·음각에서 뺀다(구멍은 부채꼴로 메움) — 이미 닫힌 입체라 오프셋하면 반대편으로 삐져나옴
        var door = new PartDef { name = "Door_FL", hingeAtFront = true, thickFilter = c => !(c.x < -0.895f && c.y > 1.10f && c.y < 1.50f && c.z > 0.50f && c.z < 0.85f) };
        door.planes.Add(new CutPlane("outerSide x<-0.60", Vector3.right, new Vector3(-0.60f, 0, 0), false));
        door.planes.Add(new CutPlane("front z<0.836", Vector3.forward, new Vector3(0, 0, 0.836f), true));
        door.planes.Add(new CutPlane("rear z>-0.210", Vector3.back, new Vector3(0, 0, -0.210f), true));
        door.planes.Add(new CutPlane("bottom y>0.345", Vector3.down, new Vector3(0, 0.345f, 0), true));
        door.planes.Add(new CutPlane("top y<1.885", Vector3.up, new Vector3(0, 1.885f, 0), true));
        // A필러 쪽 경사: (z0.836,y1.21) ~ (z0.46,y1.89). 바깥(앞·위)을 향하는 법선
        var aDir = new Vector3(0, 1.89f - 1.21f, 0.46f - 0.836f).normalized;
        var aN = new Vector3(0, -aDir.z, aDir.y); // (0, 0.376, 0.68) 정규화 → 앞+위
        door.planes.Add(new CutPlane("A-pillar slant", aN, new Vector3(0, 1.21f, 0.836f), true));
        // 사이드미러 박스(정점 실측: x -1.037~-0.933, y 1.18~1.39, z 0.575~0.694) — 경사 평면에 미러 윗모서리가 잘리지 않게 통째로 도어에 포함
        door.extraRegions.Add(new List<CutPlane>
        {
            new CutPlane("mirror x<-0.920", Vector3.right, new Vector3(-0.920f, 0, 0), false),
            new CutPlane("mirror y>1.05", Vector3.down, new Vector3(0, 1.05f, 0), false),
            new CutPlane("mirror y<1.50", Vector3.up, new Vector3(0, 1.50f, 0), false),
            new CutPlane("mirror z>0.50", Vector3.back, new Vector3(0, 0, 0.50f), false),
            new CutPlane("mirror z<0.85", Vector3.forward, new Vector3(0, 0, 0.85f), false),
        });

        var hood = new PartDef { name = "Hood", hingeAtFront = false };
        hood.planes.Add(new CutPlane("rear z>1.135", Vector3.back, new Vector3(0, 0, 1.135f), true));
        hood.planes.Add(new CutPlane("lip y>0.980", Vector3.down, new Vector3(0, 0.98f, 0), false));
        // 좌우: |x| < 0.585 (z1.12) → 0.525 (z1.74) 로 좁아짐
        float slope = (0.585f - 0.525f) / (1.74f - 1.12f);
        hood.planes.Add(new CutPlane("left edge", new Vector3(-1, 0, slope), new Vector3(-0.585f, 0, 1.12f), true));
        hood.planes.Add(new CutPlane("right edge", new Vector3(1, 0, slope), new Vector3(0.585f, 0, 1.12f), true));
        return new List<PartDef> { door, hood };
    }

    private static Vector3 ComputePivot(PartDef part, Bounds b)
    {
        var vs = part.shell.verts;
        if (part.hingeAtFront)
        {
            // 도어: 앞쪽 끝(힌지 라인) 5cm 띠의 평균 x, 높이 중앙, z 최대
            var band = vs.Where(v => v.p.z > b.max.z - 0.05f).ToList();
            float x = band.Count > 0 ? band.Average(v => v.p.x) : b.center.x;
            return new Vector3(x, b.center.y, b.max.z);
        }
        // 후드: 뒤쪽 끝(힌지) 5cm 띠의 평균 y, x 중앙, z 최소
        var rear = vs.Where(v => v.p.z < b.min.z + 0.05f).ToList();
        float y = rear.Count > 0 ? rear.Average(v => v.p.y) : b.center.y;
        return new Vector3(b.center.x, y, b.min.z);
    }

    // ───────────── 텍스처 선 보정 ─────────────
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
            px = copy.GetPixels(); UnityEngine.Object.DestroyImmediate(copy);
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

    /// <summary>
    /// 평면을 법선 방향으로 δ만큼 옮겨가며 절단선(평면∩다른 평면 안쪽 삼각형)을 따라 텍스처 휘도를 샘플,
    /// "선 중앙 휘도 − 양옆(±1.2cm) 평균" 대비가 가장 낮은(가장 선다운) δ를 채택한다.
    /// </summary>
    private static void RefinePlane(PartDef part, int k, V[] verts, int[] tri, TexReader tex)
    {
        var pl = part.planes[k];
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
            if (mx < -REFINE_RANGE - REFINE_SIDE || mn > REFINE_RANGE + REFINE_SIDE) continue;
            cand.Add(t);
        }
        Func<float, float> lineLum = delta =>
        {
            float sum = 0, wsum = 0;
            foreach (int t in cand)
            {
                var a = verts[tri[t]]; var b = verts[tri[t + 1]]; var c = verts[tri[t + 2]];
                var pts = new List<V>(2);
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
        };
        float best = 0f, bestScore = float.MaxValue;
        for (float delta = -REFINE_RANGE; delta <= REFINE_RANGE + 1e-6f; delta += REFINE_STEP)
        {
            float score = lineLum(delta) - 0.5f * (lineLum(delta - REFINE_SIDE) + lineLum(delta + REFINE_SIDE));
            if (score < bestScore) { bestScore = score; best = delta; }
        }
        pl.contrast = bestScore;
        if (bestScore <= REFINE_MIN_CONTRAST) { pl.delta = best; pl.d += best; }
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

    /// <summary>
    /// 두께 부여. cavity=false: 바깥 껍데기 + 안쪽 껍데기(뒤집음) + 테두리 띠.
    /// cavity=true: 안쪽 껍데기와 테두리만, 바깥에서 보이게 뒤집어 차체 구멍의 음각(속 채움)으로 쓴다.
    /// 오프셋 방향은 같은 위치 정점들의 평균 법선(하드 엣지에서 갈라지지 않게).
    /// </summary>
    private static Builder MakeThick(Builder outerShell, Builder src, float depth, bool cavity, List<CutPlane> cutPlanes)
    {
        var vs = src.verts; var ts = src.tris;
        var weldId = new int[vs.Count]; var keyToId = new Dictionary<Vector3Int, int>(); var nSum = new List<Vector3>();
        for (int i = 0; i < vs.Count; i++)
        {
            var key = Vector3Int.RoundToInt(vs[i].p * 20000f); // 0.05mm
            if (!keyToId.TryGetValue(key, out int id)) { id = nSum.Count; keyToId[key] = id; nSum.Add(Vector3.zero); }
            weldId[i] = id; nSum[id] += vs[i].n;
        }
        // 얇게 접힌 곳(창틀·A필러 쪽 접힘)에서 오프셋이 반대편 겉면을 뚫지 않도록,
        // 원본 메쉬로 법선 반대 방향 레이를 쏴 맞은 거리의 45%로 깊이를 제한한다.
        var depthOf = new float[nSum.Count];
        for (int id = 0; id < depthOf.Length; id++) depthOf[id] = -1f;
        float DepthAt(int i)
        {
            int id = weldId[i];
            if (depthOf[id] < 0f)
            {
                float hit = RayHitDistance(vs[i].p, -nSum[id].normalized, depth / 0.45f);
                depthOf[id] = Mathf.Min(depth, hit * 0.45f);
            }
            return depthOf[id];
        }
        Vector3 Inner(int i) => vs[i].p - nSum[weldId[i]].normalized * DepthAt(i);

        var outB = new Builder();
        if (outerShell != null)
            for (int t = 0; t < outerShell.tris.Count; t += 3)
                outB.AddPoly(new List<V> { outerShell.verts[outerShell.tris[t]], outerShell.verts[outerShell.tris[t + 1]], outerShell.verts[outerShell.tris[t + 2]] });

        // 안쪽 껍데기: 패널=감김 반대+법선 반대 / 음각=원래 감김(바깥에서 보임)+원래 법선
        for (int t = 0; t < ts.Count; t += 3)
        {
            var tri = new List<V>();
            for (int j = 0; j < 3; j++)
            {
                int i = ts[t + j]; var v = vs[i]; v.p = Inner(i);
                if (!cavity) { v.n = -v.n; v.t.w = -v.t.w; }
                tri.Add(v);
            }
            if (!cavity) tri.Reverse();
            outB.AddPoly(tri);
        }

        // 경계 변: 용접 기준 한 삼각형에만 쓰인 변
        var edgeCount = new Dictionary<(int, int), int>();
        for (int t = 0; t < ts.Count; t += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = weldId[ts[t + j]], b = weldId[ts[t + (j + 1) % 3]];
                if (a == b) continue;
                var key = a < b ? (a, b) : (b, a);
                edgeCount.TryGetValue(key, out int c); edgeCount[key] = c + 1;
            }
        // 절단 평면 위의 경계 변에만 테두리 띠를 세운다. 그 밖의 경계(두께 필터로 뺀 미러 밑동 구멍 등)는
        // 띠를 세우면 평균 법선이 틀어져 겉면 밖으로 삐져나오므로, 안쪽 껍데기 높이에서 부채꼴로 메운다.
        bool OnSamePlane(Vector3 p, Vector3 q) => cutPlanes.Any(pl => Mathf.Abs(pl.Dist(p)) < 1e-4f && Mathf.Abs(pl.Dist(q)) < 1e-4f);
        var holeNext = new Dictionary<int, (int ia, int ib)>();
        for (int t = 0; t < ts.Count; t += 3)
            for (int j = 0; j < 3; j++)
            {
                int ia = ts[t + j], ib = ts[t + (j + 1) % 3];
                int a = weldId[ia], b = weldId[ib];
                if (a == b || edgeCount[a < b ? (a, b) : (b, a)] != 1) continue;
                if (!OnSamePlane(vs[ia].p, vs[ib].p)) { holeNext[a] = (ia, ib); continue; }
                // 패널 테두리: b → a → a' → b' (바깥 향함). 음각: 반대(구멍 안쪽을 향함)
                var pa = vs[ia].p; var pb = vs[ib].p; var qa = Inner(ia); var qb = Inner(ib);
                if (cavity)
                {
                    // 음각 테두리가 겉면 절단선과 정확히 겹치면 깊이 경합으로 조립 상태에서 1px 선이 비친다 → 2mm 안쪽에서 시작
                    pa -= nSum[weldId[ia]].normalized * 0.002f; pb -= nSum[weldId[ib]].normalized * 0.002f;
                }
                var quad = cavity ? new List<Vector3> { pa, pb, qb, qa } : new List<Vector3> { pb, pa, qa, qb };
                var fn = Vector3.Cross(quad[1] - quad[0], quad[2] - quad[0]).normalized;
                var tan = (Vector4)(quad[1] - quad[0]).normalized; tan.w = 1f;
                var uvA = vs[ia].uv; var uvB = vs[ib].uv;
                var uvs = cavity ? new[] { uvA, uvB, uvB, uvA } : new[] { uvB, uvA, uvA, uvB };
                var poly = new List<V>();
                for (int q = 0; q < 4; q++) poly.Add(new V { p = quad[q], n = fn, t = tan, uv = uvs[q] });
                outB.AddPoly(poly);
            }

        // 비절단 경계 루프 메우기 (부채꼴, 중심 = 안쪽 정점 평균)
        var used = new HashSet<int>();
        foreach (var start in holeNext.Keys.ToList())
        {
            if (used.Contains(start)) continue;
            var loop = new List<(int ia, int ib)>(); int cur = start;
            while (holeNext.TryGetValue(cur, out var e) && used.Add(cur)) { loop.Add(e); cur = weldId[e.ib]; }
            if (loop.Count < 3) continue;
            var center = Vector3.zero; var nAvg = Vector3.zero;
            foreach (var e in loop) { center += Inner(e.ia); nAvg += nSum[weldId[e.ia]]; }
            center /= loop.Count; nAvg = nAvg.normalized * (cavity ? 1f : -1f);
            var cv = new V { p = center, n = nAvg, t = new Vector4(1, 0, 0, 1), uv = vs[loop[0].ia].uv };
            foreach (var e in loop)
            {
                var va = vs[e.ia]; va.p = Inner(e.ia); va.n = nAvg;
                var vb = vs[e.ib]; vb.p = Inner(e.ib); vb.n = nAvg;
                // 패널 안쪽 껍데기(뒤집힘)는 a'→b' 변을 가진 면이 이웃, 음각은 b'→a'
                outB.AddPoly(cavity ? new List<V> { vb, va, cv } : new List<V> { va, vb, cv });
            }
        }
        return outB;
    }

    private static Vector3[] s_srcPos; private static int[] s_srcTri;

    /// <summary>원본 메쉬 삼각형과의 최근접 교차 거리(Möller–Trumbore, 시작점 1mm 이내 무시). 없으면 maxDist.</summary>
    private static float RayHitDistance(Vector3 o, Vector3 dir, float maxDist)
    {
        float best = maxDist;
        for (int t = 0; t < s_srcTri.Length; t += 3)
        {
            Vector3 a = s_srcPos[s_srcTri[t]], b = s_srcPos[s_srcTri[t + 1]], c = s_srcPos[s_srcTri[t + 2]];
            Vector3 e1 = b - a, e2 = c - a, pv = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, pv);
            if (Mathf.Abs(det) < 1e-12f) continue;
            float inv = 1f / det; Vector3 tv = o - a;
            float u = Vector3.Dot(tv, pv) * inv; if (u < 0f || u > 1f) continue;
            Vector3 qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(dir, qv) * inv; if (v < 0f || u + v > 1f) continue;
            float dist = Vector3.Dot(e2, qv) * inv;
            if (dist > 0.001f && dist < best) best = dist;
        }
        return best;
    }

    private static Builder Filter(Builder src, Func<Vector3, bool> keep)
    {
        var b = new Builder();
        for (int t = 0; t < src.tris.Count; t += 3)
        {
            var a = src.verts[src.tris[t]]; var c1 = src.verts[src.tris[t + 1]]; var c2 = src.verts[src.tris[t + 2]];
            if (keep(a.p) && keep(c1.p) && keep(c2.p)) b.AddPoly(new List<V> { a, c1, c2 });
        }
        return b;
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
        if (b.verts.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.SetVertices(b.verts.Select(v => v.p + offset).ToList());
        m.SetNormals(b.verts.Select(v => v.n).ToList());
        m.SetTangents(b.verts.Select(v => v.t).ToList());
        m.SetUVs(0, b.verts.Select(v => v.uv).ToList());
        m.SetTriangles(b.tris, 0);
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
            UnityEngine.Object.DestroyImmediate(mesh);
            EditorUtility.SetDirty(existing);
            return existing;
        }
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    private static Material SaveMaterial(string path, Func<Material> make)
    {
        var fresh = make();
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null)
        {
            existing.CopyPropertiesFromMaterial(fresh);
            existing.doubleSidedGI = fresh.doubleSidedGI;
            UnityEngine.Object.DestroyImmediate(fresh);
            EditorUtility.SetDirty(existing);
            return existing;
        }
        AssetDatabase.CreateAsset(fresh, path);
        return fresh;
    }

    private static void BuildPrefab(string path, string rootName, Mesh body, Dictionary<string, Mesh> cavities,
        Dictionary<string, Mesh> partMeshes, List<PartDef> parts, Material mat, Material cavityMat, bool exploded)
    {
        var root = new GameObject(rootName);
        try
        {
            // 원본 프리팹(Car_Sedan.prefab)의 Model 자식 오프셋(피벗 보정, 약 1mm)을 그대로 따라 완성 모양을 원본과 일치시킨다
            var model = new GameObject("Model").transform;
            model.SetParent(root.transform, false);
            var srcPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SRC_PREFAB);
            var srcModel = srcPrefab != null ? srcPrefab.transform.Find("Model") : null;
            if (srcModel != null) { model.localPosition = srcModel.localPosition; model.localRotation = srcModel.localRotation; }
            var bodyGo = MakeRenderer("Body", model, body, mat);
            foreach (var kv in cavities)
                MakeRenderer($"Cavity_{kv.Key}", bodyGo.transform, kv.Value, cavityMat);
            foreach (var part in parts)
            {
                var go = MakeRenderer(part.name, model, partMeshes[part.name], mat);
                go.transform.localPosition = part.pivot; // = assembledPos
                if (!exploded) continue;
                if (part.hingeAtFront)
                {
                    // 도어: 차 왼쪽 1.6m 옆에 세우고 안쪽이 보이게 150° 돌림
                    go.transform.localPosition = part.pivot + new Vector3(-1.6f, 0f, -0.3f);
                    go.transform.localRotation = Quaternion.Euler(0f, 150f, 0f);
                }
                else
                {
                    // 후드: 차 왼쪽 앞에 힌지 쪽을 바닥에 대고 세워 둠(안쪽 확인용)
                    go.transform.localPosition = new Vector3(-2.0f, 0.15f, part.pivot.z + 1.2f);
                    go.transform.localRotation = Quaternion.Euler(-70f, -60f, 0f);
                }
            }
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    private static GameObject MakeRenderer(string name, Transform parent, Mesh mesh, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        return go;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
