#if UNITY_EDITOR
using System;
using System.Collections;
using System.Text;
using UnityEngine;

/// <summary>Run by the integration owner; never changes scenes or GameView resolution.</summary>
public static class FactoryResponsiveChecks
{
    public static string Result = "idle";
    static void Need(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    public static string CheckLayout()
    {
        var log = new StringBuilder();
        foreach (var screen in new[] { new Vector2(640, 360), new Vector2(1280, 720), new Vector2(1920, 1080) })
        {
            float scale = PrototypeGui.ScaleFor(screen);
            float width = screen.x / scale, height = screen.y / scale;
            var rects = new[] {
                new Rect(width - 310f, 50f, 298f, 130f),
                new Rect((width - 440f) * .5f, (height - 380f) * .5f, 440f, 380f),
                new Rect((width - 370f) * .5f, (height - 302f) * .5f, 370f, 302f),
                new Rect(18f, 54f, 296f, 274f)
            };
            foreach (Rect logical in rects)
            {
                Rect physical = PrototypeGui.ToScreenRect(logical, screen);
                Need(physical.xMin >= 0f && physical.yMin >= 0f && physical.xMax <= screen.x + .01f && physical.yMax <= screen.y + .01f,
                    "Panel offscreen at " + screen + ": " + physical);
                Need(logical.Contains(physical.center / scale), "Pointer conversion failed at " + screen);
                Need(!logical.Contains((physical.max + Vector2.one) / scale), "Outside pointer accepted at " + screen);
            }
            log.AppendLine(screen + ": header, report, hiring, expanded admin fit; pointer centers and outside edges match.");
        }
        return log.ToString();
    }
    public static void StartSettlementCheck()
    {
        Need(Application.isPlaying, "Play mode required for settlement check");
        var cycle = PrototypeDayCycle.Current;
        Need(cycle != null && cycle.isActiveAndEnabled, "Active day cycle required");
        Result = "running\n" + CheckLayout();
        cycle.StartCoroutine(CheckSettlement(cycle));
    }
    static IEnumerator CheckSettlement(PrototypeDayCycle cycle)
    {
        if (cycle.IsSettlement) cycle.ContinueNextDay();
        int firstDay = cycle.DayNumber;
        cycle.AdvanceClock(cycle.settings.dayRealSeconds * 2f);
        Need(cycle.IsSettlement && Time.timeScale == 0f, "Settlement must pause simulation");
        double started = cycle.SettlementStartedAt;
        double observedElapsed = 0d;
        int earlyObservations = 0;
        // A tool/editor stall can skip any number of frames. Judge the transition's timestamp,
        // rather than assuming a coroutine scheduled at 9 seconds actually resumed at 9 seconds.
        while (cycle.IsSettlement)
        {
            observedElapsed = Time.realtimeSinceStartupAsDouble - started;
            if (observedElapsed < PrototypeDayCycle.SettlementDuration)
            {
                if (cycle.DayNumber != firstDay)
                { Result = "FAIL: day changed during settlement at elapsed=" + observedElapsed.ToString("F3"); yield break; }
                earlyObservations++;
            }
            if (observedElapsed > 30d)
            { Result = "FAIL: settlement still open at elapsed=" + observedElapsed.ToString("F3"); yield break; }
            yield return null;
        }
        observedElapsed = Time.realtimeSinceStartupAsDouble - started;
        double transitionElapsed = cycle.LastSettlementElapsedSeconds;
        if (cycle.DayNumber != firstDay + 1 || transitionElapsed < PrototypeDayCycle.SettlementDuration || cycle.LastSettlementWasAccelerated)
        {
            Result = "FAIL: automatic transition day=" + cycle.DayNumber + " transitionElapsed=" + transitionElapsed.ToString("F3") +
                " observedElapsed=" + observedElapsed.ToString("F3") + " accelerated=" + cycle.LastSettlementWasAccelerated;
            yield break;
        }
        string timing = "Automatic transition elapsed=" + transitionElapsed.ToString("F3") +
            "s; observed=" + observedElapsed.ToString("F3") + "s; pre-deadline frames=" + earlyObservations + ". ";
        cycle.ContinueNextDay();
        if (cycle.DayNumber != firstDay + 1)
        { Result = "FAIL: late manual click advanced twice"; yield break; }
        cycle.AdvanceClock(cycle.settings.dayRealSeconds * 2f);
        int manualDay = cycle.DayNumber;
        cycle.AdvanceSettlement(9.99f);
        cycle.ContinueNextDay();
        cycle.AdvanceSettlement(.02f);
        cycle.ContinueNextDay();
        if (cycle.DayNumber != manualDay + 1 || cycle.IsSettlement || cycle.SettlementSecondsRemaining != 0f)
        { Result = "FAIL: manual/timer duplicate or countdown not cleared"; yield break; }
        Result = "PASS\n" + CheckLayout() + timing + "10 real seconds at timeScale=0; automatic, late manual, manual-before-timer and duplicate continuation passed.";
        Debug.Log(Result);
    }
}
#endif
