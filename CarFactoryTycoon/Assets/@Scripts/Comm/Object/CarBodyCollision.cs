using UnityEngine;

/// <summary>Solid, inexpensive exterior proxy; the root trigger still detects production stations.</summary>
[DisallowMultipleComponent]
public sealed class CarBodyCollision : MonoBehaviour
{
    public const int CarBodyLayer = 11;
    const string ChildName = "CarBodyCollision";
    static Mesh sharedExterior;
    static Vector3 sharedCenter, sharedSize;
    static readonly Collider[] actorOverlaps = new Collider[32];

    public static CarBodyCollision EnsureCreated(GameObject vehicleRoot,bool sanitizeParts=true)
    {
        if (vehicleRoot == null) return null;
        if(sanitizeParts)foreach (var part in vehicleRoot.GetComponentsInChildren<AssemblyPart>(true))
        {
            foreach (var child in part.GetComponentsInChildren<Transform>(true))
                if (child.gameObject != vehicleRoot && child.name != "Origin") child.gameObject.layer = 9;
            foreach (var collider in part.GetComponentsInChildren<Collider>(true))
                if (collider.gameObject != vehicleRoot && collider.name != "Origin") collider.enabled = false;
        }
        var existing = vehicleRoot.GetComponentInChildren<CarBodyCollision>(true);
        if (existing != null) return existing;
        var trigger = vehicleRoot.GetComponent<BoxCollider>();
        var bounds = new Bounds(trigger != null ? trigger.center : Vector3.zero,
            trigger != null ? trigger.size * .8f : new Vector3(2.08f, 1.46f, 4.52f));
        bool found = false;
        foreach (var part in vehicleRoot.GetComponentsInChildren<AssemblyPart>(true))
        {
            if (part.myType != PartType.Frame_1) continue;
            foreach (var renderer in part.GetComponentsInChildren<Renderer>(true))
            {
                var b = renderer.localBounds;
                for (int x=-1;x<=1;x+=2) for(int y=-1;y<=1;y+=2) for(int z=-1;z<=1;z+=2)
                {
                    var p = vehicleRoot.transform.InverseTransformPoint(renderer.transform.TransformPoint(
                        b.center + Vector3.Scale(b.extents, new Vector3(x,y,z))));
                    if (!found) { bounds = new Bounds(p, Vector3.zero); found = true; }
                    else bounds.Encapsulate(p);
                }
            }
        }
        var body = new GameObject(ChildName);
        body.layer = CarBodyLayer;
        body.transform.SetParent(vehicleRoot.transform, false);
        var rb = body.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.None;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        var result = body.AddComponent<CarBodyCollision>();
        var hull = body.AddComponent<MeshCollider>();
        hull.convex = true;
        // Share one cooked shape for this model. No mesh/shape updates when panels attach.
        if (sharedExterior == null || (sharedCenter-bounds.center).sqrMagnitude > .000001f ||
            (sharedSize-bounds.size).sqrMagnitude > .000001f)
        {
            sharedExterior = BuildExteriorMesh(bounds);
            sharedCenter = bounds.center; sharedSize = bounds.size;
            Physics.BakeMesh(sharedExterior.GetEntityId(), true, hull.cookingOptions);
        }
        hull.sharedMesh = sharedExterior;
        return result;
    }

