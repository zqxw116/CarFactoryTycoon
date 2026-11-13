#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Rebind hired process actors while keeping gameplay roots and all source art.</summary>
public static class FactoryWorkerRobotSetup
{
    public const string RobotPrefab = "Assets/UnityTechnologies/SpaceRobotKyle/Prefabs/RobotKyle.prefab";
    [MenuItem("Tools/CarFactory/Characters/Use Robot Process Workers")]
    public static void Run() => Debug.Log(Apply());

    public static void DisableDemoControls(GameObject visual)
    {
        foreach (var behaviour in visual.GetComponentsInChildren<Behaviour>(true))
        {
            string name = behaviour.GetType().FullName;
            if (name == "StarterAssets.ThirdPersonController" || name == "StarterAssets.StarterAssetsInputs" || name == "UnityEngine.InputSystem.PlayerInput")
            { Undo.DestroyObjectImmediate(behaviour); continue; }
            if (name != null && (name.StartsWith("StarterAssets.", StringComparison.Ordinal) ||
                name == "UnityEngine.InputSystem.PlayerInput"))
            { Undo.RecordObject(behaviour, "Disable Demo Controls"); behaviour.enabled = false; EditorUtility.SetDirty(behaviour); }
        }
    }

    public static Animator NormalizeVisual(Worker worker)
    {
        var actors=worker.GetComponentsInChildren<Animator>(true);
        Animator model=null;
        foreach(var actor in actors)
            if(actor.isHuman && actor.avatar!=null && actor.avatar.name=="KyleRobotAvatar" && actor.GetComponentsInChildren<Animator>(true).Length==1)
            {model=actor;break;}
        if(model==null)throw new InvalidOperationException("Robot skeleton Animator missing: "+worker.name);
        RuntimeAnimatorController controller=null;
        foreach(var actor in actors)if(actor.runtimeAnimatorController!=null){controller=actor.runtimeAnimatorController;break;}
        foreach(var actor in actors)if(actor!=model)
        {
            foreach(var rig in actor.GetComponents<WorkerInteractionRig>())Undo.DestroyObjectImmediate(rig);
            foreach(var builder in actor.GetComponents<UnityEngine.Animations.Rigging.RigBuilder>())Undo.DestroyObjectImmediate(builder);
            foreach(var ik in actor.GetComponents<WorkerHandIK>())Undo.DestroyObjectImmediate(ik);
            var children=new System.Collections.Generic.List<GameObject>();
            foreach(Transform child in actor.transform)
                if(child.name=="ProcessInteractionRig" || child.name.StartsWith("ProcessLeft",StringComparison.Ordinal) || child.name.StartsWith("ProcessRight",StringComparison.Ordinal))children.Add(child.gameObject);
            foreach(var child in children)Undo.DestroyObjectImmediate(child);
            Undo.DestroyObjectImmediate(actor);
        }
        model.runtimeAnimatorController=controller!=null?controller:AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/@Resource/Animations/WorkerAnimator.controller");
        model.applyRootMotion=false;model.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        if(model.GetComponent<WorkerFootstepReceiver>()==null)Undo.AddComponent<WorkerFootstepReceiver>(model.gameObject);
        if(!WorkerSetupTool.SetupOne(worker.gameObject))throw new InvalidOperationException("Robot skeleton rebinding failed.");
        var serialized=new SerializedObject(worker);serialized.FindProperty("animator").objectReferenceValue=model;serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(model);EditorUtility.SetDirty(worker);return model;
    }

    public static string Apply()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Edit mode required.");
        var progression = Object.FindFirstObjectByType<ProductionProgression>(FindObjectsInactive.Include);
        if (progression == null) throw new InvalidOperationException("ProductionProgression missing.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RobotPrefab);
        var template = prefab != null ? prefab.GetComponentInChildren<Animator>(true) : null;
        if (template == null || !template.isHuman || template.avatar == null || !template.avatar.isValid)
            throw new InvalidOperationException("RobotKyle Humanoid missing.");
        var workers = new HashSet<Worker>();
        foreach (var entry in progression.processes)
            if (entry.workers != null) foreach (var worker in entry.workers) if (worker != null) workers.Add(worker);
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Use Robot Process Workers");
        int changed = 0;
        GameObject archive = null;
        foreach (var sceneRoot in progression.gameObject.scene.GetRootGameObjects())
            if (sceneRoot.name == "RetiredWorkerVisuals") archive = sceneRoot;
        try
        {
            foreach (var worker in workers)
            {
                var root = worker.gameObject;
                var current = root.GetComponentInChildren<Animator>(true);
                if (current != null && current.avatar == template.avatar)
                { DisableDemoControls(root); NormalizeVisual(worker); continue; }
                var oldVisual = root.transform.Find("CharacterVisual");
                if (oldVisual == null) throw new InvalidOperationException("Expected CharacterVisual on " + root.name);
                // Keep the previous scene visual for rollback; never alter/delete FBX or prefab assets.
                if (archive == null)
                {
                    archive = new GameObject("RetiredWorkerVisuals");
                    Undo.RegisterCreatedObjectUndo(archive, "Archive Previous Worker Visuals");
                    archive.SetActive(false);
                }
                Undo.SetTransformParent(oldVisual, archive.transform, "Preserve Previous Worker Visual");
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
                Undo.RegisterCreatedObjectUndo(visual, "Create Robot Worker Visual");
                visual.name = "CharacterVisual";
                DisableDemoControls(visual);
                visual.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                visual.transform.localScale = Vector3.one;
                var animator = visual.GetComponentInChildren<Animator>(true);
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/@Resource/Animations/WorkerAnimator.controller");
                foreach (var t in visual.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 8;
                foreach (var c in visual.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                animator=NormalizeVisual(worker);
                if (!WorkerSetupTool.SetupOne(root)) throw new InvalidOperationException("Worker setup failed: " + root.name);
                var serialized = new SerializedObject(worker);
                serialized.FindProperty("animator").objectReferenceValue = animator;
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(worker);
                changed++;
            }
            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(progression.gameObject.scene);
            return "Process workers: " + workers.Count + "; RobotKyle rebound: " + changed + ". Scene not automatically saved.";
        }
        catch { Undo.RevertAllDownToGroup(group); throw; }
    }
}
#endif
