#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;
using Object=UnityEngine.Object;

/// <summary>Explicit scene authoring. Runtime progression only changes visibility, never authored robot poses.</summary>
public static class ProductionProgressionSetup
{
    [MenuItem("Tools/CarFactory/Build human-first progression")]
    public static void Apply()
    {
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if(Application.isPlaying||scene.name!="Car_Factory 3")throw new InvalidOperationException("Open factory scene in edit mode.");
        if(Object.FindFirstObjectByType<ProductionProgression>()!=null)throw new InvalidOperationException("Progression already authored; preserve existing setup.");
        var template=Object.FindFirstObjectByType<Worker>();
        var oldStation=Object.FindFirstObjectByType<WorkerStation>();
        var spline=GameObject.Find("MainSpline").GetComponent<SplineContainer>();
        var robots=Object.FindObjectsByType<StationController>(FindObjectsInactive.Include,FindObjectsSortMode.None);
        var wheel=Object.FindFirstObjectByType<WheelStation>();
        if(template==null||oldStation==null||spline==null||wheel==null)throw new InvalidOperationException("Missing production templates.");
        Undo.IncrementCurrentGroup();Undo.SetCurrentGroupName("Human-first factory growth");
        var root=new GameObject("ProductionProgression");Undo.RegisterCreatedObjectUndo(root,"Growth");
        var manager=root.AddComponent<ProductionProgression>();manager.mainSpline=spline;
        manager.conveyor=Object.FindFirstObjectByType<SplineConveyorDeformer>();
        var list=new List<ProductionProgression.ProcessEntry>();
        Add(list,manager,template,spline,robots,null,0,"조작부",PartType.Controll_21);
        Add(list,manager,template,spline,robots,null,0,"엔진",PartType.Engine_22);
        Add(list,manager,template,spline,robots,null,0,"엔진커버",PartType.EngineCover_23);
        Add(list,manager,template,spline,robots,null,0,"오른쪽 좌석",PartType.CarSeat_Right_24);
        Add(list,manager,template,spline,robots,null,0,"왼쪽 좌석",PartType.CarSeat_Left_25);
        Add(list,manager,template,spline,robots,null,1,"왼쪽 뒤유리",PartType.BehindWindow_Left_31);
        Add(list,manager,template,spline,robots,null,1,"오른쪽 뒤유리",PartType.BehindWindow_Right_32);
        Add(list,manager,template,spline,robots,null,1,"앞유리",PartType.FrontWindow_33);
        Add(list,manager,template,spline,robots,null,1,"왼쪽 문",PartType.FrontDoor_Left_34);
        Add(list,manager,template,spline,robots,null,1,"오른쪽 문",PartType.FrontDoor_Right_35);
        Add(list,manager,template,spline,robots,null,1,"앞범퍼",PartType.Bumper_51);
        Add(list,manager,template,spline,robots,null,1,"트렁크",PartType.Trunk_52);
        Add(list,manager,template,spline,robots,null,2,"보닛",PartType.Hood_53);
        Add(list,manager,template,spline,robots,wheel,2,"바퀴 작업팀",PartType.Wheel_FrontRight_41,PartType.Wheel_BehindRight_42,PartType.Wheel_FrontLeft_43,PartType.Wheel_BehindLeft_44);
        Add(list,manager,template,spline,robots,null,2,"오른쪽 미러",PartType.BackMirror_Right_62);
        Add(list,manager,template,spline,robots,null,2,"왼쪽 미러",PartType.BackMirror_Left_61);
        Add(list,manager,template,spline,robots,null,3,"왼쪽 전조등",PartType.FrontLight_Left_63);
        Add(list,manager,template,spline,robots,null,3,"오른쪽 전조등",PartType.FrontLight_Right_64);
        Add(list,manager,template,spline,robots,null,3,"왼쪽 뒤조명",PartType.BehindLight_LeftBig_66);
        Add(list,manager,template,spline,robots,null,3,"오른쪽 뒤조명",PartType.BehindLight_RightBig_65);
        Add(list,manager,template,spline,robots,null,3,"왼쪽 보조조명",PartType.BehindLight_LeftSmall_67);
        Add(list,manager,template,spline,robots,null,3,"오른쪽 보조조명",PartType.BehindLight_RightSmall_68);
        Add(list,manager,template,spline,robots,null,3,"뒤범퍼",PartType.Bumper2_69);
        list.Sort((a,b)=>a.gateProgress.CompareTo(b.gateProgress));
        manager.processes=list.ToArray();
        var finish=new List<GameObject>();
        foreach(string name in new[]{"WaterTestStation","ShakeTestStation","FuelStation","Pool","GantryCrane","RejectHatch"})
        {var go=GameObject.Find(name);if(go!=null&&!finish.Contains(go))finish.Add(go);}
        // Any complete inspection hardware shares the station root in the authored setup.
        manager.finishingRoots=finish.ToArray();
        var floor=GameObject.Find("Object/Plane");
        var cover=GameObject.CreatePrimitive(PrimitiveType.Plane);cover.name="FloorBeforeInspection";cover.transform.SetParent(root.transform,false);
        cover.transform.SetPositionAndRotation(floor.transform.position+Vector3.up*.002f,floor.transform.rotation);cover.transform.localScale=floor.transform.lossyScale;
        Object.DestroyImmediate(cover.GetComponent<Collider>());cover.GetComponent<Renderer>().sharedMaterial=floor.GetComponent<Renderer>().sharedMaterial;
        manager.constructionRows=new[]{cover};
        foreach(var p in manager.processes)
        {
            p.hired=p==manager.processes[0];
            p.humanRoot.SetActive(p.hired);
            foreach(var r in p.robots)r.gameObject.SetActive(false);
            foreach(var decor in p.robotDecor)if(decor!=null)decor.SetActive(false);
            if(p.wheel!=null)p.wheel.gameObject.SetActive(false);
        }
        foreach(var go in finish)go.SetActive(false);
        // Retain the original demonstration worker without running a second bumper station.
        oldStation.enabled=false;template.gameObject.SetActive(false);
        if(oldStation.partStacks!=null)foreach(var stack in oldStation.partStacks)if(stack!=null)stack.gameObject.SetActive(false);
        manager.conveyor.SetProductionEnd(manager.EndFor(1));
        EditorUtility.SetDirty(manager);EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[Production] Authored 23 sequential workplaces across four rows; first worker only.");
    }
    static void Add(List<ProductionProgression.ProcessEntry> list,ProductionProgression manager,Worker template,SplineContainer spline,StationController[] all,WheelStation wheel,int row,string label,params PartType[] parts)
    {
        var matching=new List<StationController>();foreach(var r in all)if(Array.IndexOf(parts,r.targetPartType)>=0)matching.Add(r);
        if(matching.Count!=parts.Length)throw new InvalidOperationException("Missing robot template for "+label);
        Vector3 source=wheel!=null?wheel.transform.position:matching[0].transform.position;
        float3 nearest;float t;SplineUtility.GetNearestPoint(spline.Spline,(float3)spline.transform.InverseTransformPoint(source),out nearest,out t);
        Vector3 dock=spline.transform.TransformPoint((Vector3)nearest);
        var human=new GameObject("Human_"+parts[0]);human.transform.SetParent(manager.transform,false);human.transform.position=dock;
        var station=human.AddComponent<WorkerStation>();station.enabled=false;station.mainLineSpline=spline;station.autoProgressFromPosition=false;station.gateProgress=t;
        station.targetPartTypes=parts;station.externalWorkCoordinator=wheel!=null;station.leadDistance=2f;
        var workers=new List<Worker>();
        for(int i=0;i<(wheel!=null?2:1);i++)
        {
            var model=Object.Instantiate(template.gameObject,human.transform);model.name="Worker_"+i;
            Vector3 home=wheel!=null?dock+new Vector3(0,-.405f,i==0?2.8f:-2.8f):source;
            home.y=-.405f;model.transform.position=home;model.transform.rotation=Quaternion.identity;
            var worker=model.GetComponent<Worker>();worker.condition=100;worker.currentState=Worker.WorkerState.Idle;worker.assembleSpeed=4;
            worker.SetHome(home);worker.enabled=true;workers.Add(worker);
        }
        station.workers=workers.ToArray();
        var stacks=new PartStack[parts.Length];
        for(int i=0;i<parts.Length;i++)
        {
            var go=new GameObject("Stock_"+parts[i]);go.transform.SetParent(human.transform,false);
            Vector3 pos=matching.Find(r=>r.targetPartType==parts[i]).stationPilePos.position;
            pos.y=.1f;go.transform.position=pos;
            var stack=go.AddComponent<PartStack>();stack.partType=parts[i];stack.capacity=12;stack.startCount=12;stack.itemScale=.25f;
            stack.stepOffset=new Vector3(0,.12f,0);stack.perRow=4;stack.rowOffset=new Vector3(.4f,0,0);
            stacks[i]=stack;
        }
        station.partStacks=stacks;station.pilePos=stacks[0].transform;
        var entry=new ProductionProgression.ProcessEntry{displayName=label,row=row,parts=parts,humanStation=station,workers=workers.ToArray(),robots=matching.ToArray(),humanRoot=human,gateProgress=t,wheel=wheel};
        var decor=new List<GameObject>();foreach(var r in matching){var pad=GameObject.Find("Decor/Pedestals/Pedestal_"+r.name);if(pad!=null)decor.Add(pad);}entry.robotDecor=decor.ToArray();
        if(wheel!=null){wheel.humanWorkers=entry.workers;wheel.humanPartStacks=stacks;wheel.useHumanWorkers=true;}
        list.Add(entry);station.enabled=true;
    }
}
#endif
