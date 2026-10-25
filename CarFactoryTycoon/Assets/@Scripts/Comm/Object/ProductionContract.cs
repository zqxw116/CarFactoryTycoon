using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>A vehicle keeps the production promise made when it entered the line.</summary>
public sealed class ProductionContract : MonoBehaviour
{
    public PartType[] requiredParts = Array.Empty<PartType>();
    public float endProgress = 1f;
    public int payout;
    public bool isCompleteVehicle;
    public int processCount;
    public bool settled;
    public ProductionDispatch dispatch;

    struct Pose
    {
        public Transform target;
        public Vector3 position, scale;
        public Quaternion rotation;
    }

    readonly List<Pose> poses = new List<Pose>();
    AssemblyPart[] frozenParts;
    bool[] enabledStates;
    bool hasContract;
    bool returned;

    public bool Requires(PartType type)
    {
        if (type == PartType.None || type == PartType.Frame_1 || requiredParts == null) return false;
        return Array.IndexOf(requiredParts, type) >= 0;
    }

    public void ResetContract(PartType[] required, float end, int reward, bool complete,
        int count, ProductionDispatch outlet)
    {
        RestorePoses();
        requiredParts = required != null ? (PartType[])required.Clone() : Array.Empty<PartType>();
        endProgress = Mathf.Clamp01(end);
        payout = Mathf.Max(0, reward);
        isCompleteVehicle = complete;
        processCount = count;
        dispatch = outlet;
        settled = false;
        hasContract = true;
        returned = false;
    }

    // A missing component is also a failure (and necessarily returns a null missing reference).
    public bool Validate(out AssemblyPart missing)
    {
        missing = null;
        var parts = GetComponentsInChildren<AssemblyPart>(true);
        if (requiredParts == null) return false;
        foreach (var type in requiredParts)
        {
            if (type == PartType.None || type == PartType.Frame_1) continue;
            bool found = false;
            foreach (var part in parts)
            {
                if (part.myType != type) continue;
                found = true;
                if (!part.IsAssembled) { missing = part; return false; }
            }
            if (!found) return false;
        }
        return true;
    }

    public void SetCompression(float fraction)
    {
        if (frozenParts == null)
        {
            frozenParts = GetComponentsInChildren<AssemblyPart>(true);
            enabledStates = new bool[frozenParts.Length];
            for (int i = 0; i < frozenParts.Length; i++)
            {
                enabledStates[i] = frozenParts[i].enabled;
                frozenParts[i].enabled = false; // Do not let a late assembly pose undo the press.
            }
            var car = GetComponent<CarController>();
            Transform dolly = car != null && car.Dolly != null ? car.Dolly.transform : null;
            CollectRoots(transform, dolly);
        }
        float squash = Mathf.Lerp(1f, 0.17f, Mathf.Clamp01(fraction));
        foreach (var pose in poses)
        {
            if (pose.target == null) continue;
            // Compress each independent visual subtree around the dolly deck, never the car root.
            var carLocal = transform.InverseTransformPoint(pose.target.parent.TransformPoint(pose.position));
            carLocal.y = 0.35f + (carLocal.y - 0.35f) * squash;
            pose.target.position = transform.TransformPoint(carLocal);
            pose.target.localScale = Vector3.Scale(pose.scale, new Vector3(1f, squash, 1f));
        }
    }

    void CollectRoots(Transform parent, Transform dolly)
    {
        foreach (Transform child in parent)
        {
            if (child == dolly || child.GetComponent<CarDolly>() != null) continue;
            if (dolly != null && dolly.IsChildOf(child)) { CollectRoots(child, dolly); continue; }
            if (child.GetComponentInChildren<Renderer>(true) == null) continue;
            poses.Add(new Pose { target = child, position = child.localPosition,
                rotation = child.localRotation, scale = child.localScale });
        }
    }

    public void RestorePoses()
    {
        foreach (var pose in poses)
        {
            if (pose.target == null) continue;
            pose.target.localPosition = pose.position;
            pose.target.localRotation = pose.rotation;
            pose.target.localScale = pose.scale;
        }
        poses.Clear();
        if (frozenParts != null)
            for (int i = 0; i < frozenParts.Length; i++)
                if (frozenParts[i] != null) frozenParts[i].enabled = enabledStates[i];
        frozenParts = null;
        enabledStates = null;
    }

    void OnDisable()
    {
        RestorePoses();
        if (!hasContract || returned) return;
        returned = true;
        if (ProductionProgression.Current != null)
            ProductionProgression.Current.ContractReturned(this);
    }
}
