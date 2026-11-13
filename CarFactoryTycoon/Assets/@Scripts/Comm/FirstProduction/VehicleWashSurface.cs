using System.Collections.Generic;
using UnityEngine;

/// <summary>Five outward mesh projections; the underside and hidden cavity walls are not washing targets.
/// Owns only runtime materials, query colliders and one mask. Call Initialize after the car's initial
/// parts have been activated. Reuses those resources across pooled lifetimes.</summary>
public sealed class VehicleWashSurface : MonoBehaviour
{
    const int Resolution = 48;
    const int Faces = 5;
    const float CleanPerSecond = 3f;
    sealed class Surface
    {
        public Renderer renderer;
        public MeshCollider query;
        public Mesh baked;
        public Material[] original, owned;
    }
    struct Cell
    {
        public Vector3 point, normal, approach;
        public bool jump;
        public int pixel;
        public float clean;
    }
    struct ExteriorApproach { public Vector3 eye, feet; public bool jump; }
    readonly List<ExteriorApproach> approaches = new List<ExteriorApproach>();
    readonly List<Surface> surfaces = new List<Surface>();
    readonly List<Cell> cells = new List<Cell>();
    readonly List<Vector3> aimPoints = new List<Vector3>();
    readonly List<Vector3> aimNormals = new List<Vector3>();
    Texture2D mask;
    Color32[] pixels;
    Transform queryRoot;
    CarController car;
    Bounds bounds;
    int lifeVersion = -1;
    double cleanTotal;
    int cleanCells;
    bool initialized;
    static readonly int MaskId = Shader.PropertyToID("_WashMask");
    static readonly int MatrixId = Shader.PropertyToID("_WashWorldToLocal");
    static readonly int MinId = Shader.PropertyToID("_WashMin");
    static readonly int SizeId = Shader.PropertyToID("_WashSize");
    public float Coverage => IsClean ? 1f : cells.Count == 0 ? 0f : Mathf.Clamp01((float)(cleanTotal / cells.Count));
    public bool IsClean => cells.Count > 0 && cleanCells == cells.Count;
    public int SampleCount => cells.Count;
    public IReadOnlyList<Vector3> AimPoints { get { UpdateAimPoints(); return aimPoints; } }
    public IReadOnlyList<Vector3> AimNormals { get { UpdateAimPoints(); return aimNormals; } }

