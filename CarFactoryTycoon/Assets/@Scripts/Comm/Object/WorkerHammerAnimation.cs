using UnityEngine;

/// <summary>Real clip timing and bounded aim for hand-held hammers.</summary>
public static class WorkerHammerAnimation
{
    public const float Duration = .5f;
    public const float HeadDistance = .26f;
    /// <summary>Place the shaft across the palm, toward the index side, rather than along the fingers.</summary>
    public static float CalibrateGrip(Animator actor, Transform tool)
    {
        if(actor==null || !actor.isHuman){tool.localPosition=Vector3.zero;tool.localRotation=Quaternion.identity;return .03f;}
        var hand=actor.GetBoneTransform(HumanBodyBones.RightHand);
        var index=actor.GetBoneTransform(HumanBodyBones.RightIndexProximal);
        var little=actor.GetBoneTransform(HumanBodyBones.RightLittleProximal);
        var middle=actor.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
        var thumb=actor.GetBoneTransform(HumanBodyBones.RightThumbProximal);
        if(hand==null || index==null || little==null || middle==null)
        {
            Debug.LogWarning("Hammer palm calibration requires mapped right finger bases.",actor);
            tool.localPosition=Vector3.zero;tool.localRotation=Quaternion.identity;return .03f;
        }
        Vector3 i=hand.InverseTransformPoint(index.position),l=hand.InverseTransformPoint(little.position),m=hand.InverseTransformPoint(middle.position);
        Vector3 shaft=(i-l).normalized;
        Vector3 fingers=Vector3.ProjectOnPlane((i+l+m)/3f,shaft).normalized;
        Vector3 normal=Vector3.Cross(fingers,shaft).normalized;
        Vector3 center=(i+l+m)/3f*.6f;
        float width=Vector3.Distance(index.position,little.position);
        float palmSide=thumb!=null?Mathf.Sign(Vector3.Dot(hand.InverseTransformPoint(thumb.position)-center,normal)):1f;
        // A small width-derived offset seats the shaft on the palm side of the finger bases.
        tool.localPosition=center+normal*(palmSide*width*.15f);
        tool.localRotation=Quaternion.LookRotation(normal,shaft);
        return Mathf.Clamp(width*.35f,.024f,.04f);
    }
    public static bool Play(Animator actor)
    {
        if (actor == null || !actor.isActiveAndEnabled || actor.runtimeAnimatorController == null) return false;
        int layer = actor.GetLayerIndex("Hammer");
        if (layer < 0) return false;
        actor.SetLayerWeight(layer, 1f);
        actor.Play("Hammer.Strike", layer, 0f);
        return true;
    }
    public static void Stop(Animator actor)
    {
        if(actor==null || actor.runtimeAnimatorController==null)return;
        int layer=actor.GetLayerIndex("Hammer");
        if(layer>=0)actor.SetLayerWeight(layer,0f);
    }
    public static bool IsFinalIKPass(Animator actor,int layerIndex)
    {
        int hammer=actor.GetLayerIndex("Hammer");
        int active=hammer>=0 && actor.GetLayerWeight(hammer)>.001f ? hammer : 0;
        return layerIndex==active;
    }
    public static Vector3 Aim(Animator actor, HumanBodyBones upperBone, HumanBodyBones lowerBone,
        HumanBodyBones handBone, Vector3 desired, float maximumOffset = .10f)
    {
        var upper = actor.GetBoneTransform(upperBone); var lower = actor.GetBoneTransform(lowerBone);
        var hand = actor.GetBoneTransform(handBone);
        if (upper == null || lower == null || hand == null) return desired;
        float length = Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, hand.position);
        Vector3 target = hand.position + Vector3.ClampMagnitude(desired - hand.position, maximumOffset);
        return upper.position + Vector3.ClampMagnitude(target - upper.position, length * .94f);
    }
}
