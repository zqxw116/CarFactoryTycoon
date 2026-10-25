#if UNITY_EDITOR
using System;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using Unity.AI.Navigation;
using Object=UnityEngine.Object;

/// <summary>Explicit setup of the approved split inspection line. Does not alter robot placements.</summary>
public static class InspectionLineSetup
{
    public static string Apply()
    {
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if(Application.isPlaying || scene.name!="Car_Factory 3")throw new InvalidOperationException("Open Car_Factory 3 in edit mode.");
        var water=Object.FindFirstObjectByType<WaterTestStation>();
        if(water==null || !water.sharedRejectDeck)throw new InvalidOperationException("Shared water deck is required.");
        if(Mathf.Abs(water.transform.position.x-56.6f)>.05f && Mathf.Abs(water.transform.position.x-50f)>.05f)
            throw new InvalidOperationException("Water station position differs from the approved layout.");
        Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Split assembly and water inspection");
        Undo.RecordObject(water.transform,"Move water inspection downstream");
        water.transform.position=new Vector3(50,water.transform.position.y,7);
        var line=GameObject.Find("MainSpline").GetComponent<SplineContainer>();
        Undo.RecordObject(water,"Update inspection progress");
        water.gateProgress=Nearest(line,water.transform.position);water.releaseProgress=Nearest(line,water.releasePoint.position);
        var root=Root("ShakeTestStation",water.transform.parent);
        Undo.RecordObject(root,"Place assembly inspection");root.position=new Vector3(57,0,7);
        float progress=Nearest(line,root.position);
        SplineUtility.Evaluate(line.Spline,progress,out _,out float3 tangent,out float3 up);
        root.rotation=Quaternion.LookRotation(line.transform.TransformDirection(tangent),line.transform.TransformDirection(up));
        var shake=root.GetComponent<ShakeTestStation>();if(shake==null)shake=Undo.AddComponent<ShakeTestStation>(root.gameObject);
        Undo.RecordObject(shake,"Configure assembly inspection");shake.mainLineSpline=line;shake.gateProgress=progress;
        shake.warningMaterial=Mat("InspectionArc");
        var dark=Mat("SupplyDark");var steel=Mat("SupplySteel");var yellow=Mat("SafetyYellow");var lamp=Mat("InspectionLens");
        // Low frame keeps the neighbouring robot arms visible.
        for(int side=-1;side<=1;side+=2)
        {
            Box(root,"RimSide_"+side,new Vector3(side*1.68f,-.235f,0),new Vector3(.16f,.1f,5.7f),yellow);
            Box(root,"RimEnd_"+side,new Vector3(0,-.235f,side*2.78f),new Vector3(3.2f,.1f,.16f),yellow);
        }
        shake.wheelPads=new Transform[4];shake.pistons=new Transform[4];shake.rejectLeaves=new Transform[2];
        Vector3[] wheelPositions={new Vector3(.9f,-.06f,1.65f),new Vector3(.9f,-.06f,-1.26f),new Vector3(-.9f,-.06f,1.65f),new Vector3(-.9f,-.06f,-1.26f)};
        for(int i=0;i<4;i++)
        {
            var p=wheelPositions[i];
            shake.wheelPads[i]=Box(root,"WheelPad_"+i,p,new Vector3(.68f,.12f,.65f),steel).transform;
            shake.pistons[i]=Box(root,"Piston_"+i,new Vector3(p.x,-.23f,p.z),new Vector3(.13f,.35f,.13f),yellow).transform;
        }
        for(int i=0;i<2;i++)
        {
            float side=i==0?-1:1;
            var leaf=Root("RejectLeaf_"+i,root);Undo.RecordObject(leaf,"Inspection leaf");leaf.localPosition=new Vector3(side*1.6f,-.231f,0);leaf.localRotation=Quaternion.identity;
            Box(leaf,"Plate",new Vector3(-side*.8f,0,0),new Vector3(1.6f,.05f,5.4f),dark);
            for(int j=0;j<4;j++)Box(leaf,"Rib_"+j,new Vector3(-side*.8f,.027f,-1.9f+j*1.25f),new Vector3(1.45f,.004f,.04f),steel);
            shake.rejectLeaves[i]=leaf;
        }
        var pit=Root("RecoveryPit",root);
        Box(pit,"Wall_Left",new Vector3(-1.68f,-1.85f,0),new Vector3(.16f,3.3f,5.6f),dark);
        Box(pit,"Wall_Right",new Vector3(1.68f,-1.85f,0),new Vector3(.16f,3.3f,5.6f),dark);
        Box(pit,"Wall_Front",new Vector3(0,-1.85f,2.78f),new Vector3(3.2f,3.3f,.16f),dark);
        Box(pit,"Wall_Back",new Vector3(0,-1.85f,-2.78f),new Vector3(3.2f,3.3f,.16f),dark);
        var black=water.transform.Find("RejectHatch/HoleDark").GetComponent<Renderer>().sharedMaterial;
        Box(pit,"DarkBottom",new Vector3(0,-3.54f,0),new Vector3(3.4f,.08f,5.6f),black);
        var pitLamp=Mat("PitDepthLamp");
        for(int i=0;i<2;i++)Box(pit,"DepthLine_"+i,new Vector3(1.59f,-.8f-i*1.4f,0),new Vector3(.025f,.035f,3.6f),pitLamp);
        Box(root,"StatusPole",new Vector3(-1.85f,.45f,2.3f),new Vector3(.1f,1.6f,.1f),steel);
        shake.indicator=Box(root,"ResultLamp",new Vector3(-1.85f,1.28f,2.3f),new Vector3(.34f,.17f,.18f),lamp);
        var volume=root.GetComponent<NavMeshModifierVolume>();if(volume==null)volume=Undo.AddComponent<NavMeshModifierVolume>(root.gameObject);
        Undo.RecordObject(volume,"Inspection navigation blocker");volume.center=new Vector3(0,1,0);volume.size=new Vector3(3.6f,3,5.8f);volume.area=1;
        var belt=Object.FindFirstObjectByType<SplineConveyorDeformer>();
        var so=new SerializedObject(belt);var ranges=so.FindProperty("m_ExcludeRanges");ranges.arraySize=2;
        SetRange(ranges.GetArrayElementAtIndex(0),water.releaseProgress*line.CalculateLength(),2.7f);
        SetRange(ranges.GetArrayElementAtIndex(1),progress*line.CalculateLength(),2.85f);
        so.ApplyModifiedProperties();belt.Rebuild();
        EditorUtility.SetDirty(water);EditorUtility.SetDirty(shake);
        // Rebuild the original floor with both current openings, retaining its material and UVs.
        WaterInspectionVisualSetup.Apply();
        var log=new StringBuilder();NavMeshBakeTool.Rebake(log,false);
        EditorSceneManager.MarkSceneDirty(scene);
        if(!EditorSceneManager.SaveScene(scene))throw new InvalidOperationException("Could not save inspection scene.");
        Undo.CollapseUndoOperations(group);
        return "Assembly shake 57/7 -> water gate 50/7 -> water/reject 43.4, dolly advance 6.6m. "+log;
    }
    static void SetRange(SerializedProperty range,float distance,float half)
    {range.FindPropertyRelative("splineIndex").intValue=0;range.FindPropertyRelative("start").floatValue=distance-half;range.FindPropertyRelative("end").floatValue=distance+half;}
    static float Nearest(SplineContainer line,Vector3 world)
    {SplineUtility.GetNearestPoint(line.Spline,(float3)line.transform.InverseTransformPoint(world),out _,out float t);return t;}
    static Material Mat(string name)=>AssetDatabase.LoadAssetAtPath<Material>("Assets/@Resource/Materials/FactoryFlow/"+name+".mat");
    static Transform Root(string name,Transform parent)
    {var t=parent!=null?parent.Find(name):GameObject.Find(name)?.transform;if(t==null){var go=new GameObject(name);Undo.RegisterCreatedObjectUndo(go,name);t=go.transform;t.SetParent(parent,false);}return t;}
    static MeshRenderer Box(Transform parent,string name,Vector3 pos,Vector3 size,Material mat)
    {
        var t=parent.Find(name);if(t==null){var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;Undo.RegisterCreatedObjectUndo(go,name);t=go.transform;t.SetParent(parent,false);Object.DestroyImmediate(go.GetComponent<Collider>());}
        Undo.RecordObject(t,name);t.localPosition=pos;t.localRotation=Quaternion.identity;t.localScale=size;
        var r=t.GetComponent<MeshRenderer>();Undo.RecordObject(r,name);r.sharedMaterial=mat;return r;
    }
}
#endif
