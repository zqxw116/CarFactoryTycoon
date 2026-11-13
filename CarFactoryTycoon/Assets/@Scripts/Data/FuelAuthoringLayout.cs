using System;
using UnityEngine;

[Serializable]
public class FuelAuthoringLayout
{
    public bool configured;
    public AuthoringPose station,home,nozzleDock,hoseAnchor,workStand;
    public Vector3 carPort = new Vector3(1.13f,.95f,-1.1f);
    public float stopOffsetMeters;
    public bool useWorkStand;
}
