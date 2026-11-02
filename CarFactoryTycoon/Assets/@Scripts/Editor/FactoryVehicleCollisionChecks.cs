#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Object=UnityEngine.Object;

/// <summary>Isolated runtime fixture; Start in Play, poll Result. RunGeometry also works in edit mode.</summary>
public static class FactoryVehicleCollisionChecks
{
    public static string Result="idle";
    static void Need(bool condition,string description){if(!condition)throw new InvalidOperationException(description);}
    public static string RunGeometry()
    {
        var mesh=CarBodyCollision.BuildExteriorMesh(new Bounds(new Vector3(0,.93f,.11f),new Vector3(2.08f,1.46f,4.52f)));
        try
        {
            var v=mesh.vertices;var t=mesh.triangles;Need(v.Length==32 && t.Length==180,"32 vertex / 60 triangle budget");
            var edges=new Dictionary<string,int>();float volume=0;
            for(int i=0;i<t.Length;i+=3)
            {
                var a=v[t[i]];var b=v[t[i+1]];var c=v[t[i+2]];
                Need(Vector3.Cross(b-a,c-a).sqrMagnitude>.00000001f,"nondegenerate triangles");
                volume+=Vector3.Dot(a,Vector3.Cross(b,c))/6f;
                for(int j=0;j<3;j++){int x=t[i+j],y=t[i+(j+1)%3];string key=Mathf.Min(x,y)+":"+Mathf.Max(x,y);int n;edges.TryGetValue(key,out n);edges[key]=n+1;}
            }
            foreach(var n in edges.Values)Need(n==2,"closed manifold edges");
            Need(volume>0,"outward winding and positive volume");
            for(int i=0;i<3;i++)
            {
                var normal=Vector3.Cross(v[(i+1)*8]-v[i*8],v[i*8+1]-v[i*8]).normalized;
                Need(Vector3.Angle(normal,Vector3.up)<45f,"all longitudinal top ramps below 45 degrees");
            }
            return "PASS geometry:32 vertices/60 triangles,closed manifold,outward winding,approach slopes<45deg,volume="+volume;
        }
        finally{Object.DestroyImmediate(mesh);}
    }
    public static void Start()
    {
        if(!Application.isPlaying)throw new InvalidOperationException("Play required");
        if(Result=="running")return;
        var player=Object.FindFirstObjectByType<FactoryPlayerController>();Need(player!=null,"player required");
        Result="running";player.StartCoroutine(Run(player));
    }
    static IEnumerator Run(FactoryPlayerController player)
    {
        var oldPosition=player.transform.position;var oldRotation=player.transform.rotation;float oldYaw=player.ViewYaw;
        bool oldEnabled=player.enabled;player.enabled=false;
        GameObject fixture=null,floor=null;var log=new StringBuilder();
        var steps=Exercise(player,go=>fixture=go,go=>floor=go,log);
        Exception failure=null;
        while(true)
        {
            bool next=false;object current=null;
            try{next=steps.MoveNext();if(next)current=steps.Current;}catch(Exception e){failure=e;}
            if(failure!=null || !next)break;yield return current;
        }
        if(fixture!=null)Object.Destroy(fixture);if(floor!=null)Object.Destroy(floor);
        player.RotateView(new Vector2(Mathf.DeltaAngle(player.ViewYaw,oldYaw),0));
        player.Teleport(oldPosition);player.transform.rotation=oldRotation;player.enabled=oldEnabled;
        Result=failure==null?"PASS\n"+log:"FAIL\n"+log+failure;
        Debug.Log(Result);
    }
    static IEnumerator Exercise(FactoryPlayerController player,Action<GameObject> rememberCar,Action<GameObject> rememberFloor,StringBuilder log)
    {
        log.AppendLine(RunGeometry());
        var prefab=Resources.Load<GameObject>("Prefabs/CarModel_Origin");Need(prefab!=null,"production car prefab");
        var go=Object.Instantiate(prefab,new Vector3(700,10,700),Quaternion.identity);rememberCar(go);
        var car=go.GetComponent<CarController>();car.enabled=false;
        var hull=CarBodyCollision.EnsureCreated(go);var collider=hull.GetComponent<MeshCollider>();
        Need(collider!=null && collider.convex && hull.GetComponents<Collider>().Length==1,"one convex collider per vehicle");
        var mesh=collider.sharedMesh;
        var second=CarBodyCollision.EnsureCreated(go);Need(second==hull && collider.sharedMesh==mesh,"EnsureCreated stable/no recook");
        var floor=new GameObject("VehicleCollisionCheckFloor");rememberFloor(floor);floor.transform.position=new Vector3(700,9.75f,700);
        var floorBox=floor.AddComponent<BoxCollider>();floorBox.size=new Vector3(15,.5f,15);
        Physics.SyncTransforms();var bounds=collider.bounds;
        for(int side=0;side<2;side++)
        {
            float yaw=side==0?0:180;
            player.RotateView(new Vector2(Mathf.DeltaAngle(player.ViewYaw,yaw),0));
            float z=side==0?bounds.min.z-.65f:bounds.max.z+.65f;
            player.Teleport(new Vector3(bounds.center.x,10.02f,z));
            for(int i=0;i<5;i++){player.SimulateMovement(Vector2.zero,false,false,.02f);yield return null;}
            bool roofReached=false;
            for(int i=0;i<100;i++)
            {
                player.SimulateMovement(Vector2.up,false,i==0,.02f);yield return null;
                if(player.transform.position.y>bounds.max.y-.14f && player.SupportCar==car){roofReached=true;break;}
            }
            Need(roofReached,(side==0?"rear":"front")+" bumper jump and continuous slope ascent to supported roof");
            log.AppendLine((side==0?"rear":"front")+" approach ascends to roof");
        }
        // Engine-room landing is deliberately supported even without the hood/panels.
        player.Teleport(new Vector3(bounds.center.x,bounds.max.y+1f,Mathf.Lerp(bounds.center.z,bounds.max.z,.65f)));
        for(int i=0;i<80;i++){player.SimulateMovement(Vector2.zero,false,false,.02f);yield return null;}
        Need(player.SupportCar==car && player.transform.position.y>bounds.min.y+.45f,"engine bay cannot trap player in hollow frame");
        log.AppendLine("empty engine bay landing supported");
        player.Teleport(new Vector3(bounds.center.x,bounds.max.y+.05f,bounds.center.z));
        for(int i=0;i<20;i++){player.SimulateMovement(Vector2.zero,false,false,.02f);yield return null;}
        Need(player.SupportCar==car,"roof support acquired");
        var before=player.transform.position;go.transform.position+=Vector3.right*.5f;
        player.SimulateMovement(Vector2.zero,false,false,.02f);
        Need(Mathf.Abs(player.transform.position.x-before.x-.5f)<.08f,"support translation");
        var local=go.transform.InverseTransformPoint(player.transform.position);go.transform.rotation=Quaternion.Euler(0,25,0);
        var expected=go.transform.TransformPoint(local);player.SimulateMovement(Vector2.zero,false,false,.02f);
        Need(Vector3.Distance(player.transform.position,expected)<.12f,"support rotation");
        player.SimulateMovement(Vector2.zero,false,true,.02f);Need(player.SupportCar==null,"jump clears support");
        log.AppendLine("roof support translation/rotation/jump lifecycle");
        var localCases=new[]{new Vector3(mesh.bounds.center.x,mesh.bounds.min.y,mesh.bounds.center.z),
            new Vector3(mesh.bounds.center.x+mesh.bounds.extents.x*.8f,mesh.bounds.min.y,mesh.bounds.center.z),
            new Vector3(mesh.bounds.center.x,mesh.bounds.min.y,mesh.bounds.center.z+mesh.bounds.extents.z*.65f)};
        for(int sample=0;sample<localCases.Length;sample++)
        {
            var inside=go.transform.TransformPoint(localCases[sample]);
            Physics.SyncTransforms();
            var beforeOverlaps=Physics.OverlapCapsule(inside+Vector3.up*.32f,inside+Vector3.up*1.48f,.32f,1<<11,QueryTriggerInteraction.Ignore);
            Need(beforeOverlaps.Length>0,"NPC overlap fixture must start inside convex hull case="+sample);
            var escaped=inside+CarBodyCollision.ConstrainActorStep(inside,Vector3.zero);
            var overlaps=Physics.OverlapCapsule(escaped+Vector3.up*.32f,escaped+Vector3.up*1.48f,.32f,1<<11,QueryTriggerInteraction.Ignore);
            log.AppendLine("NPC overlap case="+sample+" start="+inside.ToString("F4")+" end="+escaped.ToString("F4")+
                " before="+beforeOverlaps.Length+" remaining="+overlaps.Length+" hull="+collider.bounds);
            Need(Mathf.Abs(escaped.y-inside.y)<.0001f,"NPC recovery remains on floor case="+sample);
            Need(overlaps.Length==0,"idle NPC escapes convex hull overlap case="+sample);
        }
        log.AppendLine("NPC convex overlap escape center/side/front at 25deg rotation");
    }
}
#endif
