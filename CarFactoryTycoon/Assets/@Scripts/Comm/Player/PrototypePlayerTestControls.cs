using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Runtime-only prototype controls for nearby test vehicles. No scene wiring or economy rewards.</summary>
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
        if (keyboard == null || IsModalOpen()) return;
        if (keyboard.digit1Key.wasPressedThisFrame) TrySetNearbyVehicle(false);
        if (keyboard.digit2Key.wasPressedThisFrame) TrySetNearbyVehicle(true);
    }

    public CarController ContactCar => closestCar;

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
        ProductionProgression progression = ProductionProgression.Current;
        if (progression == null || !progression.prototypeMode)
        { status = "프로토타입에서만 사용할 수 있습니다"; return false; }
        PrototypeDayCycle cycle = PrototypeDayCycle.Current;
        if (cycle != null && cycle.IsSettlement)
        { status = "정산 중에는 사용할 수 없습니다"; return false; }
        if (closestCar == null) { status = "접촉 차량 없음 — 사용 불가"; return false; }
        if (!closestCar.CanRepair) { status = "수리 가능 시간이 지난 차량입니다"; return false; }

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
        ProductionProgression progression = ProductionProgression.Current;
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
        const float width = 270f;
        float x = 18f;
        float y = Screen.height - 220f;
        string carLabel = closestCar != null ? "가까운 차량 · 테스트 가능" : "접촉 차량 없음 — 사용 불가";
        GUI.Box(new Rect(x, y, width, 118f), "프로토타입 테스트");
        GUI.Label(new Rect(x + 10f, y + 24f, width - 20f, 22f), carLabel);
        bool available = IsAvailable();
        GUI.enabled = available && !IsModalOpen();
        if (GUI.Button(new Rect(x + 10f, y + 49f, width - 20f, 27f), "1 · 마지막 2개 미체결")) TrySetNearbyVehicle(false);
        if (GUI.Button(new Rect(x + 10f, y + 80f, width - 20f, 27f), "2 · 전체 완성")) TrySetNearbyVehicle(true);
        GUI.enabled = true;
        if (!string.IsNullOrEmpty(status)) GUI.Label(new Rect(x, y + 121f, width, 36f), status);
    }

    private bool IsAvailable()
    {
        ProductionProgression progression = ProductionProgression.Current;
        PrototypeDayCycle cycle = PrototypeDayCycle.Current;
        return closestCar != null && closestCar.CanRepair && progression != null && progression.prototypeMode &&
            (cycle == null || !cycle.IsSettlement);
    }
}
