using System;
using nadena.dev.ndmf;
using UnityEngine;

namespace AjisaiFlow.AntiRipping
{
    /// <summary>
    /// Build-only renderer references captured before optimization. AAO remaps serialized
    /// component references when it merges renderers, so final encryption uses the resulting meshes.
    /// This component contains no key or original mesh data and is removed before export.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class MeshEncryptionPlan : MonoBehaviour, INDMFEditorOnly
    {
        [HideInInspector] public SkinnedMeshRenderer[] Targets = Array.Empty<SkinnedMeshRenderer>();
        // Keep each prepared source owner's reference remappable through optimizer merges.
        // Source mesh identities remain in build state and are never serialized here.
        [HideInInspector] public SkinnedMeshRenderer[] SourceOwners = Array.Empty<SkinnedMeshRenderer>();
        [HideInInspector] public SkinnedMeshRenderer[] ExcludedTargets = Array.Empty<SkinnedMeshRenderer>();
        [HideInInspector] public MeshRenderer[] ExcludedMeshRenderers = Array.Empty<MeshRenderer>();
    }
}
