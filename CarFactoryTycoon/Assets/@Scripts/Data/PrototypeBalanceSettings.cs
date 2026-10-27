using UnityEngine;

/// <summary>Prototype defaults from GAME_DESIGN.md. All economic/tuning values are provisional.</summary>
[CreateAssetMenu(menuName = "Car Factory/Prototype Balance")]
public sealed class PrototypeBalanceSettings : ScriptableObject
{
    [Header("M1 line")]
    [Min(1)] public int queueCapacity = 5;
    [Min(.01f)] public float lowSpeed = .5f;
    [Min(.01f)] public float initialHighSpeed = 1f;
    [Min(1f)] public float highSpeedUpgradeFactor = 1.25f;
    [Min(.1f)] public float workZoneMeters = 6f;
    [Header("Assembly (M2 / M3)")]
    public float playerAttachSeconds = .6f, playerHitInterval = .3f;
    public float npcAttachSeconds = 1f, npcHitInterval = .5f;
    public int requiredHits = 3;
    public float conditionCost = 25f, idleRecoveryPerSecond = 5f;
    [Header("Day and economy (M4; provisional)")]
    public float dayRealSeconds = 600f;
    public int startingMoney = 0, vehicleRevenue = 100, dailyWage = 30;
    public int hirePrice = 0, robotPrice = 1000, lostPartCost = 10;
    public int dailyMissionCars = 3, dailyMissionBonus = 300;
    public float overtimeWageMultiplier = 1.5f, overtimeFatigueMultiplier = 2f;
    public float droppedPartLifetime = 300f, droppedPartWarningSeconds = 30f;
}
