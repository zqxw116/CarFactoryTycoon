#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class FactoryProcessAuthoringChecks
{
    static void Need(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public static string CheckCameraBasis()
    {
        var player = Object.FindFirstObjectByType<FactoryPlayerController>(FindObjectsInactive.Include);
        Need(player != null, "Factory player missing.");
        float yaw = player.ViewYaw;
        try
        {
            var before = player.ResolveMoveDirection(Vector2.up);
            player.RotateView(new Vector2(180, 0));
            Need(Vector3.Dot(before, player.ResolveMoveDirection(Vector2.up)) < -.99f, "Held W must follow new view yaw.");
            Need(Vector3.Dot(player.ResolveMoveDirection(Vector2.up), player.ResolveMoveDirection(Vector2.down)) < -.99f, "S uses opposite view direction.");
            Need(player.ResolveMoveDirection(Vector2.one).magnitude <= 1.0001f, "Diagonal input cannot move faster.");
            Need(player.ResolveMoveDirection(Vector2.zero) == Vector3.zero, "No input means no translation.");
            Need(player.lookTurnSpeed > player.movementTurnSpeed, "Right-drag facing must be faster.");
            return "PASS: held W follows live yaw; S/diagonal/idle input; faster look facing.";
        }
        finally { player.RotateView(new Vector2(Mathf.DeltaAngle(player.ViewYaw, yaw), 0)); }
    }
    public static string CheckRobotWorkers()
    {
        var p = Object.FindFirstObjectByType<ProductionProgression>(FindObjectsInactive.Include);
        Need(p != null, "Production missing.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FactoryWorkerRobotSetup.RobotPrefab);
        var avatar = prefab.GetComponentInChildren<Animator>(true).avatar;
        var seen = new HashSet<Worker>();
        foreach (var entry in p.processes) if (entry.workers != null) foreach (var w in entry.workers)
        {
            if (w == null || !seen.Add(w)) continue;
            var a = w.GetComponentInChildren<Animator>(true);
            Need(a != null && a.avatar == avatar && a.isHuman && a.humanScale > .5f && a.humanScale < 2f, "Robot Avatar mismatch: " + w.name);
            Need(w.handPos != null && w.handPos.IsChildOf(a.GetBoneTransform(HumanBodyBones.RightHand)), "Hand anchor mismatch: " + w.name);
            Need(w.handIK != null && w.handIK.gameObject == a.gameObject, "IK mismatch: " + w.name);
            Need(w.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0, "Robot renderer missing.");
            foreach (var b in w.GetComponentsInChildren<Behaviour>(true))
                Need(b.GetType().FullName != "StarterAssets.ThirdPersonController", "Worker must remove demo controller: animation events invoke disabled scripts.");
        }
        return "PASS: " + seen.Count + " process workers RobotKyle/scale/hand/IK/renderers.";
    }
}
#endif
