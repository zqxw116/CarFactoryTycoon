using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Scene-only visual feedback for the water inspection: a neutral scan pulse, then a
/// visible pass/fail light and brief electrical arcs on defective cars.
/// </summary>
public sealed class WaterInspectionFeedback : MonoBehaviour
{
    private const int ArcCount = 3;
    private const int MaxArcPoints = 8;
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int ShallowColorId = Shader.PropertyToID("_ShallowColor");
    private static readonly int DeepColorId = Shader.PropertyToID("_DeepColor");
    private static readonly int WaveColorId = Shader.PropertyToID("_WaveColor");
    private const float WaterTintStrength = 0.24f;

    [Header("Inspection")]
    [SerializeField] private WaterTestStation station;
    [SerializeField] private Light inspectionLight;
    [SerializeField] private Light fillLight;
    [SerializeField] private Renderer[] indicatorRenderers;
    [SerializeField] private Renderer[] waterRenderers;

    [Header("Electrical arcs")]
    [SerializeField] private Material arcMaterial;
    [SerializeField, Min(0.001f)] private float arcWidth = 0.035f;

    private sealed class WaterRendererCache
    {
        public Renderer renderer;
        public MaterialPropertyBlock[] originalBlocks;
        public Color[][] defaultColors;
    }

    private readonly LineRenderer[] arcs = new LineRenderer[ArcCount];
    private readonly Vector3[][] arcLocalPoints = new Vector3[ArcCount][];
    private readonly int[] arcPointCounts = new int[ArcCount];
    private readonly float[] arcEndsAt = new float[ArcCount];
    private readonly float[] nextArcAt = new float[ArcCount];
    private readonly bool[] arcIsActive = new bool[ArcCount];
    private MaterialPropertyBlock workingBlock;
    private MaterialPropertyBlock[] originalBlocks;
    private bool[] hasOriginalBlocks;
    private System.Random visualRandom;
    private float lightDefaultIntensity;
    private bool hasLightDefaultIntensity;
    private float fillLightDefaultIntensity;
    private bool hasFillLightDefaultIntensity;
    private WaterRendererCache[] waterRendererCaches;
    private bool waterTintApplied;
    private bool arcsWereEnabled;

    private static readonly Color ScanColor = new Color(0.75f, 0.95f, 1f, 1f);
    private static readonly Color PassColor = new Color(0.12f, 0.48f, 1f, 1f);
    private static readonly Color DefectColor = new Color(1f, 0.075f, 0.035f, 1f);
    private static readonly Color ArcColor = new Color(0.55f, 0.95f, 1f, 1f);

    private void Awake()
    {
        visualRandom = new System.Random(unchecked(Environment.TickCount ^ GetInstanceID()));
        workingBlock = new MaterialPropertyBlock();
        CreateArcs();

        if (inspectionLight != null)
        {
            lightDefaultIntensity = inspectionLight.intensity;
            hasLightDefaultIntensity = true;
            inspectionLight.enabled = false;
        }
        if (fillLight != null)
        {
            fillLightDefaultIntensity = fillLight.intensity;
            hasFillLightDefaultIntensity = true;
            fillLight.enabled = false;
        }
    }

    private void OnEnable()
    {
        CacheOriginalIndicatorBlocks();
        CacheWaterRenderers();
        if (visualRandom == null)
            visualRandom = new System.Random(unchecked(Environment.TickCount ^ GetInstanceID()));
    }

    private void OnDisable()
    {
        SetLightOff();
        HideArcs();
        arcsWereEnabled = false;
        RestoreIndicatorBlocks();
        RestoreWaterPropertyBlocks();
    }

