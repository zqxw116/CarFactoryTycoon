#if UNITY_EDITOR
using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using Unity.AI.Navigation;
using Object = UnityEngine.Object;

/// <summary>Explicit authoring operation for the approved supply and inspection layout. Never auto-runs.</summary>
public static class FactoryFlowSetup
{
    const string MaterialDir = "Assets/@Resource/Materials/FactoryFlow";

    public static string Apply(bool resumePartial = false)
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (Application.isPlaying || scene.name != "Car_Factory 3")
            throw new InvalidOperationException("Open Car_Factory 3 in edit mode first.");
        if (scene.isDirty && (!resumePartial || GameObject.Find("FrameSupplyTunnel") == null))
            throw new InvalidOperationException("Preserve pending scene edits before applying the layout.");
        var w = Object.FindFirstObjectByType<WaterTestStation>();
        var spawner = Object.FindFirstObjectByType<CarSpawner>();
        var paint = Object.FindFirstObjectByType<PaintBooth>();
        var line = GameObject.Find("MainSpline").GetComponent<SplineContainer>();
        if (w == null || spawner == null || paint == null || line == null)
            throw new InvalidOperationException("Missing factory station references.");
        if (resumePartial && (!w.sharedRejectDeck ||
            Vector3.Distance(spawner.transform.position, new Vector3(67.7f, 0f, 35f)) > .01f ||
            Vector3.Distance(paint.transform.position, new Vector3(54.2f, 0f, 35f)) > .01f))
            throw new InvalidOperationException("Partial authoring state does not match this operation.");
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Supply tunnel and shared inspection deck");

        if (!AssetDatabase.IsValidFolder(MaterialDir))
            AssetDatabase.CreateFolder("Assets/@Resource/Materials", "FactoryFlow");
        var dark = Material("SupplyDark", new Color(.11f, .15f, .19f), false);
        var steel = Material("SupplySteel", new Color(.32f, .38f, .41f), false);
        var yellow = Material("SafetyYellow", new Color(.96f, .64f, .10f), false);
        var lamp = Material("InspectionLens", new Color(.12f, .20f, .25f), true);

        // Extend only the upstream straight: station transforms and downstream curves stay fixed.
        Undo.RecordObject(line, "Extend supply approach");
        var knot = line.Spline[0];
        knot.Position = line.transform.InverseTransformPoint(new Vector3(70.5f, 0f, 35f));
        line.Spline[0] = knot;
        EditorUtility.SetDirty(line);
        Undo.RecordObject(spawner.transform, "Place concealed spawn");
        spawner.transform.position = new Vector3(67.7f, 0f, 35f);
        Undo.RecordObject(paint.transform, "Separate paint and robot");
        paint.transform.position = new Vector3(54.2f, 0f, 35f);

        var supply = Root("FrameSupplyTunnel", null);
        supply.position = new Vector3(65.65f, -.405f, 35f);
        Part(supply, "Side_Near", new Vector3(0, 2.05f, -2.25f), new Vector3(10.3f, 4.1f, .18f), dark);
        Part(supply, "Side_Far", new Vector3(0, 2.05f, 2.25f), new Vector3(10.3f, 4.1f, .18f), dark);
        Part(supply, "Roof", new Vector3(0, 4.12f, 0), new Vector3(10.5f, .20f, 4.7f), dark);
        Part(supply, "Back", new Vector3(5.15f, 2.05f, 0), new Vector3(.20f, 4.1f, 4.5f), dark);
        Part(supply, "EntryHeader", new Vector3(-5.15f, 3.75f, 0), new Vector3(.25f, .36f, 4.7f), yellow);
        for (int side = -1; side <= 1; side += 2)
        {
            Part(supply, "EntryPost_" + side, new Vector3(-5.15f, 1.9f, side * 2.2f), new Vector3(.28f, 3.8f, .28f), steel);
            for (int i = 0; i < 4; i++)
                Part(supply, "EdgeMarker_" + side + "_" + i, new Vector3(-5.3f, .25f + i * .32f, side * 2.2f), new Vector3(.04f, .16f, .30f), yellow);
        }
        var modifier = supply.GetComponent<NavMeshModifierVolume>();
        if (modifier == null) modifier = supply.gameObject.AddComponent<NavMeshModifierVolume>();
        modifier.center = new Vector3(0, 2.2f, 0);
        modifier.size = new Vector3(10.8f, 4.5f, 4.9f);
        modifier.area = 1;

