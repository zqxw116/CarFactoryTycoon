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
