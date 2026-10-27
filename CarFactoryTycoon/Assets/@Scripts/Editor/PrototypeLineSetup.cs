#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

public static class PrototypeLineSetup
{
    static T[] All<T>() where T : Object => Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
    public static string Apply()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Edit mode required.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != "Assets/Car_Factory/DemoScene/Car_Factory 3.unity") throw new InvalidOperationException("Open the main factory scene.");
        var progression = All<ProductionProgression>().Single();
        Undo.RegisterFullObjectHierarchyUndo(progression.gameObject, "M1 full line prototype");
        var settings = AssetDatabase.LoadAssetAtPath<PrototypeBalanceSettings>("Assets/@Scripts/Data/PrototypeBalanceSettings.asset");
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<PrototypeBalanceSettings>();
            AssetDatabase.CreateAsset(settings, "Assets/@Scripts/Data/PrototypeBalanceSettings.asset");
        }
        var control = progression.GetComponent<PrototypeLineController>();
        if (control == null) control = progression.gameObject.AddComponent<PrototypeLineController>();
        control.settings = settings;
        control.spawner = All<CarSpawner>().Single();
        control.spawner.manualInput = true;
        control.spawner.queueCapacity = settings.queueCapacity;
        control.spawner.globalLineSpeed = settings.lowSpeed;
        control.supplyTunnel = GameObject.Find("FrameSupplyTunnel").transform;
        if (control.supplyTunnel.GetComponent<BoxCollider>() == null)
        {
            var hit = control.supplyTunnel.gameObject.AddComponent<BoxCollider>();
            var renderers = control.supplyTunnel.GetComponentsInChildren<Renderer>();
            var bounds = renderers[0].bounds; foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            hit.center = control.supplyTunnel.InverseTransformPoint(bounds.center);
            hit.size = new Vector3(bounds.size.x / control.supplyTunnel.lossyScale.x, bounds.size.y / control.supplyTunnel.lossyScale.y, bounds.size.z / control.supplyTunnel.lossyScale.z);
            hit.isTrigger = true;
        }
        if (control.queueLabel == null)
        {
            var label = new GameObject("SupplyQueueLabel"); label.transform.SetParent(control.supplyTunnel, false);
            label.transform.position = control.supplyTunnel.position + Vector3.up * 4.2f;
            control.queueLabel = label.AddComponent<TextMesh>(); control.queueLabel.text = "대기 0 / 5";
            control.queueLabel.anchor = TextAnchor.MiddleCenter; control.queueLabel.fontSize = 64; control.queueLabel.characterSize = .05f;
            control.queueLabel.color = Color.white;
        }
        if (control.leverRoot == null)
        {
            var lever = new GameObject("LineSpeedLever"); lever.transform.SetParent(progression.transform, false);
            lever.transform.position = control.spawner.transform.position + new Vector3(0,0,3.8f);
            control.leverRoot = lever.transform;
            Box(lever.transform, "Pedestal", new Vector3(0,.5f,0), new Vector3(.55f,1f,.55f), "SupplySteel.mat");
            var pivot = new GameObject("HandlePivot"); pivot.transform.SetParent(lever.transform, false); pivot.transform.localPosition = Vector3.up;
            control.leverHandle = pivot.transform;
            Box(pivot.transform, "Handle", new Vector3(0,.3f,0), new Vector3(.12f,.6f,.12f), "SafetyYellow.mat");
            Box(pivot.transform, "Grip", new Vector3(0,.65f,0), new Vector3(.35f,.16f,.16f), "SupplySteel.mat");
        }
        // Keep the physical stock piles available even though their former human/robot owners are hidden.
        var stacksRoot = GameObject.Find("PrototypePartStacks") ?? new GameObject("PrototypePartStacks");
        foreach (var stack in All<PartStack>())
        {
            Undo.SetTransformParent(stack.transform, stacksRoot.transform, "Expose all prototype stock");
            stack.gameObject.SetActive(true); EditorUtility.SetDirty(stack);
        }
        progression.prototypeMode = true;
        progression.persistProgress = false;
        var serialized = new SerializedObject(progression);
        var price = serialized.FindProperty("prototypeSalePrice"); if (price != null) price.intValue = settings.vehicleRevenue;
        var balance = serialized.FindProperty("prototypeSettings"); if (balance != null) balance.objectReferenceValue = settings;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        foreach (var p in progression.processes)
        {
            p.hired = p.automated = p.experienced = false; p.workerLevel = 0;
            if (p.humanRoot != null) p.humanRoot.SetActive(false);
            if (p.humanStation != null) p.humanStation.enabled = false;
            if (p.robots != null) foreach (var r in p.robots) if (r != null) r.gameObject.SetActive(false);
            if (p.robotDecor != null) foreach (var d in p.robotDecor) if (d != null) d.SetActive(false);
            if (p.wheel != null) p.wheel.gameObject.SetActive(true);
        }
        foreach (var station in All<WorkerStation>()) SetFlow(station, settings.workZoneMeters);
        foreach (var station in All<WheelStation>()) SetFlow(station, settings.workZoneMeters);
        foreach (var station in All<StationController>()) SetFlow(station, settings.workZoneMeters);
        foreach (var worker in All<Worker>()) worker.gameObject.SetActive(false);
        foreach (var worker in All<SupplyWorker>()) worker.gameObject.SetActive(false);
        foreach (var input in All<InputManager>()) input.enabled = false;
        foreach (var fuel in All<FuelStation>())
        {
            fuel.enabled = false;
            if (fuel.attendant != null) fuel.attendant.gameObject.SetActive(false);
            if (fuel.automationRig != null) fuel.automationRig.SetActive(false);
            EditorUtility.SetDirty(fuel);
        }
        foreach (var root in progression.finishingRoots) if (root != null) root.SetActive(true);
        foreach (var root in progression.constructionRows) if (root != null) root.SetActive(false);
        progression.conveyor.SetProductionEnd(1f);
        var expansion = progression.GetComponent<FactoryExpansion>();
        if (expansion != null) { expansion.ApplyPrefix(progression.processes.Length); EditorUtility.SetDirty(expansion); }
        var camera = All<CameraController>().Single(); camera.SetOverview(true); EditorUtility.SetDirty(camera);
        foreach (var item in new Object[] {progression, control, control.spawner}) EditorUtility.SetDirty(item);
        var others = EditorBuildSettings.scenes.Where(s => s.path != scene.path).ToArray();
        EditorBuildSettings.scenes = new[] {new EditorBuildSettingsScene(scene.path, true)}.Concat(others).ToArray();
        EditorSceneManager.MarkSceneDirty(scene); AssetDatabase.SaveAssets(); EditorSceneManager.SaveScene(scene);
        return "M1 scene ready: full line, 23 unstaffed processes, manual queue, lever, complete factory footprint. Fuel hardware retained without an attendant; M3/M4 integration pending.";
    }
    static void SetFlow(MonoBehaviour station, float meters)
    {
        var so = new SerializedObject(station);
        var flow = so.FindProperty("flowThrough"); var length = so.FindProperty("workZoneMeters");
        if (flow == null || length == null) throw new InvalidOperationException("Missing flow fields: " + station.GetType().Name);
        flow.boolValue = true; length.floatValue = meters; so.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(station);
    }
    static void Box(Transform parent, string name, Vector3 pos, Vector3 size, string material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name; go.transform.SetParent(parent, false);
        go.transform.localPosition = pos; go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/@Resource/Materials/FactoryFlow/" + material);
    }
}
#endif
