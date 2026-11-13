using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

/// <summary>Single line-local layout boundary shared by the factory and the authoring scene.</summary>
public static class ProcessAuthoringLayout
{
    public static Pose Frame(ProductionProgression p, ProductionProgression.ProcessEntry entry)
    {
        return FrameAt(p.mainSpline,entry.gateProgress);
    }
    public static Pose FrameAt(SplineContainer spline,float progress)
    {
        if (spline == null) return new Pose(Vector3.zero, Quaternion.identity);
        SplineUtility.Evaluate(spline.Spline, progress, out float3 point, out float3 tangent, out float3 up);
        var forward = Vector3.ProjectOnPlane(spline.transform.TransformDirection(tangent), Vector3.up);
        if (forward.sqrMagnitude < .001f) forward = Vector3.forward;
        return new Pose(spline.transform.TransformPoint(point), Quaternion.LookRotation(forward, Vector3.up));
    }
    public static string Key(Component component)
    {
        var identity = component != null ? component.GetComponent<ProcessAuthoringIdentity>() : null;
        return identity != null ? identity.id : null;
    }
    public static ProcessLayoutRecord Record(ProductionProgression p, ProductionProgression.ProcessEntry entry)
        => p.authoringProfile != null ? p.authoringProfile.Find(entry.authoringId) : null;
    public static bool HasStock(ProductionProgression p, ProductionProgression.ProcessEntry entry, PartStack stock)
        => Record(p, entry)?.FindStock(Key(stock)) != null;
    public static Vector3 ActorPosition(ProcessAuthoringProfile profile, ProcessActorLayout actor, Pose frame)
    {
        var local = actor.root.position;
        local.x += actor.side * profile.actorDistance;
        return frame.position + frame.rotation * local;
    }
    public static AuthoringPose WithDistance(AuthoringPose pose, int side, float distance)
    { pose.position.x += side * distance; return pose; }

    public static bool ApplyVehicle(CarController car,ProcessAuthoringProfile profile,string vehicleId)
    {
        var model=profile!=null?profile.FindVehicle(vehicleId):null;if(model==null)return false;
        car.authoringVehicleId=vehicleId;
        var templates=model.prefab!=null?model.prefab.GetComponentsInChildren<AssemblyPart>(true):System.Array.Empty<AssemblyPart>();
        foreach(var part in car.GetComponentsInChildren<AssemblyPart>(true))
        {
            part.GetComponent<PartGripOverride>()?.ClearProcessOverride();
            foreach(var template in templates)if(template.myType==part.myType)
            {
                part.requiredWork=template.requiredWork;part.assembledPos=template.assembledPos;part.assembledRot=template.assembledRot;
                part.midPos=template.midPos;part.midRot=template.midRot;part.mid2Pos=template.mid2Pos;part.mid2Rot=template.mid2Rot;
                part.masterData=template.masterData;
                var originalGrip=template.GetComponent<PartGripOverride>();var currentGrip=part.GetComponent<PartGripOverride>();
                if(currentGrip!=null)
                {currentGrip.enabled=originalGrip!=null&&originalGrip.enabled;if(originalGrip!=null){currentGrip.gripA=originalGrip.gripA;currentGrip.gripB=originalGrip.gripB;currentGrip.fingerDir=originalGrip.fingerDir;currentGrip.explicitHands=originalGrip.explicitHands;}}
                break;
            }
            if(model.assemblyData!=null){part.masterData=model.assemblyData;part.LoadDataFromSO();}
            ProcessPartOverride data=null;
            foreach(var value in model.parts)if(value.type==part.myType){data=value;break;}
            if(data==null){if(!Application.isPlaying)part.SetAssembled();continue;}
            if(data.overridePose){part.assembledPos=data.attachment.position;part.assembledRot=data.attachment.euler;}
            if(data.overrideGrip)
            {
                var grip=part.GetComponent<PartGripOverride>();if(grip==null)grip=part.gameObject.AddComponent<PartGripOverride>();
                grip.enabled=true;grip.explicitHands=false;grip.gripA=data.leftGrip;grip.gripB=data.rightGrip;grip.fingerDir=data.finger;
            }
            if(!Application.isPlaying)part.SetAssembled();
        }
        return true;
    }

