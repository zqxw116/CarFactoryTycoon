#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>Play-mode technical checks for M3 crew hiring and real timed assembly.</summary>
public static class FactoryCrewM3Checks
{
    public static string State = "idle";
    public static string Results = "";

    static FactoryPlayerController player;
    static PlayerAssemblyInteractor interactor;
    static PrototypeLineController line;
    static ProductionProgression progression;
    static EconomyManager economy;
    static FactoryHiringUI hiringUI;
    static bool wasPlayerEnabled, wasInputEnabled, wasMapMode, wasHiringUIEnabled;
    static bool wasCursorVisible;
    static CursorLockMode wasCursorLock;
    static PrototypeLineController.LeverPosition wasLever;
    static Vector3 originalPlayerPosition;
    static int originalMoney;
    static readonly List<CarController> testCars = new List<CarController>();
    static readonly Dictionary<PartStack, int> originalStockCounts = new Dictionary<PartStack, int>();
    static readonly StringBuilder log = new StringBuilder();

    public static void Start()
    {
        if (!Application.isPlaying) { State = "failed"; Results = "Requires Play mode."; return; }
        if (State == "running") return;

        player = UnityEngine.Object.FindFirstObjectByType<FactoryPlayerController>();
        interactor = player != null ? player.interactor : null;
        line = PrototypeLineController.Current;
        progression = ProductionProgression.Current;
        economy = EconomyManager.Instance;
        hiringUI = progression != null ? progression.GetComponent<FactoryHiringUI>() : null;
        if (player == null || interactor == null || line == null || progression == null ||
            CarPool.Instance == null || economy == null || progression.prototypeSettings == null)
        { State = "failed"; Results = "Player/interactor/line/progression/car pool/economy/settings missing."; return; }
        if (interactor.HeldPart != null || interactor.IsAttaching)
        { State = "failed"; Results = "Start with empty hands and no attachment in progress."; return; }
        if (Time.timeScale <= 0f)
        { State = "failed"; Results = "Time scale must be positive for timed crew work."; return; }

        wasPlayerEnabled = player.enabled;
        wasInputEnabled = interactor.inputEnabled;
        wasMapMode = player.MapMode;
        wasCursorVisible = Cursor.visible;
        wasCursorLock = Cursor.lockState;
        wasLever = line.lever;
        originalPlayerPosition = player.transform.position;
        originalMoney = economy.currentMoney;
        wasHiringUIEnabled = hiringUI == null || hiringUI.enabled;
        originalStockCounts.Clear();
        foreach (var stack in UnityEngine.Object.FindObjectsByType<PartStack>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (stack != null) originalStockCounts[stack] = stack.Count;

        testCars.Clear(); log.Clear();
        State = "running";
        Results = "Running M3 technical checks with live game frames; natural traversal is not measured.";
        player.SetMapMode(true);
        player.enabled = false;
        interactor.inputEnabled = false;
        line.SetLever(PrototypeLineController.LeverPosition.Stop);
        player.StartCoroutine(GuardedRun());
    }

    static IEnumerator GuardedRun()
    {
        bool success = true;
        var routines = new Stack<IEnumerator>();
        routines.Push(Run());
        float realStart = Time.realtimeSinceStartup;
        while (true)
        {
            object wait = null;
            bool more;
            try
            {
                Require(Time.realtimeSinceStartup - realStart < 180f, "180-second safety timeout");
                more = routines.Count > 0;
                if (more)
                {
                    var routine = routines.Peek();
                    if (!routine.MoveNext()) { (routines.Pop() as IDisposable)?.Dispose(); continue; }
                    wait = routine.Current;
                    if (wait is IEnumerator nested) { routines.Push(nested); continue; }
                }
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

        while (routines.Count > 0) (routines.Pop() as IDisposable)?.Dispose();
        ReturnTestCars();
        // Let the crews observe their cars leaving the work zones and refund any unconsumed stock.
        yield return null;
        yield return null;
        RestoreStockCounts();
        if (hiringUI != null) hiringUI.enabled = wasHiringUIEnabled;
        if (player != null)
        {
            player.InteractionUIOpen = false;
            player.Teleport(originalPlayerPosition);
            player.enabled = wasPlayerEnabled;
            player.SetMapMode(wasMapMode);
        }
        if (interactor != null) interactor.inputEnabled = wasInputEnabled;
        Cursor.lockState = wasCursorLock;
        Cursor.visible = wasCursorVisible;
        if (line != null) line.SetLever(wasLever);
        if (economy != null) economy.currentMoney = originalMoney;

        log.AppendLine("Assembly was driven by real game frames. Player traversal, usability and feel were not evaluated here.");
        Results = log.ToString();
        State = success ? "passed" : "failed";
        Debug.Log("[FactoryCrewM3Checks] " + State + "\n" + Results);
    }

    static IEnumerator Run()
    {
        Require(progression.prototypeMode, "Prototype mode is off");
        Require(progression.HiredPrefix == 0, "Prototype mode must keep the legacy prefix at zero");
        Require(progression.processes != null && progression.processes.Length == 23, "Expected 23 process entries");
        foreach (var process in progression.processes)
            Require(!process.hired && !process.automated && process.workerLevel == 0, "A process did not start unstaffed");
        log.AppendLine("All 23 processes start unhired, unautomated, level 0; full-line prototype prefix remains 0: passed.");

        economy.currentMoney = 0;
        Require(progression.TryHireProcess(20), "Arbitrary process 20 could not be hired first");
        Require(progression.IsProcessHired(20) && !progression.IsProcessHired(0), "Hire order was not independent");
        Require(!progression.TryHireProcess(20), "Duplicate human hire was accepted");
        Require(economy.currentMoney == 0, "Free hire changed the zero balance");
        log.AppendLine("Process 20 human hire, free price and duplicate rejection: passed.");

        int robotPrice = progression.prototypeSettings.robotPrice;
        Require(robotPrice == 1000, "Prototype robot price is not 1,000");
        Require(!progression.TryPurchaseRobot(1), "Robot purchase succeeded without funds");
        Require(economy.currentMoney == 0 && !progression.IsProcessHired(1), "Insufficient-funds attempt changed process or balance");
        economy.currentMoney = robotPrice;
        Require(progression.TryPurchaseRobot(1), "Direct robot purchase without human hire failed");
        Require(progression.IsProcessHired(1) && progression.IsProcessAutomated(1) && economy.currentMoney == 0,
            "Robot purchase did not spend exactly 1,000 or set both process flags");
        Require(!progression.TryPurchaseRobot(1) && economy.currentMoney == 0, "Duplicate robot purchase charged or succeeded");
        log.AppendLine("Robot insufficient-funds check, direct purchase and single 1,000 charge: passed.");

        Require(progression.TryHireProcess(0), "First human process could not be hired");
        var humanEntry = progression.processes[0];
        var humanCrew = FindCrew(0);
        Require(humanEntry.workers != null && humanEntry.workers.Length > 0 && humanEntry.workers[0] != null,
            "First process has no worker");
        var worker = humanEntry.workers[0];
        CheckFatigueFormula(humanCrew, worker);

        int humanPartsBefore = humanCrew.CompletedPartCount;
        int humanStrikesBefore = humanCrew.StrikeCount;
        var humanCar = CreateCar(0);
        var humanPart = FindPart(humanCar, humanEntry.parts[0]);
        Require(humanPart != null, "First process part is missing from the test car");
        yield return WaitForBusy(humanCrew, 8f, "Human crew never began its live work cycle");
        Require(humanCrew.IsBusy, "Human crew should be busy before conversion test");
        economy.currentMoney = robotPrice;
        Require(!progression.TryPurchaseRobot(0), "Busy human process was converted to robot");
        Require(economy.currentMoney == robotPrice && !progression.IsProcessAutomated(0), "Busy conversion charged or changed mode");
        economy.currentMoney = 0;
        log.AppendLine("Busy human-to-robot conversion is rejected before charging: passed.");

        yield return WaitForPartCount(humanCrew, humanPartsBefore + 1, 25f, "First human part did not finish within 25 game seconds");
        Require(humanPart.IsAssembled, "First human process part did not assemble");
        Require(humanCrew.StrikeCount - humanStrikesBefore == 3, "Human did not land exactly three hits");
        var humanEffects = worker.GetComponent<PrototypeAssemblyEffects>();
        Require(humanEffects != null && humanEffects.PulseCount == 3, "Human did not emit three hit effects");
        Require(Mathf.Abs(worker.condition - 75f) < .1f, "One completed part did not consume 25 condition");
        Require(!worker.IsResting && worker.currentState != Worker.WorkerState.Resting, "Prototype worker entered legacy forced rest");
        log.AppendLine("Live human attach + 3 hits + one −25 fatigue charge; no forced rest: passed.");
        ReturnTestCar(humanCar);
        yield return null;

        worker.condition = 0f;
        Require(Mathf.Abs(humanCrew.HammerInterval(worker) - 1f) < .001f, "Zero condition did not yield 1.0s hammer interval");
        int fatigueStrikesBefore = humanCrew.StrikeCount;
        var fatigueCar = CreateCar(0);
        var fatiguePart = FindPart(fatigueCar, humanEntry.parts[0]);
        Require(fatiguePart != null, "Fatigue test part is missing");
        yield return WaitForStrikes(humanCrew, fatigueStrikesBefore + 2, 25f, "Fatigued crew did not reach two hits");
        Require(humanCrew.IsBusy && !worker.IsResting && worker.currentState != Worker.WorkerState.Resting,
            "Fatigued worker stopped instead of continuing slowly");
        Require(Mathf.Abs(fatiguePart.Fill - 2f / 3f) < .02f && fatiguePart.IsPlayerAttached,
            "Two crew hits did not leave a repairable attached partial part");

        float length = fatigueCar.SplineLength;
        fatigueCar.SetProgress(Mathf.Clamp01(humanEntry.gateProgress + 6.2f / length));
        yield return null;
        yield return null;
        Require(!StationWorkZone.Contains(fatigueCar, line.spawner.mainLineSpline, humanEntry.gateProgress,
            progression.prototypeSettings.workZoneMeters), "Fatigue car remained inside the 6m work window");
        Require(Mathf.Abs(fatiguePart.Fill - 2f / 3f) < .02f && fatiguePart.IsPlayerAttached,
            "Leaving the work window erased partial progress or attachment");

        player.Teleport(fatiguePart.AttachmentWorldPosition - Vector3.up * 1.1f + Vector3.right * .4f);
        Physics.SyncTransforms();
        yield return new WaitForSeconds(.31f);
        Require(interactor.TryHammer(fatiguePart) && fatiguePart.IsAssembled,
            "Player could not repair the attached partial part after leaving the work window");
        Require(!worker.IsResting && worker.currentState != Worker.WorkerState.Resting,
            "Zero-condition worker forced a rest after player repair");
        float beforeRecovery = worker.condition;
        ReturnTestCar(fatigueCar);
        yield return new WaitForSeconds(2f);
        float recovered = worker.condition - beforeRecovery;
        Require(recovered >= 8f && recovered <= 15f, "Idle recovery over 2 game seconds was outside 8–15: " + recovered.ToString("0.0"));
        log.AppendLine("At 0 condition human continues at 1.0s/hit; 2/3 progress survives zone exit, player repairs it, idle recovers about +10/2s: passed.");

        yield return CheckRobotAssembly(1);
        int wheelIndex = FindWheelProcess();
        Require(wheelIndex >= 0, "No wheel process entry found");
        yield return CheckWheelAssembly(wheelIndex);
        CheckHiringUI();
    }

    static IEnumerator CheckRobotAssembly(int processIndex)
    {
        var entry = progression.processes[processIndex];
        var crew = FindCrew(processIndex);
        int completedBefore = crew.CompletedPartCount;
        int strikesBefore = crew.StrikeCount;
        Require(entry.robots != null && entry.robots.Length > 0 && entry.robots[0] != null,
            "Robot process has no arm controller");
        var car = CreateCar(processIndex);
        var part = FindPart(car, entry.parts[0]);
        Require(part != null, "Robot process part is missing from the car");
        yield return WaitForPartCount(crew, completedBefore + 1, 35f, "Robot part did not finish within 35 game seconds");
        Require(part.IsAssembled, "Robot did not complete its process part");
        Require(crew.StrikeCount - strikesBefore == 3, "Robot did not emit three timed sparks");
        var effects = entry.robots[0].GetComponent<PrototypeAssemblyEffects>();
        Require(effects != null && effects.PulseCount == 3, "Robot did not emit three spark effects");
        log.AppendLine("Live robot attach + three timed sparks; direct robot purchase used no human prerequisite: passed.");
        ReturnTestCar(car);
        yield return null;
    }

    static IEnumerator CheckWheelAssembly(int processIndex)
    {
        var entry = progression.processes[processIndex];
        Require(entry.wheel != null, "WheelStation reference missing");
        Require(entry.workers != null && entry.workers.Length == 2 && entry.workers[0] != null && entry.workers[1] != null,
            "Wheel process does not have the expected two workers");
        Require(progression.TryHireProcess(processIndex), "Wheel process human hire failed");
        Require(entry.wheel.gameObject.activeInHierarchy && !entry.wheel.isActiveAndEnabled,
            "Wheel lift hardware is not preserved with its legacy gate disabled");

        var crew = FindCrew(processIndex);
        int completedBefore = crew.CompletedPartCount;
        int strikesBefore = crew.StrikeCount;
        var car = CreateCar(processIndex);
        var wheelParts = new List<AssemblyPart>();
        foreach (var type in entry.parts)
        {
            var part = FindPart(car, type);
            Require(part != null, "Wheel part missing from car: " + type);
            wheelParts.Add(part);
        }
        Require(wheelParts.Count == 4, "Wheel process must cover four parts");
        yield return WaitForPartCount(crew, completedBefore + 4, 45f, "Two-worker wheel process did not finish within 45 game seconds");
        foreach (var part in wheelParts) Require(part.IsAssembled, "Wheel part was left incomplete: " + part.myType);
        Require(crew.StrikeCount - strikesBefore == 12, "Wheel process did not apply 3 hits to each of four wheels");
        foreach (var worker in entry.workers)
        {
            Require(Mathf.Abs(worker.condition - 50f) < .1f, "Each wheel worker should finish at condition 50");
            Require(!worker.IsResting && worker.currentState != Worker.WorkerState.Resting, "Wheel worker entered legacy forced rest");
        }
        int pulses = 0;
        foreach (var worker in entry.workers)
        {
            var effect = worker.GetComponent<PrototypeAssemblyEffects>();
            if (effect != null) pulses += effect.PulseCount;
        }
        Require(pulses == 12, "Wheel workers did not produce twelve visible hit pulses");
        log.AppendLine("Wheel lift hardware remains active without its stopping component; two workers assemble four wheels / 12 hits / 50 condition each: passed.");
        ReturnTestCar(car);
        yield return null;
    }

    static void CheckFatigueFormula(PrototypeProcessCrew crew, Worker worker)
    {
        float original = worker.condition;
        float interval = progression.prototypeSettings.npcHitInterval;
        Require(Mathf.Abs(interval - .5f) < .001f, "Prototype NPC base hammer interval is not 0.5s");
        worker.condition = 100f;
        Require(Mathf.Abs(crew.HammerInterval(worker) - interval) < .001f, "100 condition hammer interval mismatch");
        worker.condition = 50f;
        Require(Mathf.Abs(crew.HammerInterval(worker) - interval * 1.5f) < .001f, "50 condition hammer interval mismatch");
        worker.condition = 0f;
        Require(Mathf.Abs(crew.HammerInterval(worker) - interval * 2f) < .001f, "0 condition hammer interval mismatch");
        worker.condition = original;
        log.AppendLine("Fatigue formula at condition 100/50/0: 0.5×/0.75×/1.0s hit intervals: passed.");
    }

    static void CheckHiringUI()
    {
        Require(hiringUI != null && hiringUI.postsRoot != null && hiringUI.postsRoot.childCount == 23,
            "Hiring UI or 23 generated placards missing");
        player.SetMapMode(true);
        hiringUI.OpenProcess(0);
        Require(!hiringUI.PopupIsOpen, "Remote/map hiring opened a popup");
        player.SetMapMode(false);

        var post = hiringUI.postsRoot.GetChild(0);
        player.Teleport(post.position + post.forward * 8f);
        hiringUI.OpenProcess(0);
        Require(!hiringUI.PopupIsOpen, "Hiring popup opened beyond the local interaction range");
        player.Teleport(post.position - post.forward * 1.4f);
        Physics.SyncTransforms();
        hiringUI.OpenProcess(0);
        Require(hiringUI.PopupIsOpen && player.InteractionUIOpen && Cursor.lockState == CursorLockMode.None,
            "Nearby on-foot process did not open the cursor-backed hiring popup");
        hiringUI.enabled = false;
        hiringUI.enabled = true;
        Require(!hiringUI.PopupIsOpen && !player.InteractionUIOpen && Cursor.lockState == CursorLockMode.Locked,
            "Closing the hiring popup did not restore control state");
        log.AppendLine("Hiring posts reject map/remote opening and open locally with cursor and controls gated: passed.");
    }

    static IEnumerator WaitForBusy(PrototypeProcessCrew crew, float timeout, string message)
    {
        float deadline = Time.time + timeout;
        while (!crew.IsBusy && Time.time < deadline) yield return null;
        Require(crew.IsBusy, message);
    }

    static IEnumerator WaitForPartCount(PrototypeProcessCrew crew, int target, float timeout, string message)
    {
        float deadline = Time.time + timeout;
        while (crew.CompletedPartCount < target && Time.time < deadline)
        {
            Results = $"Running: process {crew.processIndex}, {crew.CompletedPartCount}/{target} crew parts completed.";
            yield return null;
        }
        Require(crew.CompletedPartCount >= target, message);
    }

    static IEnumerator WaitForStrikes(PrototypeProcessCrew crew, int target, float timeout, string message)
    {
        float deadline = Time.time + timeout;
        while (crew.StrikeCount < target && Time.time < deadline) yield return null;
        Require(crew.StrikeCount >= target, message);
    }

    static CarController CreateCar(int processIndex)
    {
        var entry = progression.processes[processIndex];
        var spline = line.spawner.mainLineSpline;
        float length = spline.CalculateLength();
        float start = Mathf.Clamp01(entry.gateProgress + .05f / Mathf.Max(.01f, length));
        var car = CarPool.Instance.Get();
        testCars.Add(car);
        car.SetPath(spline, 0f, start);
        progression.Stamp(car);
        return car;
    }

    static AssemblyPart FindPart(CarController car, PartType type)
    {
        foreach (var part in car.GetComponentsInChildren<AssemblyPart>(true))
            if (part != null && part.myType == type) return part;
        return null;
    }

    static int FindWheelProcess()
    {
        for (int i = 0; i < progression.processes.Length; i++)
            if (progression.processes[i] != null && progression.processes[i].wheel != null) return i;
        return -1;
    }

    static PrototypeProcessCrew FindCrew(int processIndex)
    {
        foreach (var crew in progression.GetComponents<PrototypeProcessCrew>())
            if (crew != null && crew.processIndex == processIndex) return crew;
        return PrototypeProcessCrew.Ensure(progression, processIndex);
    }

    static void ReturnTestCar(CarController car)
    {
        if (car == null) return;
        if (car.gameObject.activeInHierarchy && CarPool.Instance != null) CarPool.Instance.Return(car);
        testCars.Remove(car);
    }

    static void ReturnTestCars()
    {
        foreach (var car in testCars)
            if (car != null && car.gameObject.activeInHierarchy && CarPool.Instance != null) CarPool.Instance.Return(car);
        testCars.Clear();
    }

    static void RestoreStockCounts()
    {
        foreach (var pair in originalStockCounts)
            if (pair.Key != null && pair.Key.Count < pair.Value) pair.Key.Add(pair.Value - pair.Key.Count);
        originalStockCounts.Clear();
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
#endif
