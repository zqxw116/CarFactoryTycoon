using System;
using System.Collections.Generic;
using UnityEngine;

public enum ProcessActorKind { Worker, Robot }

[Serializable]
public struct AuthoringPose
{
    public Vector3 position, euler;
    public static AuthoringPose Capture(Transform target, Pose frame) => new AuthoringPose {
        position = Quaternion.Inverse(frame.rotation) * (target.position - frame.position),
        euler = (Quaternion.Inverse(frame.rotation) * target.rotation).eulerAngles };
    public Vector3 WorldPosition(Pose frame) => frame.position + frame.rotation * position;
    public Quaternion WorldRotation(Pose frame) => frame.rotation * Quaternion.Euler(euler);
    public void Apply(Transform target, Pose frame)
    { if (target != null) target.SetPositionAndRotation(WorldPosition(frame), WorldRotation(frame)); }
}

[Serializable]
public class AuthoringCurve
{
    public bool enabled = true;
    public Vector3 control1, control2;
    public Vector3 Evaluate(Vector3 from, Vector3 to, float t)
    {
        t = Mathf.Clamp01(t); float u = 1f - t;
        return u*u*u*from + 3f*u*u*t*(from+control1) + 3f*u*t*t*(to+control2) + t*t*t*to;
    }
}

[Serializable]
public class ProcessActorLayout
{
    public string id;
    public ProcessActorKind kind;
    public PartType part;
    public int side = 1;
    public AuthoringPose root, home, assemblyWait, workPose, robotRest, robotPickup;
    public bool hasWorkPose, useAnimationRigging, bodySupport;
    public bool hasRigPreviewPose;
    public AuthoringPose rigPreviewPose;
    public Vector3 detectionCenter, detectionSize = Vector3.one;
    public AuthoringCurve fetchPath = new AuthoringCurve(), approachPath = new AuthoringCurve();
    public AuthoringCurve walkPath = new AuthoringCurve(), carryPath = new AuthoringCurve();
}

[Serializable]
public class ProcessStockLayout
{
    public string id;
    public PartType part;
    public int side = 1;
    public AuthoringPose inventory, pickup;
    public Vector3 itemEuler;
    public bool followInventoryRotation=true;
    public ProcessStockPacking packing=new ProcessStockPacking();
    public ProcessGripSettings grip = new ProcessGripSettings();
}

[Serializable]
public class ProcessLayoutRecord
{
    public string id, displayName;
    public float stopOffsetMeters = 2f;
    public bool hasHardware;
    public AuthoringPose hardware;
    public bool hasHiringPost;
    public int hiringPostSide = 1;
    public AuthoringPose hiringPost;
    public List<ProcessActorLayout> actors = new List<ProcessActorLayout>();
    public List<ProcessStockLayout> stocks = new List<ProcessStockLayout>();
    public ProcessActorLayout FindActor(string key) => actors.Find(a => a.id == key);
    public ProcessStockLayout FindStock(string key) => stocks.Find(s => s.id == key);
}

[Serializable]
public class ProcessVehicleProfile
{
    public string id = "Origin", displayName = "Origin";
    public GameObject prefab;
    public AssemblyPartDataSO assemblyData;
    public bool overrideFuelPort;
    public Vector3 fuelPort;
    public List<ProcessPartOverride> parts = new List<ProcessPartOverride>();
    public List<ProcessVehicleOverride> overrides = new List<ProcessVehicleOverride>();
}

[Serializable]
public class ProcessVehicleOverride
{
    public string processId, actorId;
    public PartType part;
    public bool overrideWait, overrideStop, overrideWork, overridePaths;
    public AuthoringPose workPose;
    public AuthoringCurve fetchPath = new AuthoringCurve(), approachPath = new AuthoringCurve();
    public AuthoringPose assemblyWait;
    public float stopOffsetMeters;
    public AuthoringCurve walkPath = new AuthoringCurve(), carryPath = new AuthoringCurve();
}

[CreateAssetMenu(menuName = "CarFactory/Process Authoring Profile")]
public sealed class ProcessAuthoringProfile : ScriptableObject
{
    public FuelAuthoringLayout fuel = new FuelAuthoringLayout();
    public string defaultVehicleId = "Origin";
    public int version = 1;
    [Min(0)] public float actorDistance = 2.7f, hiringPostDistance = 3.6f;
    [Min(.1f)] public float railWidth = 2.4f;
    [Min(8f)] public float previewLength = 24f;
    public List<ProcessLayoutRecord> processes = new List<ProcessLayoutRecord>();
    public List<ProcessVehicleProfile> vehicles = new List<ProcessVehicleProfile>();
    public ProcessLayoutRecord Find(string id) => processes.Find(p => !string.IsNullOrEmpty(id) && p.id == id);
    public ProcessVehicleProfile FindVehicle(string id) => vehicles.Find(v => v.id == id);
    public static float StepSpeed(float speed, int direction)
    {
        if (direction == 0) return Mathf.Clamp(speed, .1f, 20f);
        float next = direction > 0 ? (speed < .999f ? Mathf.Round(speed*10f+1f)/10f : Mathf.Floor(speed)+1f)
            : (speed <= 1.001f ? Mathf.Round(speed*10f-1f)/10f : Mathf.Ceil(speed)-1f);
        return Mathf.Clamp(next, .1f, 20f);
    }
}
