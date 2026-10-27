using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Counts repairable missed parts after their process work zone and shows a red, non-interactive
/// mesh proxy at each missing part's authored attachment pose. Source AssemblyPart state is untouched.
/// </summary>
public sealed class PrototypeDefectDisplay : MonoBehaviour
{
    private const float WorkZoneMeters = 6f;
    private const float ProxyScale = 1.01f;

    public ProductionProgression progression;

    public int MissingPartCount { get; private set; }
    public int AffectedCarCount { get; private set; }

    private LineTrafficManager traffic;
    private Material proxyMaterial;
    private readonly Dictionary<CarController, AssemblyPart[]> partsByCar = new Dictionary<CarController, AssemblyPart[]>();
    private readonly Dictionary<AssemblyPart, PartProxy> proxiesByPart = new Dictionary<AssemblyPart, PartProxy>();
    private readonly HashSet<CarController> liveCars = new HashSet<CarController>();
    private readonly HashSet<AssemblyPart> wantedParts = new HashSet<AssemblyPart>();
    private readonly HashSet<PartType> requiredTypes = new HashSet<PartType>();
    private readonly List<CarController> staleCars = new List<CarController>();
    private readonly List<AssemblyPart> staleParts = new List<AssemblyPart>();

    private sealed class PartProxy
    {
        public readonly List<MeshProxy> meshes = new List<MeshProxy>();
    }

    private sealed class MeshProxy
    {
        public Transform sourceTransform;
        public Transform partTransform;
        public MeshFilter sourceFilter;
        public SkinnedMeshRenderer sourceSkin;
        public MeshFilter proxyFilter;
        public MeshRenderer proxyRenderer;
        public Mesh bakedMesh;
    }

    private void OnEnable()
    {
        RefreshDisplay();
    }

    private void Update()
    {
        RefreshDisplay();
    }

    /// <summary>Recomputes counters and proxy visibility immediately; safe to call from deterministic checks.</summary>
    public void RefreshDisplay()
    {
        if (progression == null || progression.mainSpline == null || progression.processes == null)
        {
            ClearProxies();
            MissingPartCount = 0;
            AffectedCarCount = 0;
            return;
        }

        // Never use LineTrafficManager.Instance here: its MonoSingleton getter creates an object when
        // absent, including during teardown. Scene lookup only returns an already-existing manager.
        if (traffic == null) traffic = FindFirstObjectByType<LineTrafficManager>();
        if (traffic == null || !traffic.isActiveAndEnabled)
        {
            ClearProxies();
            MissingPartCount = 0;
            AffectedCarCount = 0;
            return;
        }

        liveCars.Clear();
        wantedParts.Clear();
        int missingCount = 0;
        int affectedCount = 0;
        IReadOnlyList<CarController> cars = traffic.ActiveCars;

        for (int i = 0; i < cars.Count; i++)
        {
            CarController car = cars[i];
            if (car == null || !car.gameObject.activeInHierarchy) continue;
            liveCars.Add(car);

            if (!car.CanRepair || car.targetSpline != progression.mainSpline) continue;
            AssemblyPart[] parts = GetParts(car);
            requiredTypes.Clear();
            float splineLength = car.SplineLength;
            if (splineLength <= 0f) continue;

            for (int p = 0; p < progression.processes.Length; p++)
            {
                ProductionProgression.ProcessEntry process = progression.processes[p];
                if (process == null || process.parts == null || process.parts.Length == 0) continue;

                // A process becomes a defect only after the car has passed its six-meter work window.
                float passedProgress = process.gateProgress + (progression.prototypeSettings != null ? progression.prototypeSettings.workZoneMeters : WorkZoneMeters) / splineLength;
                if (car.pathProgress <= passedProgress) continue;

                for (int t = 0; t < process.parts.Length; t++)
                {
                    PartType type = process.parts[t];
                    if (type != PartType.None && type != PartType.Frame_1) requiredTypes.Add(type);
                }
            }

            bool affected = false;
            for (int p = 0; p < parts.Length; p++)
            {
                AssemblyPart part = parts[p];
                if (part == null || !requiredTypes.Contains(part.myType) || part.IsAssembled) continue;
                missingCount++;
                affected = true;
                wantedParts.Add(part);
                EnsureProxy(part);
                UpdateProxy(part);
            }
            if (affected) affectedCount++;
        }

        MissingPartCount = missingCount;
        AffectedCarCount = affectedCount;

        PrunePartProxies();
        PruneCarCaches();
    }

    private AssemblyPart[] GetParts(CarController car)
    {
        if (partsByCar.TryGetValue(car, out AssemblyPart[] cached) && cached != null) return cached;
        AssemblyPart[] parts = car.GetComponentsInChildren<AssemblyPart>(true);
        partsByCar[car] = parts;
        return parts;
    }

