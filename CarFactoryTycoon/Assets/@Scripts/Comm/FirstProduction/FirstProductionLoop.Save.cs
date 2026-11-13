using System;
using System.Collections.Generic;
using UnityEngine;

public sealed partial class FirstProductionLoop
{
    const string SaveKey = "CarFactoryTycoon.FactoryGrowth.v1";
    const float CheckpointInterval = 10f;
    bool savedFuelHired, savedFuelAutomated;
    bool tutorialGate, savePending, unreadableSave;
    float nextCheckpoint;
    EconomyManager saveWallet;
    public bool AdminSession { get; private set; }

    [Serializable]
    sealed class FactoryRecord
    {
        public int version, openCount, balance, supplyStaff, delivered, rejected, income;
        public string lastOpenId;
        public bool automaticInput, fuelHired, fuelAutomated;
        public string[] staffed, robots;
        public PrototypeDayCycle.DayRecord day;
    }

    static string StorageKey
    {
        get
        {
#if UNITY_EDITOR
            if (UnityEditor.SessionState.GetBool("FirstProduction.UseTemporarySave", false)) return SaveKey + ".QA";
#endif
            return SaveKey;
        }
    }

    bool RestoreFactory()
    {
        saveWallet = EconomyManager.Instance;
        saveWallet.currentMoney = 0;
        if (!PlayerPrefs.HasKey(StorageKey)) return false;
        FactoryRecord record;
        try
        {
            record = JsonUtility.FromJson<FactoryRecord>(PlayerPrefs.GetString(StorageKey));
            if (record == null || record.version != 1 || record.openCount < 1 ||
                record.staffed == null || record.robots == null || record.supplyStaff < 0)
                throw new ArgumentException("저장 형식 또는 필수 값이 유효하지 않습니다.");
        }
        catch (ArgumentException ex)
        {
            unreadableSave = true;
            Debug.LogWarning("공장 기록을 읽을 수 없어 기존 저장을 보존합니다. " + ex.Message);
            return false;
        }

        var processes = progression.processes;
        int last = Array.FindIndex(processes, p => p.authoringId == record.lastOpenId);
        OpenedProcesses = last >= 0 ? last + 1 : Mathf.Clamp(record.openCount, 1, processes.Length);
        OutletPrefix = OpenedProcesses;
        var staffed = new HashSet<string>(record.staffed);
        var robots = new HashSet<string>(record.robots);
        for (int i = 0; i < OpenedProcesses; i++)
        {
            var process = processes[i];
            process.automated = robots.Contains(process.authoringId);
            process.hired = process.automated || staffed.Contains(process.authoringId);
            if (process.hired) progression.RefreshProcess(i);
        }

        saveWallet.currentMoney = record.balance;
        SuccessfulDeliveries = record.delivered;
        FailedDeliveries = record.rejected;
        DeliveryIncome = record.income;
        AutomaticInputPurchased = record.automaticInput;
        savedFuelHired = record.fuelHired;
        savedFuelAutomated = record.fuelAutomated;
        nextAutomaticRequest = Time.time + Mathf.Max(.1f, spawner.spawnInterval);
        var supply = services != null ? services.supply : null;
        if (supply != null)
            for (int i = supply.WorkerCount; i < record.supplyStaff; i++)
                if (supply.Hire() == null) break;
        PrototypeDayCycle.Current?.RestoreDay(record.day);
        return true;
    }

    void FlagWalletChange(int balance, int delta, Vector3? point) => savePending = true;

    void CheckpointIfDue()
    {
        if (!ready || AdminSession || unreadableSave) return;
        if (savePending || Time.unscaledTime >= nextCheckpoint) SaveFactory();
    }

    void OnApplicationPause(bool paused) { if (paused) SaveFactory(); }
    void OnApplicationQuit() => SaveFactory();

    public void BeginAdminSession()
    {
        if (AdminSession) return;
        SaveFactory();
        AdminSession = true;
    }

    void SaveFactory()
    {
        if (!ready || AdminSession || unreadableSave || progression == null || saveWallet == null) return;
        var staffed = new List<string>();
        var robots = new List<string>();
        for (int i = 0; i < OpenedProcesses; i++)
        {
            var process = progression.processes[i];
            if (process.automated) robots.Add(process.authoringId);
            else if (process.hired) staffed.Add(process.authoringId);
        }
        var record = new FactoryRecord {
            version = 1, openCount = OpenedProcesses,
            lastOpenId = progression.processes[OpenedProcesses - 1].authoringId,
            staffed = staffed.ToArray(), robots = robots.ToArray(), automaticInput = AutomaticInputPurchased,
            supplyStaff = services != null && services.supply != null ? services.supply.WorkerCount : 0,
            fuelHired = FullLine && services != null && services.fuel != null && services.fuel.AttendantHired,
            fuelAutomated = FullLine && services != null && services.fuel != null && services.fuel.IsAutomated,
            balance = saveWallet.currentMoney, delivered = SuccessfulDeliveries, rejected = FailedDeliveries,
            income = DeliveryIncome, day = PrototypeDayCycle.Current?.CaptureDay()
        };
        PlayerPrefs.SetString(StorageKey, JsonUtility.ToJson(record));
        PlayerPrefs.Save();
        nextCheckpoint = Time.unscaledTime + CheckpointInterval;
        savePending = false;
    }
}
