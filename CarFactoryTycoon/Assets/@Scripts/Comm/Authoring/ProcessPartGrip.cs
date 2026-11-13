using System;
using UnityEngine;

[Serializable]
public sealed class ProcessGripSettings
{
    public bool configured;
    public bool anatomicalWrist;
    public Vector3 left = new Vector3(-.25f,0,0), right = new Vector3(.25f,0,0), finger = Vector3.forward;
    public Vector3 robot,workerCarryEuler;
    public Vector3 leftWristEuler,rightWristEuler;
    public Vector3 leftElbow = new Vector3(-.3f,-.3f,-.05f), rightElbow = new Vector3(.3f,-.3f,-.05f);
    [Range(.5f,.98f)] public float reachFraction = .9f;
}

/// <summary>Part-local contact data shared by ready-slot editing and actual crew transport.</summary>
public static class ProcessPartGrip
{
    public static ProcessGripSettings Find(ProductionProgression p, PartType type)
    {
        if(p?.authoringProfile==null)return null;
        foreach(var process in p.authoringProfile.processes)foreach(var stock in process.stocks)
            if(stock.part==type&&stock.grip!=null&&stock.grip.configured)return stock.grip;
        return null;
    }
    public static Vector3 DefaultCarryEuler(Vector3 left,Vector3 right)
    {
        var across=Vector3.ProjectOnPlane(right-left,Vector3.up);
        return across.sqrMagnitude>.00001f?Quaternion.FromToRotation(across.normalized,Vector3.right).eulerAngles:Vector3.zero;
    }
    public static Vector3 Contact(Vector3 root,Quaternion rotation,Vector3 scale,Vector3 local)
        =>root+rotation*Vector3.Scale(scale,local);
    public static Vector3 RootAtContact(Vector3 contact,Quaternion rotation,Vector3 scale,Vector3 local)
        =>contact-rotation*Vector3.Scale(scale,local);
    public static void Apply(AssemblyPart part,ProcessGripSettings data,Vector3? sourceScale=null)
    {
        if(part==null)return;
        var grip=part.GetComponent<PartGripOverride>();
        if(data==null){if(grip!=null)grip.ClearProcessOverride();return;}
        if(grip==null){grip=part.gameObject.AddComponent<PartGripOverride>();grip.enabled=false;}
        grip.BeginProcessOverride();
        grip.enabled=true;grip.authoredByProcess=true;grip.explicitHands=true;
        var ratio=ScaleRatio(sourceScale??part.transform.lossyScale,part.transform.lossyScale);
        grip.processPose=data;grip.processCarryEuler=data.workerCarryEuler;grip.gripA=Vector3.Scale(data.left,ratio);grip.gripB=Vector3.Scale(data.right,ratio);grip.fingerDir=Vector3.Scale(data.finger,ratio);
    }
    public static Vector3 ScaleRatio(Vector3 source,Vector3 target)=>new Vector3(Mathf.Abs(target.x)>.00001f?source.x/target.x:1,Mathf.Abs(target.y)>.00001f?source.y/target.y:1,Mathf.Abs(target.z)>.00001f?source.z/target.z:1);
    public static Vector3 SourceScale(ProductionProgression p,PartType type)
    {
        foreach(var entry in p.processes)
        {
            var stocks=entry.humanStation!=null?entry.humanStation.partStacks:entry.wheel!=null?entry.wheel.humanPartStacks:null;
            if(stocks!=null)foreach(var stock in stocks)if(stock!=null&&stock.partType==type&&stock.ReadyDisplay!=null)return stock.ReadyDisplay.transform.lossyScale;
        }
        return Vector3.one;
    }
    public static void ApplyToCar(ProductionProgression p,CarController car)
    {if(car!=null)foreach(var part in car.GetComponentsInChildren<AssemblyPart>(true))Apply(part,Find(p,part.myType),SourceScale(p,part.myType));}
}