    private void Update()
    {
        bool active = TryGetInspectionDisplay(out Color resultColor, out bool revealed);
        if (!active)
        {
            SetLightOff();
            SetIndicatorColor(Color.black, 0f);
            RestoreWaterPropertyBlocks();
            HideArcs();
            arcsWereEnabled = false;
            return;
        }

        Color displayColor = revealed ? resultColor : ScanColor;
        float pulse = revealed
            ? 0.92f + 0.08f * Mathf.Sin(Time.time * 5.5f)
            : 0.72f + 0.28f * (0.5f + 0.5f * Mathf.Sin(Time.time * 13f + Mathf.Clamp01(station.InspectionProgress) * Mathf.PI * 2f + GetInstanceID()));
        SetLight(displayColor, pulse);
        SetIndicatorColor(displayColor, pulse);
        ApplyWaterTint(displayColor);

        WaterTestStation.WaterTestState state = station.currentState;
        bool showArcs = state == WaterTestStation.WaterTestState.Testing
            && revealed
            && station.InspectionResult != WaterTestStation.WaterTestResult.Pass
            && station.InspectionResult != WaterTestStation.WaterTestResult.None;
        UpdateArcs(showArcs, station.InspectedCar != null ? station.InspectedCar.transform : null);
    }

    private bool TryGetInspectionDisplay(out Color color, out bool revealed)
    {
        color = Color.black;
        revealed = false;
        if (station == null) return false;

        WaterTestStation.WaterTestState state = station.currentState;
        if (state == WaterTestStation.WaterTestState.Testing)
        {
            revealed = station.IsInspectionResultVisible && station.InspectionResult != WaterTestStation.WaterTestResult.None;
            if (revealed)
                color = ResultColor(station.InspectionResult);
            return true;
        }

        if (state == WaterTestStation.WaterTestState.Emerging && station.IsInspectionResultVisible && station.InspectionResult != WaterTestStation.WaterTestResult.None)
        {
            revealed = true;
            color = ResultColor(station.InspectionResult);
            return true;
        }

        return false;
    }

    private static Color ResultColor(WaterTestStation.WaterTestResult result)
    {
        return result == WaterTestStation.WaterTestResult.Pass ? PassColor : DefectColor;
    }

    private void CreateArcs()
    {
        for (int i = 0; i < ArcCount; i++)
        {
            GameObject arcObject = new GameObject($"Inspection Arc {i + 1}");
            arcObject.transform.SetParent(transform, false);

            LineRenderer line = arcObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.alignment = LineAlignment.View;
            line.loop = false;
            line.positionCount = 0;
            line.widthMultiplier = arcWidth;
            line.numCapVertices = 2;
            line.numCornerVertices = 2;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.startColor = ArcColor;
            line.endColor = Color.white;
            line.sharedMaterial = arcMaterial;

            arcs[i] = line;
            arcLocalPoints[i] = new Vector3[MaxArcPoints];
        }
    }

    private void UpdateArcs(bool enabled, Transform car)
    {
        if (!enabled || car == null)
        {
            HideArcs();
            arcsWereEnabled = false;
            return;
        }

        float now = Time.time;
        if (!arcsWereEnabled)
        {
            for (int i = 0; i < ArcCount; i++)
                nextArcAt[i] = now + RandomRange(0.03f, 0.24f);
            arcsWereEnabled = true;
        }

        for (int i = 0; i < ArcCount; i++)
        {
            LineRenderer line = arcs[i];
            if (line == null) continue;
            if (line.sharedMaterial != arcMaterial) line.sharedMaterial = arcMaterial;

            if (arcIsActive[i] && now >= arcEndsAt[i])
            {
                arcIsActive[i] = false;
                line.positionCount = 0;
            }

            if (!arcIsActive[i] && now >= nextArcAt[i])
                BeginArcBurst(i, now);

            if (!arcIsActive[i]) continue;

            line.widthMultiplier = arcWidth;
            line.positionCount = arcPointCounts[i];
            Vector3[] localPoints = arcLocalPoints[i];
            for (int p = 0; p < arcPointCounts[i]; p++)
                line.SetPosition(p, car.TransformPoint(localPoints[p]));
        }
    }

