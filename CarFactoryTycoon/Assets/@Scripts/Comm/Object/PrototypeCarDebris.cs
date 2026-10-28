using System.Collections.Generic;
using UnityEngine;

/// <summary>Short-lived, render-only physics copies of visible assembled car parts.</summary>
public sealed class PrototypeCarDebris : MonoBehaviour
{
    const int MaxActive = 40;
    const float Lifetime = 4.25f;
    static readonly HashSet<PrototypeCarDebris> Active = new HashSet<PrototypeCarDebris>();
    static readonly List<PrototypeCarDebris> Expired = new List<PrototypeCarDebris>();
    float age;
    Mesh ownedMesh;

    public static int ActiveCount { get { PruneDestroyed(); return Active.Count; } }

    public static void Burst(AssemblyPart[] parts)
    {
        if (parts == null) return;
        PruneDestroyed();
        int layer = LayerMask.NameToLayer("Parts");
        foreach (var part in parts)
        {
            if (part == null || !part.gameObject.activeInHierarchy ||
                !(part.IsAssembled || part.currentWork > 0f || part.IsPlayerAttached || part.IsPlayerAttaching)) continue;
            bool copiedPart = false;
            var renderers = part.GetComponentsInChildren<Renderer>(false);
            foreach (var source in renderers)
            {
                if (Active.Count >= MaxActive) break;
                if (source == null || !source.enabled || !source.gameObject.activeInHierarchy) continue;

                Mesh mesh = null;
                bool ownsMesh = false;
                if (source is SkinnedMeshRenderer skinned)
                {
                    mesh = new Mesh { name = source.name + "_DebrisMesh" };
                    skinned.BakeMesh(mesh);
                    ownsMesh = true;
                }
                else
                {
                    var filter = source.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null) mesh = filter.sharedMesh;
                }
                if (mesh == null || mesh.vertexCount == 0) { if (ownsMesh && mesh != null) Destroy(mesh); continue; }

                var go = new GameObject("RejectedPartDebris");
                go.layer = layer >= 0 ? layer : source.gameObject.layer;
                go.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                go.transform.localScale = Vector3.one;
                var visual = new GameObject("Mesh");
                visual.transform.SetParent(go.transform, false);
                Vector3 meshScale = source.transform.lossyScale;
                visual.transform.localScale = meshScale;
                var filterCopy = visual.AddComponent<MeshFilter>(); filterCopy.sharedMesh = mesh;
                var rendererCopy = visual.AddComponent<MeshRenderer>();
                rendererCopy.sharedMaterials = source.sharedMaterials;
                rendererCopy.shadowCastingMode = source.shadowCastingMode;
                rendererCopy.receiveShadows = source.receiveShadows;
                var bounds = mesh.bounds;
                var collider = go.AddComponent<BoxCollider>();
                collider.center = Vector3.Scale(bounds.center, meshScale);
                Vector3 scaledSize = Vector3.Scale(bounds.size, meshScale);
                collider.size = new Vector3(Mathf.Max(.04f, Mathf.Abs(scaledSize.x)),
                    Mathf.Max(.04f, Mathf.Abs(scaledSize.y)), Mathf.Max(.04f, Mathf.Abs(scaledSize.z)));
                var body = go.AddComponent<Rigidbody>();
                body.mass = Mathf.Clamp(Mathf.Abs(scaledSize.x * scaledSize.y * scaledSize.z) * 12f, .2f, 5f);
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                var car = part.GetComponentInParent<CarController>();
                Vector3 away = source.bounds.center - (car != null ? car.transform.position : part.transform.root.position);
                if (away.sqrMagnitude < .01f) away = Vector3.up;
                Vector3 horizontal = Vector3.ProjectOnPlane(away, Vector3.up);
                if (horizontal.sqrMagnitude < .01f) horizontal = Random.insideUnitSphere;
                Vector3 burstDirection = (horizontal.normalized * .8f + Vector3.up * Random.Range(.6f, .9f)).normalized;
                body.AddForce(burstDirection * Random.Range(2f, 4f), ForceMode.VelocityChange);
                body.AddTorque(Random.insideUnitSphere * Random.Range(1.2f, 3.2f), ForceMode.VelocityChange);
                var lifetime = go.AddComponent<PrototypeCarDebris>();
                if (ownsMesh) lifetime.ownedMesh = mesh;
                Active.Add(lifetime);
                copiedPart = true;
            }
            // The copied mesh is now detached and render-only. Hiding the AssemblyPart root
            // avoids duplicates and lets ShakeTestStation restore its original active state.
            if (copiedPart) part.gameObject.SetActive(false);
            if (Active.Count >= MaxActive) break;
        }
    }

    void Update()
    {
        age += Time.deltaTime;
        if (age >= Lifetime) Destroy(gameObject);
    }

    void OnDestroy()
    {
        Active.Remove(this);
        if (ownedMesh != null) Destroy(ownedMesh);
    }

    static void PruneDestroyed()
    {
        Expired.Clear();
        foreach (var item in Active) if (item == null) Expired.Add(item);
        foreach (var item in Expired) Active.Remove(item);
    }
}
