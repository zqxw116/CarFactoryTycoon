using System;
using UnityEngine;

[Serializable]
public class ProcessPartOverride
{
    public PartType type;
    public bool overridePose,overrideGrip;
    public AuthoringPose attachment;
    public Vector3 leftGrip,rightGrip,finger = Vector3.forward;
}
