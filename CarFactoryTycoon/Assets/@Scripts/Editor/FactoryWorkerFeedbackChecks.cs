#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using Object = UnityEngine.Object;

/// <summary>Runtime-only regression fixtures. Root controls entering/leaving Play; no scene assets are saved.</summary>
public static class FactoryWorkerFeedbackChecks
{
    public static string Result = "idle";
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly StringBuilder log = new StringBuilder();
    static readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
    static readonly List<GameObject> created = new List<GameObject>();
    static readonly List<Metric> metrics = new List<Metric>();
    static ProductionProgression progression;
    static FactoryPlayerController player;
    static PlayerAssemblyInteractor tool;
    static PrototypeLineController line;
    static PrototypeDayCycle day;
    static CarController car;
    static Coroutine navDriver;
    static bool navArrived;
    static Vector3 playerPosition, cameraPosition;
    static Quaternion playerRotation, cameraRotation;
    static bool playerEnabled, toolEnabled, toolInput, lineEnabled, dayEnabled;
    static float oldTimeScale;
    static PrototypeLineController.LeverPosition lever;
    static Behaviour brain;
    static bool brainEnabled, fixturesStarted;
    static int lastFrame;
    static double started;
    sealed class Metric
    {
        public string label, name; public bool milliseconds; public ProfilerRecorder recorder;
        public readonly List<double> samples = new List<double>(120);
    }
    static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static object Field(object instance, string name) => instance.GetType().GetField(name, Private).GetValue(instance);
    public static void Start(int group = 0)
    {
        if (stack.Count != 0) { Result = "already running"; return; }
        log.Clear(); created.Clear(); Result = "running";
        if (!EditorApplication.isPlaying) { Result = "FAIL: enter Play first"; return; }
        progression = ProductionProgression.Current; line = PrototypeLineController.Current;
        player = Object.FindFirstObjectByType<FactoryPlayerController>(); day = PrototypeDayCycle.Current;
        if (player == null || progression == null || line == null) { Result = "FAIL: prototype runtime missing"; return; }
        tool = player.interactor;
        playerEnabled = player.enabled; toolEnabled = tool.enabled; toolInput = tool.inputEnabled;
        lineEnabled = line.enabled; dayEnabled = day != null && day.enabled; lever = line.lever;
        oldTimeScale = Time.timeScale; playerPosition = player.transform.position; playerRotation = player.transform.rotation;
        cameraPosition = player.viewCamera.transform.position; cameraRotation = player.viewCamera.transform.rotation;
        brain = player.viewCamera.GetComponents<Behaviour>().FirstOrDefault(b => b.GetType().Name == "CinemachineBrain");
        brainEnabled = brain != null && brain.enabled;
        fixturesStarted = false; lastFrame = -1; started = EditorApplication.timeSinceStartup;
        stack.Push(Run(group)); EditorApplication.update += Tick;
    }
    public static void Cancel() { if (stack.Count > 0) Finish(false, "cancelled"); }
    static void Tick()
    {
        if (!EditorApplication.isPlaying) { Finish(false, "Play ended during checks"); return; }
        if (EditorApplication.timeSinceStartup - started > 150) { Finish(false, "150s wall-clock watchdog"); return; }
        if (Time.frameCount == lastFrame) return;
        lastFrame = Time.frameCount;
        try
        {
            while (stack.Count > 0)
            {
                var routine = stack.Peek();
                if (!routine.MoveNext()) { stack.Pop(); continue; }
                if (routine.Current is IEnumerator nested) { stack.Push(nested); continue; }
                return;
            }
            Finish(true, null);
        }
        catch (Exception e) { Finish(false, e.ToString()); }
    }
    static void Finish(bool success, string error)
    {
        EditorApplication.update -= Tick; stack.Clear();
        if (player != null && navDriver != null) player.StopCoroutine(navDriver); navDriver = null;
        foreach (var metric in metrics) if (metric.recorder.Valid) metric.recorder.Dispose(); metrics.Clear();
        if (EditorApplication.isPlaying && fixturesStarted)
        {
            var shake = Object.FindFirstObjectByType<ShakeTestStation>();
            if (shake != null && car != null && shake.InspectedCar == car) shake.CancelCycle();
            if (car != null && car.gameObject.activeInHierarchy) CarPool.Instance.Return(car); car = null;
            foreach (var go in created) if (go != null) Object.Destroy(go); created.Clear();
            if (line != null) { line.SetLever(lever); line.enabled = lineEnabled; }
            if (day != null) day.enabled = dayEnabled;
            Time.timeScale = oldTimeScale;
            if (player != null) { player.Teleport(playerPosition); player.transform.rotation = playerRotation; player.enabled = playerEnabled; }
            if (tool != null) { tool.enabled = toolEnabled; tool.inputEnabled = toolInput; }
            if (player != null && player.viewCamera != null) player.viewCamera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
            if (brain != null) brain.enabled = brainEnabled;
        }
        if (error != null) log.AppendLine("FAIL " + error);
        Result = (success ? "PASS\n" : "FAIL\n") + log;
        Debug.Log(Result);
    }
    static IEnumerator Run(int group)
    {
        if (group != 2) yield return Performance();
        if (group == 1) yield break;
        Need(tool.HeldPart == null, "start regression checks empty handed");
        Need(day == null || !day.IsSettlement, "continue settlement before regression checks");
        fixturesStarted = true;
        player.enabled = false; tool.enabled = false; tool.inputEnabled = false; line.enabled = false;
        if (day != null) day.enabled = false;
        if (brain != null) brain.enabled = false;
        Time.timeScale = 1f; line.SetLever(PrototypeLineController.LeverPosition.Stop);
        if (group != 2) { yield return SmokeAndSparks(); yield return BeltPart(); }
        yield return NavigationAndBadge();
        yield return ShakePallet();
    }
    static IEnumerator Performance()
    {
        Result = "running: baseline performance 120 Play frames";
        var available = new List<ProfilerRecorderHandle>(); ProfilerRecorderHandle.GetAvailable(available);
        AddMetric(available, "main thread", true, "Main Thread");
        AddMetric(available, "render thread", true, "Render Thread");
        AddMetric(available, "target FPS wait", true, "WaitForTargetFPS", "WaitForTargetFPS (Main Thread)");
        AddMetric(available, "editor loop", true, "EditorLoop");
        AddMetric(available, "player loop", true, "PlayerLoop");
        AddMetric(available, "present wait", true, "Gfx.WaitForPresentOnGfxThread", "Gfx.WaitForPresent");
        AddMetric(available, "render thread wait", true, "Gfx.WaitForRenderThread", "Gfx.WaitForGfxCommandsFromMainThread");
        AddMetric(available, "physics simulate", true, "Physics.Simulate", "Physics.Simulate.PhysX");
        AddMetric(available, "GPU", true, "GPU Frame Time", "GPU Frame Time (ns)");
        AddMetric(available, "script update", true, "Update.ScriptRunBehaviourUpdate", "BehaviourUpdate");
        AddMetric(available, "batches", false, "Batches Count");
        AddMetric(available, "SetPass", false, "SetPass Calls Count");
        AddMetric(available, "triangles", false, "Triangles Count");
        AddMetric(available, "GC bytes/frame", false, "GC Allocated In Frame");
        var frames = new List<double>(120);
        // Warm recorders before collecting. No fixture, teleport, or line changes precede this baseline.
        yield return null; yield return null;
        for (int i = 0; i < 120; i++)
        {
            frames.Add(Time.unscaledDeltaTime * 1000.0);
            foreach (var metric in metrics) if (metric.recorder.Valid && metric.recorder.Count > 0)
                metric.samples.Add(metric.recorder.LastValue * (metric.milliseconds ? .000001 : 1));
            yield return null;
        }
        log.AppendLine("PERF baseline120: frame " + Stats(frames) + " ms; reciprocal mean=" + (1000.0 / frames.Average()).ToString("F1") + " FPS.");
        foreach (var metric in metrics)
        {
            log.AppendLine("PERF " + metric.label + " [" + metric.name + "]: " + (metric.samples.Count > 0 ? Stats(metric.samples) + (metric.milliseconds ? " ms" : "") : "unavailable/no samples"));
            if (metric.recorder.Valid) metric.recorder.Dispose();
        }
        metrics.Clear();
        log.AppendLine("PERF context: editor Play, resolution=" + Screen.width + "x" + Screen.height + ", vSync=" + QualitySettings.vSyncCount + ", targetFPS=" + Application.targetFrameRate + ", isFocused=" + Application.isFocused + ", activeCars=" + Object.FindObjectsByType<CarController>(FindObjectsSortMode.None).Length + ", activeNPC=" + Object.FindObjectsByType<Worker>(FindObjectsSortMode.None).Length + ". Timing includes editor/render waits; GPU unavailable is not zero GPU time.");
    }
    static void AddMetric(List<ProfilerRecorderHandle> available, string label, bool milliseconds, params string[] names)
    {
        foreach (string name in names) foreach (var handle in available)
        {
            var description = ProfilerRecorderHandle.GetDescription(handle);
            if (description.Name != name) continue;
            try
            {
                var recorder = ProfilerRecorder.StartNew(description.Category, name, 1);
                if (recorder.Valid) { metrics.Add(new Metric { label = label, name = name, milliseconds = milliseconds, recorder = recorder }); return; }
                recorder.Dispose();
            }
            catch (Exception e) { log.AppendLine("PERF " + label + ": recorder unavailable (" + e.GetType().Name + ")"); }
        }
        log.AppendLine("PERF " + label + ": unavailable on this editor/platform");
    }
    static string Stats(List<double> values)
    { var sorted = values.OrderBy(x => x).ToArray(); return "mean=" + values.Average().ToString("F3") + ", p95=" + sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * .95) - 1)].ToString("F3") + ", max=" + sorted[sorted.Length - 1].ToString("F3"); }
    static IEnumerator SmokeAndSparks()
    {
        Result = "running: smoke/sparks";
        var tunnel = Object.FindFirstObjectByType<PrototypeTunnelFeedback>(); Need(tunnel != null, "tunnel feedback missing");
        var smoke = (ParticleSystem)Field(tunnel, "smoke"); Need(smoke != null, "smoke system missing");
        float before = (float)Field(tunnel, "smokeTime");
        tunnel.NotifyLeverChanged(); Need((float)Field(tunnel, "smokeTime") == before, "lever must not create smoke");
        typeof(PrototypeTunnelFeedback).GetMethod("OnRequestFeedback", Private).Invoke(tunnel, new object[] { CarSpawner.RequestFeedback.Accepted });
        Need((float)Field(tunnel, "smokeTime") >= 4f, "smoke request hold too short");
        Need(smoke.main.maxParticles <= 144 && smoke.main.startLifetime.constantMin >= 5f && smoke.main.startSpeed.constantMin >= 1.5f, "smoke lifetime/speed/cap regression");
        float began = Time.time; float maxRise = 0; int maxCount = 0; var particles = new ParticleSystem.Particle[144];
        while (Time.time - began < 5.3f)
        {
            int count = smoke.GetParticles(particles); maxCount = Math.Max(maxCount, count);
            for (int i = 0; i < count; i++) maxRise = Mathf.Max(maxRise, particles[i].position.y - smoke.transform.position.y);
            Need(count <= 144, "smoke particle budget exceeded"); yield return null;
        }
        Need(maxRise > 3f && maxCount > 25 && smoke.particleCount > 0, "smoke must rise high and persist beyond five seconds: rise=" + maxRise + " count=" + maxCount);
        log.AppendLine("Smoke: explicit request, lever exclusion, rise=" + maxRise.ToString("F2") + "m, peak=" + maxCount + "/144, particles persist>5s.");
        var go = new GameObject("FeedbackSparkProbe"); created.Add(go);
        var effects = go.AddComponent<PrototypeAssemblyEffects>(); effects.Configure(true); effects.Pulse(player.transform.position + Vector3.up * 2, false);
        var spark = go.GetComponentInChildren<ParticleSystem>(); Need(spark != null && spark.particleCount >= 20, "impact burst not visibly stronger");
        Need(spark.main.maxParticles <= 96 && spark.main.startSpeed.constantMin >= 2.5f && spark.main.startSize.constantMin >= .04f, "spark size/speed/cap regression");
        effects.Pulse(player.transform.position + Vector3.up * 2, true); Need(effects.PulseCount == 2 && spark.particleCount <= 96, "reused spark feedback/cap");
        float until = Time.time + .7f; while (Time.time < until) yield return null;
        Need(spark.particleCount == 0, "spark cleanup");
        log.AppendLine("Sparks: impact>=20, stronger speed/size, same-system completion pulse, bounded96, expires<0.7s.");
    }
    static IEnumerator BeltPart()
    {
        Result = "running: loose part belt";
        var spline = progression.mainSpline;
        Vector3 point = spline.transform.TransformPoint((Vector3)UnityEngine.Splines.SplineUtility.EvaluatePosition(spline.Spline, .025f));
        Need(Physics.Raycast(point + Vector3.up * 5, Vector3.down, out var support, 10f, 1 << 0, QueryTriggerInteraction.Ignore) && support.collider.GetComponentInParent<SplineConveyorDeformer>() != null, "belt support fixture unavailable");
        var prefab = Resources.Load<GameObject>(PartResourceManager.GetPrefabPath(PartType.BackMirror_Left_61)); Need(prefab != null, "portable prefab");
        var part = PortableAssemblyPart.Create(PartType.BackMirror_Left_61, prefab, support.point + Vector3.up, Quaternion.identity); created.Add(part.gameObject);
        var anchor = new GameObject("FeedbackHoldAnchor"); created.Add(anchor);
        Need(part.TryHold(tool, anchor.transform) && part.Throw(tool, Vector3.zero), "portable hold/throw");
        part.transform.SetPositionAndRotation(support.point + Vector3.up * (part.LocalBounds.extents.y + .025f), Quaternion.identity);
        Physics.SyncTransforms(); float until = Time.time + .6f; while (Time.time < until) yield return null;
        Vector3 carriedFrom = part.Body.position;
        line.SetLever(PrototypeLineController.LeverPosition.Low);
        until = Time.time + 1f; while (Time.time < until) yield return null;
        Vector3 target = support.collider.GetComponentInParent<SplineConveyorDeformer>().GetSurfaceVelocity(support.point);
        Need(target.magnitude > .05f && Vector3.Dot(part.Body.linearVelocity, target.normalized) > target.magnitude * .4f, "resting thrown part did not follow belt: actual=" + part.Body.linearVelocity + " target=" + target + " support=" + part.BeltSupportDiagnostic);
        Need(Vector3.Dot(part.Body.position - carriedFrom, target.normalized) > target.magnitude * .35f, "belt part velocity must produce real physical displacement: " + part.BeltSupportDiagnostic);
        line.SetLever(PrototypeLineController.LeverPosition.Stop);
        until = Time.time + .7f; while (Time.time < until) yield return null;
        Need(Vector3.ProjectOnPlane(part.Body.linearVelocity, Vector3.up).magnitude < .15f, "part did not settle after belt stop");
        Need(part.TryHold(tool, anchor.transform), "part pickup after belt"); float age = part.DroppedLifetimeElapsed;
        until = Time.time + .2f; while (Time.time < until) yield return null;
        Need(Mathf.Abs(part.DroppedLifetimeElapsed - age) < .001f && part.Body.isKinematic, "held belt part must pause lifetime/physics");
        log.AppendLine("Portable part: real box support, stopped→moving belt carry→stop, pickup pauses timer/physics.");
    }
    static CarController Spawn(float progress)
    { var value = CarPool.Instance.Get(); value.SetPath(progression.mainSpline, 0, progress); progression.Stamp(value); return value; }
    static IEnumerator NavigationAndBadge()
    {
        Result = "running: capsule/navigation/badge";
        car = Spawn(progression.processes[0].gateProgress + 12f / progression.mainSpline.CalculateLength()); car.isMoving = false;
        Physics.SyncTransforms(); var hull = car.GetComponentInChildren<CarBodyCollision>(); Need(hull != null, "car hull missing");
        Need(hull.GetComponents<BoxCollider>().Length == 3 && hull.GetComponentsInChildren<MeshCollider>().Length == 0, "vehicle primitive collision budget changed");
        Bounds bounds = hull.GetComponents<BoxCollider>()[0].bounds;
        Vector3 start = bounds.center + Vector3.forward * (bounds.extents.z + 1.4f); start.y = 0;
        Vector3 goal = bounds.center - Vector3.forward * (bounds.extents.z + 1.4f); goal.y = 0;
        Vector3 stopped = start + CarBodyCollision.ConstrainActorStep(start, goal - start);
        Need((stopped - goal).sqrMagnitude > .25f, "capsule sweep crossed vehicle");
        Vector3 inside = new Vector3(bounds.center.x, 0, bounds.center.z);
        Vector3 escaped = inside + CarBodyCollision.ConstrainActorStep(inside, Vector3.zero);
        Need(Vector3.Distance(escaped, inside) > .1f, "idle actor overlapping moving hull not pushed out");
        Need(NavMesh.SamplePosition(start, out var sampled, 2f, NavMesh.AllAreas), "navigation fixture not on mesh");
        var go = new GameObject("FeedbackNavigationProbe"); created.Add(go); go.transform.position = sampled.position;
        var mover = go.AddComponent<NavMover>(); mover.speed = 2.8f; yield return null; yield return null;
        var capsule = go.AddComponent<CapsuleCollider>(); capsule.radius = .32f; capsule.height = 1.8f; capsule.center = Vector3.up * .9f; capsule.enabled = false;
        float until = Time.time + 20f; navArrived = false;
        // Drive in PlayerLoop, before NavMover.LateUpdate. EditorApplication.update polling
        // runs between Play frames and would otherwise alternate MoveTo with idle yielding.
        navDriver = player.StartCoroutine(DriveNavigation(mover, goal));
        while (Time.time < until && !navArrived)
        {
            Physics.SyncTransforms();
            foreach (var box in hull.GetComponents<BoxCollider>())
                Need(!Physics.ComputePenetration(capsule, go.transform.position, go.transform.rotation, box, box.transform.position, box.transform.rotation, out _, out float depth) || depth < .025f, "navigation capsule penetrated vehicle primitive");
            yield return null;
        }
        Need(navArrived, "NavMesh complete vehicle detour failed: " + mover.DiagnosticState);
        var part = car.GetComponentsInChildren<AssemblyPart>(true).First(p => p.myType != PartType.None && p.myType != PartType.Frame_1);
        part.Reset(); Need(part.TryBeginPlayerAttachment(tool, part.AttachmentWorldPosition, part.AttachmentWorldRotation) && part.CompletePlayerAttachment(tool), "badge attachment fixture");
        var display = Object.FindFirstObjectByType<PrototypeDefectDisplay>(); Need(display != null, "defect display");
        player.Teleport(car.transform.position + car.transform.right * .8f); tool.RefreshNearbyCars(); display.RefreshDisplay();
        player.viewCamera.transform.SetPositionAndRotation(car.transform.position + Vector3.up * 1.6f, Quaternion.LookRotation(car.transform.forward));
        int seen = 0; until = Time.time + .5f;
        while (Time.time < until) { seen = Math.Max(seen, display.VehicleBadgeCount); yield return null; }
        Need(seen > 0, "near/inside vehicle badge never rendered; keep Game view rendering for OnGUI checks");
        log.AppendLine("Collision: 3 boxes/no mesh; long capsule step blocked, overlap corrected; actual NavMover detours with<2.5cm penetration. Near/inside vehicle badge visible.");
        CarPool.Instance.Return(car); car = null;
    }
    static IEnumerator DriveNavigation(NavMover mover, Vector3 goal)
    {
        yield return null;
        while (mover != null && !navArrived)
        { navArrived = mover.MoveTo(goal); yield return null; }
        navDriver = null;
    }
    static IEnumerator ShakePallet()
    {
        Result = "running: shake debris/pallet";
        var shake = Object.FindFirstObjectByType<ShakeTestStation>(); Need(shake != null && shake.currentState == ShakeTestStation.TestState.Idle, "shake station must be idle");
        Need(PrototypeCarDebris.ActiveCount == 0, "begin shake fixture without existing debris");
        car = Spawn(shake.gateProgress - .0001f);
        var parts = car.GetComponentsInChildren<AssemblyPart>(true); foreach (var part in parts) part.SetAssembled();
        var loose = parts.First(p => p.myType != PartType.Frame_1 && VehicleInspectionRules.DomainOf(p.myType) == VehicleInspectionDomain.Assembly);
        loose.Reset(); Need(loose.TryBeginPlayerAttachment(tool, loose.AttachmentWorldPosition, loose.AttachmentWorldRotation) && loose.CompletePlayerAttachment(tool), "loose shake fixture");
        Physics.SyncTransforms(); float until = Time.time + 12f; bool during = false, breaking = false; int peak = 0; float lowest = car.transform.position.y;
        while (Time.time < until && car.gameObject.activeInHierarchy)
        {
            peak = Math.Max(peak, PrototypeCarDebris.ActiveCount); Need(peak <= 40, "debris cap40 exceeded");
            if (shake.currentState == ShakeTestStation.TestState.Testing && PrototypeCarDebris.ActiveCount > 0) during = true;
            if (shake.currentState == ShakeTestStation.TestState.Breaking)
            {
                breaking = true; lowest = Mathf.Min(lowest, car.transform.position.y);
                Need(car.Dolly != null && !car.Dolly.IsDetached && car.Dolly.transform.IsChildOf(car.transform), "pallet must descend attached with frame");
            }
            yield return null;
        }
        Need(during && breaking && !car.gameObject.activeInHierarchy && lowest < -3f && peak > 1, "shake release/reject/descent regression");
        car = null; until = Time.time + 4.6f; while (Time.time < until) yield return null;
        Need(PrototypeCarDebris.ActiveCount == 0, "debris must expire");
        log.AppendLine("Shake: loose part releases during testing, reject scatters multiple parts, frame/pallet descend together below-3m, cap40, zero debris after4.6s.");
    }
}
#endif
