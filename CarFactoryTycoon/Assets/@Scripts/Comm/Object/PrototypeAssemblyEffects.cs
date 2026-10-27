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
        rest=Quaternion.Euler(0,0,90);tool.localRotation=rest;
        var shader=Shader.Find("Universal Render Pipeline/Lit");
        if(shader!=null){material=new Material(shader);material.color=new Color(.3f,.32f,.34f);}
        Piece("Handle",new Vector3(0,.12f,0),new Vector3(.035f,.3f,.035f));
        Piece("Head",new Vector3(0,.29f,0),new Vector3(.21f,.1f,.09f));
    }
    void Piece(string name,Vector3 p,Vector3 size)
    {
        var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(tool,false);
        go.transform.localPosition=p;go.transform.localScale=size;
        var collider=go.GetComponent<Collider>();collider.enabled=false;Destroy(collider);
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
            var main=particles.main;main.loop=false;main.playOnAwake=false;main.startLifetime=.15f;main.startSpeed=1.2f;main.startSize=.03f;main.maxParticles=40;main.simulationSpace=ParticleSystemSimulationSpace.World;
            var emission=particles.emission;emission.enabled=false;var shape=particles.shape;shape.shapeType=ParticleSystemShapeType.Sphere;shape.radius=.015f;
            var shader=Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if(shader!=null){particlesMaterial=new Material(shader);particles.GetComponent<ParticleSystemRenderer>().sharedMaterial=particlesMaterial;}
        }
        particles.transform.position=point;var settings=particles.main;settings.startColor=complete?new Color(.5f,1f,1f):new Color(1f,.7f,.15f);
        particles.Emit(complete?18:8);
    }
    void LateUpdate(){pulse=Mathf.MoveTowards(pulse,0,Time.deltaTime*5);if(tool!=null)tool.localRotation=rest*Quaternion.Euler(-70*Mathf.Sin(pulse*Mathf.PI),0,0);}
    public void ApplyHandIK(Animator actor)
    {
        if(robot || (WorkingPart==null && pulse<=0))return;
        Vector3 point=WorkingPart!=null?WorkingPart.AttachmentWorldPosition:lastPoint;
        if(WorkingPart!=null){var r=WorkingPart.GetComponentInChildren<Renderer>();if(r!=null)point=r.bounds.ClosestPoint(actor.transform.position+Vector3.up*1.2f);}
        float swing=Mathf.Sin(pulse*Mathf.PI);
        actor.SetIKPositionWeight(AvatarIKGoal.RightHand,1);actor.SetIKRotationWeight(AvatarIKGoal.RightHand,0);
        actor.SetIKPosition(AvatarIKGoal.RightHand,point-transform.forward*(.12f+swing*.22f)+Vector3.up*swing*.12f);
        actor.SetIKPositionWeight(AvatarIKGoal.LeftHand,.4f);actor.SetIKRotationWeight(AvatarIKGoal.LeftHand,0);
        actor.SetIKPosition(AvatarIKGoal.LeftHand,point-transform.right*.16f);
    }
    void OnDestroy(){if(material!=null)Destroy(material);if(particlesMaterial!=null)Destroy(particlesMaterial);}
}

