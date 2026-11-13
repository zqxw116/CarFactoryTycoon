#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ProcessAuthoringRigSetup
{
    public static int Prepare(ProductionProgression p)
    {
        int count=0;
        foreach(var entry in p.processes)if(entry.workers!=null)foreach(var worker in entry.workers)
        {
            if(worker==null)continue;
            FactoryWorkerRobotSetup.NormalizeVisual(worker);
            if(worker.handIK==null)continue;
            var rig=worker.handIK.GetComponent<WorkerInteractionRig>();
            if(rig==null)rig=Undo.AddComponent<WorkerInteractionRig>(worker.handIK.gameObject);
            rig.Prepare();EditorUtility.SetDirty(rig);count++;
        }
        EditorSceneManager.MarkSceneDirty(p.gameObject.scene);return count;
    }
    [MenuItem("Tools/CarFactory/Process Authoring/Prepare Worker Rigs")]
    public static void Run()
    {
        if(Application.isPlaying)throw new System.InvalidOperationException("Edit mode required.");
        var p=Object.FindFirstObjectByType<ProductionProgression>(FindObjectsInactive.Include);
        Debug.Log("Prepared "+Prepare(p)+" worker rigs.");
    }
}
#endif
