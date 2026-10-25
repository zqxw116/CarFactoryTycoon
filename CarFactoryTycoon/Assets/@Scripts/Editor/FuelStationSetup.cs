#if UNITY_EDITOR
using System;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Splines;
using Unity.Mathematics;
using Unity.AI.Navigation;
using Object=UnityEngine.Object;

/// <summary>Explicitly authors the fuel preparation bay on the final straight before departure.</summary>
public static class FuelStationSetup
{
    const string Dir="Assets/@Resource/Materials/FactoryFlow";
    public static string Apply()
    {
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if(Application.isPlaying || scene.name!="Car_Factory 3")throw new InvalidOperationException("Open Car_Factory 3 in edit mode.");
        var worker=Object.FindFirstObjectByType<Worker>();
        var line=GameObject.Find("MainSpline").GetComponent<SplineContainer>();
        if(worker==null || line==null)throw new InvalidOperationException("Missing worker model or production line.");
        Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Fuel preparation and automation");
        var root=Root("FuelStation",null);Undo.RecordObject(root,"Place fuel bay");root.position=new Vector3(38.5f,0,0);
        var fuel=root.GetComponent<FuelStation>();if(fuel==null)fuel=Undo.AddComponent<FuelStation>(root.gameObject);
        Undo.RecordObject(fuel,"Configure fuel bay");fuel.mainLineSpline=line;
        SplineUtility.GetNearestPoint(line.Spline,(float3)line.transform.InverseTransformPoint(root.position),out _,out float progress);fuel.gateProgress=progress;
        Material dark=Mat("SupplyDark"),steel=Mat("SupplySteel"),yellow=Mat("SafetyYellow");
        var green=NewMat("FuelGreen",new Color(.06f,.58f,.35f),true);
        var rubber=NewMat("FuelHose",new Color(.025f,.028f,.03f),false);
        var pump=Root("Pump",root);pump.localPosition=new Vector3(-1.7f,-.405f,-3.35f);
        Box(pump,"Plinth",new Vector3(0,.08f,0),new Vector3(1.25f,.16f,1.05f),dark);
        Box(pump,"Cabinet",new Vector3(0,.88f,0),new Vector3(.8f,1.6f,.65f),steel);
        Box(pump,"GreenPanel",new Vector3(0,.91f,-.34f),new Vector3(.72f,1.18f,.035f),green);
        Box(pump,"Canopy",new Vector3(0,1.77f,0),new Vector3(.98f,.18f,.78f),dark);
        Box(pump,"Screen",new Vector3(0,1.25f,-.37f),new Vector3(.61f,.35f,.04f),dark);
        fuel.fillBar=Box(pump,"FillMeter",new Vector3(0,1.24f,-.4f),new Vector3(.52f,.075f,.025f),green).transform;
        fuel.statusLens=Box(pump,"StatusLamp",new Vector3(.29f,1.64f,-.36f),new Vector3(.13f,.1f,.04f),Mat("InspectionLens"));
        var title=Root("FuelLabel",pump);title.localPosition=new Vector3(-.08f,1.62f,-.39f);
        var text=title.GetComponent<TextMeshPro>();if(text==null)text=Undo.AddComponent<TextMeshPro>(title.gameObject);
        text.font=TMP_Settings.defaultFontAsset;text.text="FUEL";text.fontSize=2.2f;text.enableWordWrapping=false;text.alignment=TextAlignmentOptions.Center;text.color=Color.white;text.rectTransform.sizeDelta=new Vector2(.52f,.18f);
        fuel.nozzleDock=Root("NozzleDock",root);fuel.nozzleDock.localPosition=new Vector3(-1.1f,1.05f,-2.9f);fuel.nozzleDock.localRotation=Quaternion.identity;
        fuel.hoseAnchor=Root("HoseAnchor",root);fuel.hoseAnchor.localPosition=new Vector3(-1.32f,.75f,-3.35f);
        fuel.homePoint=Root("AttendantHome",root);fuel.homePoint.localPosition=new Vector3(-.5f,-.305f,-3.2f);
        fuel.nozzle=Root("Nozzle",root);Undo.RecordObject(fuel.nozzle,"Park fuel nozzle");fuel.nozzle.SetPositionAndRotation(fuel.nozzleDock.position,fuel.nozzleDock.rotation);
        Box(fuel.nozzle,"MetalSpout",new Vector3(0,0,-.13f),new Vector3(.055f,.055f,.26f),steel);
        Box(fuel.nozzle,"Handle",new Vector3(0,-.07f,-.36f),new Vector3(.13f,.19f,.23f),green);
        Box(fuel.nozzle,"Trigger",new Vector3(0,-.17f,-.3f),new Vector3(.075f,.025f,.17f),dark);
        fuel.hoseEnd=Root("HoseEnd",fuel.nozzle);fuel.hoseEnd.localPosition=new Vector3(0,-.14f,-.48f);
        var hose=Root("Hose",root);fuel.hose=hose.GetComponent<LineRenderer>();if(fuel.hose==null)fuel.hose=Undo.AddComponent<LineRenderer>(hose.gameObject);
        fuel.hose.sharedMaterial=rubber;fuel.hose.widthMultiplier=.065f;fuel.hose.useWorldSpace=true;fuel.hose.positionCount=24;fuel.hose.numCapVertices=4;fuel.hose.numCornerVertices=3;fuel.hose.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
        // A short idle hose is also visible in edit mode.
        for(int i=0;i<24;i++){float t=i/23f;fuel.hose.SetPosition(i,Vector3.Lerp(fuel.hoseAnchor.position,fuel.hoseEnd.position,t)+Vector3.down*Mathf.Sin(t*Mathf.PI)*.45f);}
        var model=root.Find("FuelAttendant");
        if(model==null)
        {
            var copy=Object.Instantiate(worker.gameObject,root);copy.name="FuelAttendant";Undo.RegisterCreatedObjectUndo(copy,"Fuel attendant");model=copy.transform;
            // Keep only the animated visual, hand IK and animation-event receiver, not another assembly worker.
            foreach(var status in copy.GetComponentsInChildren<WorkerStatusUI>(true))Object.DestroyImmediate(status);
            foreach(var script in copy.GetComponentsInChildren<MonoBehaviour>(true))
                if(!(script is WorkerHandIK) && !(script is WorkerFootstepReceiver))Object.DestroyImmediate(script);
            foreach(var agent in copy.GetComponentsInChildren<NavMeshAgent>(true))Object.DestroyImmediate(agent);
            foreach(var obstacle in copy.GetComponentsInChildren<NavMeshObstacle>(true))Object.DestroyImmediate(obstacle);
            foreach(var collider in copy.GetComponentsInChildren<Collider>(true))Object.DestroyImmediate(collider);
            foreach(var rigidbody in copy.GetComponentsInChildren<Rigidbody>(true))Object.DestroyImmediate(rigidbody);
        }
        Undo.RecordObject(model,"Fuel attendant home");model.position=fuel.homePoint.position;model.rotation=Quaternion.Euler(0,0,0);
        fuel.attendant=model;fuel.attendantMover=model.GetComponent<NavMover>();if(fuel.attendantMover==null)fuel.attendantMover=Undo.AddComponent<NavMover>(model.gameObject);
        fuel.attendantMover.baseOffset=.085f;fuel.attendantMover.agentRadius=.28f;fuel.attendantMover.arriveRadius=.08f;
        fuel.attendantAnimator=model.GetComponentInChildren<Animator>(true);fuel.attendantAnimator.applyRootMotion=false;fuel.attendantAnimator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        fuel.handIK=fuel.attendantAnimator.GetComponent<WorkerHandIK>();if(fuel.handIK==null)fuel.handIK=Undo.AddComponent<WorkerHandIK>(fuel.attendantAnimator.gameObject);
        var modifier=model.GetComponent<NavMeshModifier>();if(modifier==null)modifier=Undo.AddComponent<NavMeshModifier>(model.gameObject);modifier.ignoreFromBuild=true;
        var automatic=Root("AutomaticFuelArm",root);
        Box(automatic,"Pedestal",new Vector3(-1.35f,.1f,-2.8f),new Vector3(.5f,1,.5f),dark);
        fuel.autoShoulder=Root("Shoulder",automatic);fuel.autoShoulder.localPosition=new Vector3(-1.35f,1.05f,-2.8f);
        Box(automatic,"ShoulderJoint",new Vector3(-1.35f,1.05f,-2.8f),new Vector3(.3f,.3f,.3f),yellow);
        fuel.upperArm=Box(automatic,"UpperArm",new Vector3(-1.2f,1.2f,-2.6f),new Vector3(.15f,.7f,.15f),yellow).transform;
        fuel.lowerArm=Box(automatic,"LowerArm",new Vector3(-1.1f,1.1f,-2.4f),new Vector3(.15f,.7f,.15f),steel).transform;
        fuel.elbowJoint=Box(automatic,"Elbow",new Vector3(-1.1f,1.2f,-2.5f),new Vector3(.24f,.24f,.24f),yellow).transform;
        fuel.automationRig=automatic.gameObject;automatic.gameObject.SetActive(false);
        // A clear stance marker on the factory floor; no collider or second conveyor stop platform.
        Box(root,"WorkMark",new Vector3(-1.1f,-.4f,-1.95f),new Vector3(.75f,.008f,.6f),yellow);
        EditorUtility.SetDirty(fuel);EditorUtility.SetDirty(fuel.attendantMover);EditorUtility.SetDirty(modifier);
        var log=new StringBuilder();NavMeshBakeTool.Rebake(log,false);
        EditorSceneManager.MarkSceneDirty(scene);if(!EditorSceneManager.SaveScene(scene))throw new InvalidOperationException("Could not save fuel bay.");
        Undo.CollapseUndoOperations(group);
        return "Fuel stop (38.5,0,0), pump outside belt, human attendant and automatic arm ready; prices automation 1200, speed 300/510/867. "+log;
    }
    static Material Mat(string name)=>AssetDatabase.LoadAssetAtPath<Material>(Dir+"/"+name+".mat");
    static Material NewMat(string name,Color color,bool emission)
    {
        var m=Mat(name);if(m==null){m=new Material(Mat("SupplySteel"));m.name=name;AssetDatabase.CreateAsset(m,Dir+"/"+name+".mat");}
        m.SetColor("_BaseColor",color);if(emission){m.EnableKeyword("_EMISSION");m.SetColor("_EmissionColor",color*.6f);}EditorUtility.SetDirty(m);AssetDatabase.SaveAssetIfDirty(m);return m;
    }
    static Transform Root(string name,Transform parent)
    {var t=parent!=null?parent.Find(name):GameObject.Find(name)?.transform;if(t==null){var go=new GameObject(name);Undo.RegisterCreatedObjectUndo(go,name);t=go.transform;t.SetParent(parent,false);}return t;}
    static MeshRenderer Box(Transform parent,string name,Vector3 pos,Vector3 scale,Material material)
    {var t=parent.Find(name);if(t==null){var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;Undo.RegisterCreatedObjectUndo(go,name);Object.DestroyImmediate(go.GetComponent<Collider>());t=go.transform;t.SetParent(parent,false);}Undo.RecordObject(t,name);t.localPosition=pos;t.localRotation=Quaternion.identity;t.localScale=scale;var r=t.GetComponent<MeshRenderer>();r.sharedMaterial=material;return r;}
}
#endif
