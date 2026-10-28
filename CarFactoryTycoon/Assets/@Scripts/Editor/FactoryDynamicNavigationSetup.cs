#if UNITY_EDITOR
using System;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object=UnityEngine.Object;

/// <summary>Explicit full-layout bake through the canonical prototype-aware navigation tool.</summary>
public static class FactoryDynamicNavigationSetup
{
    [MenuItem("Tools/CarFactory/Dynamic supply navigation (no crossings)")]
    static void ApplyMenu() => Debug.Log(Apply());
    public static string Apply()
    {
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if(Application.isPlaying||scene.name!="Car_Factory 3")throw new InvalidOperationException("Factory edit mode required.");
        var progression=Object.FindFirstObjectByType<ProductionProgression>();
        if(progression==null||!progression.prototypeMode)throw new InvalidOperationException("Prototype mode required.");
        var log=new StringBuilder();
        FactoryLayoutRevisionSetup.BakeCompleteLayout(log);
        foreach(var mover in Object.FindObjectsByType<NavMover>(FindObjectsInactive.Include,FindObjectsSortMode.None))
        {mover.useNavMesh=true;mover.requireCompletePath=true;EditorUtility.SetDirty(mover);}
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);
        log.AppendLine(FactoryPlayFeedbackSetup.Routes());
        return log.ToString();
    }
}
#endif
