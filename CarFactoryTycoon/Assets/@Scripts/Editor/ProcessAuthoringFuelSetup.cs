#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;
using UnityEngine.AI;
using Unity.AI.Navigation;
using Object=UnityEngine.Object;

public static class ProcessAuthoringFuelSetup
{
    [MenuItem("Tools/CarFactory/Process Authoring/Import Fuel Station")]
    public static void Import()
    {
        if(Application.isPlaying)throw new InvalidOperationException("Edit mode required.");
        var preview=Object.FindFirstObjectByType<ProcessAuthoringPreview>();if(preview==null)throw new InvalidOperationException("Open authoring scene first.");
        if(preview.fuelStation!=null)return;
        var main=SceneManager.GetSceneByPath("Assets/Car_Factory/DemoScene/Car_Factory 3.unity");bool opened=!main.isLoaded;
        if(opened)main=EditorSceneManager.OpenScene("Assets/Car_Factory/DemoScene/Car_Factory 3.unity",OpenSceneMode.Additive);
        try
        {
            FuelStation source=null;ProductionProgression owner=null;
            foreach(var root in main.GetRootGameObjects()){if(source==null)source=root.GetComponentInChildren<FuelStation>(true);if(owner==null)owner=root.GetComponentInChildren<ProductionProgression>(true);}
            if(source==null||owner==null)throw new InvalidOperationException("Main fuel station missing.");
            var profile=preview.progression.authoringProfile;
            var frame=ProcessAuthoringLayout.FrameAt(source.mainLineSpline,source.gateProgress);
            if(!profile.fuel.configured)
            {
                profile.fuel.configured=true;profile.fuel.station=AuthoringPose.Capture(source.transform,frame);
                profile.fuel.home=AuthoringPose.Capture(source.homePoint,frame);profile.fuel.nozzleDock=AuthoringPose.Capture(source.nozzleDock,frame);
                if(source.hoseAnchor!=null)profile.fuel.hoseAnchor=AuthoringPose.Capture(source.hoseAnchor,frame);
                profile.fuel.carPort=source.carPortLocal;
                profile.fuel.workStand=new AuthoringPose{position=new Vector3(source.workerSideDistance,profile.fuel.home.position.y,source.carPortLocal.z)};
            }
            owner.authoringFuelStation=source;owner.authoringFuelAnchorProgress=source.gateProgress;
            EditorUtility.SetDirty(owner);EditorUtility.SetDirty(profile);AssetDatabase.SaveAssets();EditorSceneManager.SaveScene(main);
            var clone=Object.Instantiate(source.gameObject);clone.name="FuelProcess";
            SceneManager.MoveGameObjectToScene(clone,preview.gameObject.scene);clone.transform.SetParent(preview.transform,false);
            preview.fuelStation=clone.GetComponent<FuelStation>();preview.fuelStation.mainLineSpline=preview.progression.mainSpline;
            preview.fuelStation.autoProgressFromPosition=false;preview.fuelStation.gateProgress=.45f;
            preview.progression.authoringFuelStation=preview.fuelStation;preview.progression.authoringFuelAnchorProgress=.45f;
            clone.SetActive(false);ProcessAuthoringLayout.Apply(preview.progression,profile);
            var floor=Array.Find(preview.gameObject.scene.GetRootGameObjects(),x=>x.name=="PreviewFloor");
            if(floor!=null)
            {
                var surface=floor.GetComponent<NavMeshSurface>();if(surface==null)surface=floor.AddComponent<NavMeshSurface>();
                surface.collectObjects=CollectObjects.Children;surface.useGeometry=NavMeshCollectGeometry.PhysicsColliders;surface.layerMask=1;surface.overrideVoxelSize=true;surface.voxelSize=.08f;surface.BuildNavMesh();
                if(surface.navMeshData!=null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(surface.navMeshData)))
                    AssetDatabase.CreateAsset(surface.navMeshData,"Assets/@Resource/Data/ProcessAuthoringNavMesh.asset");
            }
            EditorUtility.SetDirty(preview);EditorUtility.SetDirty(preview.progression);EditorSceneManager.SaveScene(preview.gameObject.scene);
        }
        finally{if(opened)EditorSceneManager.CloseScene(main,true);SceneManager.SetActiveScene(preview.gameObject.scene);}
    }
}
#endif
