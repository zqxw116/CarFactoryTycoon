using UnityEngine;

[RequireComponent(typeof(Animator))]
public sealed class PrototypeCrewHandIK : MonoBehaviour
{
    public PrototypeAssemblyEffects effects;
    Animator actor;
    void Awake(){actor=GetComponent<Animator>();}
    void OnAnimatorIK(int layer){if(actor!=null&&WorkerHammerAnimation.IsFinalIKPass(actor,layer)&&effects!=null)effects.ApplyHandIK(actor);}
}
