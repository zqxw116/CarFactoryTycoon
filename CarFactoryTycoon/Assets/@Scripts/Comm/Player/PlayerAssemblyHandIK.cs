using UnityEngine;

/// <summary>Animator callbacks must live on the same GameObject as the full-body Animator.</summary>
public sealed class PlayerAssemblyHandIK : MonoBehaviour
{
    public PlayerAssemblyInteractor interactor;
    private Animator animator;
    private void Awake() => animator = GetComponent<Animator>();
    private void OnAnimatorIK(int layerIndex)
    {
        if (interactor != null && interactor.isActiveAndEnabled) interactor.ApplyHandIK(animator);
    }
}
