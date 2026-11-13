#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ProcessAuthoringCapture
{
    public const string AssetPath = "Assets/@Resource/Data/ProcessAuthoringProfile.asset";
    public static string EnsureIdentity(Component component)
    {
        var identity = component.GetComponent<ProcessAuthoringIdentity>();
        if (identity == null) identity = Undo.AddComponent<ProcessAuthoringIdentity>(component.gameObject);
        if (string.IsNullOrEmpty(identity.id)) { Undo.RecordObject(identity,"Assign Authoring ID"); identity.id=Guid.NewGuid().ToString("N"); EditorUtility.SetDirty(identity); }
        return identity.id;
    }
    static AuthoringPose CapturePose(Transform target, Pose frame, int side, float distance)
    {
        var pose = AuthoringPose.Capture(target, frame); pose.position.x -= side*distance; return pose;
    }
    [MenuItem("Tools/CarFactory/Process Authoring/Import Main Layout")]
    public static void ImportMain()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Edit mode required.");
        var p=UnityEngine.Object.FindFirstObjectByType<ProductionProgression>(FindObjectsInactive.Include);
        if(p==null || !p.gameObject.scene.path.EndsWith("Car_Factory 3.unity")) throw new InvalidOperationException("Open main factory scene first.");
        if(p.authoringProfile!=null && p.authoringProfile.processes.Count>0)
            throw new InvalidOperationException("Layout already imported. Use editor handles to edit; do not overwrite authored data.");
        p.ConfigurePickupSlots();
        var profile=AssetDatabase.LoadAssetAtPath<ProcessAuthoringProfile>(AssetPath);
        if(profile==null)
        {
            if(!AssetDatabase.IsValidFolder("Assets/@Resource/Data"))AssetDatabase.CreateFolder("Assets/@Resource","Data");
            profile=ScriptableObject.CreateInstance<ProcessAuthoringProfile>();AssetDatabase.CreateAsset(profile,AssetPath);
        }
        if(profile.processes.Count>0)throw new InvalidOperationException("Existing profile must be assigned, not reimported.");
        if(p.conveyor!=null)
        {
            var serialized=new SerializedObject(p.conveyor);
            var width=serialized.FindProperty("m_Width");if(width!=null&&width.floatValue>0)profile.railWidth=width.floatValue;
        }
        foreach(var entry in p.processes)
        {
            if(string.IsNullOrEmpty(entry.authoringId))entry.authoringId=Guid.NewGuid().ToString("N");
            var frame=ProcessAuthoringLayout.Frame(p,entry);
            var record=new ProcessLayoutRecord { id=entry.authoringId,displayName=entry.displayName };
            if(entry.Platform!=null){record.hasHardware=true;record.hardware=AuthoringPose.Capture(entry.Platform,frame);}
            if(entry.workers!=null)foreach(var worker in entry.workers)
            {
                if(worker==null)continue;
                int side=Vector3.Dot(worker.transform.position-frame.position,frame.rotation*Vector3.right)<0?-1:1;
                var pose=CapturePose(worker.transform,frame,side,profile.actorDistance);
                var wait=pose;wait.position.x=side*1.8f-side*profile.actorDistance;wait.position.z=record.stopOffsetMeters;
                record.actors.Add(new ProcessActorLayout { id=EnsureIdentity(worker),kind=ProcessActorKind.Worker,side=side,root=pose,home=pose,assemblyWait=wait });
            }
            if(entry.robots!=null)foreach(var robot in entry.robots)
            {
                if(robot==null)continue;
                int side=Vector3.Dot(robot.transform.position-frame.position,frame.rotation*Vector3.right)<0?-1:1;
                var actor=new ProcessActorLayout { id=EnsureIdentity(robot),kind=ProcessActorKind.Robot,part=robot.targetPartType,side=side,
                    root=CapturePose(robot.transform,frame,side,profile.actorDistance) };
                actor.home=actor.root;
                actor.robotRest=CapturePose(robot.endPos!=null?robot.endPos:robot.transform,frame,side,profile.actorDistance);
                actor.robotPickup=CapturePose(robot.stationPilePos!=null?robot.stationPilePos:robot.transform,frame,side,profile.actorDistance);
                if(robot.TryGetComponent<BoxCollider>(out var box)){actor.detectionCenter=box.center;actor.detectionSize=box.size;}
                record.actors.Add(actor);
            }
            var stocks=entry.humanStation!=null?entry.humanStation.partStacks:entry.wheel!=null?entry.wheel.humanPartStacks:null;
            if(stocks!=null)foreach(var stock in stocks)
            {
                if(stock==null)continue;
                int side=Vector3.Dot(stock.ReadyPosition-frame.position,frame.rotation*Vector3.right)<0?-1:1;
                var pickup=new AuthoringPose { position=Quaternion.Inverse(frame.rotation)*(stock.ReadyPosition-frame.position)-Vector3.right*(side*profile.actorDistance),
                    euler=(Quaternion.Inverse(frame.rotation)*stock.ReadyRotation).eulerAngles };
                record.stocks.Add(new ProcessStockLayout { id=EnsureIdentity(stock),part=stock.partType,side=side,inventory=CapturePose(stock.transform,frame,side,profile.actorDistance),pickup=pickup,itemEuler=stock.itemRotation });
            }
            int index=Array.IndexOf(p.processes,entry);
            var post=p.transform.Find("FactoryHiringPosts/Post_"+index.ToString("D2"));
            if(post!=null){int side=Vector3.Dot(post.position-frame.position,frame.rotation*Vector3.right)<0?-1:1;entry.hiringPost=post;record.hasHiringPost=true;record.hiringPostSide=side;record.hiringPost=CapturePose(post,frame,side,profile.hiringPostDistance);}
            profile.processes.Add(record);
        }
        profile.vehicles.Add(new ProcessVehicleProfile { id="Origin",displayName="기존 차량",prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Prefabs/CarModel_Origin.prefab") });
        Undo.RecordObject(p,"Bind Process Authoring Profile");p.authoringProfile=profile;
        EditorUtility.SetDirty(p);EditorUtility.SetDirty(profile);AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(p.gameObject.scene);
        Debug.Log("Imported "+profile.processes.Count+" processes; identities, stock slots and actor poses preserved.");
    }
}
#endif
