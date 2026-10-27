#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Play-mode technical checks. Teleport deliberately isolates interaction APIs from natural traversal.
/// Start from an idle player's empty hands. The assembled FullCar stays on the stopped line for inspection tests.
/// </summary>
public static class FactoryAssemblyM2Checks
{
    public static string State = "idle";
    public static string Results = "";
    public static CarController FullCar;
    private static FactoryPlayerController player;
    private static PlayerAssemblyInteractor interactor;
    private static CarController lockCar;
    private static bool wasEnabled, wasInputEnabled;
    private static Vector3 originalPosition;
    private static readonly StringBuilder log = new StringBuilder();

    public static void Start()
    {
        if (!Application.isPlaying) { State = "failed"; Results = "Requires Play mode."; return; }
        if (State == "running") return;
        player = UnityEngine.Object.FindFirstObjectByType<FactoryPlayerController>();
        interactor = player != null ? player.interactor : null;
        if (player == null || interactor == null || PrototypeLineController.Current == null || CarPool.Instance == null)
        { State = "failed"; Results = "Player/interactor/line/car pool missing."; return; }
        if (interactor.HeldPart != null || interactor.IsAttaching)
        { State = "failed"; Results = "Start with empty hands and no attachment in progress."; return; }
        State = "running"; Results = "Technical API checks running (movement uses Teleport).";
        log.Clear(); FullCar = null; lockCar = null;
        wasEnabled = player.enabled; wasInputEnabled = interactor.inputEnabled; originalPosition = player.transform.position;
        player.enabled = false; interactor.inputEnabled = false;
        player.StartCoroutine(GuardedRun());
    }

    private static IEnumerator GuardedRun()
    {
        var routine = Run();
        float started = Time.realtimeSinceStartup;
        bool success = true;
        while (true)
        {
            object wait = null;
            bool more = false;
            try
            {
                Require(Time.realtimeSinceStartup - started < 120f, "120-second timeout");
                more = routine.MoveNext();
                if (more) wait = routine.Current;
            }
            catch (Exception exception)
            {
                success = false;
                log.AppendLine("FAIL: " + exception.Message);
                break;
            }
            if (!more) break;
            yield return wait;
        }
        (routine as IDisposable)?.Dispose();
        if (lockCar != null) { CarPool.Instance.Return(lockCar); lockCar = null; }
        if (interactor != null) { interactor.SetPlayerHammerHits(3); interactor.inputEnabled = wasInputEnabled; }
        if (player != null) { player.Teleport(originalPosition); player.enabled = wasEnabled; }
        log.AppendLine("Traversal was teleported; walking/jumping/camera feel not evaluated here.");
        Results = log.ToString();
        State = success ? "passed" : "failed";
        Debug.Log("[FactoryAssemblyM2Checks] " + State + "\n" + Results);
    }

