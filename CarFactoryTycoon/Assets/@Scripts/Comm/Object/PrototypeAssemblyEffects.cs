using UnityEngine;

/// <summary>Three distinct impacts share feedback while the existing humanoid/robot rig supplies the motion.</summary>
public sealed class PrototypeAssemblyEffects : MonoBehaviour
{
    public AssemblyPart WorkingPart;
    public int PulseCount { get; private set; }
    bool robot, configured;
    Animator animator;
    Transform tool;
    Quaternion rest;
    BoxCollider hammerHead;

    bool hammerRunning;



    public bool HammerRunning => hammerRunning;
    public void BeginHammerSwing()
    {
        if (robot || hammerRunning || WorkingPart == null || hammerHead == null || !WorkerHammerAnimation.Play(animator)) return;
        hammerRunning = true;
    }
    public void EndHammerSwing() { hammerRunning=false; WorkerHammerAnimation.Stop(animator); }
    float pulse;
    Vector3 lastPoint;
    ParticleSystem particles;
    Material material, particlesMaterial;
    public void Configure(bool robotMode)
    {
        if(configured)return;configured=true;robot=robotMode;
        animator=GetComponentInChildren<Animator>();
        if(robot||animator==null||!animator.isHuman)return;
        var ik=animator.GetComponent<PrototypeCrewHandIK>();if(ik==null)ik=animator.gameObject.AddComponent<PrototypeCrewHandIK>();ik.effects=this;
        var hand=animator.GetBoneTransform(HumanBodyBones.RightHand);
        if(hand==null)return;
        tool=new GameObject("CrewHammer").transform;tool.SetParent(hand,false);
        float shaftWidth=WorkerHammerAnimation.CalibrateGrip(animator,tool);rest=tool.localRotation;
        var shader=Shader.Find("Universal Render Pipeline/Lit");
        if(shader!=null){material=new Material(shader);material.color=new Color(.3f,.32f,.34f);}
        Piece("Handle",new Vector3(0,.10f,0),new Vector3(shaftWidth,.3f,shaftWidth));
        Piece("Head",new Vector3(0,WorkerHammerAnimation.HeadDistance,0),new Vector3(.21f,.1f,.09f));
    }
    void Piece(string name,Vector3 p,Vector3 size)
    {
        var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(tool,false);
        go.transform.localPosition=p;go.transform.localScale=size;
        var collider=go.GetComponent<Collider>();collider.enabled=false;
        if(name=="Head")hammerHead=collider as BoxCollider;else Destroy(collider);
        if(material!=null)go.GetComponent<Renderer>().sharedMaterial=material;
    }
    public void Pulse(Vector3 point,bool complete)
    {
        PulseCount++;pulse=1f;lastPoint=point;
        PrototypeWorkAudio.PlayWorkPulse(point,complete,false,robot);
        if(particles==null)
        {
            particles=new GameObject("CrewImpact").AddComponent<ParticleSystem>();particles.transform.SetParent(transform,false);
            particles.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
            var main=particles.main;main.loop=false;main.playOnAwake=false;main.startLifetime=new ParticleSystem.MinMaxCurve(.24f,.48f);main.startSpeed=new ParticleSystem.MinMaxCurve(2.8f,5.2f);main.startSize=new ParticleSystem.MinMaxCurve(.045f,.085f);main.gravityModifier=.65f;main.maxParticles=96;main.simulationSpace=ParticleSystemSimulationSpace.World;
            var emission=particles.emission;emission.enabled=false;var shape=particles.shape;shape.shapeType=ParticleSystemShapeType.Sphere;shape.radius=.015f;
            var shader=Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if(shader!=null){particlesMaterial=new Material(shader);particles.GetComponent<ParticleSystemRenderer>().sharedMaterial=particlesMaterial;}
        }
        particles.transform.position=point;var settings=particles.main;settings.startColor=complete?new Color(.5f,1f,1f):new Color(1f,.7f,.15f);
        particles.Emit(complete?36:22);
    }
    void LateUpdate()
    {
        pulse=Mathf.MoveTowards(pulse,0,Time.deltaTime*5);
        if(tool!=null)tool.localRotation=rest;
        if(hammerRunning && WorkingPart==null)EndHammerSwing();
    }
    public void ApplyHandIK(Animator actor)
    {
        if(robot || (WorkingPart==null && pulse<=0))return;
        Vector3 point=WorkingPart!=null?WorkingPart.AttachmentWorldPosition:lastPoint;
        if(WorkingPart!=null){var r=WorkingPart.GetComponentInChildren<Renderer>();if(r!=null)point=r.bounds.ClosestPoint(actor.transform.position+Vector3.up*1.2f);}
        if(!hammerRunning)return;
        var hand=actor.GetBoneTransform(HumanBodyBones.RightHand);
        Vector3 gripToHead=hammerHead!=null&&hand!=null?hammerHead.transform.position-hand.position:Vector3.zero;
        actor.SetIKPositionWeight(AvatarIKGoal.RightHand,.75f);
        actor.SetIKRotationWeight(AvatarIKGoal.RightHand,0);
        actor.SetIKPosition(AvatarIKGoal.RightHand,WorkerHammerAnimation.Aim(actor,
            HumanBodyBones.RightUpperArm,HumanBodyBones.RightLowerArm,HumanBodyBones.RightHand,point-gripToHead,.35f));
    }

    void OnDisable(){hammerRunning=false;WorkerHammerAnimation.Stop(animator);}
    void OnDestroy(){if(material!=null)Destroy(material);if(particlesMaterial!=null)Destroy(particlesMaterial);}
}
