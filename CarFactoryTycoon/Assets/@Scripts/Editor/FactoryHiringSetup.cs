#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Creates the generated on-foot hiring placards without changing the source scene by hand.</summary>
public static class FactoryHiringSetup
{
    const string MainScenePath = "Assets/Car_Factory/DemoScene/Car_Factory 3.unity";
    const string PostsRootName = "FactoryHiringPosts";

    [MenuItem("Car Factory/Prototype/Setup Hiring Posts")]
    public static string Apply()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Edit mode required.");
        var scene = SceneManager.GetActiveScene();
        if (scene.path != MainScenePath) throw new InvalidOperationException("Open the factory main scene first.");

        var progression = UnityEngine.Object.FindFirstObjectByType<ProductionProgression>();
        var player = UnityEngine.Object.FindFirstObjectByType<FactoryPlayerController>();
        if (progression == null || progression.processes == null || player == null)
            throw new InvalidOperationException("M1 and M2 setup are required first.");

        var oldRoot = progression.transform.Find(PostsRootName);
        if (oldRoot != null) Undo.DestroyObjectImmediate(oldRoot.gameObject);
        var rootObject = new GameObject(PostsRootName);
        Undo.RegisterCreatedObjectUndo(rootObject, "Create hiring placards");
        var root = rootObject.transform;
        root.SetParent(progression.transform, false);

        var ui = progression.GetComponent<FactoryHiringUI>();
        if (ui == null) ui = Undo.AddComponent<FactoryHiringUI>(progression.gameObject);
        ui.Configure(progression, player, root);

        var signMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/@Resource/Materials/MatFloorConcreteGrid.mat");
        for (int i = 0; i < progression.processes.Length; i++)
        {
            var process = progression.processes[i];
            if (process == null) continue;
            var post = new GameObject($"Post_{i:D2}");
            post.transform.SetParent(root, true);
            post.transform.position = GetPostPosition(process);
            post.transform.rotation = GetPostRotation(process);
            var selectBox = post.AddComponent<BoxCollider>();
            selectBox.center = new Vector3(0f, .73f, 0f);
            selectBox.size = new Vector3(.72f, 1.45f, .48f);

            AddBlock(post.transform, "Base", new Vector3(0f, .05f, 0f), new Vector3(.52f, .1f, .38f), signMaterial);
            AddBlock(post.transform, "Stem", new Vector3(0f, .61f, 0f), new Vector3(.075f, 1.05f, .075f), signMaterial);
            AddBlock(post.transform, "Placard", new Vector3(0f, 1.18f, 0f), new Vector3(.55f, .38f, .08f), signMaterial);
            AddIndexLabel(post.transform, i + 1);
        }

        EditorUtility.SetDirty(ui);
        EditorUtility.SetDirty(progression);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        return $"Created {progression.processes.Length} on-foot hiring placards and attached the hiring panel.";
    }

    static Vector3 GetPostPosition(ProductionProgression.ProcessEntry process)
    {
        Vector3 home = process.humanRoot != null ? process.humanRoot.transform.position : Vector3.zero;
        if (process.workers != null)
            foreach (var worker in process.workers)
                if (worker != null) { home = worker.transform.position; break; }

        Vector3 away = process.workers != null && process.workers.Length > 0 && process.workers[0] != null
            ? -process.workers[0].transform.forward
            : process.humanRoot != null ? -process.humanRoot.transform.forward : Vector3.back;
        away.y = 0f;
        if (away.sqrMagnitude < .01f) away = Vector3.back;
        return home + away.normalized * 1.35f;
    }

    static Quaternion GetPostRotation(ProductionProgression.ProcessEntry process)
    {
        var basis = process.workers != null && process.workers.Length > 0 && process.workers[0] != null
            ? process.workers[0].transform : process.humanRoot != null ? process.humanRoot.transform : null;
        return basis != null ? Quaternion.Euler(0f, basis.eulerAngles.y, 0f) : Quaternion.identity;
    }

    static void AddBlock(Transform parent, string name, Vector3 localPosition, Vector3 localScale, Material material)
    {
        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = name;
        block.transform.SetParent(parent, false);
        block.transform.localPosition = localPosition;
        block.transform.localScale = localScale;
        var collider = block.GetComponent<Collider>();
        if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
        if (material != null) block.GetComponent<MeshRenderer>().sharedMaterial = material;
    }

    static void AddIndexLabel(Transform parent, int index)
    {
        var go = new GameObject("Index");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, 1.18f, -.046f);
        var text = go.AddComponent<TextMesh>();
        text.text = index.ToString("D2");
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.fontSize = 32;
        text.characterSize = .12f;
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font != null) text.font = font;
    }
}
#endif
