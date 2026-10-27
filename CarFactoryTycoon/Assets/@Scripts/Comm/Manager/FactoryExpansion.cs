using System;
using UnityEngine;

/// <summary>The building footprint grows with unlocked rows; future floor and props do not exist visually.</summary>
[DefaultExecutionOrder(-100)]
public sealed class FactoryExpansion : MonoBehaviour
{
    [Serializable] public struct Prop { public GameObject root; public float minZ; public bool authoredActive; }
    public ProductionProgression progression;
    public MeshFilter floor;
    public MeshCollider floorCollider;
    public Mesh finishedFloor;
    public Transform[] walls;
    public Transform[] wallCaps;
    public Prop[] props;
    public UnityEngine.AI.NavMeshObstacle futureArea;
    public float minX=22,maxX=92,maxZ=45,finalMinZ=-20;
    [SerializeField] int visiblePrefix=1;
    Mesh temporaryFloor;
    public int VisiblePrefix => visiblePrefix;
    public Bounds ActiveBounds => GetBoundsForPrefix(visiblePrefix);
    public event Action<Bounds> BoundsChanged;

    void Awake(){if(progression!=null)ApplyPrefix(progression.prototypeMode?progression.processes.Length:progression.HiredPrefix);}
    public Bounds GetBoundsForPrefix(int prefix)
    {
        int count=progression.processes.Length;
        int i=Mathf.Clamp(prefix-1,0,count-1);
        float bottom=prefix>=count?finalMinZ:25f-9f*progression.processes[i].row;
        return new Bounds(new Vector3((minX+maxX)*.5f,2.8f,(bottom+maxZ)*.5f),new Vector3(maxX-minX,6.4f,maxZ-bottom));
    }
    /// <summary>Presentation only. Does not hire workers, modify contracts or write the production save.</summary>
    public void ApplyPrefix(int prefix)
    {
        if(progression==null||floor==null)return;
        visiblePrefix=progression.prototypeMode?progression.processes.Length:Mathf.Clamp(prefix,1,progression.processes.Length);
        var b=ActiveBounds;float bottom=b.min.z;
        bool complete=visiblePrefix==progression.processes.Length;
        if(complete) floor.sharedMesh=finishedFloor;
        else
        {
            if(temporaryFloor==null){temporaryFloor=new Mesh{name="Active factory floor"};temporaryFloor.hideFlags=HideFlags.DontSave;}
            temporaryFloor.Clear();
            var t=floor.transform;float y=t.position.y;
            temporaryFloor.vertices=new[]{t.InverseTransformPoint(new Vector3(minX,y,bottom)),t.InverseTransformPoint(new Vector3(minX,y,maxZ)),t.InverseTransformPoint(new Vector3(maxX,y,maxZ)),t.InverseTransformPoint(new Vector3(maxX,y,bottom))};
            temporaryFloor.triangles=new[]{0,1,2,0,2,3};
            temporaryFloor.uv=new[]{new Vector2(0,(bottom-finalMinZ)/(maxZ-finalMinZ)),new Vector2(0,1),new Vector2(1,1),new Vector2(1,(bottom-finalMinZ)/(maxZ-finalMinZ))};
            temporaryFloor.RecalculateNormals();temporaryFloor.RecalculateBounds();floor.sharedMesh=temporaryFloor;
        }
        if(floorCollider!=null)floorCollider.sharedMesh=floor.sharedMesh;
        foreach(var prop in props)if(prop.root!=null)prop.root.SetActive(prop.authoredActive&&prop.minZ>=bottom-.01f);
        for(int i=0;i<4;i++)
        {
            bool horizontal=i<2;
            var center=horizontal?new Vector3(b.center.x,-.405f,i==0?maxZ:bottom):new Vector3(i==2?minX:maxX,-.405f,b.center.z);
            var size=horizontal?new Vector3(b.size.x,1.2f,.22f):new Vector3(.22f,1.2f,b.size.z);
            if(walls!=null&&i<walls.Length&&walls[i]!=null){walls[i].position=center+Vector3.up*.6f;walls[i].localScale=size;}
            if(wallCaps!=null&&i<wallCaps.Length&&wallCaps[i]!=null){wallCaps[i].position=center+Vector3.up*1.21f;wallCaps[i].localScale=new Vector3(size.x+.03f,.08f,size.z+.03f);}
        }
        if(futureArea!=null)
        {
            float depth=Mathf.Max(0,bottom-finalMinZ);
            futureArea.gameObject.SetActive(depth>.01f);
            futureArea.transform.position=new Vector3(b.center.x,0,finalMinZ+depth*.5f-.25f);
            futureArea.size=new Vector3(b.size.x+4,8,depth+.5f);
        }
        BoundsChanged?.Invoke(b);
    }
    void OnDestroy(){if(temporaryFloor!=null){if(Application.isPlaying)Destroy(temporaryFloor);else DestroyImmediate(temporaryFloor);}}
}
