#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FactoryFuelRobotSetup
{
    public static string Apply(FuelStation fuel)
    {
        if(Application.isPlaying)throw new InvalidOperationException("Edit mode required.");
        if(fuel==null||fuel.attendant==null)throw new InvalidOperationException("Fuel attendant missing.");
        var root=fuel.attendant;
        var current=root.GetComponentInChildren<Animator>(true);
        if(current==null||current.avatar==null||current.avatar.name!="KyleRobotAvatar")
        {
            var old=root.Find("CharacterVisual");if(old==null)throw new InvalidOperationException("Fuel CharacterVisual missing.");
            var archive=Array.Find(fuel.gameObject.scene.GetRootGameObjects(),x=>x.name=="RetiredFuelVisuals");
            if(archive==null){archive=new GameObject("RetiredFuelVisuals");UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(archive,fuel.gameObject.scene);archive.SetActive(false);}
            Undo.SetTransformParent(old,archive.transform,"Preserve Prior Fuel Visual");
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(FactoryWorkerRobotSetup.RobotPrefab);
            var visual=(GameObject)PrefabUtility.InstantiatePrefab(prefab,root);Undo.RegisterCreatedObjectUndo(visual,"Use Robot Fuel Attendant");visual.name="CharacterVisual";
            visual.transform.SetLocalPositionAndRotation(Vector3.zero,Quaternion.identity);visual.transform.localScale=Vector3.one;
            FactoryWorkerRobotSetup.DisableDemoControls(visual);
            var actors=visual.GetComponentsInChildren<Animator>(true);
            current=actors.FirstOrDefault(a=>a.isHuman && a.GetComponentsInChildren<Animator>(true).Length==1);
            if(current==null)throw new InvalidOperationException("Kyle skeleton Animator missing.");
            foreach(var a in actors)if(a!=current)Undo.DestroyObjectImmediate(a);
            foreach(var collider in visual.GetComponentsInChildren<Collider>(true))collider.enabled=false;
        }
        current.runtimeAnimatorController=AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/@Resource/Animations/WorkerAnimator.controller");
        current.applyRootMotion=false;current.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        var ik=current.GetComponent<WorkerHandIK>();if(ik==null)ik=Undo.AddComponent<WorkerHandIK>(current.gameObject);
        if(current.GetComponent<WorkerFootstepReceiver>()==null)Undo.AddComponent<WorkerFootstepReceiver>(current.gameObject);
        fuel.attendantAnimator=current;fuel.handIK=ik;
        foreach(var child in root.GetComponentsInChildren<Transform>(true))child.gameObject.layer=8;
        var body=root.GetComponent<BoxCollider>();if(body!=null){body.center=new Vector3(0,.968f,0);body.size=new Vector3(.6f,1.936f,.5f);}
        EditorUtility.SetDirty(current);EditorUtility.SetDirty(ik);EditorUtility.SetDirty(fuel);EditorSceneManager.MarkSceneDirty(fuel.gameObject.scene);
        return "Fuel attendant RobotKyle; one skeleton Animator; NavMover and service root preserved.";
    }
}
#endif
