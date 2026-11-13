#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

public static class FactoryProcessEditorChecks
{
    static void Need(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    public static string Run()
    {
        if(Application.isPlaying)throw new InvalidOperationException("Edit mode required");
        var preview=Object.FindFirstObjectByType<ProcessAuthoringPreview>();
        Need(preview!=null,"Open workbench scene");
        int priorProcess=preview.selectedProcess;bool priorRobot=preview.robotMode;
        preview.selectedProcess=8;preview.robotMode=true;
        var window=ScriptableObject.CreateInstance<ProcessAuthoringWindow>();
        try
        {
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            Need((int)typeof(ProcessAuthoringWindow).GetField("processIndex",flags).GetValue(window)==8&&(bool)typeof(ProcessAuthoringWindow).GetField("robot",flags).GetValue(window),"reopened window follows current selection");
        }
        finally{Object.DestroyImmediate(window);preview.selectedProcess=priorProcess;preview.robotMode=priorRobot;}
        var fixture=Object.Instantiate(preview.progression.authoringProfile);
        Undo.IncrementCurrentGroup();int undo=Undo.GetCurrentGroup();
        try
        {
            var car=preview.vehicleInstances[0];var model=fixture.vehicles[0];
            var template=model.prefab.GetComponentsInChildren<AssemblyPart>(true).First();
            var part=car.GetComponentsInChildren<AssemblyPart>(true).First(x=>x.myType==template.myType);
            var data=new ProcessPartOverride{type=part.myType,overridePose=true,overrideGrip=true,attachment=new AuthoringPose{position=template.assembledPos+Vector3.up*.2f},leftGrip=Vector3.left,rightGrip=Vector3.right};model.parts.Add(data);
            ProcessAuthoringLayout.ApplyVehicle(car,fixture,model.id);Need(Vector3.Distance(part.assembledPos,data.attachment.position)<.0001f,"attachment override applied");
            data.overridePose=data.overrideGrip=false;ProcessAuthoringLayout.ApplyVehicle(car,fixture,model.id);
            Need(Vector3.Distance(part.assembledPos,template.assembledPos)<.0001f,"disabled attachment restores prefab");
            Need(!part.GetComponent<PartGripOverride>().enabled,"disabled grip restores fallback");
            model.prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Prefabs/CarModel.prefab");
            Need(model.prefab!=null,"replacement prefab exists");ProcessAuthoringVehicles.Sync(preview,fixture);
            Need(PrefabUtility.GetCorrespondingObjectFromSource(preview.vehicleInstances[0].gameObject)==model.prefab,"same ID replacement uses new prefab");
            Undo.RevertAllDownToGroup(undo);
            Need(PrefabUtility.GetCorrespondingObjectFromSource(preview.vehicleInstances[0].gameObject)==preview.progression.authoringProfile.vehicles[0].prefab,"vehicle sync Undo restores source");
        }
        finally{Undo.RevertAllDownToGroup(undo);Object.DestroyImmediate(fixture);}
        var previous=SceneManager.GetActiveScene();
        var main=EditorSceneManager.OpenScene("Assets/Car_Factory/DemoScene/Car_Factory 3.unity",OpenSceneMode.Additive);
        try
        {
            var p=main.GetRootGameObjects().SelectMany(x=>x.GetComponentsInChildren<ProductionProgression>(true)).First();
            var sentinel=new GameObject("UnsavedAuthoringCheck");SceneManager.MoveGameObjectToScene(sentinel,main);EditorSceneManager.MarkSceneDirty(main);
            ProcessAuthoringMainApply.ApplySaved();Need(main.isDirty,"main apply preserves unrelated unsaved state");
            foreach(var entry in p.processes)
            {
                var frame=ProcessAuthoringLayout.Frame(p,entry);var record=p.authoringProfile.Find(entry.authoringId);
                foreach(var w in entry.workers)
                {
                    var actor=record.FindActor(ProcessAuthoringLayout.Key(w));
                    Need(Vector3.Distance(w.transform.position,ProcessAuthoringLayout.ActorPosition(p.authoringProfile,actor,frame))<.001f,"main worker saved pose");
                }
            }
        }
        finally{EditorSceneManager.CloseScene(main,true);SceneManager.SetActiveScene(previous);}
        return "PASS: reopened selection, attachment/grip disable reset, same-ID prefab replacement/Undo, main dirty preservation, all worker poses.";
    }
}
#endif
