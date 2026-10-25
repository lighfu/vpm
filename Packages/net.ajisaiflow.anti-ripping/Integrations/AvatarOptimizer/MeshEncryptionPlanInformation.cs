#if AAO_VAO_AVAILABLE
using Anatawa12.AvatarOptimizer.API;
using UnityEngine;

namespace AjisaiFlow.AntiRipping.Editor.Pipeline
{
    /// <summary>Keep the build plan and its references until AAO has applied its renderer replacement mapping.</summary>
    [ComponentInformation(typeof(MeshEncryptionPlan))]
    internal sealed class MeshEncryptionPlanInformation : ComponentInformation<MeshEncryptionPlan>
    {
        public MeshEncryptionPlanInformation() { }

        protected override void CollectDependency(MeshEncryptionPlan component, ComponentDependencyCollector collector)
        {
            collector.MarkEntrypoint();
            // A root dependency also retains the plan if a preceding tool temporarily disables it.
            collector.AddDependency(component.transform.root, component);
            AddTargets(component.Targets, collector);
            AddTargets(component.SourceOwners, collector);
            AddTargets(component.ExcludedTargets, collector);
            AddTargets(component.ExcludedMeshRenderers, collector);
        }

        private static void AddTargets(Renderer[] targets, ComponentDependencyCollector collector)
        {
            if (targets == null) return;
            foreach (Renderer renderer in targets)
                if (renderer != null) collector.AddDependency(renderer);
        }
        // AAO maps all serialized ObjectReference properties automatically; no special mapping callback is required.
    }
}
#endif
