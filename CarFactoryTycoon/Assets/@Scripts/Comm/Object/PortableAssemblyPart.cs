using UnityEngine;

/// <summary>A single physical stock item, independent of the vehicle's assembly placeholder.</summary>
public sealed class PortableAssemblyPart : MonoBehaviour
{
    public PartType PartType { get; private set; }
    public PlayerAssemblyInteractor HeldBy { get; private set; }
    public bool IsHeld => HeldBy != null;
    public Rigidbody Body { get; private set; }
    public BoxCollider PhysicsCollider { get; private set; }
    public Bounds LocalBounds { get; private set; }

    private Renderer[] visuals;
    private bool[] originalRendererStates;
    private bool visibleRequested = true;
    private bool blinkVisible = true;
    private bool droppedLifetimeActive;
    private bool lossRecorded;

    public float LifetimeSeconds => Mathf.Max(0f, Settings != null ? Settings.droppedPartLifetime : 300f);
    public float WarningSeconds => Mathf.Max(0f, Settings != null ? Settings.droppedPartWarningSeconds : 30f);
    public float DroppedLifetimeElapsed { get; private set; }
    public float DroppedLifetimeRemaining => Mathf.Max(0f, LifetimeSeconds - DroppedLifetimeElapsed);
    public bool IsInWarningPeriod => droppedLifetimeActive && !IsHeld && !IsExpired &&
        DroppedLifetimeRemaining <= WarningSeconds;
    public bool IsExpired { get; private set; }

    private PrototypeBalanceSettings Settings =>
        ProductionProgression.Current != null ? ProductionProgression.Current.prototypeSettings : null;

    public static PortableAssemblyPart Create(PartType type, GameObject prefab, Vector3 position, Quaternion rotation)
    {
        if (prefab == null || type == PartType.None || type == PartType.Frame_1) return null;
        var root = new GameObject("Portable_" + type);
        root.layer = 9;
        root.transform.SetPositionAndRotation(position, rotation);
        var model = Instantiate(prefab, root.transform);
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        foreach (var part in model.GetComponentsInChildren<AssemblyPart>(true)) part.enabled = false;
        foreach (var collider in model.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        foreach (var rb in model.GetComponentsInChildren<Rigidbody>(true))
        {
            rb.isKinematic = true; rb.detectCollisions = false;
            Destroy(rb);
        }
        foreach (var child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 9;
        model.SetActive(true);
        var portable = root.AddComponent<PortableAssemblyPart>();
        portable.PartType = type;
        portable.visuals = model.GetComponentsInChildren<Renderer>(true);
        portable.originalRendererStates = new bool[portable.visuals.Length];
        for (int i = 0; i < portable.visuals.Length; i++)
            portable.originalRendererStates[i] = portable.visuals[i] != null && portable.visuals[i].enabled;

        Bounds bounds = CalculateBounds(root.transform, model);
        // Center the actual model on the grip/physics pivot without changing its dimensions.
        model.transform.localPosition -= bounds.center;
        bounds.center = Vector3.zero;
        portable.LocalBounds = bounds;
        portable.Body = root.AddComponent<Rigidbody>();
        portable.Body.mass = 2f; // Temporary handling value; final tuning follows play feedback.
        portable.Body.isKinematic = true;
        portable.Body.useGravity = false;
        portable.Body.detectCollisions = false;
        portable.Body.interpolation = RigidbodyInterpolation.None;
        portable.Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        portable.PhysicsCollider = root.AddComponent<BoxCollider>();
        portable.PhysicsCollider.center = bounds.center;
        portable.PhysicsCollider.size = Vector3.Max(bounds.size, Vector3.one * .04f);
        portable.PhysicsCollider.enabled = false;
        return portable;
    }

    private static Bounds CalculateBounds(Transform root, GameObject model)
    {
        bool found = false;
        Bounds result = new Bounds(Vector3.zero, Vector3.one * .15f);
        foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null) continue;
            var bounds = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 point = root.InverseTransformPoint(filter.transform.TransformPoint(corner));
                if (!found) { result = new Bounds(point, Vector3.zero); found = true; }
                else result.Encapsulate(point);
            }
        }
        return result;
    }

    private void Update() => AdvanceDroppedLifetime(Time.deltaTime);

    /// <summary>Advances the dropped timer deterministically; held and never-thrown items do not age.</summary>
    public void AdvanceDroppedLifetime(float delta)
    {
        if (!droppedLifetimeActive || IsHeld || IsExpired || delta <= 0f) return;

        DroppedLifetimeElapsed = Mathf.Min(LifetimeSeconds, DroppedLifetimeElapsed + delta);
        if (DroppedLifetimeElapsed >= LifetimeSeconds)
        {
            IsExpired = true;
            droppedLifetimeActive = false;
            Show(false);
            if (!lossRecorded)
            {
                lossRecorded = true;
                var progression = ProductionProgression.Current;
                if (progression != null && progression.prototypeSettings != null)
                    PrototypeDayCycle.Current?.RecordPartLoss(progression.prototypeSettings.lostPartCost);
                else
                    PrototypeDayCycle.Current?.RecordPartLoss(10);
            }
            Destroy(gameObject);
            return;
        }

        bool shouldBlink = IsInWarningPeriod;
        bool nextBlinkVisible = !shouldBlink ||
            Mathf.FloorToInt((DroppedLifetimeElapsed - (LifetimeSeconds - WarningSeconds)) / .5f) % 2 == 0;
        if (blinkVisible != nextBlinkVisible)
        {
            blinkVisible = nextBlinkVisible;
            ApplyRendererVisibility();
        }
    }

    public bool TryHold(PlayerAssemblyInteractor holder, Transform anchor)
    {
        if (holder == null || anchor == null || IsHeld || IsExpired) return false;
        HeldBy = holder;
        blinkVisible = true;
        if (!Body.isKinematic) { Body.linearVelocity = Vector3.zero; Body.angularVelocity = Vector3.zero; }
        Body.isKinematic = true; Body.useGravity = false; Body.detectCollisions = false;
        Body.interpolation = RigidbodyInterpolation.None;
        PhysicsCollider.enabled = false;
        transform.SetParent(anchor, true);
        // Keep the near face ahead of the first-person eye even for deep/large parts.
        transform.localPosition = Vector3.forward * Mathf.Max(0f, LocalBounds.extents.z + .35f - anchor.localPosition.z);
        transform.localRotation = Quaternion.identity;
        Show(true);
        return true;
    }

    public bool Throw(PlayerAssemblyInteractor holder, Vector3 velocity)
    {
        if (HeldBy != holder || holder == null) return false;
        HeldBy = null;
        droppedLifetimeActive = true;
        transform.SetParent(null, true);
        Show(true);
        PhysicsCollider.enabled = true;
        Body.detectCollisions = true; Body.isKinematic = false; Body.useGravity = true;
        Body.interpolation = RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        Body.linearVelocity = velocity;
        Body.angularVelocity = transform.right * 2f;
        return true;
    }

    public void Show(bool visible)
    {
        visibleRequested = visible;
        ApplyRendererVisibility();
    }

    private void ApplyRendererVisibility()
    {
        if (visuals == null) return;
        bool visible = visibleRequested && blinkVisible;
        for (int i = 0; i < visuals.Length; i++)
            if (visuals[i] != null) visuals[i].enabled = visible && originalRendererStates[i];
    }
}
