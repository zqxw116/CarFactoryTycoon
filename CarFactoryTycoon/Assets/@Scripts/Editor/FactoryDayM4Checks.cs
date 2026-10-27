#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEngine;

/// <summary>Deterministic M4 integration checks using real stock, cars, purchases and payroll APIs.</summary>
public static class FactoryDayM4Checks
{
    public static string Run()
    {
        if (!Application.isPlaying) throw new InvalidOperationException("Play mode required.");
        var cycle = PrototypeDayCycle.Current;
        var p = ProductionProgression.Current;
        var economy = EconomyManager.Instance;
        var defects = p.GetComponent<PrototypeDefectDisplay>();
        var player = UnityEngine.Object.FindFirstObjectByType<FactoryPlayerController>();
        var line = PrototypeLineController.Current;
        var upgrades = p.GetComponent<PrototypeUpgradePurchases>();
        Check(cycle != null && cycle.DayNumber == 1 && cycle.GameHour < 20f && economy.currentMoney == 0, "fresh 08:00 / day 1 / zero funds");
        cycle.enabled = false; player.enabled = false; player.interactor.inputEnabled = false;
        line.SetLever(PrototypeLineController.LeverPosition.Stop);
        // No elapsed test frames: this exact ledger check controls only the day clock.
        float startingHour = cycle.GameHour;
        Check(p.TryHireProcess(0), "free first hire");
        int wheels = Array.FindIndex(p.processes, x => x.wheel != null);
        Check(wheels >= 0 && p.TryHireProcess(wheels) && cycle.HumanCount == 3, "wheel station counts two paid humans");
        Check(!upgrades.TryUpgradeHammer() && !upgrades.TryUpgradeLine(), "unaffordable upgrades rejected");

        // Actual shipment completion enters Economy.SellCar, then emits the mission event once.
        for (int i = 0; i < 3; i++)
        {
            var sale = CarPool.Instance.Get(); sale.SetPath(p.mainSpline, 10f, .999999f); p.Stamp(sale);
            foreach (var part in sale.GetComponentsInChildren<AssemblyPart>(true)) part.SetAssembled();
            sale.CloseRepairWindow(); sale.MoveStep(1f, 1f); sale.MoveStep(1f, 1f);
            sale.LeaveLine(); CarPool.Instance.Return(sale);
        }
        Check(cycle.SoldCars == 3 && cycle.SalesIncome == 300 && cycle.MissionIncome == 300 && economy.currentMoney == 600, "three real sales / one mission award / duplicate finish guarded");
        Check(upgrades.TryUpgradeHammer() && !upgrades.TryUpgradeHammer() && player.interactor.PlayerHammerHits == 2, "paid one-time hammer upgrade");
        Check(upgrades.TryUpgradeLine() && line.HighSpeedLevel == 1 && cycle.PurchaseCosts == 250, "paid line maximum upgrade");
        line.SetLever(PrototypeLineController.LeverPosition.High);
        Check(Mathf.Abs(line.CurrentSpeed - 1.25f) < .001f, "line maximum multiplied by 1.25");
        line.SetLever(PrototypeLineController.LeverPosition.Stop);

        var stack = UnityEngine.Object.FindObjectsByType<PartStack>(UnityEngine.FindObjectsSortMode.None).First(x => x.partType == p.processes[0].parts[0]);
        var anchor = new GameObject("M4LifetimeCheckAnchor").transform;
        PortableAssemblyPart portable;
        Check(stack.TryTakePortable(out portable), "real portable stock");
        portable.enabled = false;
        portable.AdvanceDroppedLifetime(1000f);
        Check(portable.DroppedLifetimeElapsed == 0f && !portable.IsExpired, "never-thrown part does not expire");
        Check(portable.TryHold(player.interactor, anchor) && portable.Throw(player.interactor, Vector3.zero), "hold/throw");
        portable.AdvanceDroppedLifetime(portable.LifetimeSeconds - portable.WarningSeconds - 1f);
        Check(!portable.IsInWarningPeriod, "warning begins only in final thirty seconds");
        portable.AdvanceDroppedLifetime(1.6f);
        Check(portable.IsInWarningPeriod && portable.GetComponentsInChildren<Renderer>(true).All(x => !x.enabled), "warning actually blinks renderer off");
        float elapsed = portable.DroppedLifetimeElapsed;
        Check(portable.TryHold(player.interactor, anchor), "pickup warning part");
        portable.AdvanceDroppedLifetime(1000f);
        Check(portable.DroppedLifetimeElapsed == elapsed && !portable.IsExpired && portable.GetComponentsInChildren<Renderer>(true).Any(x => x.enabled), "held lifetime pauses and visibility restores");
        Check(portable.Throw(player.interactor, Vector3.zero), "rethrow");
        portable.AdvanceDroppedLifetime(1000f); portable.AdvanceDroppedLifetime(1000f);
        Check(portable.IsExpired && cycle.PartLosses == 10 && economy.currentMoney == 340, "single expiry loss may not bill twice");
        PortableAssemblyPart consumed;
        Check(stack.TryTakePortable(out consumed) && consumed.TryHold(player.interactor, anchor), "held consume setup");
        UnityEngine.Object.Destroy(consumed.gameObject);
        Check(cycle.PartLosses == 10, "normal destruction/consumption is not a loss");
        UnityEngine.Object.Destroy(anchor.gameObject);

        var car = CarPool.Instance.Get();
        float length = p.mainSpline.CalculateLength();
        car.SetPath(p.mainSpline, 0f, p.processes[0].gateProgress + 5.9f / length); p.Stamp(car);
        defects.RefreshDisplay(); Check(defects.MissingPartCount == 0, "no red future parts before first work window closes");
        car.pathProgress = p.processes[0].gateProgress + 6.1f / length;
        defects.RefreshDisplay();
        int expected = p.processes.Where(x => car.pathProgress > x.gateProgress + 6f / length).SelectMany(x => x.parts).Distinct().Count();
        Check(defects.MissingPartCount == expected && defects.AffectedCarCount == 1, "missed-part counter follows process windows");
        var missing = car.GetComponentsInChildren<AssemblyPart>(true).First(x => x.myType == p.processes[0].parts[0]);
        Check(!missing.gameObject.activeSelf && missing.Fill == 0f && !missing.IsPlayerAttached, "red proxy never mutates assembly state");
        missing.SetAssembled(); defects.RefreshDisplay(); Check(defects.MissingPartCount == expected - 1, "repair removes red counter immediately");
        car.CloseRepairWindow(); defects.RefreshDisplay(); Check(defects.MissingPartCount == 0, "inspection deadline removes repair indicators");
        car.LeaveLine(); CarPool.Instance.Return(car); defects.RefreshDisplay();
        Check(defects.AffectedCarCount == 0, "pooled cars leave display");

        // Start the exact wages sample at 08:00. Initial natural frames were subsecond.
        float remainingRegularHours = 20f - cycle.GameHour;
        cycle.AdvanceClock(remainingRegularHours / 14f * cycle.settings.dayRealSeconds);
        Check(Mathf.Abs(cycle.GameHour - 20f) < .001f && p.GetComponents<PrototypeProcessCrew>().All(x => !x.WorkersOnDuty), "20:00 default retirement");
        Check(Mathf.Abs(cycle.RegularWagesAccrued - (20f - startingHour) * 7.5f) < .01f, "three workers accrue a full regular wage each");
        cycle.SetOvertime(true);
        Check(p.GetComponents<PrototypeProcessCrew>().All(x => x.WorkersOnDuty && x.Overtime), "optional overtime enables fatigue hook");
        int expectedRegular = Mathf.CeilToInt(cycle.RegularWagesAccrued - .0001f);
        cycle.AdvanceClock(cycle.settings.dayRealSeconds * 2f / 14f + .01f);
        Check(cycle.IsSettlement && cycle.RegularWages == expectedRegular && cycle.OvertimeWages == 23 && Time.timeScale == 0f, "22:00 once-only payroll and paused report");
        Check(economy.currentMoney == 340 - expectedRegular - 23 && cycle.NetIncome == economy.currentMoney, "ledger reconciles income/purchases/loss/payroll");
        int settledBalance = economy.currentMoney;
        cycle.AdvanceClock(1000); Check(economy.currentMoney == settledBalance, "settlement cannot charge twice");
        var speedUI = UnityEngine.Object.FindFirstObjectByType<TimeScaleUI>();
        float previousSpeed = speedUI.CurrentScale;
        speedUI.SetScale(4); speedUI.StepUp(); speedUI.StepDown();
        Check(Time.timeScale == 0f && speedUI.CurrentScale == previousSpeed, "speed controls cannot unpause settlement");
        cycle.ContinueNextDay();
        Check(cycle.DayNumber == 2 && cycle.GameHour == 8f && cycle.SoldCars == 0 && !cycle.OvertimeEnabled && Time.timeScale > 0f, "next day resets daily ledger but preserves factory");
        economy.Charge(economy.currentMoney + 73); Check(economy.currentMoney == -73 && !p.TryHireProcess(2) && !upgrades.TryUpgradeLine(), "mandatory costs create debt and block free hire/purchases");
        economy.SellCar(100); Check(economy.currentMoney == 27 && cycle.SoldCars == 1 && p.TryHireProcess(2), "revenue repays debt and unlocks free hire");
        cycle.enabled = true;
        return "PASS: real sale/mission events; paid upgrades; dropped timer/blink/pickup/resume/single charge; non-mutating red proxies/counters; 20/22 duty, proportional regular/overtime payroll, settlement pause/resume and debt recovery. Diagnostic placement used APIs; feel not evaluated. Stop Play to restore initial state.";
    }
    static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException("M4 FAIL: " + message);
    }
}
#endif
