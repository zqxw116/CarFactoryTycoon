#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class FactoryStockPackingChecks
{
    static void Need(bool value,string text){if(!value)throw new InvalidOperationException(text);}
    static readonly BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
    public static string Geometry()
    {
        var a=new Vector3(3,2,5);
        Need(Vector3.Distance(StockPackingGeometry.Along(a,a+Vector3.right*.1f,a,a,false,5),a+Vector3.right*5)<.0001f,"short endpoint must not limit line");
        Need(Vector3.Distance(StockPackingGeometry.Along(a,a+Vector3.right*100,a,a,false,5),a+Vector3.right*5)<.0001f,"long endpoint must not stretch line");
        Need(Vector3.Distance(StockPackingGeometry.Along(Vector3.zero,Vector3.right,Vector3.right/3,Vector3.right*2/3,true,3),Vector3.right*3)<.001f,"Bezier extends by end tangent");
        return "PASS line spacing independent of endpoint length / Bezier distance and extension";
    }
    public static string PoolLayout()
    {
        var preview=UnityEngine.Object.FindFirstObjectByType<ProcessAuthoringPreview>();
        var stock=preview.progression.processes[8].humanStation.partStacks[0];
        var original=preview.progression.authoringProfile;var oldPosition=stock.transform.position;var oldRotation=stock.transform.rotation;
        var items=(GameObject[])typeof(PartStack).GetField("items",F).GetValue(stock);var ids=items.Select(x=>x.GetInstanceID()).ToArray();int count=stock.Count;
        var cfg=new ProcessStockPacking{enabled=true,centerSpacing=true,columns=6,layers=2,gap=new Vector3(1.7f,1.9f,.08f)};
        var frame=new Pose(Vector3.zero,Quaternion.identity);var start=stock.transform.position;cfg.end=start+Vector3.right*.1f;
        try
        {
            stock.SetWorldPacking(cfg,frame,0,0,Vector3.zero);
            var first=stock.PackedSlotWorld(0);
            Need(Vector3.Distance(stock.PackedSlotWorld(10)-first,Vector3.right*8.5f)<.001f,"12 slots / 2 layers => 6 columns beyond short endpoint");
            Need(Vector3.Distance(stock.PackedSlotWorld(1)-first,Vector3.up*.08f)<.001f,"world height step");
            cfg.end=start+Vector3.right*100;stock.SetWorldPacking(cfg,frame,0,0,Vector3.zero);
            Need(Vector3.Distance(stock.PackedSlotWorld(10)-stock.PackedSlotWorld(0),Vector3.right*8.5f)<.001f,"far endpoint retains spacing");
            cfg.offset=new Vector3(.2f,.3f,.4f);stock.SetWorldPacking(cfg,frame,0,0,Vector3.zero);
            Need(Vector3.Distance(stock.PackedSlotWorld(0)-first,cfg.offset)<.001f,"world offset");
            cfg.offset=Vector3.zero;cfg.centerSpacing=false;cfg.gap=Vector3.one*.02f;
            float minimum=float.MaxValue;Vector3 flat=Vector3.zero;
            foreach(var euler in new[]{Vector3.zero,new Vector3(90,0,0),new Vector3(0,0,90)})
            {stock.SetWorldPacking(cfg,frame,0,0,euler);if(stock.PackedWorldSize.y<minimum){minimum=stock.PackedWorldSize.y;flat=euler;}}
            stock.SetWorldPacking(cfg,frame,0,0,flat);
            Need(Mathf.Abs((stock.PackedSlotWorld(1)-stock.PackedSlotWorld(0)).y-(stock.PackedWorldSize.y+.02f))<.001f,"laying part uses rotated world thickness");
            stock.transform.rotation=Quaternion.Euler(25,60,15);stock.SetWorldPacking(cfg,frame,0,0,flat);
            Need(Mathf.Abs((stock.PackedSlotWorld(1)-stock.PackedSlotWorld(0)).y-(stock.PackedWorldSize.y+.02f))<.001f,"stack root rotation cannot skew world height");
            Need(stock.Count==count&&items.Select(x=>x.GetInstanceID()).SequenceEqual(ids),"layout keeps inventory and pooled instances");
            if(count>0)
            {
                stock.ResetReadySlot();Need(stock.TryReserveReady(stock),"reserve ready");
                Need(stock.TryTakeReady(stock,stock.transform,out var pos,out var rot),"take ready");Need(stock.Count==count-1&&!stock.HasReadyPart,"one take empties ready once");
                typeof(PartStack).GetMethod("TickReadySlot",F).Invoke(stock,new object[]{Time.time+1.1f});
                Need(stock.HasReadyPart&&stock.Count==count-1,"timed refill without extra consumption");
                stock.Add(1);
            }
            return "PASS 12/6x2 endpoints/offset/rotated thickness/root tilt/pool IDs/count/ready refill; flat thickness="+minimum;
        }
        finally
        {
            if(stock.Count<count)stock.Add(count-stock.Count);while(stock.Count>count)stock.TryConsume();
            stock.transform.SetPositionAndRotation(oldPosition,oldRotation);
            ProcessAuthoringLayout.Apply(preview.progression,original,false);stock.ResetReadySlot();
        }
    }
    public static string RigTransforms()
    {
        var w=Resources.FindObjectsOfTypeAll<ProcessAuthoringWindow>().First();var preview=UnityEngine.Object.FindFirstObjectByType<ProcessAuthoringPreview>();
        var data=(ProcessAuthoringProfile)typeof(ProcessAuthoringWindow).GetField("draft",F).GetValue(w);string before=EditorJsonUtility.ToJson(data);
        Action<string> call=m=>typeof(ProcessAuthoringWindow).GetMethod(m,F).Invoke(w,null);
        try
        {
            typeof(ProcessAuthoringWindow).GetField("tabIndex",F).SetValue(w,1);call("UpdateRigPreview");
            var rig=(ProcessAuthoringRigPreview)typeof(ProcessAuthoringWindow).GetField("rigPreview",F).GetValue(w);
            var position=rig.root.transform.position;var rotation=rig.root.transform.rotation;
            var next=Quaternion.AngleAxis(65,Vector3.up)*rig.part.rotation;rig.part.rotation=next;call("UpdateRigPreview");
            Need(Quaternion.Angle(preview.progression.processes[8].humanStation.partStacks[0].ReadyRotation,next)<.01f,"part rotation committed");
            Need(Quaternion.Angle(rotation,rig.root.transform.rotation)<.01f&&Vector3.Distance(position,rig.root.transform.position)<.001f,"part cannot move worker");
            var ready=preview.progression.processes[8].humanStation.partStacks[0].ReadyRotation;
            var desired=Quaternion.AngleAxis(180,Vector3.up)*rig.root.transform.rotation;rig.root.transform.rotation=desired;call("UpdateRigPreview");
            Need(Quaternion.Angle(desired,rig.root.transform.rotation)<.01f,"worker rotation preserved");
            Need(Quaternion.Angle(ready,preview.progression.processes[8].humanStation.partStacks[0].ReadyRotation)<.01f,"worker cannot rotate part");
            return "PASS independent part/worker transforms and ready synchronization";
        }
        finally{EditorJsonUtility.FromJsonOverwrite(before,data);call("DisposeRigPreview");call("ApplyLayout");call("UpdateRigPreview");}
    }
}
#endif