    private void EnsureProxy(AssemblyPart part)
    {
        if (proxiesByPart.ContainsKey(part)) return;
        PartProxy proxy = new PartProxy();
        proxiesByPart.Add(part, proxy);

        Transform[] children = part.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            Transform source = children[i];
            MeshFilter filter = source.GetComponent<MeshFilter>();
            SkinnedMeshRenderer skin = source.GetComponent<SkinnedMeshRenderer>();
            if (filter == null && skin == null) continue;
            Mesh mesh = filter != null ? filter.sharedMesh : skin.sharedMesh;
            if (mesh == null) continue;

            var go = new GameObject("DefectProxy_" + part.myType);
            go.hideFlags = HideFlags.DontSave;
            var proxyFilter = go.AddComponent<MeshFilter>();
            var proxyRenderer = go.AddComponent<MeshRenderer>();
            proxyFilter.sharedMesh = mesh;
            proxyRenderer.sharedMaterial = GetProxyMaterial();
            proxyRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            proxyRenderer.receiveShadows = false;
            proxyRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            proxyRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            proxy.meshes.Add(new MeshProxy
            {
                sourceTransform = source,
                partTransform = part.transform,
                sourceFilter = filter,
                sourceSkin = skin,
                proxyFilter = proxyFilter,
                proxyRenderer = proxyRenderer
            });
        }
    }

    private Material GetProxyMaterial()
    {
        if (proxyMaterial != null) return proxyMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) return null;

        proxyMaterial = new Material(shader) { name = "PrototypeDefectProxyMaterial", hideFlags = HideFlags.DontSave };
        Color red = new Color(1f, .035f, .02f, 1f);
        if (proxyMaterial.HasProperty("_BaseColor")) proxyMaterial.SetColor("_BaseColor", red);
        if (proxyMaterial.HasProperty("_Color")) proxyMaterial.SetColor("_Color", red);
        return proxyMaterial;
    }

    private void UpdateProxy(AssemblyPart part)
    {
        if (!proxiesByPart.TryGetValue(part, out PartProxy proxy)) return;
        Vector3 attachmentPosition = part.AttachmentWorldPosition;
        Quaternion attachmentRotation = part.AttachmentWorldRotation;

        for (int i = 0; i < proxy.meshes.Count; i++)
        {
            MeshProxy meshProxy = proxy.meshes[i];
            if (meshProxy.sourceTransform == null || meshProxy.proxyFilter == null) continue;

            if (meshProxy.sourceSkin != null)
            {
                if (meshProxy.bakedMesh == null)
                {
                    meshProxy.bakedMesh = new Mesh { name = "DefectProxyBakedMesh", hideFlags = HideFlags.DontSave };
                    meshProxy.proxyFilter.sharedMesh = meshProxy.bakedMesh;
                }
                meshProxy.sourceSkin.BakeMesh(meshProxy.bakedMesh);
            }
            else if (meshProxy.sourceFilter != null && meshProxy.proxyFilter.sharedMesh != meshProxy.sourceFilter.sharedMesh)
            {
                meshProxy.proxyFilter.sharedMesh = meshProxy.sourceFilter.sharedMesh;
            }

            Matrix4x4 relative = meshProxy.partTransform.worldToLocalMatrix * meshProxy.sourceTransform.localToWorldMatrix;
            Vector3 localPosition = relative.MultiplyPoint3x4(Vector3.zero);
            Vector3 right = relative.GetColumn(0);
            Vector3 up = relative.GetColumn(1);
            Vector3 forward = relative.GetColumn(2);
            Vector3 localScale = new Vector3(right.magnitude, up.magnitude, forward.magnitude);
            if (Vector3.Dot(Vector3.Cross(right, up), forward) < 0f) localScale.x = -localScale.x;
            Quaternion localRotation = forward.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, up.normalized)
                : Quaternion.identity;

            Transform proxyTransform = meshProxy.proxyFilter.transform;
            Vector3 partWorldScale = part.transform.lossyScale;
            proxyTransform.SetPositionAndRotation(
                attachmentPosition + attachmentRotation * Vector3.Scale(partWorldScale, localPosition),
                attachmentRotation * localRotation);
            proxyTransform.localScale = Vector3.Scale(partWorldScale, localScale) * ProxyScale;
            meshProxy.proxyRenderer.enabled = proxyMaterial != null;
        }
    }

    private void PrunePartProxies()
    {
        staleParts.Clear();
        foreach (KeyValuePair<AssemblyPart, PartProxy> entry in proxiesByPart)
            if (entry.Key == null || !wantedParts.Contains(entry.Key)) staleParts.Add(entry.Key);
        for (int i = 0; i < staleParts.Count; i++) ReleaseProxy(staleParts[i]);
    }

    private void PruneCarCaches()
    {
        staleCars.Clear();
        foreach (KeyValuePair<CarController, AssemblyPart[]> entry in partsByCar)
            if (entry.Key == null || !liveCars.Contains(entry.Key)) staleCars.Add(entry.Key);
        for (int i = 0; i < staleCars.Count; i++) partsByCar.Remove(staleCars[i]);
    }

    private void ReleaseProxy(AssemblyPart part)
    {
        if (!proxiesByPart.TryGetValue(part, out PartProxy proxy)) return;
        proxiesByPart.Remove(part);
        for (int i = 0; i < proxy.meshes.Count; i++)
        {
            MeshProxy mesh = proxy.meshes[i];
            if (mesh.proxyFilter != null) mesh.proxyFilter.sharedMesh = null;
            if (mesh.bakedMesh != null) Destroy(mesh.bakedMesh);
            if (mesh.proxyFilter != null) Destroy(mesh.proxyFilter.gameObject);
        }
    }

    private void ClearProxies()
    {
        staleParts.Clear();
        foreach (KeyValuePair<AssemblyPart, PartProxy> entry in proxiesByPart) staleParts.Add(entry.Key);
        for (int i = 0; i < staleParts.Count; i++) ReleaseProxy(staleParts[i]);
        partsByCar.Clear();
    }

    private void OnDisable()
    {
        ClearProxies();
        MissingPartCount = 0;
        AffectedCarCount = 0;
        traffic = null;
    }

    private void OnDestroy()
    {
        ClearProxies();
        if (proxyMaterial != null) Destroy(proxyMaterial);
    }
}
