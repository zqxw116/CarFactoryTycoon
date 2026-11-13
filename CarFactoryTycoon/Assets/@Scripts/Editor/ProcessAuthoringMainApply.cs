#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ProcessAuthoringMainApply
{
    public static string ApplySaved()
    {
        if(Application.isPlaying)throw new InvalidOperationException("Apply saved layout in edit mode.");
        var previous=SceneManager.GetActiveScene();
        var main=SceneManager.GetSceneByPath("Assets/Car_Factory/DemoScene/Car_Factory 3.unity");bool opened=!main.isLoaded;
        if(opened)main=EditorSceneManager.OpenScene("Assets/Car_Factory/DemoScene/Car_Factory 3.unity",OpenSceneMode.Additive);
        bool alreadyDirty=main.isDirty;
        try
        {
            ProductionProgression p=null;
            foreach(var root in main.GetRootGameObjects()){p=root.GetComponentInChildren<ProductionProgression>(true);if(p!=null)break;}
            if(p==null)throw new InvalidOperationException("Main progression missing.");
            p.authoringProfile=AssetDatabase.LoadAssetAtPath<ProcessAuthoringProfile>(ProcessAuthoringCapture.AssetPath);
            WheelProcessMigration.ExpandScene(p,null);
            var layout = WheelProcessMigration.ForRuntime(p.authoringProfile);
            try { ProcessAuthoringLayout.Apply(p,layout);ProcessAuthoringRigSetup.Prepare(p); }
            finally { if (layout != p.authoringProfile) UnityEngine.Object.DestroyImmediate(layout); }
            EditorUtility.SetDirty(p);EditorSceneManager.MarkSceneDirty(main);
            if(opened || !alreadyDirty)EditorSceneManager.SaveScene(main);
            return alreadyDirty&&!opened?"메인에 적용했습니다. 기존 미저장 변경과 함께 직접 저장하세요.":"메인 씬에 저장된 공정 설정을 적용했습니다.";
        }
        finally{if(opened)EditorSceneManager.CloseScene(main,true);if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);}
    }
}
#endif
