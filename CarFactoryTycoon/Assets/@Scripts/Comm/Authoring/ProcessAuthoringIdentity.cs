using UnityEngine;

/// <summary>Stable authoring identity, independent of hierarchy names and array ordering.</summary>
[DisallowMultipleComponent]
public sealed class ProcessAuthoringIdentity : MonoBehaviour
{
    public string id;
}
