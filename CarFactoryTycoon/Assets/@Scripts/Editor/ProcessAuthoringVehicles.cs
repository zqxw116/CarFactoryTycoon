#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ProcessAuthoringVehicles
{
    public static void Sync(ProcessAuthoringPreview preview,ProcessAuthoringProfile profile)
    {
        if(Application.isPlaying)throw new InvalidOperationException("Prepare vehicles in edit mode.");
        Undo.RegisterCompleteObjectUndo(preview,"Prepare Authoring Vehicles");
        var previous=preview.vehicleInstances;var retained=new HashSet<CarController>();
        var instances=new CarController[profile.vehicles.Count];
        for(int i=0;i<instances.Length;i++)
        {
            var model=profile.vehicles[i];
            if(model.prefab==null)continue;
            if(preview.vehicleInstances!=null)foreach(var car in preview.vehicleInstances)
                if(car!=null && car.authoringVehicleId==model.id && PrefabUtility.GetCorrespondingObjectFromSource(car.gameObject)==model.prefab){instances[i]=car;break;}
            if(instances[i]==null)
            {
                var go=(GameObject)PrefabUtility.InstantiatePrefab(model.prefab,preview.transform.Find("Vehicles"));
                Undo.RegisterCreatedObjectUndo(go,"Prepare Authoring Vehicle");
                instances[i]=go.GetComponent<CarController>();if(instances[i]==null)instances[i]=Undo.AddComponent<CarController>(go);
            }
            var instance=instances[i];retained.Add(instance);instance.authoringVehicleId=model.id;instance.isMoving=false;
            instance.transform.position=new Vector3(-profile.previewLength*.5f,0,0);instance.transform.rotation=Quaternion.LookRotation(Vector3.right);
            ProcessAuthoringLayout.ApplyVehicle(instance,profile,model.id);
            instance.gameObject.SetActive(i==preview.selectedVehicle);EditorUtility.SetDirty(instance);
        }
        if(previous!=null)foreach(var old in previous)if(old!=null&&!retained.Contains(old))Undo.DestroyObjectImmediate(old.gameObject);
        preview.vehicleInstances=instances;EditorUtility.SetDirty(preview);EditorSceneManager.MarkSceneDirty(preview.gameObject.scene);
    }
}
#endif
