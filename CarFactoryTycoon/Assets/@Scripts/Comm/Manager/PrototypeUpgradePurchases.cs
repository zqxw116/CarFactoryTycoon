using System;
using UnityEngine;

/// <summary>First prototype upgrades. The provisional price curve is stored in balance settings.</summary>
public sealed class PrototypeUpgradePurchases : MonoBehaviour
{
    public PrototypeBalanceSettings settings;
    public PrototypeLineController line;
    public PlayerAssemblyInteractor interactor;
    public int HammerCost => settings.playerHammerUpgradeCost;
    public bool HammerAvailable => interactor != null && interactor.PlayerHammerHits > 2;
    public int NextLineCost => (int)Math.Min(int.MaxValue, Math.Ceiling(settings.lineUpgradeBaseCost * Math.Pow(Math.Max(1, settings.lineUpgradeCostFactor), line.HighSpeedLevel)));
    bool CanBuy => PrototypeDayCycle.Current == null || !PrototypeDayCycle.Current.IsSettlement;
    public bool TryUpgradeHammer()
    {
        if (!CanBuy || !HammerAvailable || !EconomyManager.Instance.TrySpend(HammerCost)) return false;
        interactor.SetPlayerHammerHits(2);
        return true;
    }
    public bool TryUpgradeLine()
    {
        if (!CanBuy || line == null || !EconomyManager.Instance.TrySpend(NextLineCost)) return false;
        line.SetHighSpeedLevel(line.HighSpeedLevel + 1);
        return true;
    }
}
