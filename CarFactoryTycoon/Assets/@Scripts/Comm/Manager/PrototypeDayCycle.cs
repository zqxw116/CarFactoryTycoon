using System;
using UnityEngine;

/// <summary>Prototype 08:00-22:00 ledger. Prices and pacing remain phase-two tuning values.</summary>
[DefaultExecutionOrder(-100)]
public sealed class PrototypeDayCycle : MonoBehaviour
{
    public static PrototypeDayCycle Current { get; private set; }
    public static bool SettlementOpen => Current != null && Current.IsSettlement;
    public event Action<Vector3, int> OnAssemblyRewardPaid;
    public ProductionProgression progression;
    public PrototypeBalanceSettings settings;
    public FactoryPlayerController player;
    public SupplyManager supply;
    public FuelStation fuel;
    public PrototypeServicesInteraction services;
    public int DayNumber { get; private set; } = 1;
    public float GameHour { get; private set; } = 8f;
    public bool OvertimeEnabled { get; private set; }
    public bool IsSettlement { get; private set; }
    public int SoldCars { get; private set; }
    public int RejectedDeliveries { get; private set; }
    public int SalesIncome { get; private set; }
    public int MissionIncome { get; private set; }
    public int AssemblyIncome { get; private set; }
    public int PartLosses { get; private set; }
    public int PurchaseCosts { get; private set; }
    public float RegularWagesAccrued { get; private set; }
    public float OvertimeWagesAccrued { get; private set; }
    public int RegularWages { get; private set; }
    public int OvertimeWages { get; private set; }
    public bool MissionComplete => MissionIncome > 0 || missionAwarded;
    public int NetIncome => SalesIncome + MissionIncome + AssemblyIncome - PartLosses - PurchaseCosts - RegularWages - OvertimeWages;
    public int HumanCount
    {
        get
        {
            int count = 0;
            foreach (var process in progression.processes)
                if (process.hired && !process.automated && process.workers != null)
                    foreach (var worker in process.workers) if (worker != null && worker.gameObject.activeInHierarchy) count++;
            if(supply!=null)count+=supply.WorkerCount;
            if(fuel!=null&&fuel.PaidHumanActive)count++;
            return count;
        }
    }
    EconomyManager economy;
    readonly System.Collections.Generic.List<PrototypeProcessCrew> crews = new System.Collections.Generic.List<PrototypeProcessCrew>();
    bool? previousDuty;
    bool previousOvertime;
    bool missionAwarded, ownMoneyChange, paused, previousHiringEnabled;
    public const float SettlementDuration = 10f;
    public float SettlementSecondsRemaining => IsSettlement ? Mathf.Max(0f, (float)(settlementDeadline - Time.realtimeSinceStartupAsDouble)) : 0f;
    public double SettlementStartedAt { get; private set; }
    public double LastSettlementElapsedSeconds { get; private set; }
    public bool LastSettlementWasAccelerated { get; private set; }
    double settlementDeadline;
    bool settlementAccelerated;
    bool continuingDay;
    float previousTimeScale;
    CursorLockMode previousCursor;
    bool previousCursorVisible;
    FactoryHiringUI hiring;
    public string ClockText => Mathf.FloorToInt(GameHour).ToString("00") + ":" + Mathf.Min(59, Mathf.FloorToInt((GameHour % 1f) * 60f)).ToString("00");