    public static void Apply(ProductionProgression p, ProcessAuthoringProfile profile, bool repositionWorkers = true)
    {
        if (profile == null || p.processes == null) return;
        if(p.conveyor!=null)p.conveyor.SetUniformWidth(profile.railWidth);
        if(profile.fuel!=null && profile.fuel.configured && p.authoringFuelStation!=null)
        {
            var fuel=p.authoringFuelStation;var data=profile.fuel;fuel.authoringProfile=profile;
            var frame=FrameAt(p.mainSpline,p.authoringFuelAnchorProgress);
            data.station.Apply(fuel.transform,frame);data.home.Apply(fuel.homePoint,frame);data.nozzleDock.Apply(fuel.nozzleDock,frame);data.hoseAnchor.Apply(fuel.hoseAnchor,frame);
            fuel.autoProgressFromPosition=false;
            fuel.gateProgress=Mathf.Clamp01(p.authoringFuelAnchorProgress+data.stopOffsetMeters/Mathf.Max(.01f,p.mainSpline.CalculateLength()));
            fuel.carPortLocal=data.carPort;fuel.useAuthoredWorkPoint=data.useWorkStand;fuel.authoredWorkPoint=data.workStand.WorldPosition(frame);
        }
        foreach (var entry in p.processes)
        {
            var record = profile.Find(entry.authoringId); if (record == null) continue;
            var frame = Frame(p, entry);
            if(record.hasHardware)record.hardware.Apply(entry.Platform,frame);
            if(record.hasHiringPost && entry.hiringPost!=null)
                WithDistance(record.hiringPost,record.hiringPostSide,profile.hiringPostDistance).Apply(entry.hiringPost,frame);
            if (entry.workers != null) foreach (var worker in entry.workers)
            {
                if (worker == null) continue;
                var actor = record.FindActor(Key(worker)); if (actor == null) continue;
                if(repositionWorkers)WithDistance(actor.root, actor.side, profile.actorDistance).Apply(worker.transform, frame);
                worker.SetHome(WithDistance(actor.home, actor.side, profile.actorDistance).WorldPosition(frame));
            }
            if (entry.robots != null) foreach (var robot in entry.robots)
            {
                if (robot == null) continue;
                var actor = record.FindActor(Key(robot)); if (actor == null) continue;
                WithDistance(actor.root, actor.side, profile.actorDistance).Apply(robot.transform, frame);
                WithDistance(actor.robotRest, actor.side, profile.actorDistance).Apply(robot.endPos, frame);
                WithDistance(actor.robotPickup, actor.side, profile.actorDistance).Apply(robot.stationPilePos, frame);
                if (robot.TryGetComponent<BoxCollider>(out var box))
                { box.center = actor.detectionCenter; box.size = actor.detectionSize; }
            }
            var stocks = entry.humanStation != null ? entry.humanStation.partStacks : entry.wheel != null ? entry.wheel.humanPartStacks : null;
            if (stocks != null) foreach (var stock in stocks)
            {
                if (stock == null) continue;
                var data = record.FindStock(Key(stock)); if (data == null) continue;
                WithDistance(data.inventory, data.side, profile.actorDistance).Apply(stock.transform, frame);
                stock.SetInventoryRotation(data.itemEuler);
                stock.SetWorldPacking(data.packing,frame,data.side,profile.actorDistance,data.itemEuler);
                if(data.packing!=null&&data.packing.enabled&&data.followInventoryRotation)data.pickup.euler=data.itemEuler;
                var pickup = WithDistance(data.pickup, data.side, profile.actorDistance);
                stock.ConfigureReadySlot(pickup.WorldPosition(frame), pickup.WorldRotation(frame));
                var stand = stock.transform.Find("ReadySlotStand");
                if (stand == null)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = "ReadySlotStand";
                    stand = go.transform; stand.SetParent(stock.transform, true); go.GetComponent<Collider>().enabled = false; go.layer = 2;
                }
                var foot = stock.ReadyBounds.center; foot.y = stock.ReadyBounds.min.y - .06f;
                stand.SetPositionAndRotation(foot, Quaternion.identity); stand.localScale = new Vector3(.6f,.12f,.6f);
            }
        }
    }
}
