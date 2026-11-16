namespace Deskribe.Plugins.Runtime.Kubernetes;

/// <summary>Thrown when one or more Deployments do not finish rolling out.</summary>
public sealed class KubernetesRolloutException : Exception
{
    public KubernetesRolloutException(string message, IReadOnlyList<string> failedWorkloads)
        : base(message)
    {
        FailedWorkloads = failedWorkloads;
    }

    /// <summary>Names of the workloads whose rollout failed.</summary>
    public IReadOnlyList<string> FailedWorkloads { get; }
}