    private static IEnumerator Run()
    {
        var line = PrototypeLineController.Current;
        Require(line.spawner != null && line.spawner.mainLineSpline != null, "Main spline missing");
        Require(Time.timeScale > 0f, "Time scale must be positive for attachment Update");
        line.SetLever(PrototypeLineController.LeverPosition.Stop);
        var spline = line.spawner.mainLineSpline;
        float start = StationWorkZone.ProgressAt(spline, line.spawner.transform.position);
        FullCar = CarPool.Instance.Get();
        FullCar.SetPath(spline, 0f, start);
        if (ProductionProgression.Current != null) ProductionProgression.Current.Stamp(FullCar);
        var required = new List<AssemblyPart>();
        foreach (var part in FullCar.GetComponentsInChildren<AssemblyPart>(true))
            if (part.myType != PartType.None && part.myType != PartType.Frame_1) required.Add(part);
        Require(required.Count == 26, "Expected 26 non-frame parts; got " + required.Count);
        var stacks = UnityEngine.Object.FindObjectsByType<PartStack>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        int totalHits = 0;
        for (int index = 0; index < required.Count; index++)
        {
            var part = required[index];
            PartStack stack = null;
            foreach (var candidate in stacks)
                if (candidate.partType == part.myType && candidate.Count > 0) { stack = candidate; break; }
            Require(stack != null, "No stocked source for " + part.myType);
            player.Teleport(stack.transform.position - Vector3.up * .4f);
            Physics.SyncTransforms();
            int before = stack.Count;
            Require(interactor.TryTake(stack), "Take failed: " + part.myType);
            Require(stack.Count == before - 1, "Take must consume exactly one: " + part.myType);
            Require(!interactor.TryTake(stack) && stack.Count == before - 1, "Second carried item accepted or stock changed");
            var portable = interactor.HeldPart;
            Require(portable != null && portable.PartType == part.myType, "Wrong portable type");
            if (index == 0)
            {
                Require(interactor.TryThrow(), "Throw failed");
                Require(interactor.HeldPart == null && !portable.Body.isKinematic && portable.Body.useGravity && portable.Body.detectCollisions,
                    "Throw did not enable dynamic physics");
                Require(portable.PhysicsCollider.enabled && portable.gameObject.layer == 9 &&
                    portable.Body.collisionDetectionMode == CollisionDetectionMode.ContinuousSpeculative, "Throw collider/layer/CCD incorrect");
                foreach (var collider in portable.GetComponentsInChildren<Collider>(true))
                    Require(collider == portable.PhysicsCollider || !collider.enabled, "Portable kept a model collider enabled");
                player.Teleport(portable.transform.position - Vector3.up * 1.1f);
                Require(interactor.TryPickUp(portable), "Pick-up failed");
                Require(portable.Body.isKinematic && !portable.Body.useGravity && !portable.Body.detectCollisions && !portable.PhysicsCollider.enabled,
                    "Pick-up did not disable physics");
                Require(stack.Count == before - 1, "Pick-up consumed additional stock");
                log.AppendLine("Stock -1 / one-item limit / throw CCD+Parts9 / pick-up without extra stock: passed.");
            }
            NearPart(part);
            int hitCount = index == 1 ? 2 : 3;
            interactor.SetPlayerHammerHits(hitCount);
            Require(interactor.TryAttach(FullCar), "Attachment start failed: " + part.myType);
            Require(interactor.IsAttaching && !part.IsAssembled, "Attachment skipped phase");
            yield return new WaitForSeconds(.65f);
            Require(!interactor.IsAttaching && interactor.HeldPart == null && part.IsPlayerAttached && !part.IsAssembled,
                "Attachment did not finish at unfastened state: " + part.myType);
            // Wait in game time; editor/API stalls need not match wall-clock time.
            for (int hit = 0; hit < hitCount; hit++)
            {
                yield return new WaitForSeconds(.31f);
                Require(interactor.TryHammer(part), "Hammer failed: " + part.myType + " hit " + (hit + 1));
                totalHits++;
                Require(!interactor.TryHammer(part), "Repeated same-frame hammer accepted");
                Require(part.IsAssembled == (hit + 1 == hitCount), "Wrong completion count: " + part.myType);
            }
            interactor.SetPlayerHammerHits(3);
            Results = "Running: " + (index + 1) + "/26 assembled; " + part.myType;
        }
        var fullParts = FullCar.GetComponentsInChildren<AssemblyPart>(true);
        foreach (var part in required) Require(part.IsAssembled, "Incomplete final part " + part.myType);
        Require(VehicleInspectionRules.CountAllMissing(fullParts) == 0, "Complete car failed all-part inspection");
        var filtered = new List<AssemblyPart>(fullParts);
        filtered.Remove(required[0]);
        Require(VehicleInspectionRules.CountAllMissing(filtered.ToArray()) == 1, "Missing component was not rejected");
        log.AppendLine("26 actual stock parts attached/hammered; " + totalHits + " timed hits; 3-hit and 2-hit upgrade, repeated-click rejection: passed.");
        log.AppendLine("All-part inspection=0 missing; filtered missing component=1: passed. FullCar retained.");

        // A separate pooled vehicle verifies salvage/deadline/reset without undoing FullCar.
        lockCar = CarPool.Instance.Get();
        lockCar.SetPath(spline, 0f, start);
        if (ProductionProgression.Current != null) ProductionProgression.Current.Stamp(lockCar);
        AssemblyPart repair = null;
        foreach (var part in lockCar.GetComponentsInChildren<AssemblyPart>(true))
            if (part.myType == required[0].myType) { repair = part; break; }
        Require(repair != null, "Lock-test part missing");
        repair.SetWork(repair.requiredWork * .8f);
        NearPart(repair);
        yield return new WaitForSeconds(.31f);
        Require(interactor.TryHammer(repair) && repair.IsAssembled, "NPC 80% partial work not salvageable in one hit");
        lockCar.ResetParts();
        repair.SetWork(repair.requiredWork * .8f);
        lockCar.CloseRepairWindow();
        yield return new WaitForSeconds(.31f);
        Require(!interactor.TryHammer(repair) && !repair.IsAssembled, "Deadline allowed hammer completion");
        lockCar.ResetParts();
        Require(!lockCar.RepairDeadlineReached && !repair.IsPlayerAttached, "Pool reset retained lock or player state");
        repair.SetWork(repair.requiredWork * .8f);
        yield return new WaitForSeconds(.31f);
        Require(interactor.TryHammer(repair) && repair.IsAssembled, "Reset vehicle remained unrepairable");
        log.AppendLine("NPC partial salvage / deadline rejection / reset unlock and player-state reset: passed.");
    }

    private static void NearPart(AssemblyPart part)
    {
        player.Teleport(part.AttachmentWorldPosition - Vector3.up * 1.1f + Vector3.right * .4f);
        Physics.SyncTransforms();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
#endif
