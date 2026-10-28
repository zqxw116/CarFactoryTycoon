#if UNITY_EDITOR
using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

public static class FactoryPlayFeedbackSetup
{
    static Material Steel => AssetDatabase.LoadAssetAtPath<Material>("Assets/@Resource/Materials/FactoryFlow/SupplySteel.mat");
    static Material Yellow => AssetDatabase.LoadAssetAtPath<Material>("Assets/@Resource/Materials/FactoryFlow/SafetyYellow.mat");
    public static string Apply()
    {
        if(Application.isPlaying) throw new InvalidOperationException("Edit mode required");
        var line=Object.FindFirstObjectByType<PrototypeLineController>();
        line.settings.playerAttachSeconds=2f; EditorUtility.SetDirty(line.settings);
        // Forward is west here: its left side is south. Keep lever beside the mouth, not behind the tunnel.
        line.leverRoot.position=new Vector3(59.7f,0,31.9f);
        var tags=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layer=tags.FindProperty("layers").GetArrayElementAtIndex(12);
        if(!string.IsNullOrEmpty(layer.stringValue)&&layer.stringValue!="PlayerSafety")throw new InvalidOperationException("Layer12 occupied");
        layer.stringValue="PlayerSafety";tags.ApplyModifiedPropertiesWithoutUndo();
        for(int i=0;i<32;i++)Physics.IgnoreLayerCollision(12,i,i!=10);
        var safety=Root("PrototypeSafety",null);
        Guard(safety,"Shake",new Vector3(57,0,-1),new Vector3(6.1f,3.8f,3.9f));
        Guard(safety,"Reject",new Vector3(43.4f,0,-1),new Vector3(6.2f,3.8f,3.4f));
        Guard(safety,"WaterTank",new Vector3(48.9f,0,-4.7f),new Vector3(7.1f,3.8f,4.1f));
        var water=Object.FindFirstObjectByType<WaterTestStation>();
        foreach(var r in water.GetComponentsInChildren<MeshRenderer>(true))
        {
            if(!(r.name.StartsWith("TankWall_")||r.name.Contains("Column")||r.name.Contains("Support")||r.name.Contains("Pedestal")))continue;
            if(r.GetComponent<Collider>()==null)r.gameObject.AddComponent<BoxCollider>();
        }
        var warehouse=GameObject.Find("WarehouseBuilding").transform;
        var door=warehouse.GetComponentsInChildren<Transform>(true).First(t=>t.name=="Door_W");
        var load=Object.FindFirstObjectByType<SupplyManager>().warehouseLoadPoint;
        load.position=new Vector3(door.position.x-1.5f,-.305f,door.position.z);
        var services=Object.FindFirstObjectByType<PrototypeServicesInteraction>();
        services.warehousePost.position=load.position+new Vector3(-.65f,0,-1.1f);
        // Logical input location follows the actual loading door; leave the user's building untouched.
        var logical=GameObject.Find("GameObjectGroup/Warehouse");if(logical!=null)logical.transform.position=load.position+Vector3.up*1.5f;
        foreach(var mover in Object.FindObjectsByType<NavMover>(FindObjectsInactive.Include,FindObjectsSortMode.None))
        {mover.requireCompletePath=true;EditorUtility.SetDirty(mover);}
        var candidates=new[]{new Vector3(49.6f,0,35),new Vector3(48.2f,0,26),new Vector3(42.7f,0,17),new Vector3(49.4f,0,8),new Vector3(36.7f,0,-1),new Vector3(35.8f,0,-10)};
        for(int i=0;i<candidates.Length;i++)
        {
            var name="LineCrossing_Row"+(i+1);
            var go=GameObject.Find(name);
            var crossing=go!=null?go.GetComponent<LineCrossing>():NavMeshBakeTool.CreateCrossing(name,candidates[i]);
            crossing.transform.position=candidates[i];crossing.halfSpan=3f;crossing.zoneHalfWidth=.65f;crossing.walkwayRise=.44f;crossing.waitSlotSpacing=.15f;
            crossing.SnapToSpline();crossing.ApplyLink();EditorUtility.SetDirty(crossing);
            Walkway(crossing);
        }
        var log=new StringBuilder();FactoryLayoutRevisionSetup.BakeCompleteLayout(log);
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(line.gameObject.scene);EditorSceneManager.SaveScene(line.gameObject.scene);
        return "Player hold=2s; left mouth lever; guarded inspections; warehouse door linked; 6 row walkways. "+log;
    }
    static Transform Root(string name,Transform parent)
    {var t=parent!=null?parent.Find(name):GameObject.Find(name)?.transform;if(t==null){t=new GameObject(name).transform;t.SetParent(parent,false);}return t;}
    static GameObject Box(Transform root,string name,Vector3 position,Vector3 size,Material material,bool collider=true)
    {
        var t=root.Find(name);var go=t!=null?t.gameObject:GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(root,false);
        go.transform.localPosition=position;go.transform.localRotation=Quaternion.identity;go.transform.localScale=size;go.layer=0;
        go.GetComponent<Renderer>().sharedMaterial=material;
        if(!collider&&go.GetComponent<Collider>()!=null)Object.DestroyImmediate(go.GetComponent<Collider>());
        return go;
    }
    static void Guard(Transform parent,string name,Vector3 center,Vector3 size)
    {
        var root=Root(name,parent);root.position=center;
        var volume=root.GetComponent<BoxCollider>();if(volume==null)volume=root.gameObject.AddComponent<BoxCollider>();
        root.gameObject.layer=12;volume.center=new Vector3(0,size.y*.5f-.405f,0);volume.size=size;
        // Visible perimeter makes the no-entry boundary readable while keeping the falling car visible.
        for(int side=-1;side<=1;side+=2)
        {
            Box(root,"RailZ"+side,new Vector3(0,.75f,side*size.z*.5f),new Vector3(size.x,.07f,.07f),Yellow,false);
            if(name=="WaterTank")Box(root,"RailX"+side,new Vector3(side*size.x*.5f,.75f,0),new Vector3(.07f,.07f,size.z),Yellow,false);
            for(int end=-1;end<=1;end+=2)Box(root,"Post"+side+"_"+end,new Vector3(side*size.x*.5f,.17f,end*size.z*.5f),new Vector3(.09f,1.15f,.09f),Steel,false);
        }
        var nav=root.GetComponent<Unity.AI.Navigation.NavMeshModifierVolume>();if(nav==null)nav=root.gameObject.AddComponent<Unity.AI.Navigation.NavMeshModifierVolume>();nav.center=volume.center;nav.size=size;nav.area=1;
    }
    static void Walkway(LineCrossing crossing)
    {
        var root=Root("Walkway",crossing.transform);
        // A narrow player crossing; NPC link still waits for actual vehicle clearance.
        var deck=Box(root,"Deck",new Vector3(0,.40f,0),new Vector3(1.2f,.08f,2.4f),Steel);
        for(int side=-1;side<=1;side+=2)
        {
            var ramp=Box(root,"Ramp"+side,new Vector3(0,.18f,side*2.1f),new Vector3(1.2f,.08f,1.85f),Steel);
            ramp.transform.localRotation=Quaternion.Euler(side*13.7f,0,0);
        }
        var ignore=root.GetComponent<Unity.AI.Navigation.NavMeshModifier>();if(ignore==null)ignore=root.gameObject.AddComponent<Unity.AI.Navigation.NavMeshModifier>();ignore.ignoreFromBuild=true;
    }
    public static string Routes()
    {
        var supply=Object.FindFirstObjectByType<SupplyManager>();NavMeshHit start;
        if(!NavMesh.SamplePosition(supply.LoadPoint,out start,1.5f,NavMesh.AllAreas))return "No load NavMesh";
        var result=new StringBuilder();int ok=0;
        foreach(var stack in Object.FindObjectsByType<PartStack>(FindObjectsInactive.Include,FindObjectsSortMode.None))
        {
            if(!stack.gameObject.activeInHierarchy)continue;
            NavMeshHit end;var path=new NavMeshPath();
            var target=supply.GetApproachPoint(stack);
            bool valid=NavMesh.SamplePosition(target,out end,1.5f,NavMesh.AllAreas)&&NavMesh.CalculatePath(start.position,end.position,NavMesh.AllAreas,path)&&path.status==NavMeshPathStatus.PathComplete;
            if(valid)ok++;else result.AppendLine(stack.name+" "+target+" "+path.status);
        }
        return "Complete stock routes="+ok+"; failures: "+result;
    }
}
#endif
