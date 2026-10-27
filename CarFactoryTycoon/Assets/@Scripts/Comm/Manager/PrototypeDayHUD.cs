using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Temporary prototype information only; final interface follows play feedback.</summary>
public sealed class PrototypeDayHUD : MonoBehaviour
{
    public PrototypeDayCycle cycle;
    public PrototypeDefectDisplay defects;
    public FactoryPlayerController player;
    Rect Header => new Rect(Mathf.Max(8, Screen.width - 310), 10, 298, 130);
    Rect Report => new Rect((Screen.width - 440) * .5f, (Screen.height - 340) * .5f, 440, 340);
    public bool BlocksPointer(Vector2 point) => cycle != null && (cycle.IsSettlement || Header.Contains(point));
    void Update()
    {
        if (cycle == null || cycle.IsSettlement || (player != null && player.InteractionUIOpen)) return;
        if (Keyboard.current != null && Keyboard.current.oKey.wasPressedThisFrame) cycle.SetOvertime(!cycle.OvertimeEnabled);
    }
    void OnGUI()
    {
        if (cycle == null) return;
        if (cycle.IsSettlement) { DrawSettlement(); return; }
        GUI.Box(Header, GUIContent.none);
        GUILayout.BeginArea(new Rect(Header.x + 12, Header.y + 8, Header.width - 24, Header.height - 16));
        GUILayout.Label("DAY " + cycle.DayNumber + "  ·  " + cycle.ClockText + (cycle.GameHour >= 20f ? "  특근 시간" : "  정규 근무"));
        GUILayout.Label("보유 " + EconomyManager.Instance.currentMoney + "   ·   미체결 " + (defects != null ? defects.MissingPartCount : 0));
        GUILayout.Label("출고 " + cycle.SoldCars + " / " + cycle.settings.dailyMissionCars + (cycle.MissionComplete ? "  미션 달성" : "  보너스 +" + cycle.settings.dailyMissionBonus));
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
        GUILayout.Label("정상 출고   " + cycle.SoldCars + "대     +" + cycle.SalesIncome);
        GUILayout.Label("하루 미션   " + (cycle.MissionComplete ? "달성  +" + cycle.MissionIncome : "미달성  0"));
        GUILayout.Label("정규 일당   −" + cycle.RegularWages);
        GUILayout.Label("특근비       −" + cycle.OvertimeWages);
        GUILayout.Label("부품 손실   −" + cycle.PartLosses);
        GUILayout.Label("설비 구매   −" + cycle.PurchaseCosts);
        GUILayout.Space(8);
        GUILayout.Label("순수입 " + cycle.NetIncome + "     ·     잔액 " + EconomyManager.Instance.currentMoney);
        if (EconomyManager.Instance.currentMoney < 0) GUILayout.Label("잔액 부족: 수입으로 갚을 때까지 고용·구매가 잠깁니다.");
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("다음 날 08:00 시작", GUILayout.Height(38))) cycle.ContinueNextDay();
        GUILayout.EndArea();
    }
}
