using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

/// <summary>M1 controls only. The line lever changes belt speed, never the simulation clock.</summary>
[DefaultExecutionOrder(-130)]
public sealed class PrototypeLineController : MonoBehaviour
{
    public enum LeverPosition { Stop, Low, High }
    public static PrototypeLineController Current { get; private set; }
    public PrototypeBalanceSettings settings;
    public CarSpawner spawner;
    public FactoryPlayerController player;
    public Transform supplyTunnel, leverRoot, leverHandle;
    public TextMesh queueLabel;
    PrototypeTunnelFeedback tunnelFeedback;
    public LeverPosition lever = LeverPosition.Low;
    [SerializeField, Min(0)] int highSpeedLevel;
    public float CurrentSpeed => settings == null ? 0f : lever == LeverPosition.Stop ? 0f :
        lever == LeverPosition.Low ? settings.lowSpeed : settings.initialHighSpeed * Mathf.Pow(settings.highSpeedUpgradeFactor, highSpeedLevel);
    public int HighSpeedLevel => highSpeedLevel;
    Rect panel => new Rect(16, Screen.height - 126, 510, 110);

    void Awake()
    {
        Current = this;
        if (settings == null || spawner == null) { Debug.LogError("[M1] Missing line settings or spawner."); enabled = false; return; }
        spawner.manualInput = true;
        spawner.queueCapacity = settings.queueCapacity;
        spawner.globalLineSpeed = CurrentSpeed;
        foreach (var s in FindObjectsByType<WorkerStation>(FindObjectsInactive.Include, FindObjectsSortMode.None)) { s.flowThrough = true; s.workZoneMeters = settings.workZoneMeters; }
        foreach (var s in FindObjectsByType<WheelStation>(FindObjectsInactive.Include, FindObjectsSortMode.None)) { s.flowThrough = true; s.workZoneMeters = settings.workZoneMeters; }
        foreach (var s in FindObjectsByType<StationController>(FindObjectsInactive.Include, FindObjectsSortMode.None)) { s.flowThrough = true; s.workZoneMeters = settings.workZoneMeters; }
    }
    void Start()
    {
        EconomyManager.Instance.currentMoney = settings.startingMoney;
        ApplySpeed();
        tunnelFeedback = GetComponent<PrototypeTunnelFeedback>();
        if (tunnelFeedback == null) tunnelFeedback = gameObject.AddComponent<PrototypeTunnelFeedback>();
        tunnelFeedback.tunnel = supplyTunnel;
        tunnelFeedback.lever = leverRoot;
        tunnelFeedback.queueLabel = queueLabel;
        var camera = FindFirstObjectByType<CameraController>();
        if (camera != null && player == null) camera.SetOverview(true);
    }
    public void SetLever(LeverPosition value)
    {
        if (lever == value) return;
        lever = value; ApplySpeed();
        if (tunnelFeedback != null) tunnelFeedback.NotifyLeverChanged();
    }
    // Called by the future paid upgrade interaction. M1 does not grant free upgrades in its UI.
    public void SetHighSpeedLevel(int value) { highSpeedLevel = Mathf.Max(0, value); ApplySpeed(); }
    void ApplySpeed()
    {
        if (spawner == null) return;
        spawner.globalLineSpeed = CurrentSpeed;
        if (LineTrafficManager.Instance != null)
            foreach (var car in LineTrafficManager.Instance.ActiveCars) if (car != null) car.moveSpeed = CurrentSpeed;
        if (leverHandle != null) leverHandle.localRotation = Quaternion.Euler(-35f + (int)lever * 35f, 0, 0);
    }
    void Update()
    {
        if (PrototypeDayCycle.Current != null && PrototypeDayCycle.Current.IsSettlement) return;
        var mouse = Mouse.current;
        bool localPlayer = player != null && !player.MapMode;
        if (Camera.main == null) return;
        if (localPlayer)
        {
            bool pressed=Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame || mouse != null && mouse.leftButton.wasPressedThisFrame;
            if (!player.ControlsActive || !pressed) return;
        }
        else if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;
        var pos = localPlayer ? new Vector2(Screen.width * .5f, Screen.height * .5f) : mouse.position.ReadValue();
        var hud = GetComponent<PrototypeDayHUD>();
        if ((hud != null && hud.BlocksPointer(new Vector2(pos.x, Screen.height - pos.y))) || panel.Contains(new Vector2(pos.x, Screen.height - pos.y)) ||
            (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())) return;
        if (Keyboard.current != null && (Keyboard.current.leftAltKey.isPressed || Keyboard.current.rightAltKey.isPressed)) return;
        var reach = localPlayer ? player.transform.position + Vector3.up * 1.1f : Vector3.zero;
        float rayLength = localPlayer ? 4.5f + Vector3.Distance(Camera.main.transform.position, reach) : 500f;
        if (!Physics.Raycast(Camera.main.ScreenPointToRay(pos), out var hit, rayLength, ~(1<<10), QueryTriggerInteraction.Collide)) return;
        if (localPlayer && Vector3.Distance(reach, hit.point) > 3.5f) return;
        if (supplyTunnel != null && (hit.transform == supplyTunnel || hit.transform.IsChildOf(supplyTunnel))) spawner.RequestVehicle();
        else if (leverRoot != null && (hit.transform == leverRoot || hit.transform.IsChildOf(leverRoot)))
            SetLever((LeverPosition)(((int)lever + 1) % 3));
    }
    void OnGUI()
    {
        if (settings == null || spawner == null || (player != null && !player.MapMode) || (PrototypeDayCycle.Current != null && PrototypeDayCycle.Current.IsSettlement)) return;
        GUILayout.BeginArea(panel, GUI.skin.box);
        GUILayout.Label("M1 라인 · 대기 " + spawner.PendingCount + "/" + spawner.queueCapacity + " · " + CurrentSpeed.ToString("0.00") + " m/s");
        GUILayout.BeginHorizontal();
        GUI.enabled = spawner.PendingCount < spawner.queueCapacity;
        if (GUILayout.Button("터널 투입 +1", GUILayout.Height(30))) spawner.RequestVehicle();
        GUI.enabled = true;
        if (GUILayout.Button(lever == LeverPosition.Stop ? "● 정지" : "정지", GUILayout.Height(30))) SetLever(LeverPosition.Stop);
        if (GUILayout.Button(lever == LeverPosition.Low ? "● 저속" : "저속", GUILayout.Height(30))) SetLever(LeverPosition.Low);
        if (GUILayout.Button(lever == LeverPosition.High ? "● 고속" : "고속", GUILayout.Height(30))) SetLever(LeverPosition.High);
        GUILayout.EndHorizontal();
        GUILayout.Label("터널 클릭: 대기 추가 / 레버 클릭: 속도 전환 · 카메라 2: 전체 보기");
        GUILayout.EndArea();
    }
    void OnDestroy() { if (Current == this) Current = null; }
}
