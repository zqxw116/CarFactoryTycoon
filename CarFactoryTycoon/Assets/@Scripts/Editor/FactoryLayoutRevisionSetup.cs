#if UNITY_EDITOR
using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using Object = UnityEngine.Object;

/// <summary>One-shot, explicit migration of the original 7m factory rows to 16m service aisles.</summary>
public static class FactoryLayoutRevisionSetup
{
    static T[] All<T>() where T:Object => Object.FindObjectsByType<T>(FindObjectsInactive.Include,FindObjectsSortMode.None);
    static Transform Find(string name) => All<Transform>().FirstOrDefault(t=>t.name==name);
    static void Move(Transform t,Vector3 delta){if(t==null)return;Undo.RecordObject(t,"Factory aisle layout");t.position+=delta;}
    static float Offset(float z)=>-9f*Mathf.Clamp(Mathf.RoundToInt((35f-z)/7f),0,5);
    static float Nearest(SplineContainer s,Vector3 p){SplineUtility.GetNearestPoint(s.Spline,(float3)s.transform.InverseTransformPoint(p),out _,out float t,64,4);return t;}

    public static void BakeCompleteLayout(StringBuilder log)
    {
        var p=All<ProductionProgression>().Single();
        var objects=p.processes.SelectMany(e=>e.robots.Select(r=>r.gameObject).Concat(e.robotDecor).Concat(e.wheel!=null?new[]{e.wheel.gameObject}:new GameObject[0])).Concat(p.finishingRoots).Concat(p.constructionRows).Where(g=>g!=null).Distinct().ToArray();
        var states=objects.Select(g=>g.activeSelf).ToArray();
        foreach(var text in All<TMPro.TextMeshPro>()){var modifier=text.GetComponent<Unity.AI.Navigation.NavMeshModifier>();if(modifier==null)modifier=text.gameObject.AddComponent<Unity.AI.Navigation.NavMeshModifier>();modifier.ignoreFromBuild=true;EditorUtility.SetDirty(modifier);}
        try {foreach(var g in objects)g.SetActive(!p.constructionRows.Contains(g));NavMeshBakeTool.Rebake(log,false);}
        finally {for(int i=0;i<objects.Length;i++)objects[i].SetActive(states[i]);}
    }
    public static string Apply()
    {
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if(Application.isPlaying||scene.name!="Car_Factory 3")throw new InvalidOperationException("Factory edit mode required.");
        var p=All<ProductionProgression>().Single();var spline=p.mainSpline;
        if(Mathf.Abs(spline.transform.TransformPoint((Vector3)spline.Spline[2].Position).z-28)>0.01f)
            throw new InvalidOperationException("Expected original rows. This migration must run only once.");
        Undo.IncrementCurrentGroup();Undo.SetCurrentGroupName("Factory 16m service aisles");
        // Preserve each assembly's complete local hierarchy, poses, piles and trigger geometry.
        for(int row=0;row<6;row++)Move(Find("LINE_"+(row+1)),new Vector3(0,0,-9*row));
        foreach(var e in p.processes){Move(e.humanRoot.transform,new Vector3(0,0,-9*e.row));foreach(var d in e.robotDecor)if(d!=null)Move(d.transform,new Vector3(0,0,-9*e.row));}
        Move(Find("WorkerGroup"),new Vector3(0,0,-9));
        Move(Find("DepartureStation"),new Vector3(0,0,-45));Move(Find("FuelStation"),new Vector3(0,0,-45));
        var warehouseShift=new Vector3(14,0,-18);
        Move(GameObject.Find("GameObjectGroup/Warehouse").transform,warehouseShift);
        Move(Find("WarehouseLoadPoint"),warehouseShift);Move(Find("SupplyWorker_01"),warehouseShift);
        Move(GameObject.Find("Decor/WarehouseBuilding").transform,warehouseShift);
        Move(GameObject.Find("Decor/Warehouse").transform,warehouseShift);
        Move(GameObject.Find("Decor/Outbound").transform,new Vector3(0,0,-45));
        foreach(string group in new[]{"Machines","Safety"})foreach(Transform t in GameObject.Find("Decor/"+group).transform){var q=t.position;Move(t,new Vector3(q.x>62?14:0,0,Offset(q.z)));}
        // Stretch only enclosing architecture; machines and station meshes are never stretched.
        foreach(string group in new[]{"Walls","WallsUpper"}){
            var t=GameObject.Find("Decor/"+group).transform;Undo.RecordObject(t,"Expand factory enclosure");
            t.position=new Vector3(22.3f*(1-1.27f),t.position.y,43.75f*(1-1.87f));t.localScale=new Vector3(1.27f,1,1.87f);
        }
        foreach(var crossing in All<LineCrossing>())Move(crossing.transform,new Vector3(0,0,Offset(crossing.transform.position.z)));
        Undo.RecordObject(spline,"Expand serpentine rows");
        for(int i=0;i<spline.Spline.Count;i++){
            var k=spline.Spline[i];var w=spline.transform.TransformPoint((Vector3)k.Position);w.z+=Offset(w.z);k.Position=spline.transform.InverseTransformPoint(w);
            // Tangents live in knot rotation space. Scale world Z on bends to preserve smooth joins.
            var ti=spline.transform.TransformVector((Vector3)math.rotate(k.Rotation,k.TangentIn));ti.z*=16f/7;
            var to=spline.transform.TransformVector((Vector3)math.rotate(k.Rotation,k.TangentOut));to.z*=16f/7;
            k.TangentIn=math.rotate(math.inverse(k.Rotation),(float3)spline.transform.InverseTransformVector(ti));
            k.TangentOut=math.rotate(math.inverse(k.Rotation),(float3)spline.transform.InverseTransformVector(to));
            spline.Spline.SetKnotNoNotify(i,k);
        }
        spline.Spline[0]=spline.Spline[0];EditorUtility.SetDirty(spline);
        foreach(var e in p.processes){e.gateProgress=Nearest(spline,e.humanRoot.transform.position);e.humanStation.gateProgress=e.gateProgress;EditorUtility.SetDirty(e.humanStation);}
        foreach(var w in All<WorkerStation>()){w.gateProgress=Nearest(spline,w.transform.position);EditorUtility.SetDirty(w);}
        foreach(var w in All<WheelStation>()){w.liftProgress=Nearest(spline,w.transform.position);EditorUtility.SetDirty(w);}
        var water=All<WaterTestStation>().Single();water.gateProgress=Nearest(spline,water.transform.position);water.releaseProgress=Nearest(spline,water.releasePoint.position);EditorUtility.SetDirty(water);
        var shake=All<ShakeTestStation>().Single();shake.gateProgress=Nearest(spline,shake.transform.position);EditorUtility.SetDirty(shake);
        var fuel=All<FuelStation>().Single();fuel.gateProgress=Nearest(spline,fuel.transform.position);EditorUtility.SetDirty(fuel);
        foreach(var cross in All<LineCrossing>()){cross.SnapToSpline();cross.ApplyLink();EditorUtility.SetDirty(cross);}
        var floor=GameObject.Find("Object/Plane").transform;Undo.RecordObject(floor,"Expand factory floor");floor.position=new Vector3(57,-.405f,-5);floor.localScale=new Vector3(7,1,10);
        var cover=p.constructionRows[0].transform;Undo.RecordObject(cover,"Expand construction floor");cover.position=floor.position+Vector3.up*.002f;cover.localScale=floor.localScale;
        // The existing helper requires active inspection objects, so preserve activation around authoring.
        bool wa=water.gameObject.activeSelf,sa=shake.gameObject.activeSelf;water.gameObject.SetActive(true);shake.gameObject.SetActive(true);
        WaterInspectionVisualSetup.RebuildFloorOpenings();water.gameObject.SetActive(wa);shake.gameObject.SetActive(sa);
        var so=new SerializedObject(p.conveyor);var ranges=so.FindProperty("m_ExcludeRanges");ranges.arraySize=2;
        for(int i=0;i<2;i++){var r=ranges.GetArrayElementAtIndex(i);float d=(i==0?water.releaseProgress:shake.gateProgress)*spline.CalculateLength();float half=i==0?2.7f:2.85f;r.FindPropertyRelative("start").floatValue=d-half;r.FindPropertyRelative("end").floatValue=d+half;}
        so.ApplyModifiedProperties();p.conveyor.SetProductionEnd(p.EndFor(1));p.conveyor.Rebuild();EditorUtility.SetDirty(p);
        var camera=All<CameraController>().Single();camera.initialPivot=new Vector3(55,0,-5);camera.initialDistance=100;camera.maxDistance=160;camera.maxOrthoSize=90;EditorUtility.SetDirty(camera);
        var cam=Camera.main;cam.orthographicSize=53;cam.transform.SetPositionAndRotation(camera.initialPivot-Quaternion.Euler(camera.initialPitch,camera.initialYaw,0)*Vector3.forward*100,Quaternion.Euler(camera.initialPitch,camera.initialYaw,0));EditorUtility.SetDirty(cam);
        var log=new StringBuilder();BakeCompleteLayout(log);
        EditorSceneManager.MarkSceneDirty(scene);if(!EditorSceneManager.SaveScene(scene))throw new InvalidOperationException("Scene save failed.");
        return "Rows 35,19,3,-13,-29,-45; warehouse 80.6/0.35. "+log;
    }
    static float NineOffset(float z)=>7f*Mathf.Clamp(Mathf.RoundToInt((35f-z)/16f),0,5);
    public static string ApplyNineMetres()
    {
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if(Application.isPlaying||scene.name!="Car_Factory 3")throw new InvalidOperationException("Factory edit mode required.");
        var p=All<ProductionProgression>().Single();var spline=p.mainSpline;
        if(Mathf.Abs(spline.transform.TransformPoint((Vector3)spline.Spline[2].Position).z-19)>0.01f)
            throw new InvalidOperationException("Expected 16m rows. This migration must run only once.");
        Undo.IncrementCurrentGroup();Undo.SetCurrentGroupName("Factory 9m service aisles");
        // Preserve each assembly's complete local hierarchy, poses, piles and trigger geometry.
        for(int row=0;row<6;row++)Move(Find("LINE_"+(row+1)),new Vector3(0,0,7*row));
        foreach(var e in p.processes){Move(e.humanRoot.transform,new Vector3(0,0,7*e.row));foreach(var d in e.robotDecor)if(d!=null)Move(d.transform,new Vector3(0,0,7*e.row));}
        Move(Find("WorkerGroup"),new Vector3(0,0,7));
        Move(Find("DepartureStation"),new Vector3(0,0,35));Move(Find("FuelStation"),new Vector3(0,0,35));
        var warehouseShift=new Vector3(0,0,14);
        Move(GameObject.Find("GameObjectGroup/Warehouse").transform,warehouseShift);
        Move(Find("WarehouseLoadPoint"),warehouseShift);Move(Find("SupplyWorker_01"),warehouseShift);
        Move(GameObject.Find("Decor/WarehouseBuilding").transform,warehouseShift);
        Move(GameObject.Find("Decor/Warehouse").transform,warehouseShift);
        Move(GameObject.Find("Decor/Outbound").transform,new Vector3(0,0,35));
        foreach(string group in new[]{"Machines","Safety"})foreach(Transform t in GameObject.Find("Decor/"+group).transform){var q=t.position;Move(t,new Vector3(0,0,NineOffset(q.z)));}
        // Stretch only enclosing architecture; machines and station meshes are never stretched.
        foreach(string group in new[]{"Walls","WallsUpper"}){
            var t=GameObject.Find("Decor/"+group).transform;Undo.RecordObject(t,"Expand factory enclosure");
            t.position=new Vector3(22.3f*(1-1.27f),t.position.y,43.75f*(1-1.20f));t.localScale=new Vector3(1.27f,1,1.20f);
        }
        foreach(var crossing in All<LineCrossing>())Move(crossing.transform,new Vector3(0,0,NineOffset(crossing.transform.position.z)));
        Undo.RecordObject(spline,"Expand serpentine rows");
        for(int i=0;i<spline.Spline.Count;i++){
            var k=spline.Spline[i];var w=spline.transform.TransformPoint((Vector3)k.Position);w.z+=NineOffset(w.z);k.Position=spline.transform.InverseTransformPoint(w);
            // Tangents live in knot rotation space. Scale world Z on bends to preserve smooth joins.
            var ti=spline.transform.TransformVector((Vector3)math.rotate(k.Rotation,k.TangentIn));ti.z*=9f/16;
            var to=spline.transform.TransformVector((Vector3)math.rotate(k.Rotation,k.TangentOut));to.z*=9f/16;
            k.TangentIn=math.rotate(math.inverse(k.Rotation),(float3)spline.transform.InverseTransformVector(ti));
            k.TangentOut=math.rotate(math.inverse(k.Rotation),(float3)spline.transform.InverseTransformVector(to));
            spline.Spline.SetKnotNoNotify(i,k);
        }
        spline.Spline[0]=spline.Spline[0];EditorUtility.SetDirty(spline);
        foreach(var e in p.processes){e.gateProgress=Nearest(spline,e.humanRoot.transform.position);e.humanStation.gateProgress=e.gateProgress;EditorUtility.SetDirty(e.humanStation);}
        foreach(var w in All<WorkerStation>()){w.gateProgress=Nearest(spline,w.transform.position);EditorUtility.SetDirty(w);}
        foreach(var w in All<WheelStation>()){w.liftProgress=Nearest(spline,w.transform.position);EditorUtility.SetDirty(w);}
        var water=All<WaterTestStation>().Single();water.gateProgress=Nearest(spline,water.transform.position);water.releaseProgress=Nearest(spline,water.releasePoint.position);EditorUtility.SetDirty(water);
        var shake=All<ShakeTestStation>().Single();shake.gateProgress=Nearest(spline,shake.transform.position);EditorUtility.SetDirty(shake);
        var fuel=All<FuelStation>().Single();fuel.gateProgress=Nearest(spline,fuel.transform.position);EditorUtility.SetDirty(fuel);
        foreach(var cross in All<LineCrossing>()){cross.SnapToSpline();cross.ApplyLink();EditorUtility.SetDirty(cross);}
        var floor=GameObject.Find("Object/Plane").transform;Undo.RecordObject(floor,"Expand factory floor");floor.position=new Vector3(57,-.405f,12.5f);floor.localScale=new Vector3(7,1,6.5f);
        var cover=p.constructionRows[0].transform;Undo.RecordObject(cover,"Expand construction floor");cover.position=floor.position+Vector3.up*.002f;cover.localScale=floor.localScale;
        // The existing helper requires active inspection objects, so preserve activation around authoring.
        bool wa=water.gameObject.activeSelf,sa=shake.gameObject.activeSelf;water.gameObject.SetActive(true);shake.gameObject.SetActive(true);
        WaterInspectionVisualSetup.RebuildFloorOpenings();water.gameObject.SetActive(wa);shake.gameObject.SetActive(sa);
        var so=new SerializedObject(p.conveyor);var ranges=so.FindProperty("m_ExcludeRanges");ranges.arraySize=2;
        for(int i=0;i<2;i++){var r=ranges.GetArrayElementAtIndex(i);float d=(i==0?water.releaseProgress:shake.gateProgress)*spline.CalculateLength();float half=i==0?2.7f:2.85f;r.FindPropertyRelative("start").floatValue=d-half;r.FindPropertyRelative("end").floatValue=d+half;}
        so.ApplyModifiedProperties();p.conveyor.SetProductionEnd(p.EndFor(1));p.conveyor.Rebuild();EditorUtility.SetDirty(p);
        var camera=All<CameraController>().Single();camera.initialPivot=new Vector3(55,0,12.5f);camera.initialDistance=100;camera.maxDistance=160;camera.maxOrthoSize=90;EditorUtility.SetDirty(camera);
        var cam=Camera.main;cam.orthographicSize=38;cam.transform.SetPositionAndRotation(camera.initialPivot-Quaternion.Euler(camera.initialPitch,camera.initialYaw,0)*Vector3.forward*100,Quaternion.Euler(camera.initialPitch,camera.initialYaw,0));EditorUtility.SetDirty(cam);
        var log=new StringBuilder();BakeCompleteLayout(log);
        EditorSceneManager.MarkSceneDirty(scene);if(!EditorSceneManager.SaveScene(scene))throw new InvalidOperationException("Scene save failed.");
        return "Rows 35,26,17,8,-1,-10; warehouse 80.6/14.35. "+log;
    }
}
#endif