    private void BeginArcBurst(int index, float now)
    {
        float duration = RandomRange(0.06f, 0.13f);
        float darkGap = RandomRange(0.10f, 0.27f);
        arcEndsAt[index] = now + duration;
        nextArcAt[index] = arcEndsAt[index] + darkGap;
        arcPointCounts[index] = visualRandom.Next(6, MaxArcPoints + 1);
        BuildArcShape(arcLocalPoints[index], arcPointCounts[index]);
        arcIsActive[index] = true;
    }

    private void BuildArcShape(Vector3[] points, int count)
    {
        // Keep arcs just outside the opaque body, rather than hidden inside its panels.
        int face = visualRandom.Next(4);
        bool side = face < 2;
        float sign = (face & 1) == 0 ? -1f : 1f;
        float surface = sign * (side ? 1.24f : 2.62f);
        float along = RandomRange(side ? -1.5f : -0.65f, side ? 1.5f : 0.65f);
        Vector3 start = side ? new Vector3(surface, RandomRange(.4f, 1.25f), along)
                             : new Vector3(along, RandomRange(.4f, 1.25f), surface);
        Vector3 end = start;
        end.y = Mathf.Clamp(start.y + RandomRange(-.5f, .5f), .3f, 1.45f);
        if (side) end.z = Mathf.Clamp(start.z + RandomRange(-.8f, .8f), -2.1f, 2.1f);
        else end.x = Mathf.Clamp(start.x + RandomRange(-.6f, .6f), -1.05f, 1.05f);
        points[0] = start;
        points[count - 1] = end;
        for (int i = 1; i < count - 1; i++)
        {
            Vector3 point = Vector3.Lerp(start, end, (float)i / (count - 1));
            point.y = Mathf.Clamp(point.y + RandomRange(-.16f, .16f), .3f, 1.45f);
            if (side)
            {
                point.x = surface + RandomRange(-.035f, .035f);
                point.z = Mathf.Clamp(point.z + RandomRange(-.22f, .22f), -2.1f, 2.1f);
            }
            else
            {
                point.z = surface + RandomRange(-.035f, .035f);
                point.x = Mathf.Clamp(point.x + RandomRange(-.22f, .22f), -1.05f, 1.05f);
            }
            points[i] = point;
        }
    }

    private void HideArcs()
    {
        for (int i = 0; i < ArcCount; i++)
        {
            arcIsActive[i] = false;
            if (arcs[i] != null) arcs[i].positionCount = 0;
        }
    }

    private float RandomRange(float min, float max)
    {
        return min + (float)visualRandom.NextDouble() * (max - min);
    }

    private void SetLight(Color color, float intensityScale)
    {
        SetSingleLight(inspectionLight, color, intensityScale, ref lightDefaultIntensity, ref hasLightDefaultIntensity);
        SetSingleLight(fillLight, color, intensityScale, ref fillLightDefaultIntensity, ref hasFillLightDefaultIntensity);
    }

    private static void SetSingleLight(Light target, Color color, float intensityScale, ref float intensity, ref bool hasIntensity)
    {
        if (target == null) return;
        if (!hasIntensity)
        {
            intensity = target.intensity;
            hasIntensity = true;
        }
        target.color = color;
        target.intensity = intensity * intensityScale;
        target.enabled = true;
    }

    private void SetLightOff()
    {
        if (inspectionLight != null) inspectionLight.enabled = false;
        if (fillLight != null) fillLight.enabled = false;
    }
    private void SetIndicatorColor(Color color, float intensityScale)
    {
        if (indicatorRenderers == null || workingBlock == null) return;
        Color emission = color * intensityScale;
        for (int i = 0; i < indicatorRenderers.Length; i++)
        {
            Renderer target = indicatorRenderers[i];
            if (target == null) continue;

            target.GetPropertyBlock(workingBlock);
            workingBlock.SetColor(EmissionColorId, emission);
            workingBlock.SetColor(BaseColorId, color * 0.25f);
            workingBlock.SetColor(ColorId, color * 0.25f);
            target.SetPropertyBlock(workingBlock);
        }
    }