    void Awake()
    {
        if (progression == null) progression = GetComponent<ProductionProgression>();
        if (settings == null && progression != null) settings = progression.prototypeSettings;
        if (progression == null || !progression.prototypeMode || settings == null) { enabled = false; return; }
        Current = this;
    }
    void Start()
    {
        economy = EconomyManager.Instance;
        economy.OnVehicleSold += RecordVehicleSale;
        economy.OnMoneyChanged += OnMoneyChanged;
        AssemblyPart.OnAssemblyProgressByWork += RecordAssemblyReward;
        hiring = FindFirstObjectByType<FactoryHiringUI>();
        ApplyDuty();
    }
    void Update()
    {
        if (IsSettlement) UpdateSettlementDeadline();
        else AdvanceClock(Time.deltaTime);
    }
    // Explicit acceleration for technical checks; normal Update uses an absolute real-time deadline.
    public void AdvanceSettlement(float unscaledSeconds)
    {
        if (!IsSettlement || unscaledSeconds <= 0f || float.IsNaN(unscaledSeconds) || float.IsInfinity(unscaledSeconds)) return;
        settlementAccelerated = true;
        settlementDeadline -= unscaledSeconds;
        UpdateSettlementDeadline();
    }
    void UpdateSettlementDeadline()
    {
        if (IsSettlement && Time.realtimeSinceStartupAsDouble >= settlementDeadline) ContinueNextDay();
    }
    public void SetOvertime(bool value)
    {
        if (IsSettlement) return;
        OvertimeEnabled = value;
        ApplyDuty();
    }
    // Splitting at 20 and 22 prevents long frames or accelerated technical checks skipping wages/duty.
    public void AdvanceClock(float seconds)
    {
        if (IsSettlement || settings == null || seconds <= 0f || float.IsNaN(seconds) || float.IsInfinity(seconds)) return;
        float hours = seconds * 14f / Mathf.Max(.1f, settings.dayRealSeconds);
        while (hours > .000001f && !IsSettlement)
        {
            float boundary = GameHour < 20f ? 20f : 22f;
            float step = Mathf.Min(hours, boundary - GameHour);
            int count = HumanCount;
            float hourly = settings.dailyWage / 12f;
            if (GameHour < 20f) RegularWagesAccrued += count * hourly * step;
            else if (OvertimeEnabled) OvertimeWagesAccrued += count * hourly * settings.overtimeWageMultiplier * step;
            GameHour = Mathf.Min(boundary, GameHour + step);
            hours -= step;
            ApplyDuty();
            if (GameHour >= 22f) SettleDay();
        }
    }
    void ApplyDuty()
    {
        bool overtime = GameHour >= 20f && GameHour < 22f && OvertimeEnabled;
        bool duty = !IsSettlement && (GameHour < 20f || overtime);
        if (previousDuty == duty && previousOvertime == overtime) return;
        previousDuty = duty;
        previousOvertime = overtime;
        // Crews hired mid-shift initialize their own duty and join the next shift change.
        progression.GetComponents(crews);
        foreach (var crew in crews) if (crew != null) crew.SetDutyStatus(duty, overtime);
        if(supply!=null)foreach(var worker in supply.supplyWorkers)if(worker!=null&&worker.prototypeMode)worker.SetDutyStatus(duty);
        if(fuel!=null)fuel.SetDutyStatus(duty);
    }
    public void RecordRejectedDelivery()
    {
        if (!IsSettlement) RejectedDeliveries++;
    }
    void RecordVehicleSale(int amount)
    {
        if (IsSettlement) return;
        SoldCars++;
        SalesIncome += Mathf.Max(0, amount);
        if (!missionAwarded && SoldCars >= Mathf.Max(1, settings.dailyMissionCars))
        {
            missionAwarded = true;
            MissionIncome = Mathf.Max(0, settings.dailyMissionBonus);
            economy.Earn(MissionIncome);
        }
    }
    void RecordAssemblyReward(AssemblyPart part, int units)
    {
        if(!enabled||FirstProductionLoop.Current!=null)return;
        if (Current != this || progression == null || !progression.prototypeMode || IsSettlement || part == null || settings == null || economy == null) return;
        int amount = Mathf.Max(0, settings.assemblyReward) * Mathf.Max(0, units);
        if (amount <= 0) return;
        AssemblyIncome += amount;
        Vector3 position = part.transform.position;
        economy.Earn(amount, position);
        OnAssemblyRewardPaid?.Invoke(position, amount);
    }
    public void RecordPartLoss(int amount)
    {
        if (IsSettlement || economy == null || amount <= 0) return;
        PartLosses += amount;
        ownMoneyChange = true;
        economy.Charge(amount);
        ownMoneyChange = false;
    }
    void OnMoneyChanged(int balance, int delta, Vector3? point)
    {
        if (!ownMoneyChange && !IsSettlement && delta < 0) PurchaseCosts -= delta;
    }
    void SettleDay()
    {
        if (IsSettlement) return;
        RegularWages = Mathf.CeilToInt(RegularWagesAccrued - .0001f);
        OvertimeWages = Mathf.CeilToInt(OvertimeWagesAccrued - .0001f);
        ownMoneyChange = true;
        economy.Charge(RegularWages + OvertimeWages);
        ownMoneyChange = false;
        IsSettlement = true;
        SettlementStartedAt = Time.realtimeSinceStartupAsDouble;
        settlementDeadline = SettlementStartedAt + SettlementDuration;
        settlementAccelerated = false;
        ApplyDuty();
        previousTimeScale = Time.timeScale;
        if (hiring != null) { previousHiringEnabled = hiring.enabled; hiring.enabled = false; }
        if (FirstProductionLoop.Current != null && FirstProductionLoop.Current.InvestmentOpen)
            FirstProductionLoop.Current.CloseInvestment();
        if(services!=null)services.ClosePopup();
        previousCursor = Cursor.lockState; previousCursorVisible = Cursor.visible;
        if (player != null) { player.InteractionUIOpen = true; if (player.interactor != null) player.interactor.inputEnabled = false; }
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        Time.timeScale = 0f; paused = true;
    }
    public void ContinueNextDay()
    {
        if (!IsSettlement || continuingDay) return;
        continuingDay = true;
        // Consume settlement before restoring state so manual and timer paths cannot both proceed.
        IsSettlement = false;
        LastSettlementElapsedSeconds = Time.realtimeSinceStartupAsDouble - SettlementStartedAt;
        LastSettlementWasAccelerated = settlementAccelerated;
        RestorePause();
        DayNumber++; GameHour = 8f; OvertimeEnabled = false;
        SoldCars = RejectedDeliveries = SalesIncome = MissionIncome = AssemblyIncome = PartLosses = PurchaseCosts = RegularWages = OvertimeWages = 0;
        RegularWagesAccrued = OvertimeWagesAccrued = 0f;
        missionAwarded = false;
        if (hiring != null) hiring.enabled = previousHiringEnabled;
        if (player != null) { player.InteractionUIOpen = false; if (player.interactor != null) player.interactor.inputEnabled = player.ControlsActive && (FirstProductionLoop.Current == null || !FirstProductionLoop.Current.WashToolActive); }
        ApplyDuty();
        continuingDay = false;
    }
    [Serializable]
    public sealed class DayRecord
    {
        public int day, sales, rejects, salesIncome, bonus, assembly, losses, purchases;
        public float hour, wages, overtimeWages;
        public bool overtime, awarded;
    }

