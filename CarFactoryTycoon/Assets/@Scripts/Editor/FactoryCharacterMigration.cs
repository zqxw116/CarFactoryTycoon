#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Replace only actor visuals. Gameplay roots and their scene references survive.</summary>
public static class FactoryCharacterMigration
{
    const string Folder = "Assets/@Resource/Prefabs/Characters/";
    [MenuItem("Tools/CarFactory/Characters/Migrate Factory Characters")]
    public static void Run() => Debug.Log(Apply());

    public static string Apply()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Edit mode required.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != "Assets/Car_Factory/DemoScene/Car_Factory 3.unity")
            throw new InvalidOperationException("Open the factory main scene first.");
        var report = new StringBuilder();
        var player = Object.FindFirstObjectByType<FactoryPlayerController>(FindObjectsInactive.Include);
        if (player == null) throw new InvalidOperationException("FactoryPlayer is missing.");
        ValidatePrefab("Worker_Base"); ValidatePrefab("Worker_Helmet");
        var playerAnimator = ReplaceVisual(player.gameObject, "Worker_Base", 10, report);
        playerAnimator.runtimeAnimatorController = FactoryCharacterAnimationSetup.BuildPlayerController();
        player.bodyAnimator = playerAnimator;
        var interactor = player.GetComponent<PlayerAssemblyInteractor>();
        interactor.bodyAnimator = playerAnimator;
        interactor.hammerAnchor = playerAnimator.GetBoneTransform(HumanBodyBones.RightHand);
        var playerIK = playerAnimator.GetComponent<PlayerAssemblyHandIK>();
        if (playerIK == null) playerIK = playerAnimator.gameObject.AddComponent<PlayerAssemblyHandIK>();
        playerIK.interactor = interactor;
        var cc = player.GetComponent<CharacterController>();
        report.AppendLine($"Player capsule {cc.height:F3}/{cc.radius:F3}/{cc.center} -> 1.75/0.28/(0,0.875,0)");
        cc.height = 1.75f; cc.radius = .28f; cc.center = Vector3.up * .875f;
        EditorUtility.SetDirty(player); EditorUtility.SetDirty(interactor); EditorUtility.SetDirty(cc);

        // Include inactive hired workers and supply workers, but never process their parent station.
        var roots = Object.FindObjectsByType<Worker>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Select(w => w.gameObject).Concat(Object.FindObjectsByType<SupplyWorker>(FindObjectsInactive.Include,
                FindObjectsSortMode.None).Select(w => w.gameObject)).Distinct().ToArray();
        foreach (var root in roots)
        {
            var actor = ReplaceVisual(root, "Worker_Helmet", 8, report);
            actor.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/@Resource/Animations/WorkerAnimator.controller");
            var worker = root.GetComponent<Worker>();
            if (worker != null)
            {
                var oldBox = root.GetComponent<BoxCollider>();
                var oldSize = oldBox != null ? oldBox.size : Vector3.zero;
                float oldHeight = worker.handHeight, oldForward = worker.handForward;
                if (!WorkerSetupTool.SetupOne(root)) throw new InvalidOperationException("Worker setup failed: " + root.name);
                BindAnimator(worker, actor);
                report.AppendLine($"{root.name}: collider {oldSize} -> {root.GetComponent<BoxCollider>().size}; handHeight {oldHeight:F3} -> {worker.handHeight:F3}; handForward {oldForward:F3} -> {worker.handForward:F3}");
            }
            var supply = root.GetComponent<SupplyWorker>();
            if (supply != null)
            {
                var hand = actor.GetBoneTransform(HumanBodyBones.RightHand);
                var anchor = hand.Find("HandPos");
                if (anchor == null) anchor = new GameObject("HandPos").transform;
                anchor.SetParent(hand, false); anchor.position = hand.position + root.transform.forward * .05f;
                anchor.rotation = root.transform.rotation;
                supply.handPos = anchor; BindAnimator(supply, actor);
                EditorUtility.SetDirty(supply);
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 8;
            EditorUtility.SetDirty(actor);
        }
        int fuelCount=0;
        foreach(var fuel in Object.FindObjectsByType<FuelStation>(FindObjectsInactive.Include,FindObjectsSortMode.None))
        {
            // FuelStation.attendant is the gameplay actor; Geometry/KyleRobot demo meshes are unrelated.
            if(fuel.attendant==null)continue;
            var root=fuel.attendant.gameObject;
            var actor=ReplaceVisual(root,"Worker_Helmet",8,report);
            actor.runtimeAnimatorController=AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/@Resource/Animations/WorkerAnimator.controller");
            fuel.attendantAnimator=actor;
            var ik=actor.GetComponent<WorkerHandIK>();if(ik==null)ik=actor.gameObject.AddComponent<WorkerHandIK>();
            fuel.handIK=ik;
            if(fuel.attendantMover==null)fuel.attendantMover=root.GetComponent<NavMover>();
            var body=root.GetComponent<BoxCollider>();if(body==null)body=root.AddComponent<BoxCollider>();
            body.isTrigger=false;body.center=new Vector3(0,.875f,0);body.size=new Vector3(.56f,1.75f,.50f);
            foreach(var t in root.GetComponentsInChildren<Transform>(true))t.gameObject.layer=8;
            EditorUtility.SetDirty(fuel);EditorUtility.SetDirty(actor);EditorUtility.SetDirty(ik);EditorUtility.SetDirty(body);
            report.AppendLine(root.name+": fuel gameplay root/NavMover preserved, Animator+hand IK rebound, layer8 body collider set.");fuelCount++;
        }
        EditorSceneManager.MarkSceneDirty(scene);
        AssetDatabase.SaveAssets(); EditorSceneManager.SaveScene(scene);
        report.AppendLine($"Migrated player + {roots.Length} NPC roots + {fuelCount} fuel attendants; gameplay roots retained; player=10/NPC=8.");
        return report.ToString();
    }

    public static string Validate()
    {
        var player = Object.FindFirstObjectByType<FactoryPlayerController>(FindObjectsInactive.Include);
        if (player == null || player.bodyAnimator == null || !player.bodyAnimator.isHuman)
            throw new InvalidOperationException("Player Humanoid missing.");
        if (player.GetComponent<PlayerAssemblyInteractor>().hammerAnchor != player.bodyAnimator.GetBoneTransform(HumanBodyBones.RightHand))
            throw new InvalidOperationException("Player hammer anchor is not the new right hand.");
        int count = 0;
        foreach (var worker in Object.FindObjectsByType<Worker>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var actor = worker.GetComponentInChildren<Animator>(true);
            if (actor == null || !actor.isHuman || worker.handIK == null || worker.handPos == null ||
                !worker.handPos.IsChildOf(actor.GetBoneTransform(HumanBodyBones.RightHand)))
                throw new InvalidOperationException("Worker rig binding invalid: " + worker.name);
            if (worker.GetComponentsInChildren<Transform>(true).Any(t => t.gameObject.layer != 8))
                throw new InvalidOperationException("Worker layer invalid: " + worker.name);
            count++;
        }
        foreach(var fuel in Object.FindObjectsByType<FuelStation>(FindObjectsInactive.Include,FindObjectsSortMode.None))
        {
            if(fuel.attendant==null)continue;
            var actor=fuel.attendant.GetComponentInChildren<Animator>(true);
            if(actor==null || !actor.isHuman || fuel.attendantAnimator!=actor || fuel.handIK==null || fuel.handIK.gameObject!=actor.gameObject ||
                fuel.attendant.GetComponent<BoxCollider>()==null || fuel.attendant.GetComponentsInChildren<Transform>(true).Any(t=>t.gameObject.layer!=8))
                throw new InvalidOperationException("Fuel attendant rig binding/layer/body invalid: "+fuel.name);
        }
        if (player.GetComponentsInChildren<Transform>(true).Any(t => t.gameObject.layer != 10))
            throw new InvalidOperationException("Player layer invalid.");
        return $"PASS: Player Humanoid/right-hand anchor/layer10; {count} workers Humanoid/hand IK/anchor/layer8.";
    }

    static void BindAnimator(MonoBehaviour target, Animator actor)
    {
        var serialized = new SerializedObject(target);
        var field = serialized.FindProperty("animator");
        if (field != null) { field.objectReferenceValue = actor; serialized.ApplyModifiedPropertiesWithoutUndo(); }
    }

    static void ValidatePrefab(string name)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + name + ".prefab");
        var actor = prefab != null ? prefab.GetComponentInChildren<Animator>(true) : null;
        if (actor == null || actor.avatar == null || !actor.avatar.isValid || !actor.avatar.isHuman)
            throw new InvalidOperationException(name + " requires a valid Humanoid Avatar.");
    }

    static Animator ReplaceVisual(GameObject root, string modelName, int layer, StringBuilder report)
    {
        var existing = root.transform.Find("CharacterVisual");
        if (existing != null && existing.GetComponentInChildren<Animator>(true) is Animator current &&
            current.avatar != null && current.avatar.name.StartsWith(modelName, StringComparison.Ordinal))
            return current;
        if (PrefabUtility.IsPartOfPrefabInstance(root))
            PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetOutermostPrefabInstanceRoot(root), PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        var oldActors = root.GetComponentsInChildren<Animator>(true);
        var oldRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        // Only skeleton/mesh branches are removed. Cameras, gameplay anchors and scripts remain.
        var branches = new HashSet<GameObject>();
        foreach (var actor in oldActors)
        {
            if (!actor.isHuman) continue;
            var hips = actor.GetBoneTransform(HumanBodyBones.Hips);
            if (hips != null) AddBranch(root.transform, hips, branches);
        }
        foreach (var renderer in oldRenderers) AddBranch(root.transform, renderer.transform, branches);
        // The legacy guide worker has a primitive Capsule instead of a skinned model.
        var guide = root.transform.Find("Capsule");
        if (guide != null && guide.GetComponent<MeshRenderer>() != null) branches.Add(guide.gameObject);
        foreach (var branch in branches) Object.DestroyImmediate(branch);
        foreach (var actor in oldActors)
        {
            if (actor == null) continue;
            foreach (var ik in actor.GetComponents<WorkerHandIK>()) Object.DestroyImmediate(ik);
            foreach (var ik in actor.GetComponents<PlayerAssemblyHandIK>()) Object.DestroyImmediate(ik);
            foreach (var ik in actor.GetComponents<PrototypeCrewHandIK>()) Object.DestroyImmediate(ik);
            Object.DestroyImmediate(actor);
        }
        var visual = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Folder + modelName + ".prefab"), root.transform);
        visual.name = "CharacterVisual";
        visual.transform.localPosition = Vector3.zero; visual.transform.localRotation = Quaternion.identity; visual.transform.localScale = Vector3.one;
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        foreach (var collider in visual.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        var animator = visual.GetComponentInChildren<Animator>(true);
        animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        if (animator.GetComponent<WorkerFootstepReceiver>() == null) animator.gameObject.AddComponent<WorkerFootstepReceiver>();
        report.AppendLine(root.name + ": RobotKyle/old visual -> " + modelName + "; Avatar=" + animator.avatar.name);
        return animator;
    }

    static void AddBranch(Transform root, Transform child, HashSet<GameObject> branches)
    {
        while (child.parent != null && child.parent != root) child = child.parent;
        if (child != root && child.parent == root) branches.Add(child.gameObject);
    }
}
#endif