    private void CacheWaterRenderers()
    {
        if (waterRenderers == null)
        {
            waterRendererCaches = null;
            return;
        }

        waterRendererCaches = new WaterRendererCache[waterRenderers.Length];
        for (int i = 0; i < waterRenderers.Length; i++)
        {
            Renderer target = waterRenderers[i];
            if (target == null) continue;

            Material[] materials = target.sharedMaterials;
            WaterRendererCache cache = new WaterRendererCache
            {
                renderer = target,
                originalBlocks = new MaterialPropertyBlock[materials.Length],
                defaultColors = new Color[materials.Length][]
            };
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                MaterialPropertyBlock original = new MaterialPropertyBlock();
                target.GetPropertyBlock(original, materialIndex);
                cache.originalBlocks[materialIndex] = original;

                Material material = materials[materialIndex];
                cache.defaultColors[materialIndex] = new[]
                {
                    GetMaterialColor(material, ShallowColorId),
                    GetMaterialColor(material, DeepColorId),
                    GetMaterialColor(material, WaveColorId)
                };
            }
            waterRendererCaches[i] = cache;
        }
    }

    private static Color GetMaterialColor(Material material, int propertyId)
    {
        return material != null && material.HasProperty(propertyId) ? material.GetColor(propertyId) : Color.white;
    }

    private void ApplyWaterTint(Color tint)
    {
        if (waterRendererCaches == null || workingBlock == null) return;
        for (int i = 0; i < waterRendererCaches.Length; i++)
        {
            WaterRendererCache cache = waterRendererCaches[i];
            if (cache == null || cache.renderer == null) continue;
            for (int materialIndex = 0; materialIndex < cache.originalBlocks.Length; materialIndex++)
            {
                cache.renderer.GetPropertyBlock(workingBlock, materialIndex);
                Color[] defaults = cache.defaultColors[materialIndex];
                workingBlock.SetColor(ShallowColorId, TintWaterColor(defaults[0], tint));
                workingBlock.SetColor(DeepColorId, TintWaterColor(defaults[1], tint));
                workingBlock.SetColor(WaveColorId, TintWaterColor(defaults[2], tint));
                cache.renderer.SetPropertyBlock(workingBlock, materialIndex);
            }
        }
        waterTintApplied = true;
    }

    private static Color TintWaterColor(Color original, Color tint)
    {
        Color result = Color.Lerp(original, tint, WaterTintStrength);
        result.a = original.a;
        return result;
    }

    private void RestoreWaterPropertyBlocks()
    {
        if (!waterTintApplied || waterRendererCaches == null) return;
        for (int i = 0; i < waterRendererCaches.Length; i++)
        {
            WaterRendererCache cache = waterRendererCaches[i];
            if (cache == null || cache.renderer == null) continue;
            for (int materialIndex = 0; materialIndex < cache.originalBlocks.Length; materialIndex++)
                cache.renderer.SetPropertyBlock(cache.originalBlocks[materialIndex], materialIndex);
        }
        waterTintApplied = false;
    }
    private void CacheOriginalIndicatorBlocks()
    {
        if (indicatorRenderers == null) return;
        originalBlocks = new MaterialPropertyBlock[indicatorRenderers.Length];
        hasOriginalBlocks = new bool[indicatorRenderers.Length];
        for (int i = 0; i < indicatorRenderers.Length; i++)
        {
            Renderer target = indicatorRenderers[i];
            if (target == null) continue;
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            target.GetPropertyBlock(block);
            originalBlocks[i] = block;
            hasOriginalBlocks[i] = true;
        }
    }

    private void RestoreIndicatorBlocks()
    {
        if (indicatorRenderers == null || originalBlocks == null || hasOriginalBlocks == null) return;
        int count = Mathf.Min(indicatorRenderers.Length, originalBlocks.Length);
        for (int i = 0; i < count; i++)
        {
            Renderer target = indicatorRenderers[i];
            if (target != null && hasOriginalBlocks[i])
                target.SetPropertyBlock(originalBlocks[i]);
        }
        originalBlocks = null;
        hasOriginalBlocks = null;
    }
}