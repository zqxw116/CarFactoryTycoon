using UnityEngine;

/// <summary>Measured two-arm reach. Trigger shapes visualize the same explicit geometric query.</summary>
public sealed class WorkerReachEnvelope : MonoBehaviour
{
    public Vector3 leftShoulder,rightShoulder;
    public float leftLength,rightLength;
    public SphereCollider leftVolume,rightVolume;
    public float TorsoRadius=>Mathf.Clamp(Vector3.Distance(leftShoulder,rightShoulder)*.45f+.01f,.18f,.28f)*Mathf.Abs(transform.lossyScale.x);
    public bool Ready=>leftLength>.01f&&rightLength>.01f;
    public static WorkerReachEnvelope Ensure(Worker worker)
    {
        var value=worker.GetComponent<WorkerReachEnvelope>();
        if(value==null)value=worker.gameObject.AddComponent<WorkerReachEnvelope>();
        if(!value.Ready)value.Measure(worker.handIK!=null?worker.handIK.GetComponent<Animator>():worker.GetComponentInChildren<Animator>());
        return value;
    }
    public void Measure(Animator animator)
    {
        if(animator==null||!animator.isHuman)return;
        MeasureArm(animator,true,out leftShoulder,out leftLength);
        MeasureArm(animator,false,out rightShoulder,out rightLength);
        leftVolume=Volume("왼팔 도달 범위",leftVolume,leftShoulder,leftLength);
        rightVolume=Volume("오른팔 도달 범위",rightVolume,rightShoulder,rightLength);
    }
    void MeasureArm(Animator a,bool left,out Vector3 shoulder,out float length)
    {
        var upper=a.GetBoneTransform(left?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm);
        var lower=a.GetBoneTransform(left?HumanBodyBones.LeftLowerArm:HumanBodyBones.RightLowerArm);
        var hand=a.GetBoneTransform(left?HumanBodyBones.LeftHand:HumanBodyBones.RightHand);
        shoulder=transform.InverseTransformPoint(upper.position);
        length=(Vector3.Distance(upper.position,lower.position)+Vector3.Distance(lower.position,hand.position))/Mathf.Max(.0001f,Mathf.Abs(transform.lossyScale.x));
    }
    SphereCollider Volume(string name,SphereCollider existing,Vector3 center,float radius)
    {
        if(existing==null){var child=new GameObject(name);child.layer=2;child.hideFlags=gameObject.hideFlags&HideFlags.DontSave;child.transform.SetParent(transform,false);existing=child.AddComponent<SphereCollider>();}
        existing.center=center;existing.radius=radius*.98f;existing.isTrigger=true;return existing;
    }
    public bool Contains(Vector3 left,Vector3 right,float fraction=.98f)
        =>ContainsAt(transform.position,transform.rotation,left,right,fraction);
    // The preview uses the measured standing pose. Low-slot pickup uses the same arm
    // lengths, with shoulder origins read from the evaluated approach pose.
    Vector3 posedLeft, posedRight;
    bool usePosedShoulders;
    public void SamplePickupShoulders(Worker worker)
    {
        usePosedShoulders = worker.UsingWorkPosture && worker.handIK != null;
        if (!usePosedShoulders) return;
        var a = worker.handIK.GetComponent<Animator>();
        posedLeft = transform.InverseTransformPoint(a.GetBoneTransform(HumanBodyBones.LeftUpperArm).position);
        posedRight = transform.InverseTransformPoint(a.GetBoneTransform(HumanBodyBones.RightUpperArm).position);
    }
    public void ClearPickupShoulders() => usePosedShoulders = false;

    bool ContainsAt(Vector3 position,Quaternion rotation,Vector3 left,Vector3 right,float fraction)
    {
        float scale=Mathf.Abs(transform.lossyScale.x);fraction=Mathf.Clamp(fraction,.5f,.98f);
        var a=position+rotation*((usePosedShoulders?posedLeft:leftShoulder)*scale);var b=position+rotation*((usePosedShoulders?posedRight:rightShoulder)*scale);
        return Ready&&Vector3.Distance(a,left)<=leftLength*scale*fraction&&Vector3.Distance(b,right)<=rightLength*scale*fraction;
    }
    public Pose BalancedPoseFor(Vector3 left,Vector3 right,float floor,float fraction)
    {
        var axis=Vector3.ProjectOnPlane(right-left,Vector3.up);
        var shoulderAxis=Vector3.ProjectOnPlane(rightShoulder-leftShoulder,Vector3.up);
        var rotation=axis.sqrMagnitude>.00001f&&shoulderAxis.sqrMagnitude>.00001f?Quaternion.FromToRotation(shoulderAxis,axis):transform.rotation;
        float scale=Mathf.Abs(transform.lossyScale.x);
        var center=(left+right)*.5f;
        var start=center-rotation*((leftShoulder+rightShoulder)*(.5f*scale));start.y=floor;
        var forward=axis.sqrMagnitude>.00001f?Vector3.Cross(axis.normalized,Vector3.up):rotation*Vector3.forward;
        var best=start;
        for(int i=0;i<=240;i++)
        {
            var point=start-forward*(i*.005f);
            if(ContainsAt(point,rotation,left,right,Mathf.Clamp(fraction,.5f,.98f)))best=point;
        }
        return new Pose(best,rotation);
    }
    public Vector3 BalancedStandFor(Vector3 left,Vector3 right,Vector3 fallbackAway,float floor,float fraction)
    {
        var across=Vector3.ProjectOnPlane(right-left,Vector3.up);
        var away=across.sqrMagnitude>.0001f?-Vector3.Cross(across.normalized,Vector3.up):fallbackAway;
        return StandFor(left,right,away,floor,fraction);
    }
    public Vector3 StandFor(Vector3 left,Vector3 right,Vector3 away,float floor,float fraction)
    {
        var center=(left+right)*.5f;away=Vector3.ProjectOnPlane(away,Vector3.up);
        if(away.sqrMagnitude<.001f)away=-transform.forward;away.Normalize();
        float scale=Mathf.Abs(transform.lossyScale.x);
        fraction=Mathf.Clamp(fraction-.015f/Mathf.Max(.01f,Mathf.Min(leftLength,rightLength)*scale),.5f,.98f);
        var rotation=Quaternion.LookRotation(-away);var best=new Vector3(center.x,floor,center.z);
        // Farthest valid stand preserves a stretched arm rather than walking inside the part.
        for(int i=0;i<=100;i++)
        {
            var point=center+away*(i*.012f);point.y=floor;
            if(ContainsAt(point,rotation,left,right,fraction))best=point;
        }
        return best;
    }
}