        var paintVisual = Root("PaintBoothFrame", paint.transform);
        paintVisual.position = paint.transform.position;
        paintVisual.rotation = Quaternion.identity;
        paintVisual.localScale = Vector3.one;
        // Parent booth is scaled. Root uses inverse scale so geometry dimensions remain metres.
        paintVisual.localScale = new Vector3(1f / paint.transform.lossyScale.x, 1f / paint.transform.lossyScale.y, 1f / paint.transform.lossyScale.z);
        Part(paintVisual, "LeftPost", new Vector3(0, 1.6f, -2.1f), new Vector3(.25f, 4f, .25f), steel);
        Part(paintVisual, "RightPost", new Vector3(0, 1.6f, 2.1f), new Vector3(.25f, 4f, .25f), steel);
        Part(paintVisual, "Header", new Vector3(0, 3.65f, 0), new Vector3(.28f, .2f, 4.5f), yellow);

        var tank = w.transform.Find("GlassTank");
        if (tank == null) throw new InvalidOperationException("Missing glass tank.");
        Undo.RecordObject(tank, "Move tank off line");
        tank.position = new Vector3(50f, -.405f, 3.5f);
        tank.localScale = new Vector3(1f, 1f, .85f);
        var hatch = w.transform.Find("RejectHatch");
        Undo.RecordObject(hatch, "Move hatch onto line");
        hatch.position = new Vector3(50f, -.271f, 7f);
        foreach (var rib in hatch.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Rib"))
        {
            Undo.RecordObject(rib, "Flush hatch ribs");
            var pos = rib.localPosition; pos.y = .027f; rib.localPosition = pos;
            var scale = rib.localScale; scale.y = .004f; rib.localScale = scale;
        }
        Undo.RecordObject(w.releasePoint, "Shared deck release");
        w.releasePoint.position = new Vector3(50f, 0f, 7f);
        Undo.RecordObject(w.rejectPoint, "Shared deck reject");
        w.rejectPoint.position = new Vector3(50f, -.206f, 7f);
        Undo.RecordObject(w, "Enable shared-deck inspection");
        w.sharedRejectDeck = true;
        w.useElectricalInspectionFeedback = true;
        w.releaseProgress = Nearest(line, w.releasePoint.position);
        w.gateProgress = Nearest(line, w.transform.position);
        if (w.failVFX != null) w.failVFX.gameObject.SetActive(false);
        if (w.failFlashLight != null) w.failFlashLight.enabled = false;

        // Keep the trolley/hook hierarchy and Cartesian drive. Remove the camera-side rail.
        var gantry = w.craneBridge.parent;
        foreach (var t in gantry.GetComponentsInChildren<Transform>(true))
        {
            if (t.IsChildOf(w.craneBridge)) continue;
            if (t.name == "Column" || t.name == "ColumnBase")
            {
                bool far = t.position.z > 5f;
                t.gameObject.SetActive(far);
                if (far) t.position = new Vector3(t.position.x < 50f ? 47f : 58.2f, t.position.y, 9.6f);
            }
            if (t.name == "Rail")
            {
                bool far = t.position.z > 5f;
                t.gameObject.SetActive(far);
                if (far)
                {
                    t.position = new Vector3(52.6f, t.position.y, 9.6f);
                    t.localScale = new Vector3(11.5f, .35f, .3f);
                }
            }
            if (t.name == "EndTie") t.gameObject.SetActive(false);
        }
        foreach (Transform t in w.craneBridge)
        {
            if (t.name == "Girder")
            {
                t.position = new Vector3(t.position.x, t.position.y, 6.55f);
                t.localScale = new Vector3(.32f, .32f, 6.4f);
            }
            if (t.name == "EndTruck")
            {
                bool far = t.position.z > 5f;
                t.gameObject.SetActive(far);
                if (far) t.position = new Vector3(t.position.x, t.position.y, 9.6f);
            }
        }

        var fixture = Root("InspectionLightStand", w.transform);
        fixture.position = new Vector3(50f, -.405f, 1.55f);
        Part(fixture, "Stand", new Vector3(0, 1.55f, 0), new Vector3(.12f, 3.1f, .12f), dark);
        Part(fixture, "Housing", new Vector3(0, 3f, 0), new Vector3(.6f, .3f, .28f), steel);
        var lens = Part(fixture, "InspectionLens", new Vector3(0, 3f, .15f), new Vector3(.43f, .18f, .035f), lamp);
        var spotlight = ChildLight(fixture, "InspectionSpot", LightType.Spot, 18f, 9f);
        spotlight.transform.localPosition = new Vector3(0, 3f, .2f);
        spotlight.transform.rotation = Quaternion.LookRotation(new Vector3(50f, .6f, 3.5f) - spotlight.transform.position);
        spotlight.spotAngle = 70f;
        spotlight.innerSpotAngle = 35f;
        var fill = ChildLight(fixture, "UnderwaterFill", LightType.Point, 5f, 5.5f);
        fill.transform.position = new Vector3(50f, .6f, 3.5f);
        var feedback = w.GetComponent<WaterInspectionFeedback>();
        if (feedback == null) feedback = Undo.AddComponent<WaterInspectionFeedback>(w.gameObject);
        var fso = new SerializedObject(feedback);
        fso.FindProperty("station").objectReferenceValue = w;
        fso.FindProperty("inspectionLight").objectReferenceValue = spotlight;
        fso.FindProperty("fillLight").objectReferenceValue = fill;
        var indicators = fso.FindProperty("indicatorRenderers");
        indicators.arraySize = 1; indicators.GetArrayElementAtIndex(0).objectReferenceValue = lens;
        var water = tank.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name.StartsWith("Water_")).ToArray();
        var waterProperty = fso.FindProperty("waterRenderers"); waterProperty.arraySize = water.Length;
        for (int i = 0; i < water.Length; i++) waterProperty.GetArrayElementAtIndex(i).objectReferenceValue = water[i];
        string arcPath = MaterialDir + "/InspectionArc.mat";
        var arc = AssetDatabase.LoadAssetAtPath<Material>(arcPath);
        if (arc == null)
        {
            var shader = Shader.Find("CarFactory/InspectionElectricArc");
            if (shader == null) throw new InvalidOperationException("Inspection arc shader not imported.");
            arc = new Material(shader); arc.name = "InspectionArc";
            AssetDatabase.CreateAsset(arc, arcPath);
        }
        arc.renderQueue = 3100;
        fso.FindProperty("arcMaterial").objectReferenceValue = arc;
        fso.ApplyModifiedProperties();

