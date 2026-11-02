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
    Rect panel => new Rect(16, PrototypeGui.Height - 120, 510, 96);

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
    readonly Collider[] contactHits=new Collider[48];
    public bool TryHammerContact(Vector3 from,Vector3 to,float radius)
    {
        if(player==null || player.MapMode || player.InteractionUIOpen ||
            PrototypeDayCycle.Current!=null && PrototypeDayCycle.Current.IsSettlement)return false;
        var chest=player.transform.position+Vector3.up*1.1f;
        if(Vector3.Distance(chest,to)>player.interactor.interactionRange)return false;
        Physics.SyncTransforms();
        int count=Physics.OverlapCapsuleNonAlloc(from,to,Mathf.Clamp(radius,.04f,.22f),contactHits,~(1<<10),QueryTriggerInteraction.Collide);
        for(int i=0;i<count;i++)
        {
            var hit=contactHits[i].transform;
            bool tunnel=supplyTunnel!=null && (hit==supplyTunnel || hit.IsChildOf(supplyTunnel));
            bool leverHit=leverRoot!=null && (hit==leverRoot || hit.IsChildOf(leverRoot));
            if(!tunnel && !leverHit)continue;
            var point=contactHits[i].ClosestPoint(to);
            if(!player.CanReachInteraction(point,player.interactor.interactionRange))continue;
            if(tunnel)spawner.RequestVehicle();
            else SetLever((LeverPosition)(((int)lever+1)%3));
            return true;
        }
        return false;
    }
    void OnGUI()
    {
        if(settings==null || spawner==null || player!=null && !player.MapMode)return;
        using var gui=PrototypeGui.Scope();
        GUI.Box(panel,"라인 · 대기 "+spawner.PendingCount+"/"+spawner.queueCapacity+" · "+CurrentSpeed.ToString("0.00")+" m/s");
        GUI.Label(new Rect(panel.x+12,panel.y+35,panel.width-24,50),"가까이에서 망치로 터널을 두드려 투입 / 레버를 두드려 속도 전환");
    }
    void OnDestroy() { if (Current == this) Current = null; }
}
