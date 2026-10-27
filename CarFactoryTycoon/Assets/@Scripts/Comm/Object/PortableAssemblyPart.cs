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

    public bool TryHold(PlayerAssemblyInteractor holder, Transform anchor)
    {
        if (holder == null || anchor == null || IsHeld) return false;
        HeldBy = holder;
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
        if (visuals == null) return;
        foreach (var visual in visuals) if (visual != null) visual.enabled = visible;
    }
}