        var belt = Object.FindFirstObjectByType<SplineConveyorDeformer>();
        var bso = new SerializedObject(belt);
        bso.FindProperty("m_HeightScale").floatValue = .5f;
        var ranges = bso.FindProperty("m_ExcludeRanges"); ranges.arraySize = 1;
        var range = ranges.GetArrayElementAtIndex(0);
        float distance = w.releaseProgress * line.CalculateLength();
        range.FindPropertyRelative("splineIndex").intValue = 0;
        range.FindPropertyRelative("start").floatValue = distance - 2.7f;
        range.FindPropertyRelative("end").floatValue = distance + 2.7f;
        bso.ApplyModifiedProperties(); belt.Rebuild();
        EditorUtility.SetDirty(w); EditorUtility.SetDirty(feedback);
        AssetDatabase.SaveAssetIfDirty(arc);
        EditorSceneManager.MarkSceneDirty(scene);
        var log = new StringBuilder();
        NavMeshBakeTool.Rebake(log, false);
        if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save factory scene.");
        Undo.CollapseUndoOperations(group);
        return "Supply start 70.5, hidden spawn 67.7, paint 54.2; robot transforms untouched. Tank 50/3.5, shared deck 50/7, upstream gate 56.6/7. " + log;
    }

    static float Nearest(SplineContainer line, Vector3 world)
    {
        SplineUtility.GetNearestPoint(line.Spline, (float3)line.transform.InverseTransformPoint(world), out _, out float t);
        return t;
    }
    static Transform Root(string name, Transform parent)
    {
        Transform t = parent != null ? parent.Find(name) : GameObject.Find(name)?.transform;
        if (t == null)
        {
            var go = new GameObject(name); Undo.RegisterCreatedObjectUndo(go, name);
            t = go.transform; t.SetParent(parent, false);
        }
        return t;
    }
    static MeshRenderer Part(Transform parent, string name, Vector3 position, Vector3 scale, Material material)
    {
        Transform child = parent.Find(name);
        if (child == null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            Undo.RegisterCreatedObjectUndo(go, name); child = go.transform; child.SetParent(parent, false);
            Object.DestroyImmediate(go.GetComponent<Collider>());
        }
        Undo.RecordObject(child, name); child.localPosition = position; child.localRotation = Quaternion.identity; child.localScale = scale;
        var renderer = child.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material;
        return renderer;
    }
    static Material Material(string name, Color color, bool emission)
    {
        string path = MaterialDir + "/" + name + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            var template = AssetDatabase.LoadAssetAtPath<Material>("Assets/@Resource/Materials/WaterTest/CraneSteelDark.mat");
            mat = new Material(template) { name = name };
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_BaseColor", color);
        if (emission) { mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", color); }
        EditorUtility.SetDirty(mat); AssetDatabase.SaveAssetIfDirty(mat); return mat;
    }
    static Light ChildLight(Transform parent, string name, LightType type, float intensity, float range)
    {
        var t = Root(name, parent);
        var light = t.GetComponent<Light>();
        if (light == null) light = t.gameObject.AddComponent<Light>();
        light.type = type; light.intensity = intensity; light.range = range;
        light.shadows = LightShadows.None; light.enabled = false; return light;
    }
}
#endif
