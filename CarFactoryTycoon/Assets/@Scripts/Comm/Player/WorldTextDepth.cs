using System.Collections.Generic;
using UnityEngine;

/// <summary>Depth-tested legacy world text, separate from the shared GUI font material.</summary>
public static class WorldTextDepth
{
    public const string ShaderName = "CarFactory/WorldTextDepth";
    static readonly Dictionary<Font, Material> materials = new Dictionary<Font, Material>();
    static bool listening;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetCache()
    {
        Font.textureRebuilt -= RefreshAtlas;
        listening = false;
        foreach (var material in materials.Values)
            if (material != null) Object.Destroy(material);
        materials.Clear();
    }

    public static bool Apply(TextMesh text)
    {
        // Transient materials must never become serialized scene or prefab references.
        if (!Application.isPlaying || text == null || text.font == null) return false;
        var renderer = text.GetComponent<MeshRenderer>();
        if (renderer == null) return false;
        var font = text.font;
        if (!materials.TryGetValue(font, out var material) || material == null)
        {
            // Resources reference keeps the shader available in player builds.
            var shader = Resources.Load<Shader>("PrototypeFeedback/WorldTextDepth");
            if (shader == null)
            {
                Debug.LogError("World text depth shader is missing.", text);
                return false;
            }
            material = new Material(shader) { name = font.name + " World Depth", hideFlags = HideFlags.HideAndDontSave };
            materials[font] = material;
        }
        material.mainTexture = font.material.mainTexture;
        renderer.sharedMaterial = material;
        if (!listening)
        {
            Font.textureRebuilt += RefreshAtlas;
            listening = true;
        }
        return true;
    }

    static void RefreshAtlas(Font font)
    {
        if (font != null && materials.TryGetValue(font, out var material) && material != null)
            material.mainTexture = font.material.mainTexture;
    }
}
