using UnityEngine;

/// <summary>
/// Physical, non-trigger car hull used for player/part contact. The legacy root trigger
/// remains dedicated to line and station detection.
/// </summary>
[DisallowMultipleComponent]
public sealed class CarBodyCollision : MonoBehaviour
{
    public const int CarBodyLayer = 11;
    const string ChildName = "CarBodyCollision";

    /// <summary>Creates the independent collision body if this vehicle does not already have one.</summary>
    public static CarBodyCollision EnsureCreated(GameObject vehicleRoot)
    {
        if (vehicleRoot == null) return null;

        // Installed/carried placeholders never participate as loose physical parts.
        // Preserve the legacy Origin mesh and the root trigger; the independent hull owns contact.
        foreach (var part in vehicleRoot.GetComponentsInChildren<AssemblyPart>(true))
        {
            foreach (var child in part.GetComponentsInChildren<Transform>(true))
                if (child.gameObject != vehicleRoot && child.name != "Origin") child.gameObject.layer = 9;
            foreach (var collider in part.GetComponentsInChildren<Collider>(true))
                if (collider.gameObject != vehicleRoot && collider.name != "Origin") collider.enabled = false;
        }
        var existing = vehicleRoot.GetComponentInChildren<CarBodyCollision>(true);
        if (existing != null) return existing;

        var rootTrigger = vehicleRoot.GetComponent<BoxCollider>();
        Vector3 sourceSize = rootTrigger != null ? rootTrigger.size : new Vector3(2.37f, 2.96f, 5.12f);
        Vector3 sourceCenter = rootTrigger != null ? rootTrigger.center : Vector3.zero;

        var body = new GameObject(ChildName);
        body.layer = CarBodyLayer;
        body.transform.SetParent(vehicleRoot.transform, false);
        var bodyRigidbody = body.AddComponent<Rigidbody>();
        bodyRigidbody.isKinematic = true;
        bodyRigidbody.useGravity = false;
        bodyRigidbody.interpolation = RigidbodyInterpolation.None; // Parenting supplies motion; interpolation would overwrite the child pose.
        bodyRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        var collision = body.AddComponent<CarBodyCollision>();
        collision.CreateLocalHull(sourceCenter, sourceSize);
        return collision;
    }

    // Shared scratch buffers: Unity actor movement runs sequentially on the main thread.
    static readonly Collider[] actorOverlaps = new Collider[32];
    public static Vector3 ConstrainActorStep(Vector3 position, Vector3 step, float radius = .32f, float height = 1.8f)
    {
        radius = Mathf.Max(.1f, radius);
        height = Mathf.Max(radius * 2f, height);
        Vector3 start = position;
        // A moving kinematic hull can reach an idle actor between its own movement calls.
        // Resolve against the same primitive boxes used by the sweep, without adding actor rigidbodies.
        for (int pass = 0; pass < 3; pass++)
        {
            int count = Physics.OverlapCapsuleNonAlloc(position + Vector3.up * radius,
                position + Vector3.up * (height - radius), radius, actorOverlaps, 1 << CarBodyLayer, QueryTriggerInteraction.Ignore);
            bool pushed = false;
            for (int i = 0; i < count; i++)
            {
                if (!(actorOverlaps[i] is BoxCollider box)) continue;
                var t = box.transform;
                Vector3 p = t.InverseTransformPoint(position + Vector3.up * (height * .5f)) - box.center;
                Vector3 scale = t.lossyScale;
                Vector3 e = box.size * .5f;
                e.x += radius / Mathf.Max(.001f, Mathf.Abs(scale.x));
                e.z += radius / Mathf.Max(.001f, Mathf.Abs(scale.z));
                if (Mathf.Abs(p.x) >= e.x || Mathf.Abs(p.z) >= e.z) continue;
                float x = (e.x - Mathf.Abs(p.x)) * Mathf.Abs(scale.x);
                float z = (e.z - Mathf.Abs(p.z)) * Mathf.Abs(scale.z);
                Vector3 correction = x < z ? t.right * ((p.x < 0 ? -1 : 1) * (x + .015f)) :
                    t.forward * ((p.z < 0 ? -1 : 1) * (z + .015f));
                correction.y = 0; position += correction; pushed = true;
            }
            if (!pushed) break;
        }
        float distance = step.magnitude;
        if (distance > .00001f && Physics.CapsuleCast(position + Vector3.up * radius,
            position + Vector3.up * (height - radius), radius, step / distance, out var hit,
            distance + .02f, 1 << CarBodyLayer, QueryTriggerInteraction.Ignore))
            step *= Mathf.Clamp01((hit.distance - .02f) / distance);
        return position - start + step;
    }

    void CreateLocalHull(Vector3 sourceCenter, Vector3 sourceSize)
    {
        // Match the authored car footprint in local space while leaving its outer wheel
        // edges open enough for close player interaction. Three boxes avoid mesh contacts.
        float width = Mathf.Max(0.1f, sourceSize.x);
        float height = Mathf.Max(0.1f, sourceSize.y);
        float length = Mathf.Max(0.1f, sourceSize.z);
        float hullLength = length * 0.8f;
        float lowerCenterY = sourceCenter.y - height * 0.12f;

        AddBox(new Vector3(width * 0.53f, height * 0.58f, hullLength * 0.96f),
            new Vector3(sourceCenter.x, lowerCenterY + height * 0.06f, sourceCenter.z));

        float railWidth = width * 0.1f;
        float railX = width * 0.32f;
        float railHeight = height * 0.44f;
        float railY = lowerCenterY - height * 0.04f;
        AddBox(new Vector3(railWidth, railHeight, hullLength),
            new Vector3(sourceCenter.x - railX, railY, sourceCenter.z));
        AddBox(new Vector3(railWidth, railHeight, hullLength),
            new Vector3(sourceCenter.x + railX, railY, sourceCenter.z));
    }

    void AddBox(Vector3 size, Vector3 center)
    {
        var box = gameObject.AddComponent<BoxCollider>();
        box.size = size;
        box.center = center;
        box.isTrigger = false;
    }
}
