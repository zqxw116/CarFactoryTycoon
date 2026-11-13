#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;
using Unity.Mathematics;
using Object=UnityEngine.Object;

public static class ProcessAuthoringSceneSetup
{
    public const string ScenePath="Assets/@Scene/ProcessAuthoringScene.unity";
    [MenuItem("Tools/CarFactory/Process Authoring/Create Scene From Main")]
    public static void Create()
    {
        if(Application.isPlaying)throw new InvalidOperationException("Edit mode required.");
        var source=Object.FindFirstObjectByType<ProductionProgression>(FindObjectsInactive.Include);
        if(source==null||!source.gameObject.scene.path.EndsWith("Car_Factory 3.unity"))throw new InvalidOperationException("Open main factory first.");
        if(source.authoringProfile==null)throw new InvalidOperationException("Import main layout first.");
        if(System.IO.File.Exists(ScenePath))throw new InvalidOperationException("Authoring scene already exists; open it instead of overwriting edits.");
        if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;
        var sourceScene=source.gameObject.scene;
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
        try
        {
            var root=new GameObject("ProcessAuthoring");
            var preview=root.AddComponent<ProcessAuthoringPreview>();
            var traffic=new GameObject("LineTrafficManager").AddComponent<LineTrafficManager>();
            var path=new GameObject("PreviewRail").AddComponent<SplineContainer>();
            float length=source.authoringProfile.previewLength;
            var spline=new Spline();
            spline.Add(new BezierKnot(new float3(-length*.5f,0,0)),TangentMode.Linear);
            spline.Add(new BezierKnot(new float3(length*.5f,0,0)),TangentMode.Linear);
            path.Spline=spline;
            var p=root.AddComponent<ProductionProgression>();
            p.prototypeMode=true;p.persistProgress=false;p.authoringProfile=source.authoringProfile;
            p.prototypeSettings=source.prototypeSettings;p.mainSpline=path;
            p.processes=new ProductionProgression.ProcessEntry[source.processes.Length];
            p.finishingRoots=Array.Empty<GameObject>();p.constructionRows=Array.Empty<GameObject>();
            preview.progression=p;preview.vehicleSpeed=source.prototypeSettings.lowSpeed;preview.processRoots=new GameObject[p.processes.Length];
            for(int i=0;i<source.processes.Length;i++)
            {
                var original=source.processes[i];
                var group=new GameObject("Process_"+i.ToString("D2")+"_"+original.displayName);group.transform.SetParent(root.transform,false);
                var humans=new GameObject("Workers");humans.transform.SetParent(group.transform,false);
                var robots=new GameObject("RobotArms");robots.transform.SetParent(group.transform,false);
                var inventory=new GameObject("Inventory");inventory.transform.SetParent(group.transform,false);
                var station=humans.AddComponent<WorkerStation>();station.flowThrough=true;station.autoProgressFromPosition=false;station.mainLineSpline=path;
                var entry=new ProductionProgression.ProcessEntry { authoringId=original.authoringId,displayName=original.displayName,row=0,
                    parts=(PartType[])original.parts.Clone(),humanStation=station,humanRoot=humans,gateProgress=.35f };
                var workers=new List<Worker>();
                if(original.workers!=null)foreach(var worker in original.workers)
                    if(worker!=null){var clone=Object.Instantiate(worker.gameObject,humans.transform).GetComponent<Worker>();clone.transform.localScale=worker.transform.lossyScale;clone.name=worker.name;clone.gameObject.SetActive(true);workers.Add(clone);}
                entry.workers=workers.ToArray();station.workers=entry.workers;station.targetPartTypes=entry.parts;
                var stacks=new List<PartStack>();
                var oldStacks=original.humanStation!=null?original.humanStation.partStacks:original.wheel!=null?original.wheel.humanPartStacks:null;
                if(oldStacks!=null)foreach(var stock in oldStacks)
                    if(stock!=null){var clone=Object.Instantiate(stock.gameObject,inventory.transform).GetComponent<PartStack>();clone.transform.localScale=stock.transform.lossyScale;clone.name=stock.name;stacks.Add(clone);}
                station.partStacks=stacks.ToArray();
                var arms=new List<StationController>();
                if(original.robots!=null)foreach(var robot in original.robots)
                    if(robot!=null){var clone=Object.Instantiate(robot.gameObject,robots.transform).GetComponent<StationController>();clone.transform.localScale=robot.transform.lossyScale;clone.name=robot.name;clone.prototypeMode=true;clone.gameObject.SetActive(true);arms.Add(clone);}
                entry.robots=arms.ToArray();entry.robotDecor=Array.Empty<GameObject>();
                if(original.wheel!=null)
                {
                    var lift=new GameObject("WheelHardware");lift.transform.SetParent(group.transform,false);
                    entry.wheel=lift.AddComponent<WheelStation>();entry.wheel.mainLineSpline=path;entry.wheel.autoProgressFromPosition=false;
                    entry.wheel.flowThrough=true;entry.wheel.humanWorkers=entry.workers;entry.wheel.humanPartStacks=station.partStacks;entry.wheel.wheelStations=entry.robots;
                    entry.wheel.enabled=false;
                    if(original.wheel.liftPlatform!=null)entry.wheel.liftPlatform=Object.Instantiate(original.wheel.liftPlatform.gameObject,lift.transform).transform;
                }
                if (original.wheelPlatform != null)
                    entry.wheelPlatform=Object.Instantiate(original.wheelPlatform.gameObject,group.transform).transform;
                var post=original.hiringPost;
                if(post!=null){var clone=Object.Instantiate(post.gameObject,group.transform);clone.name="HiringPost";entry.hiringPost=clone.transform;}
                p.processes[i]=entry;preview.processRoots[i]=group;robots.SetActive(false);
                group.SetActive(i==0);
            }
            preview.vehicleInstances=new CarController[source.authoringProfile.vehicles.Count];
            var vehicles=new GameObject("Vehicles");vehicles.transform.SetParent(root.transform,false);
            for(int i=0;i<preview.vehicleInstances.Length;i++)
            {
                var model=source.authoringProfile.vehicles[i];if(model.prefab==null)continue;
                var instance=(GameObject)PrefabUtility.InstantiatePrefab(model.prefab,vehicles.transform);
                var car=instance.GetComponent<CarController>();if(car==null)car=instance.AddComponent<CarController>();
                preview.vehicleInstances[i]=car;car.isMoving=false;instance.transform.position=new Vector3(-length*.5f,0,0);instance.SetActive(i==0);
            }
            if(source.conveyor!=null)
            {
                var conveyor=Object.Instantiate(source.conveyor.gameObject).GetComponent<SplineConveyorDeformer>();conveyor.name="PreviewConveyor";
                var data=new SerializedObject(conveyor);data.FindProperty("m_Spline").objectReferenceValue=path;
                data.FindProperty("m_LineSource").objectReferenceValue=null;
                data.FindProperty("m_OriginalBeltRenderer").objectReferenceValue=null;
                data.FindProperty("m_HideOriginalBelt").boolValue=false;
                data.FindProperty("m_SyncLineSpeed").boolValue=false;
                var widths=data.FindProperty("m_SplineWidths");if(widths!=null){widths.arraySize=1;widths.GetArrayElementAtIndex(0).floatValue=source.authoringProfile.railWidth;}
                var ranges=data.FindProperty("m_ExcludeRanges");if(ranges!=null&&ranges.isArray)ranges.arraySize=0;
                data.ApplyModifiedPropertiesWithoutUndo();p.conveyor=conveyor;
            }
            var frame=ProcessAuthoringLayout.Frame(source,source.processes[0]);
            float floorY=source.processes[0].workers.Length>0?source.processes[0].workers[0].transform.position.y-frame.position.y:-.9f;
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.name="PreviewFloor";floor.transform.position=new Vector3(0,floorY-.1f,0);floor.transform.localScale=new Vector3(length+10,.2f,18);
            var camera=new GameObject("Main Camera").AddComponent<Camera>();camera.tag="MainCamera";camera.gameObject.AddComponent<AudioListener>();
            camera.transform.position=new Vector3(3,9,-12);camera.transform.LookAt(new Vector3(0,1,0));camera.nearClipPlane=.1f;camera.farClipPlane=100;
            var light=new GameObject("Directional Light").AddComponent<Light>();light.type=LightType.Directional;light.intensity=2;light.transform.rotation=Quaternion.Euler(50,-30,0);
            ProcessAuthoringLayout.Apply(p,p.authoringProfile);
            for(int i=0;i<p.processes.Length;i++)
            {
                var entry=p.processes[i];var record=p.authoringProfile.Find(entry.authoringId);
                var post=preview.processRoots[i].transform.Find("HiringPost");
                if(post!=null&&record!=null&&record.hasHiringPost)ProcessAuthoringLayout.WithDistance(record.hiringPost,record.hiringPostSide,p.authoringProfile.hiringPostDistance).Apply(post,ProcessAuthoringLayout.Frame(p,entry));
                if(entry.humanStation!=null)entry.humanStation.enabled=false;
                if(entry.robots!=null)foreach(var arm in entry.robots)if(arm!=null)arm.enabled=false;
            }
            ProcessAuthoringRigSetup.Prepare(p);
            EditorSceneManager.SaveScene(scene,ScenePath);
            EditorSceneManager.CloseScene(sourceScene,true);
            SceneManager.SetActiveScene(scene);
            Selection.activeGameObject=root;
            Debug.Log("Created single authoring scene with "+p.processes.Length+" imported process groups.");
        }
        catch{EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(sourceScene);throw;}
    }
}
#endif
