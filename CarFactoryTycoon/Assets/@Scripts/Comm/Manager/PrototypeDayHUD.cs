using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Temporary prototype information only; final interface follows play feedback.</summary>
public sealed class PrototypeDayHUD : MonoBehaviour
{
    public PrototypeDayCycle cycle;
    public PrototypeDefectDisplay defects;
    public FactoryPlayerController player;
    public static Rect HeaderRect => new Rect(PrototypeGui.Width - 310f, 50f, 298f, 130f);
    Rect Header => HeaderRect;
    public static Rect ReportRect => new Rect((PrototypeGui.Width - 440f) * .5f, (PrototypeGui.Height - 380f) * .5f, 440f, 380f);
    Rect Report => ReportRect;
    public bool BlocksPointer(Vector2 point) => cycle != null && (cycle.IsSettlement || ((player == null || !player.InteractionUIOpen) && PrototypeGui.ToScreenRect(Header).Contains(point)));
    void Update()
    {
        if (cycle == null || cycle.IsSettlement || (player != null && player.InteractionUIOpen)) return;
        if (Keyboard.current != null && Keyboard.current.oKey.wasPressedThisFrame) cycle.SetOvertime(!cycle.OvertimeEnabled);
    }
    void OnGUI()
    {
        if (cycle == null) return;
        using var gui = PrototypeGui.Scope();
        if (cycle.IsSettlement) { DrawSettlement(); return; }
        if (player != null && player.InteractionUIOpen) return;
        GUI.Box(Header, GUIContent.none);
        GUILayout.BeginArea(new Rect(Header.x + 12, Header.y + 8, Header.width - 24, Header.height - 16));
        GUILayout.Label("DAY " + cycle.DayNumber + "  ·  " + cycle.ClockText + (cycle.GameHour >= 20f ? "  특근 시간" : "  정규 근무"));
        if (FirstProductionLoop.Current != null)
            GUILayout.Label("근무 인원 " + cycle.HumanCount + "명 · 납품 미완료 " + cycle.RejectedDeliveries + "대");
        else
            GUILayout.Label("보유 " + EconomyManager.Instance.currentMoney + "   ·   미체결 " + (defects != null ? defects.MissingPartCount : 0));
        GUILayout.Label("납품 " + cycle.SoldCars + " / " + cycle.settings.dailyMissionCars + (cycle.MissionComplete ? "  미션 달성" : "  보너스 +" + cycle.settings.dailyMissionBonus));
        bool clickable = player == null || player.MapMode || Cursor.lockState != CursorLockMode.Locked;
        GUI.enabled = clickable;
        if (GUILayout.Button("[O] 특근 " + (cycle.OvertimeEnabled ? "ON · 20~22시" : "OFF · 20시 퇴근"))) cycle.SetOvertime(!cycle.OvertimeEnabled);
        GUI.enabled = true;
        GUILayout.EndArea();
    }
    void DrawSettlement()
    {
        GUI.Box(Report, GUIContent.none);
        GUILayout.BeginArea(new Rect(Report.x + 24, Report.y + 18, Report.width - 48, Report.height - 36));
        GUILayout.Label("DAY " + cycle.DayNumber + "  ·  22:00 작업 정산");
        GUILayout.Space(10);
        GUILayout.Label("정상 납품   " + cycle.SoldCars + "대     +" + cycle.SalesIncome);
        if (FirstProductionLoop.Current != null)
            GUILayout.Label("미완료 인수   " + cycle.RejectedDeliveries + "대     0원");
        else
            GUILayout.Label("부품 체결   +" + cycle.AssemblyIncome);
        GUILayout.Label("하루 미션   " + (cycle.MissionComplete ? "달성  +" + cycle.MissionIncome : "미달성  0"));
        GUILayout.Label("정규 일당   −" + cycle.RegularWages);
        GUILayout.Label("특근비       −" + cycle.OvertimeWages);
        GUILayout.Label("부품 손실   −" + cycle.PartLosses);
        GUILayout.Label("고용·설비 구매   −" + cycle.PurchaseCosts);
        GUILayout.Space(8);
        GUILayout.Label("순수입 " + cycle.NetIncome + "     ·     잔액 " + EconomyManager.Instance.currentMoney);
        if (EconomyManager.Instance.currentMoney < 0) GUILayout.Label("잔액 부족: 수입으로 갚을 때까지 고용·구매가 잠깁니다.", new GUIStyle(GUI.skin.label) { wordWrap = true });
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("다음 날 08:00 시작 · " + Mathf.CeilToInt(cycle.SettlementSecondsRemaining) + "초 후 자동 시작", GUILayout.Height(38))) cycle.ContinueNextDay();
        GUILayout.EndArea();
    }
}
