namespace Deskribe.Plugins.Runtime.Kubernetes.Tests.Integration;

/// <summary>
/// A fact that only runs when <c>DESKRIBE_E2E_KUBECONFIG</c> points at an existing kubeconfig file.
/// </summary>
public sealed class KubernetesE2EFactAttribute : FactAttribute
{
    public const string KubeconfigVariable = "DESKRIBE_E2E_KUBECONFIG";

    public KubernetesE2EFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable(KubeconfigVariable);
        if (string.IsNullOrWhiteSpace(path))
            Skip = $"Set {KubeconfigVariable} to a kubeconfig file to run Kubernetes integration tests.";
        else if (!File.Exists(path))
            Skip = $"{KubeconfigVariable} points at '{path}', which does not exist.";
    }
}
