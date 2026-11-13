using UnityEngine;

public partial class PartStack
{
    bool worldPackingActive;
    Vector3[] packedLocalPositions;
    Quaternion packedLocalRotation;
    int packingSignature;
    GameObject packingSample;
    public Vector3 PackedWorldSize {get;private set;}
    public bool HasWorldPacking=>worldPackingActive;
    public int InventoryExampleIndex {get{if(items==null)return -1;for(int i=0;i<items.Length;i++)if(i!=stagedDisplayIndex&&items[i]!=null)return i;return -1;}}
    public Transform InventoryExample=>InventoryExampleIndex>=0?items[InventoryExampleIndex].transform:null;
    public Vector3 PackedSlotWorld(int index)=>transform.TransformPoint(GetSlotLocalPos(Mathf.Clamp(index,0,capacity-1)));
    public void SetWorldPacking(ProcessStockPacking settings,Pose frame,int side,float distance,Vector3 itemEuler)
    {
        if(settings==null||!settings.enabled)
        {
            if(worldPackingActive){worldPackingActive=false;ApplyLayout();}
            return;
        }
        EnsureBuilt();if(items==null||items.Length==0||items[0]==null)return;
        var rotation=frame.rotation*Quaternion.Euler(itemEuler);
        int signature=settings.Signature()^transform.position.GetHashCode()^transform.rotation.GetHashCode()^transform.lossyScale.GetHashCode()^rotation.GetHashCode()^frame.position.GetHashCode()^frame.rotation.GetHashCode()^distance.GetHashCode();
        if(worldPackingActive&&packingSignature==signature&&packingSample!=null&&packingSample==items[0]&&packedLocalPositions!=null&&packedLocalPositions.Length==items.Length)return;
        RestoreStagedDisplay();
        if(packedLocalPositions==null||packedLocalPositions.Length!=items.Length)packedLocalPositions=new Vector3[items.Length];
        var sample=items[0].transform;var oldPosition=sample.localPosition;var oldRotation=sample.localRotation;
        sample.SetPositionAndRotation(Vector3.zero,rotation);
        bool has=false;var bounds=new Bounds();
        foreach(var filter in sample.GetComponentsInChildren<MeshFilter>(true))
        {
            if(filter.sharedMesh==null)continue;var b=filter.sharedMesh.bounds;
            for(int c=0;c<8;c++){var point=filter.transform.TransformPoint(b.center+Vector3.Scale(b.extents,kCorners[c]));if(!has){bounds=new Bounds(point,Vector3.zero);has=true;}else bounds.Encapsulate(point);}
        }
        sample.localPosition=oldPosition;sample.localRotation=oldRotation;
        if(!has)bounds=new Bounds(Vector3.zero,Vector3.one*.15f);
        PackedWorldSize=bounds.size;
        var start=transform.position;
        var end=frame.position+frame.rotation*(settings.end+Vector3.right*(side*distance));
        var c1=frame.position+frame.rotation*(settings.control1+Vector3.right*(side*distance));
        var c2=frame.position+frame.rotation*(settings.control2+Vector3.right*(side*distance));
        var direction=end-start;if(direction.sqrMagnitude<.000001f)direction=frame.rotation*Vector3.right;direction.Normalize();
        var row=Vector3.Cross(Vector3.up,direction);if(row.sqrMagnitude<.000001f)row=frame.rotation*Vector3.forward;row.Normalize();
        var size=bounds.size;
        float alongSize=Mathf.Abs(direction.x)*size.x+Mathf.Abs(direction.y)*size.y+Mathf.Abs(direction.z)*size.z;
        if(settings.curve)alongSize=size.magnitude;
        float rowSize=Mathf.Abs(row.x)*size.x+Mathf.Abs(row.y)*size.y+Mathf.Abs(row.z)*size.z;
        float a=Mathf.Max(.001f,settings.gap.x+(settings.centerSpacing?0:alongSize));
        float bStep=Mathf.Max(.001f,settings.gap.y+(settings.centerSpacing?0:rowSize));
        float h=Mathf.Max(.001f,settings.gap.z+(settings.centerSpacing?0:size.y));
        int layers=Mathf.Max(1,settings.layers),columns=Mathf.Max(1,settings.columns);
        for(int i=0;i<items.Length;i++)
        {
            int level=i%layers,ground=i/layers,column=ground%columns,line=ground/columns;
            var anchor=StockPackingGeometry.Along(start,end,c1,c2,settings.curve,column*a);
            var world=anchor+row*(line*bStep)+Vector3.up*(level*h)+frame.rotation*(settings.offset+settings.rowOffset*line+settings.layerOffset*level);
            world+=Vector3.up*bounds.extents.y-bounds.center;
            packedLocalPositions[i]=transform.InverseTransformPoint(world);
        }
        worldPackingActive=true;packingSignature=signature;packingSample=items[0];packedLocalRotation=Quaternion.Inverse(transform.rotation)*rotation;
        ApplyItemLayout();BuildFillOrder();FitCollider(false);Refresh();
    }
}