    /// <summary>32 vertices / 60 triangles. Closed bevelled prism with continuous front/rear ramps.</summary>
    public static Mesh BuildExteriorMesh(Bounds bounds)
    {
        var s=bounds.size;var c=bounds.center;
        float bottom=bounds.min.y+.025f, roof=bounds.max.y-.02f;
        // <= atan(.85) = 40.4 degrees on either approach (player slope limit is 45).
        float drop=Mathf.Min(Mathf.Max(.05f,roof-bottom-.25f), s.z*.30f*.85f);
        float[] zs={bounds.min.z,bounds.min.z+s.z*.30f,bounds.min.z+s.z*.64f,bounds.max.z};
        float[] tops={roof-drop,roof,roof,roof-drop};
        float[] widths={.87f,1f,1f,.87f};
        var vertices=new Vector3[32];
        for(int i=0;i<4;i++)
        {
            float half=s.x*.5f*widths[i], top=tops[i];
            float bevel=Mathf.Min(.12f,(top-bottom)*.25f);
            // Clockwise cross section as viewed from the front; no UV splits or open seams.
            vertices[i*8+0]=new Vector3(c.x-half*.84f,top,zs[i]);
            vertices[i*8+1]=new Vector3(c.x+half*.84f,top,zs[i]);
            vertices[i*8+2]=new Vector3(c.x+half,top-bevel,zs[i]);
            vertices[i*8+3]=new Vector3(c.x+half,bottom+bevel,zs[i]);
            vertices[i*8+4]=new Vector3(c.x+half*.90f,bottom,zs[i]);
            vertices[i*8+5]=new Vector3(c.x-half*.90f,bottom,zs[i]);
            vertices[i*8+6]=new Vector3(c.x-half,bottom+bevel,zs[i]);
            vertices[i*8+7]=new Vector3(c.x-half,top-bevel,zs[i]);
        }
        var triangles=new System.Collections.Generic.List<int>(180);
        for(int ring=0;ring<3;ring++) for(int edge=0;edge<8;edge++)
        {
            int a=ring*8+edge,b=ring*8+(edge+1)%8,d=a+8,e=b+8;
            triangles.Add(a);triangles.Add(d);triangles.Add(b);
            triangles.Add(b);triangles.Add(d);triangles.Add(e);
        }
        for(int i=1;i<7;i++)
        {
            triangles.Add(0);triangles.Add(i);triangles.Add(i+1);
            triangles.Add(24);triangles.Add(24+i+1);triangles.Add(24+i);
        }
        var mesh=new Mesh { name="CarExterior_Convex_32v" };
        mesh.vertices=vertices;mesh.triangles=triangles.ToArray();mesh.RecalculateBounds();mesh.RecalculateNormals();
        return mesh;
    }

    public static Vector3 ConstrainActorStep(Vector3 position, Vector3 step, float radius=.32f, float height=1.8f)
    {
        radius=Mathf.Max(.1f,radius);height=Mathf.Max(radius*2f,height);
        var start=position;
        for(int pass=0;pass<3;pass++)
        {
            int count=Physics.OverlapCapsuleNonAlloc(position+Vector3.up*radius,
                position+Vector3.up*(height-radius),radius,actorOverlaps,1<<CarBodyLayer,QueryTriggerInteraction.Ignore);
            bool pushed=false;
            for(int i=0;i<count;i++)
            {
                var other=actorOverlaps[i];
                // The actual capsule overlap above establishes contact. Use the horizontal
                // bounds exit directly: a 3D minimum-translation vector can point upward,
                // and projecting it does not guarantee a floor walker leaves the hull.
                // World bounds also remain conservative for rotated/scaled vehicles.
                var b=other.bounds;
                float left=position.x-(b.min.x-radius),right=b.max.x+radius-position.x;
                float back=position.z-(b.min.z-radius),front=b.max.z+radius-position.z;
                float exit=Mathf.Min(Mathf.Min(left,right),Mathf.Min(back,front));
                if(exit<=0f)continue; // An earlier overlap correction may already have cleared this bounds.
                exit+=.015f;
                if(left<=right && left<=back && left<=front)position.x-=exit;
                else if(right<=back && right<=front)position.x+=exit;
                else if(back<=front)position.z-=exit;
                else position.z+=exit;
                pushed=true;
            }
            if(!pushed)break;
        }
        float length=step.magnitude;
        if(length>.00001f && Physics.CapsuleCast(position+Vector3.up*radius,
            position+Vector3.up*(height-radius),radius,step/length,out var hit,length+.02f,
            1<<CarBodyLayer,QueryTriggerInteraction.Ignore))step*=Mathf.Clamp01((hit.distance-.02f)/length);
        return position-start+step;
    }
}