    public DayRecord CaptureDay()
    {
        // Wages have already been charged at settlement. Resume at the next shift.
        if (IsSettlement) return new DayRecord { day = DayNumber + 1, hour = 8f };
        return new DayRecord {
            day = DayNumber, hour = GameHour, overtime = OvertimeEnabled, awarded = missionAwarded,
            sales = SoldCars, rejects = RejectedDeliveries, salesIncome = SalesIncome,
            bonus = MissionIncome, assembly = AssemblyIncome, losses = PartLosses, purchases = PurchaseCosts,
            wages = RegularWagesAccrued, overtimeWages = OvertimeWagesAccrued
        };
    }

    public void RestoreDay(DayRecord record)
    {
        if (record == null || record.day < 1 || !(record.hour >= 8f && record.hour < 22f)) return;
        DayNumber = record.day;
        GameHour = record.hour;
        OvertimeEnabled = record.overtime;
        missionAwarded = record.awarded;
        SoldCars = record.sales;
        RejectedDeliveries = record.rejects;
        SalesIncome = record.salesIncome;
        MissionIncome = record.bonus;
        AssemblyIncome = record.assembly;
        PartLosses = record.losses;
        PurchaseCosts = record.purchases;
        RegularWagesAccrued = record.wages;
        OvertimeWagesAccrued = record.overtimeWages;
        previousDuty = null;
        ApplyDuty();
    }

    void RestorePause()
    {
        if (!paused) return;
        Time.timeScale = previousTimeScale;
        Cursor.lockState = player != null && player.MapMode ? CursorLockMode.None : previousCursor;
        Cursor.visible = player != null && player.MapMode || previousCursorVisible;
        paused = false;
    }
    void OnDestroy()
    {
        RestorePause();
        if (economy != null) { economy.OnVehicleSold -= RecordVehicleSale; economy.OnMoneyChanged -= OnMoneyChanged; }
        AssemblyPart.OnAssemblyProgressByWork -= RecordAssemblyReward;
        if (Current == this) Current = null;
    }
}
