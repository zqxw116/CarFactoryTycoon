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
    [Header("M2 provisional movement / feel")]
    public float playerWalkSpeed = 3.4f, playerSprintSpeed = 6f;
    public float jumpHeight = 1.2f, playerGravity = -20f;
    public float lookSensitivity = .12f, thirdPersonDistance = 3.2f;
    [Header("Assembly (M2 / M3)")]
    public float playerAttachSeconds = .6f, playerHitInterval = .3f;
    public float npcAttachSeconds = 1f, npcHitInterval = .5f;
    public int requiredHits = 3;
    [Header("M3 provisional robot timing (phase 2 tuning pending)")]
    public float robotAttachSeconds = .6f, robotSparkInterval = .25f;
    public float conditionCost = 25f, idleRecoveryPerSecond = 5f;
    [Header("Day and economy (M4; provisional)")]
    public float dayRealSeconds = 600f;
    public int startingMoney = 0, vehicleRevenue = 100, dailyWage = 30;
    public int hirePrice = 0, robotPrice = 1000, lostPartCost = 10;
    public int dailyMissionCars = 3, dailyMissionBonus = 300;
    public float overtimeWageMultiplier = 1.5f, overtimeFatigueMultiplier = 2f;
    public float droppedPartLifetime = 300f, droppedPartWarningSeconds = 30f;
}
