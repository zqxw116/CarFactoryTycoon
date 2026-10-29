#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class FactoryCharacterAnimationSetup
{
    const string Path = "Assets/@Resource/Animations/FactoryPlayer.controller";
    const string Legacy = "Assets/UnityTechnologies/SpaceRobotKyle/Animations/";
    const string Mixamo = "Assets/@Resource/Animations/Characters/Worker/";

    public static string[] MissingMixamoClips() => new[] { "Worker_Idle", "Worker_Walk", "Worker_Walk_Backward", "Worker_Strafe_Left", "Worker_Strafe_Right", "Worker_Run", "Worker_Run_Backward", "Worker_Run_Left", "Worker_Run_Right", "Worker_Jump_Takeoff", "Worker_Jump_Air", "Worker_Jump_Land", "Worker_Hammer" }
        .Where(name => !AssetDatabase.LoadAllAssetsAtPath(Mixamo + name + ".fbx").OfType<AnimationClip>().Any(c => !c.name.StartsWith("__preview"))).ToArray();

    public static RuntimeAnimatorController BuildPlayerController()
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(Path);
        if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(Path);
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(Path))
            if (asset != controller) UnityEngine.Object.DestroyImmediate(asset, true);
        controller.layers = Array.Empty<AnimatorControllerLayer>();
        controller.parameters = Array.Empty<AnimatorControllerParameter>();
        foreach (var name in new[] { "MoveX", "MoveZ", "Speed", "MotionSpeed" }) controller.AddParameter(name, AnimatorControllerParameterType.Float);
        foreach (var name in new[] { "Grounded", "Jump", "FreeFall" }) controller.AddParameter(name, AnimatorControllerParameterType.Bool);
        controller.AddLayer("Locomotion");
        var layers = controller.layers; layers[0].iKPass = true; controller.layers = layers;
        var machine = layers[0].stateMachine;
        var locomotion = machine.AddState("Directional Locomotion");
        machine.defaultState = locomotion;
        var tree = new BlendTree { name = "Walk Run Forward Back Strafe", blendType = BlendTreeType.FreeformDirectional2D,
            blendParameter = "MoveX", blendParameterY = "MoveZ", useAutomaticThresholds = false };
        AssetDatabase.AddObjectToAsset(tree, controller); locomotion.motion = tree;
        var idle = Clip("Worker_Idle", "Stand--Idle.anim.fbx");
        var walk = Clip("Worker_Walk", "Locomotion--Walk_N.anim.fbx");
        var run = Clip("Worker_Run", "Locomotion--Run_N.anim.fbx");
        var settings = AssetDatabase.LoadAssetAtPath<PrototypeBalanceSettings>("Assets/@Scripts/Data/PrototypeBalanceSettings.asset");
        float walkSpeed = settings != null ? settings.playerWalkSpeed : 3.4f;
        float runSpeed = settings != null ? settings.playerSprintSpeed : 6f;
        tree.AddChild(idle, Vector2.zero);
        tree.AddChild(walk, new Vector2(0, walkSpeed));
        tree.AddChild(Clip("Worker_Walk_Backward", "Locomotion--Run_S.anim.fbx"), new Vector2(0, -walkSpeed));
        tree.AddChild(Clip("Worker_Strafe_Left", "Locomotion--Walk_N.anim.fbx"), new Vector2(-walkSpeed, 0));
        tree.AddChild(Clip("Worker_Strafe_Right", "Locomotion--Walk_N.anim.fbx"), new Vector2(walkSpeed, 0));
        tree.AddChild(run, new Vector2(0, runSpeed));
        tree.AddChild(Clip("Worker_Run_Backward", "Locomotion--Run_S.anim.fbx"), new Vector2(0, -runSpeed));
        tree.AddChild(Clip("Worker_Run_Left", "Locomotion--Run_N.anim.fbx"), new Vector2(-runSpeed, 0));
        tree.AddChild(Clip("Worker_Run_Right", "Locomotion--Run_N.anim.fbx"), new Vector2(runSpeed, 0));
        var jump = machine.AddState("Takeoff"); jump.motion = Clip("Worker_Jump_Takeoff", "Jump--Jump.anim.fbx");
        var air = machine.AddState("Airborne"); air.motion = Clip("Worker_Jump_Air", "Jump--InAir.anim.fbx");
        var land = machine.AddState("Grounded Landing"); land.motion = Clip("Worker_Jump_Land", "Locomotion--Run_N_Land.anim.fbx");
        var takeoff = locomotion.AddTransition(jump); Configure(takeoff); takeoff.AddCondition(AnimatorConditionMode.If, 0, "Jump");
        var falling = locomotion.AddTransition(air); Configure(falling); falling.AddCondition(AnimatorConditionMode.IfNot, 0, "Grounded"); falling.AddCondition(AnimatorConditionMode.IfNot, 0, "Jump");
        var flight = jump.AddTransition(air); Configure(flight); flight.hasExitTime = true; flight.exitTime = .85f;
        foreach (var state in new[] { jump, air })
        {
            var grounded = state.AddTransition(land); Configure(grounded); grounded.AddCondition(AnimatorConditionMode.If, 0, "Grounded"); grounded.AddCondition(AnimatorConditionMode.IfNot, 0, "Jump");
        }
        var resume = land.AddTransition(locomotion); Configure(resume); resume.hasExitTime = true; resume.exitTime = .8f;
        var repeatJump = land.AddTransition(jump); Configure(repeatJump); repeatJump.AddCondition(AnimatorConditionMode.If, 0, "Jump");
        var ledge = land.AddTransition(air); Configure(ledge); ledge.AddCondition(AnimatorConditionMode.IfNot, 0, "Grounded"); ledge.AddCondition(AnimatorConditionMode.IfNot, 0, "Jump");
        AddHammerLayer(controller); EditorUtility.SetDirty(controller); return controller;
    }
    public static RuntimeAnimatorController BuildWorkerController()
    {
        const string path = "Assets/@Resource/Animations/WorkerAnimator.controller";
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(path);
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            if (asset != controller) UnityEngine.Object.DestroyImmediate(asset, true);
        controller.layers = Array.Empty<AnimatorControllerLayer>();
        controller.parameters = Array.Empty<AnimatorControllerParameter>();
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.AddParameter("MotionSpeed", AnimatorControllerParameterType.Float);
        controller.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
        controller.AddLayer("Locomotion");
        var layers = controller.layers; layers[0].iKPass = true; controller.layers = layers;
        var state = layers[0].stateMachine.AddState("Idle Walk Run");
        layers[0].stateMachine.defaultState = state;
        var tree = new BlendTree { name = "Worker speed", blendType = BlendTreeType.Simple1D,
            blendParameter = "Speed", useAutomaticThresholds = false };
        AssetDatabase.AddObjectToAsset(tree, controller); state.motion = tree;
        tree.AddChild(Clip("Worker_Idle", "Stand--Idle.anim.fbx"), 0f);
        tree.AddChild(Clip("Worker_Walk", "Locomotion--Walk_N.anim.fbx"), 1.7f);
        tree.AddChild(Clip("Worker_Run", "Locomotion--Run_N.anim.fbx"), 4f);
        AddHammerLayer(controller); EditorUtility.SetDirty(controller); return controller;
    }
    static void AddHammerLayer(AnimatorController controller)
    {
        var clip = AssetDatabase.LoadAllAssetsAtPath(Mixamo + "Worker_Hammer.fbx")
            .OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
        if (clip == null) { Debug.LogWarning("Worker_Hammer.fbx missing: hammer playback unavailable."); return; }
        var mask = new AvatarMask { name = "Worker upper body" };
        for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i, false);
        foreach (var part in new[] { AvatarMaskBodyPart.Body, AvatarMaskBodyPart.LeftArm,
            AvatarMaskBodyPart.RightArm, AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers })
            mask.SetHumanoidBodyPartActive(part, true);
        AssetDatabase.AddObjectToAsset(mask, controller);
        controller.AddLayer("Hammer");
        var layers = controller.layers; var layer = layers[layers.Length - 1];
        layer.avatarMask = mask; layer.defaultWeight = 0f; layer.iKPass = true; controller.layers = layers;
        var idle = layer.stateMachine.AddState("Ready"); idle.writeDefaultValues = false; layer.stateMachine.defaultState = idle;
        var strike = layer.stateMachine.AddState("Strike"); strike.motion = clip; strike.writeDefaultValues = false;
        strike.speed = clip.length / WorkerHammerAnimation.Duration;
        var done = strike.AddTransition(idle); Configure(done); done.hasExitTime = true; done.exitTime = .98f;
    }
    static void Configure(AnimatorStateTransition transition)
    {
        transition.hasExitTime = false; transition.hasFixedDuration = true; transition.duration = .08f;
        transition.canTransitionToSelf = false;
    }
    static AnimationClip Clip(string name, string fallback)
    {
        var path = Mixamo + name + ".fbx";
        var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
        if (clip != null) return clip;
        Debug.LogWarning($"Missing worker clip {path}; temporary legacy fallback {fallback}.");
        return AssetDatabase.LoadAllAssetsAtPath(Legacy + fallback).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview"));
    }
}
#endif