    public void Initialize(CarController owner)
    {
        if (owner == null) return;
        if (initialized) { if (car == owner && lifeVersion != owner.LifeVersion) ResetWash(); return; }
        car = owner;
        var shader = Shader.Find("CarFactory/VehicleWash");
        if (shader == null) { Debug.LogError("VehicleWash shader is missing.", this); return; }
        queryRoot = new GameObject("WashMeshQueries_" + GetInstanceID()) { hideFlags = HideFlags.HideAndDontSave }.transform;
        // Native query shapes stay at world identity. Each ray is transformed to the source mesh space.
        bool hasBounds = false;
        foreach (var renderer in owner.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled || renderer.GetComponentInParent<CarDolly>() != null) continue;
            Mesh mesh = null, baked = null;
            if (renderer.TryGetComponent<MeshFilter>(out var filter)) mesh = filter.sharedMesh;
            else if (renderer is SkinnedMeshRenderer skin) { baked = new Mesh(); skin.BakeMesh(baked); mesh = baked; }
            if (mesh == null) continue;
            var node = new GameObject("MeshQuery") { hideFlags = HideFlags.HideAndDontSave, layer = 2 };
            node.transform.SetParent(queryRoot, false);
            var query = node.AddComponent<MeshCollider>();
            query.enabled = false;
            query.sharedMesh = mesh;
            var original = renderer.sharedMaterials;
            var owned = new Material[original.Length];
            for (int i = 0; i < owned.Length; i++)
            {
                if (original[i] == null) continue;
                owned[i] = new Material(original[i]) { shader = shader, hideFlags = HideFlags.DontSave };
                // Standard and URP property naming are both supported.
                if (!original[i].HasProperty("_BaseMap") && original[i].HasProperty("_MainTex"))
                {
                    owned[i].SetTexture("_BaseMap", original[i].GetTexture("_MainTex"));
                    owned[i].SetTextureScale("_BaseMap", original[i].GetTextureScale("_MainTex"));
                    owned[i].SetTextureOffset("_BaseMap", original[i].GetTextureOffset("_MainTex"));
                }
                if (!original[i].HasProperty("_BaseColor") && original[i].HasProperty("_Color"))
                    owned[i].SetColor("_BaseColor", original[i].GetColor("_Color"));
            }
            renderer.sharedMaterials = owned;
            surfaces.Add(new Surface { renderer = renderer, query = query, baked = baked, original = original, owned = owned });
            var b = mesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                var p = b.center + Vector3.Scale(b.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                p = transform.InverseTransformPoint(renderer.transform.TransformPoint(p));
                if (!hasBounds) { bounds = new Bounds(p, Vector3.zero); hasBounds = true; } else bounds.Encapsulate(p);
            }
        }
        pixels = new Color32[Resolution * Resolution * Faces];
        mask = new Texture2D(Resolution * Faces, Resolution, TextureFormat.RGBA32, false, true)
        { name = "VehicleWashMask", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave };
        initialized = true;
        PrepareExteriorApproaches();
        BuildCells();
        ResetWash();
    }

    static int Face(Vector3 normal)
    {
        var a = new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));
        if (a.y >= a.x && a.y >= a.z) return normal.y > 0 ? 4 : -1;
        if (a.x >= a.z) return normal.x > 0 ? 0 : 1;
        return normal.z > 0 ? 2 : 3;
    }
    static Vector3 FaceNormal(int face) => face == 0 ? Vector3.right : face == 1 ? Vector3.left : face == 2 ? Vector3.forward : face == 3 ? Vector3.back : Vector3.up;
    Vector3 Project(int face, float u, float v)
    {
        var min = bounds.min; var size = bounds.size;
        if (face < 2) return new Vector3(face == 0 ? bounds.max.x : min.x, min.y + v * size.y, min.z + u * size.z);
        if (face < 4) return new Vector3(min.x + u * size.x, min.y + v * size.y, face == 2 ? bounds.max.z : min.z);
        return new Vector3(min.x + u * size.x, bounds.max.y, min.z + v * size.z);
    }
    void BuildCells()
    {
        SetQueries(true);
        try
        {
            for (int face = 0; face < Faces; face++)
            for (int y = 0; y < Resolution; y++)
            for (int x = 0; x < Resolution; x++)
            {
                var normal = transform.TransformDirection(FaceNormal(face)).normalized;
                var start = transform.TransformPoint(Project(face, (x + .5f) / Resolution, (y + .5f) / Resolution)) + normal * .15f;
                if (!Contact(new Ray(start, -normal), bounds.size.magnitude * transform.lossyScale.magnitude + 1f, out var point, out var hitNormal)) continue;
                var localNormal = transform.InverseTransformDirection(hitNormal).normalized;
                if (Face(localNormal) != face) continue;
                // Openings can expose internal sheet metal to a horizontal projection.
                // Require a facing, unobstructed jet from an exterior standing/jumping position.
                var localPoint = transform.InverseTransformPoint(point);
                if (!FindExteriorApproach(localPoint, localNormal, out var approach, out var jump)) continue;
                cells.Add(new Cell { point = localPoint, normal = localNormal, approach = approach, jump = jump, pixel = y * Resolution * Faces + face * Resolution + x });
                aimPoints.Add(point); aimNormals.Add(hitNormal);
            }
        }
        finally { SetQueries(false); }
    }
    void PrepareExteriorApproaches()
    {
        approaches.Clear();
        Physics.SyncTransforms();
        for (int i = 0; i < 64; i++)
        {
            float angle = i * Mathf.PI / 32f;
            var probe = transform.TransformPoint(new Vector3(
                bounds.center.x + Mathf.Cos(angle) * (bounds.extents.x + 1.4f), bounds.max.y + 5f,
                bounds.center.z + Mathf.Sin(angle) * (bounds.extents.z + 1.4f)));
            // The factory floor and conveyor deck have different heights. Sample actual support.
            if (!Physics.Raycast(probe, Vector3.down, out var floor, 15f, ~((1 << 2) | (1 << 10)), QueryTriggerInteraction.Ignore)
                || floor.normal.y < .7f || floor.transform.IsChildOf(transform)) continue;
            var feet = floor.point + Vector3.up * .03f; // CharacterController skin clearance.
            for (int height = 0; height < 2; height++)
                approaches.Add(new ExteriorApproach { feet = transform.InverseTransformPoint(feet),
                    eye = transform.InverseTransformPoint(feet + Vector3.up * (height == 0 ? 1.62f : 2.58f)), jump = height != 0 });
        }
    }
    bool VisibleJet(Vector3 origin, Vector3 point, Vector3 normal)
    {
        var delta = point - origin; float distance = delta.magnitude;
        return distance <= 4.4f && distance > .01f && Vector3.Dot(normal, -delta / distance) > .08f &&
            Contact(new Ray(origin, delta / distance), distance + .025f, out var visible, out _) &&
            (visible - point).sqrMagnitude <= .04f * .04f;
    }
    bool FindExteriorApproach(Vector3 point, Vector3 normal, out Vector3 approach, out bool jump)
    {
        approach = default; jump = false; float best = float.MaxValue;
        var worldPoint = transform.TransformPoint(point);
        var worldNormal = transform.TransformDirection(normal).normalized;
        for (int i = 0; i < approaches.Count; i++)
        {
            var candidate = approaches[i];
            var origin = transform.TransformPoint(candidate.eye);
            float score = Vector3.Distance(origin, worldPoint) + (candidate.jump ? .25f : 0f);
            if (score >= best || !VisibleJet(origin, worldPoint, worldNormal)) continue;
            // An exterior patch must tolerate ordinary hand/camera movement. Tiny pinhole views
            // into internal sheet metal do not turn those internal walls into washing objectives.
            if (!VisibleJet(origin + Vector3.right * .12f, worldPoint, worldNormal)
                || !VisibleJet(origin - Vector3.right * .12f, worldPoint, worldNormal)
                || !VisibleJet(origin + Vector3.forward * .12f, worldPoint, worldNormal)
                || !VisibleJet(origin - Vector3.forward * .12f, worldPoint, worldNormal)
                || !VisibleJet(origin + Vector3.up * .25f, worldPoint, worldNormal)
                || !VisibleJet(origin - Vector3.up * .25f, worldPoint, worldNormal)) continue;
            best = score; approach = candidate.feet; jump = candidate.jump;
        }
        return best < float.MaxValue;
    }
    void SetQueries(bool enabled)
    {
        for (int i = 0; i < surfaces.Count; i++)
            surfaces[i].query.enabled = enabled && surfaces[i].renderer != null && surfaces[i].renderer.enabled && surfaces[i].renderer.gameObject.activeInHierarchy;
    }
    bool Contact(Ray ray, float distance, out Vector3 point, out Vector3 normal)
    {
        point = normal = default;
        float closest = distance;
        bool found = false;
        for (int i = 0; i < surfaces.Count; i++)
        {
            var s = surfaces[i];
            if (!s.query.enabled || !s.renderer.bounds.IntersectRay(ray, out var entry) || entry > closest) continue;
            var t = s.renderer.transform;
            var a = t.InverseTransformPoint(ray.origin);
            var delta = t.InverseTransformPoint(ray.GetPoint(closest)) - a;
            float length = delta.magnitude;
            if (length < .00001f || !s.query.Raycast(new Ray(a, delta / length), out var hit, length)) continue;
            var worldPoint = t.TransformPoint(hit.point);
            float d = Vector3.Dot(worldPoint - ray.origin, ray.direction);
            if (d < 0 || d > closest) continue;
            closest = d; point = worldPoint;
            normal = t.localToWorldMatrix.inverse.transpose.MultiplyVector(hit.normal).normalized;
            found = true;
        }
        return found;
    }

    public bool Spray(Ray ray, float seconds, float radius = .85f, float maxDistance = 4f)
    {
        if (!initialized) Initialize(GetComponent<CarController>());
        if (!initialized || seconds <= 0f || radius <= 0f || maxDistance <= 0f || ray.direction.sqrMagnitude < .00001f) return false;
        if (lifeVersion != car.LifeVersion) ResetWash();
        ray.direction = ray.direction.normalized;
        bool changed = false;
        SetQueries(true);
        try
        {
            if (!Contact(ray, maxDistance, out var hit, out _)) return false;
            // Existing environment/other-car colliders can block the jet before this car's visible mesh.
            if (Physics.Raycast(ray, out var blocker, Vector3.Distance(ray.origin, hit) - .025f, ~((1 << 2) | (1 << 10)), QueryTriggerInteraction.Ignore)
                && !blocker.transform.IsChildOf(car.transform)) return false;
            float r2 = radius * radius;
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                if (cell.clean >= 1f) continue;
                var point = transform.TransformPoint(cell.point);
                if ((point - hit).sqrMagnitude > r2) continue;
                var delta = point - ray.origin;
                float distance = delta.magnitude;
                if (distance > maxDistance || distance < .001f) continue;
                var normal = transform.TransformDirection(cell.normal).normalized;
                if (Vector3.Dot(normal, -delta / distance) <= .08f) continue;
                // Test the complete line from the nozzle, so neither opposite panels nor hidden
                // cavity walls get washed through the car. A small endpoint tolerance is geometric.
                if (!Contact(new Ray(ray.origin, delta / distance), distance + .025f, out var visiblePoint, out _)
                    || (visiblePoint - point).sqrMagnitude > .045f * .045f) continue;
                float next = Mathf.Min(1f, cell.clean + seconds * CleanPerSecond);
                cleanTotal += next - cell.clean;
                if (next >= 1f) cleanCells++;
                cell.clean = next; cells[i] = cell;
                byte value = (byte)Mathf.RoundToInt(next * 255f);
                pixels[cell.pixel] = new Color32(value, value, value, 255);
                changed = true;
            }
        }
        finally { SetQueries(false); }
        if (changed) UploadMask();
        return true;
    }
    public void CompleteForAdmin()
    {
        if (!initialized || IsClean) return;
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            cell.clean = 1f;
            cells[i] = cell;
            pixels[cell.pixel] = new Color32(255, 255, 255, 255);
        }
        cleanCells = cells.Count;
        cleanTotal = cleanCells;
        UploadMask();
    }

    public void ResetWash()
    {
        if (!initialized) return;
        lifeVersion = car != null ? car.LifeVersion : -1;
        cleanTotal = 0; cleanCells = 0;
        // Empty atlas cells represent air/openings, not unreachable dirt.
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i]; cell.clean = 0; cells[i] = cell;
            pixels[cell.pixel] = new Color32(0, 0, 0, 255);
        }
        UploadMask(); UpdateMaterials();
    }
    void UploadMask() { mask.SetPixels32(pixels); mask.Apply(false, false); }
    void UpdateAimPoints()
    {
        for (int i = 0; i < cells.Count; i++)
        {
            aimPoints[i] = transform.TransformPoint(cells[i].point);
            aimNormals[i] = transform.TransformDirection(cells[i].normal).normalized;
        }
    }
    void UpdateMaterials()
    {
        var matrix = transform.worldToLocalMatrix;
        foreach (var s in surfaces)
        foreach (var material in s.owned)
        {
            if (material == null) continue;
            material.SetTexture(MaskId, mask); material.SetMatrix(MatrixId, matrix);
            material.SetVector(MinId, bounds.min); material.SetVector(SizeId, bounds.size);
        }
    }
    void LateUpdate()
    {
        if (!initialized) return;
        if (car != null && lifeVersion != car.LifeVersion) ResetWash();
        UpdateMaterials();
    }
    void OnEnable() { if (initialized) ResetWash(); }
    void OnDestroy()
    {
        foreach (var s in surfaces)
        {
            if (s.renderer != null) s.renderer.sharedMaterials = s.original;
            foreach (var material in s.owned) if (material != null) Destroy(material);
            if (s.baked != null) Destroy(s.baked);
        }
        if (mask != null) Destroy(mask);
        if (queryRoot != null) Destroy(queryRoot.gameObject);
    }
}



