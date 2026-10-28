using System.Collections.Generic;
using UnityEngine;

/// <summary>Query-only mesh contacts. No persistent physical blockers or triggers; shapes are disabled outside synchronous queries.</summary>
public sealed class PrototypeHammerContact : MonoBehaviour
{
    sealed class Surface
    {
        public AssemblyPart part;
        public Renderer source;
        public MeshCollider mesh;
        public Mesh baked;
    }
    readonly List<Surface> surfaces = new List<Surface>();
    readonly HashSet<CarController> cachedCars = new HashSet<CarController>();
    LineTrafficManager traffic;

    void Prepare(Vector3 origin, float range)
    {
        if (traffic == null) traffic = FindFirstObjectByType<LineTrafficManager>();
        if (traffic == null) return;
        foreach (var car in traffic.ActiveCars)
        {
            if (car == null || !car.gameObject.activeInHierarchy || Vector3.Distance(origin, car.transform.position) > range + 6f || cachedCars.Contains(car)) continue;
            cachedCars.Add(car);
            foreach (var part in car.GetComponentsInChildren<AssemblyPart>(true))
            foreach (var renderer in part.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.GetComponentInParent<AssemblyPart>(true) != part) continue;
                Mesh mesh = null, baked = null;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null) mesh = filter.sharedMesh;
                else if (renderer is SkinnedMeshRenderer skin) { baked = new Mesh(); skin.BakeMesh(baked); mesh = baked; }
                if (mesh == null) continue;
                var node = new GameObject("HammerQuerySurface") { hideFlags = HideFlags.DontSave };
                // Keep the disabled query shape at identity. Rays are transformed into the current
                // renderer mesh space, so moving cars never rely on a disabled collider's stale pose.
                var collider = node.AddComponent<MeshCollider>();
                collider.enabled = false;
                collider.sharedMesh = mesh;
                surfaces.Add(new Surface { part = part, source = renderer, mesh = collider, baked = baked });
            }
        }
    }

    public bool Sweep(BoxCollider head, Vector3 from, Quaternion fromRotation, Vector3 to, Quaternion toRotation,
        Vector3 origin, float range, out AssemblyPart part, out Vector3 point)
    {
        part = null; point = to;
        if (head == null) return false;
        Prepare(origin, range);
        // Substep the real hammer head, including rotation; fast swings do not tunnel through thin panels.
        int steps = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(from, to) / .035f + Quaternion.Angle(fromRotation, toRotation) / 8f), 1, 64);
        for (int step = 0; step <= steps; step++)
        {
            float t = (float)step / steps;
            var position = Vector3.Lerp(from, to, t);
            var rotation = Quaternion.Slerp(fromRotation, toRotation, t);
            foreach (var surface in surfaces)
            {
                if (surface.part == null || surface.source == null || surface.mesh == null || !surface.part.gameObject.activeInHierarchy || !surface.source.enabled) continue;
                if (surface.part.IsPlayerAttaching || (!surface.part.IsPlayerAttached && surface.part.currentWork <= 0f)) continue;
                if (Vector3.Distance(origin, position) > range || surface.source.bounds.SqrDistance(position) > .21f * .21f) continue;
                if (!HeadIntersectsMesh(head, position, rotation, surface, out point)) continue;
                part = surface.part;
                return true;
            }
        }
        return false;
    }

    public bool TrySurfacePoint(AssemblyPart part,Ray ray,Vector3 origin,float range,out Vector3 point)
    {
        Prepare(origin,range);point=default;float closest=float.PositiveInfinity;
        foreach(var surface in surfaces)
        {
            if(surface.part!=part||surface.mesh==null||surface.source==null)continue;
            surface.mesh.enabled=true;
            try
            {
                if(!Segment(surface,ray.origin,ray.GetPoint(range+Vector3.Distance(ray.origin,origin)),out var hit))continue;
                float distance=(hit-ray.origin).sqrMagnitude;if(distance>=closest)continue;
                closest=distance;point=hit;
            }
            finally{surface.mesh.enabled=false;}
        }
        return closest<float.PositiveInfinity;
    }

    static bool Segment(Surface surface, Vector3 a, Vector3 b, out Vector3 point)
    {
        var mesh=surface.mesh;
        a=surface.source.transform.InverseTransformPoint(a);b=surface.source.transform.InverseTransformPoint(b);
        Vector3 delta=b-a;float distance=delta.magnitude;RaycastHit hit;
        if(distance>.00001f && (mesh.Raycast(new Ray(a,delta/distance),out hit,distance) || mesh.Raycast(new Ray(b,-delta/distance),out hit,distance)))
        {point=surface.source.transform.TransformPoint(hit.point);return true;}
        point=default;return false;
    }
    static bool HeadIntersectsMesh(BoxCollider head,Vector3 position,Quaternion rotation,Surface surface,out Vector3 point)
    {
        // Collider.Raycast requires an enabled native shape. Activate only inside this synchronous
        // query at identity, and always remove it before the next simulation step.
        surface.mesh.enabled=true;
        try{return HeadMeshCore(head,position,rotation,surface,out point);}
        finally{surface.mesh.enabled=false;}
    }
    static bool HeadMeshCore(BoxCollider head,Vector3 position,Quaternion rotation,Surface surface,out Vector3 point)
    {
        // Open/thin vehicle panels cannot reliably produce a penetration volume. Test the actual
        // triangle surface against the hammer cuboid's edges and interior cross-sections instead.
        Vector3 extent=Vector3.Scale(head.size*.5f,head.transform.lossyScale);
        extent=new Vector3(Mathf.Abs(extent.x),Mathf.Abs(extent.y),Mathf.Abs(extent.z));
        var center=position+rotation*Vector3.Scale(head.center,head.transform.lossyScale);
        for(int axis=0;axis<3;axis++)
        for(int a=-1;a<=1;a++)for(int b=-1;b<=1;b++)
        {
            Vector3 start=default,end=default;
            start[axis]=-extent[axis];end[axis]=extent[axis];
            int j=(axis+1)%3,k=(axis+2)%3;
            start[j]=end[j]=a*extent[j];start[k]=end[k]=b*extent[k];
            if(Segment(surface,center+rotation*start,center+rotation*end,out point))return true;
        }
        point=default;return false;
    }

    void OnDestroy()
    {
        foreach (var surface in surfaces)
        {
            if (surface.mesh != null) Destroy(surface.mesh.gameObject);
            if (surface.baked != null) Destroy(surface.baked);
        }
        surfaces.Clear(); cachedCars.Clear();
    }
}
