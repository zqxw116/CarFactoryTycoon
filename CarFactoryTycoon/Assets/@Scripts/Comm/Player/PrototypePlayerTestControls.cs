using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Runtime-only administrator controls for camera, nearby test vehicles, and prototype economy.</summary>
[DefaultExecutionOrder(50)]
public sealed class PrototypePlayerTestControls : MonoBehaviour
{
    [SerializeField, Min(0.1f)] private float contactRadius = 1.65f;
    private readonly Collider[] overlapBuffer = new Collider[64];
    private readonly List<AssemblyPart> orderedParts = new List<AssemblyPart>(64);
    private CarController closestCar;
    private float refreshAt;
    private string status = "";

    private void Update()
    {
        if (Time.unscaledTime >= refreshAt)
            RefreshContactCar();

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || IsModalOpen() || FirstProductionLoop.Current != null) return;
        if (keyboard.digit1Key.wasPressedThisFrame) TrySetNearbyVehicle(false);
        if (keyboard.digit2Key.wasPressedThisFrame) TrySetNearbyVehicle(true);
    }

    public CarController ContactCar => closestCar;

    static bool panelExpanded;
    static ProductionProgression Progression => FirstProductionLoop.Current != null ?
        FirstProductionLoop.Current.progression : ProductionProgression.Current;
    public static Rect AdminPanelRect => FirstProductionLoop.Current != null
        ? new Rect(12f, 164f, 296f, panelExpanded ? 346f : 42f)
        : new Rect(18f, 54f, 296f, panelExpanded ? 274f : 60f);

    public static bool PointerOverAdminPanel(Mouse mouse)
    {
        if (mouse == null) return false;
        return AdminPanelRect.Contains(PrototypeGui.PointerPosition);
    }

    public void RefreshContactCar()
    {
        refreshAt = Time.unscaledTime + 0.15f;
        Vector3 center = transform.position + Vector3.up * 0.85f;
        int count = Physics.OverlapSphereNonAlloc(center, contactRadius, overlapBuffer, 1 << 11, QueryTriggerInteraction.Ignore);
        CarController next = null;
        float closestDistance = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            Collider hit = overlapBuffer[i];
            overlapBuffer[i] = null;
            if (hit == null) continue;
            CarController car = hit.GetComponentInParent<CarController>();
            if (car == null || !car.isActiveAndEnabled) continue;
            float distance = (hit.ClosestPoint(center) - center).sqrMagnitude;
            if (distance < closestDistance)
            {
                closestDistance = distance;
                next = car;
            }
        }
        closestCar = next;
    }

    public bool TrySetNearbyVehicle(bool completeAll)
    {
        RefreshContactCar();
        if (IsModalOpen()) { status = "창을 닫은 뒤 사용할 수 있습니다"; return false; }
        ProductionProgression progression = Progression;
        if (progression == null || !progression.prototypeMode)
        { status = "프로토타입에서만 사용할 수 있습니다"; return false; }
        PrototypeDayCycle cycle = PrototypeDayCycle.Current;
        if (cycle != null && cycle.IsSettlement)
        { status = "정산 중에는 사용할 수 없습니다"; return false; }
        if (closestCar == null) { status = "접촉 차량 없음 — 사용 불가"; return false; }
        if (!closestCar.CanRepair) { status = "수리 가능 시간이 지난 차량입니다"; return false; }

        FirstProductionLoop.Current?.BeginAdminSession();
        return completeAll ? CompleteAll() : LeaveLastTwoUnassembled();
    }

    private bool IsModalOpen()
    {
        FactoryPlayerController player = GetComponent<FactoryPlayerController>();
        return player != null && player.InteractionUIOpen;
    }

    private void GetOrderedParts(CarController car)
    {
        orderedParts.Clear();
        if (car == null) return;
        AssemblyPart[] hierarchyOrder = car.GetComponentsInChildren<AssemblyPart>(true);
        bool[] added = new bool[hierarchyOrder.Length];
        ProductionProgression progression = Progression;
        if (progression != null && progression.processes != null)
        {
            // Follow the authored process/type order; duplicate same-type parts retain car hierarchy order.
            for (int processIndex = 0; processIndex < progression.processes.Length; processIndex++)
            {
                ProductionProgression.ProcessEntry process = progression.processes[processIndex];
                if (process == null || process.parts == null) continue;
                for (int typeIndex = 0; typeIndex < process.parts.Length; typeIndex++)
                    for (int partIndex = 0; partIndex < hierarchyOrder.Length; partIndex++)
                    {
                        AssemblyPart part = hierarchyOrder[partIndex];
                        if (added[partIndex] || part == null || part.myType == PartType.None ||
                            part.myType == PartType.Frame_1 || part.myType != process.parts[typeIndex]) continue;
                        orderedParts.Add(part);
                        added[partIndex] = true;
                    }
            }
        }
        for (int i = 0; i < hierarchyOrder.Length; i++)
        {
            AssemblyPart part = hierarchyOrder[i];
            if (!added[i] && part != null && part.myType != PartType.None && part.myType != PartType.Frame_1)
                orderedParts.Add(part);
        }
    }

    private bool LeaveLastTwoUnassembled()
    {
        GetOrderedParts(closestCar);
        if (orderedParts.Count < 2)
        {
            status = "미체결 부품 2개를 찾지 못했습니다";
            return false;
        }
        int keepFrom = Mathf.Max(0, orderedParts.Count - 2);
        for (int i = 0; i < keepFrom; i++) { orderedParts[i].Reset(); orderedParts[i].SetAssembled(); }
        for (int i = keepFrom; i < orderedParts.Count; i++) orderedParts[i].Reset();
        status = $"불량 시나리오 · 마지막 2개 미체결: {PlayerAssemblyInteractor.PartLabel(orderedParts[keepFrom].myType)}, {PlayerAssemblyInteractor.PartLabel(orderedParts[keepFrom + 1].myType)}";
        return true;
    }

    private bool CompleteAll()
    {
        GetOrderedParts(closestCar);
        for (int i = 0; i < orderedParts.Count; i++) { orderedParts[i].Reset(); orderedParts[i].SetAssembled(); }
        status = $"전체 체결 완료 · {orderedParts.Count}개";
        return orderedParts.Count > 0;
    }

    private void OnGUI()
    {
        FactoryPlayerController player = GetComponent<FactoryPlayerController>();
        if (player != null && (player.MapMode || player.InteractionUIOpen)) return;

        using var gui = PrototypeGui.Scope();
        Rect panel = AdminPanelRect;
        float x = panel.x + 10f;
        float y = panel.y + 36f;
        GUI.Box(panel, GUIContent.none);
        if (GUI.Button(new Rect(panel.x + 10f, panel.y + 8f, 276f, 26f),
            panelExpanded ? "관리자 테스트 도구 ▲ 접기" : "관리자 테스트 도구 ▼ 펼치기")) panelExpanded = !panelExpanded;
        if (!panelExpanded) return;
        bool modal = IsModalOpen();
        ProductionProgression progression = Progression;
        bool prototype = progression != null && progression.prototypeMode;
        PrototypeDayCycle cycle = PrototypeDayCycle.Current;
        bool settlement = cycle != null && cycle.IsSettlement;
        bool canEdit = prototype && !settlement && !modal;
        string carLabel = closestCar == null ? "차량 없음 · 반경 1.65m" :
            closestCar.CanRepair ? "차량 접촉 중 · 테스트 가능" : "수리 기한 만료 · 변경 불가";
        GUI.Label(new Rect(x, y + 17f, 276f, 20f), carLabel);

        GUI.enabled = !modal;
        if (GUI.Button(new Rect(x, y + 39f, 276f, 27f), player == null ? "카메라 전환 (C)" : (player.FirstPerson ? "카메라 · 3인칭 (C)" : "카메라 · 1인칭 (C)")))
        {
            if (player != null) player.SetFirstPerson(!player.FirstPerson);
            else status = "플레이어 카메라를 찾지 못했습니다";
        }
        GUI.enabled = canEdit && IsAvailable();
        if (GUI.Button(new Rect(x, y + 70f, 276f, 27f), FirstProductionLoop.Current == null ? "1 · 마지막 2개 미체결" : "접촉 차량 · 마지막 2개 미체결")) TrySetNearbyVehicle(false);
        if (GUI.Button(new Rect(x, y + 101f, 276f, 27f), FirstProductionLoop.Current == null ? "2 · 전체 완성" : "접촉 차량 · 전체 체결")) TrySetNearbyVehicle(true);
        GUI.enabled = canEdit;
        if (GUI.Button(new Rect(x, y + 132f, 134f, 27f), "+1,000원")) GrantDebugMoney(1000);
        if (GUI.Button(new Rect(x + 142f, y + 132f, 134f, 27f), "+10,000원")) GrantDebugMoney(10000);
        var loop = FirstProductionLoop.Current;
        float extra = 0f;
        if (loop != null)
        {
            if (GUI.Button(new Rect(x, y + 162f, 276f, 27f), "전체 조립 라인 개방 + 사람 작업자 배치") && loop.AdminOpenStaffedLine())
                status = "전체 공정 작업자 배치 · 새 차량부터 전체 공정";
            if (GUI.Button(new Rect(x, y + 192f, 276f, 27f), "테스트 차량 자동 세척  " + (loop.AdminWashVehicles ? "ON" : "OFF")))
                loop.SetAdminWashVehicles(!loop.AdminWashVehicles);
            if (GUI.Button(new Rect(x, y + 222f, 276f, 27f), "다음 차량 1대 투입 요청"))
            {
                bool queued = loop.spawner.RequestVehicle();
                if (queued) loop.BeginAdminSession();
                status = queued ? "투입 대기에 추가" : "투입 대기가 가득 찼습니다";
            }
            extra = 90f;
        }
        GUI.enabled = true;

        string availability = modal ? "창을 닫으면 사용할 수 있습니다" : !prototype ? "프로토타입 장면에서만 사용 가능" :
            settlement ? "정산 중 · 경제/차량 변경 잠김" : closestCar == null ? "차량 가까이에서 체결 상태 변경 가능" :
            !closestCar.CanRepair ? "수리 기한이 지난 차량입니다" : "차량 테스트 및 자금 조정 가능";
        GUI.Label(new Rect(x, y + 162f + extra, 276f, 26f), loop != null && loop.AdminSession ? "관리자 테스트 중 · 일반 진행 저장 일시 중지" : availability);
        if (!string.IsNullOrEmpty(status)) GUI.Label(new Rect(x, y + 190f + extra, 276f, 26f), status);
    }

    private void GrantDebugMoney(int amount)
    {
        ProductionProgression progression = Progression;
        if (progression == null || !progression.prototypeMode) { status = "프로토타입에서만 사용할 수 있습니다"; return; }
        PrototypeDayCycle cycle = PrototypeDayCycle.Current;
        if (cycle != null && cycle.IsSettlement) { status = "정산 중에는 사용할 수 없습니다"; return; }
        FirstProductionLoop.Current?.BeginAdminSession();
        EconomyManager.Instance.Earn(amount);
        status = $"자금 +{amount:N0} · 미션 진행에는 반영되지 않음";
    }

    private bool IsAvailable()
    {
        ProductionProgression progression = Progression;
        PrototypeDayCycle cycle = PrototypeDayCycle.Current;
        return closestCar != null && closestCar.CanRepair && progression != null && progression.prototypeMode &&
            (cycle == null || !cycle.IsSettlement);
    }
}
