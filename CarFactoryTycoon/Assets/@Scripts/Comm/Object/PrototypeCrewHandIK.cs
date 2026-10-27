using UnityEngine;

[RequireComponent(typeof(Animator))]
public sealed class PrototypeCrewHandIK : MonoBehaviour
{
    public PrototypeAssemblyEffects effects;
    Animator actor;
    void Awake(){actor=GetComponent<Animator>();}
    void OnAnimatorIK(int layer){if(effects!=null)effects.ApplyHandIK(actor);}
}
