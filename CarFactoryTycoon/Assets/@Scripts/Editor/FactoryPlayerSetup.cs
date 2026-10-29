#if UNITY_EDITOR
using System;
using System.Linq;
using Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object=UnityEngine.Object;

public static class FactoryPlayerSetup
{
    public static string Apply()
    {
        if(Application.isPlaying)throw new InvalidOperationException("Edit mode required.");
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if(scene.path!="Assets/Car_Factory/DemoScene/Car_Factory 3.unity")throw new InvalidOperationException("Open factory main scene.");
        var line=Object.FindFirstObjectByType<PrototypeLineController>();
        if(line==null)throw new InvalidOperationException("M1 setup required.");
        ConfigurePhysics();
        var player=Object.FindFirstObjectByType<FactoryPlayerController>();
        if(player==null)
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/@Resource/Prefabs/Characters/Worker_Base.prefab");
            var go=(GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(go,"M2 factory player");
            PrefabUtility.UnpackPrefabInstance(go,PrefabUnpackMode.Completely,InteractionMode.AutomatedAction);
            go.name="FactoryPlayer";
            foreach(var script in go.GetComponents<MonoBehaviour>())
                if(script.GetType().Namespace=="StarterAssets" || script.GetType().Name=="BasicRigidBodyPush")Object.DestroyImmediate(script);
            var input=go.GetComponent<UnityEngine.InputSystem.PlayerInput>();if(input!=null)Object.DestroyImmediate(input);
            player=go.AddComponent<FactoryPlayerController>();
            go.AddComponent<PlayerAssemblyInteractor>();
            var first=Object.FindFirstObjectByType<ProductionProgression>().processes[0];
            var pos=first.workers!=null && first.workers.Length>0 && first.workers[0]!=null ? first.workers[0].transform.position : first.humanRoot.transform.position+new Vector3(0,0,-2.8f);
            // Stand in the first process aisle; project to the authored floor.
            RaycastHit hit;
            if(Physics.Raycast(pos+Vector3.up*6,Vector3.down,out hit,12f,1<<0,QueryTriggerInteraction.Ignore))pos.y=hit.point.y+.04f;
            else pos.y=-.36f;
            go.transform.position=pos;
            var direction=first.humanRoot.transform.position-pos;direction.y=0;go.transform.rotation=Quaternion.LookRotation(direction.normalized);
        }
        foreach(var t in player.GetComponentsInChildren<Transform>(true))t.gameObject.layer=10;
        var cc=player.GetComponent<CharacterController>();
        cc.height=player.transform.Find("CharacterVisual") != null ? 1.75f : 1.8f;cc.radius=.28f;cc.center=Vector3.up*(cc.height*.5f);cc.stepOffset=.3f;cc.skinWidth=.03f;
        var animator=player.GetComponentInChildren<Animator>();
        animator.runtimeAnimatorController=AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(player.transform.Find("CharacterVisual") != null ? "Assets/@Resource/Animations/FactoryPlayer.controller" : "Assets/@Resource/Animations/WorkerAnimator.controller");
        animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        player.settings=line.settings;player.bodyAnimator=animator;
        player.interactor=player.GetComponent<PlayerAssemblyInteractor>();
        player.viewCamera=Camera.main;player.mapCamera=Object.FindFirstObjectByType<CameraController>();
        player.brain=Camera.main.GetComponent<CinemachineBrain>();if(player.brain==null)player.brain=Camera.main.gameObject.AddComponent<CinemachineBrain>();
        player.firstPersonCamera=Vcam(player.transform,"PlayerFirstPersonCamera",72f);
        player.thirdPersonCamera=Vcam(player.transform,"PlayerThirdPersonCamera",62f);
        if(player.holdAnchor==null)
        {
            var hold=new GameObject("PlayerHoldAnchor");hold.transform.SetParent(player.transform,false);
            hold.transform.localPosition=new Vector3(0,1.1f,.65f);player.holdAnchor=hold.transform;
        }
        player.interactor.cameraTransform=Camera.main.transform;player.interactor.viewCamera=Camera.main;
        player.interactor.holdAnchor=player.holdAnchor;player.interactor.bodyAnimator=animator;player.interactor.settings=line.settings;
        if(animator.isHuman)player.interactor.hammerAnchor=animator.GetBoneTransform(HumanBodyBones.RightHand);
        var belt=Object.FindFirstObjectByType<SplineConveyorDeformer>();belt.enableWalkableSurface=true;belt.Rebuild();
        line.player=player;
        foreach(var water in Object.FindObjectsByType<WaterTestStation>(FindObjectsInactive.Include,FindObjectsSortMode.None))
        {water.passBonus=0;EditorUtility.SetDirty(water);} // Prototype income is paid once at export.
        foreach(var item in new Object[]{player,player.interactor,animator,cc,line,belt,player.brain})EditorUtility.SetDirty(item);
        EditorSceneManager.MarkSceneDirty(scene);AssetDatabase.SaveAssets();EditorSceneManager.SaveScene(scene);
        return "Factory player ready: Worker_Base, CharacterController, two Cinemachine views, map, physical belt, Parts/Player/CarBody matrix.";
    }
    static CinemachineVirtualCamera Vcam(Transform parent,string name,float fov)
    {
        var child=parent.Find(name);var go=child!=null?child.gameObject:new GameObject(name);
        go.transform.SetParent(parent,false);
        var camera=go.GetComponent<CinemachineVirtualCamera>();if(camera==null)camera=go.AddComponent<CinemachineVirtualCamera>();
        camera.m_Lens.FieldOfView=fov;camera.m_Lens.NearClipPlane=.03f;camera.m_Lens.FarClipPlane=300;
        return camera;
    }
    public static void ConfigurePhysics()
    {
        var tags=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers=tags.FindProperty("layers");layers.GetArrayElementAtIndex(10).stringValue="Player";layers.GetArrayElementAtIndex(11).stringValue="CarBody";tags.ApplyModifiedPropertiesWithoutUndo();
        for(int i=0;i<32;i++)
        {
            Physics.IgnoreLayerCollision(9,i,!(i==0||i==9||i==10||i==11));
            Physics.IgnoreLayerCollision(10,i,!(i==0||i==9||i==11));
            Physics.IgnoreLayerCollision(11,i,!(i==9||i==10));
        }
        // Existing trigger and scenery pairs are preserved.
        Physics.IgnoreLayerCollision(6,7,false);Physics.IgnoreLayerCollision(6,8,false);
        Physics.IgnoreLayerCollision(0,0,false);Physics.IgnoreLayerCollision(0,8,false);
    }
}
#endif
