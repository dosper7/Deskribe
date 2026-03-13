using Deskribe.Sdk;
using Deskribe.Plugins.Runtime.Kubernetes;

namespace Deskribe.Plugins.Tests;

public class SecretsStrategyTests
{
    private readonly KubernetesRuntimePlugin _plugin = new();

    private static WorkloadPlan CreatePlan(string secretsStrategy = "opaque", string? externalSecretsStore = null) => new()
    {
        AppName = "test-app",
        Environment = "dev",
        Namespace = "test-app-dev",
        Image = "nginx:latest",
        Replicas = 1,
        Cpu = "250m",
        Memory = "256Mi",
        EnvironmentVariables = new Dictionary<string, string>
        {
            ["ConnectionStrings__Postgres"] = "Host=pg;Database=test"
        },
        SecretsStrategy = secretsStrategy,
        ExternalSecretsStore = externalSecretsStore
    };

    [Fact]
    public async Task OpaqueStrategy_GeneratesV1Secret()
    {
        var plan = CreatePlan("opaque");
        var artifact = await _plugin.RenderAsync(plan);

        Assert.Contains("kind: Secret", artifact.Yaml);
        Assert.Contains("type: Opaque", artifact.Yaml);
        Assert.DoesNotContain("ExternalSecret", artifact.Yaml);
        Assert.DoesNotContain("sealedsecrets.bitnami.com", artifact.Yaml);
        Assert.Contains("Secret/test-app-dev/test-app-env", artifact.ResourceNames);
    }

    [Fact]
    public async Task ExternalSecretsStrategy_GeneratesExternalSecretCrd()
    {
        var plan = CreatePlan("external-secrets", "azure-keyvault");
        var artifact = await _plugin.RenderAsync(plan);

        Assert.Contains("kind: ExternalSecret", artifact.Yaml);
        Assert.Contains("external-secrets.io/v1beta1", artifact.Yaml);
        Assert.Contains("azure-keyvault", artifact.Yaml);
        Assert.Contains("ClusterSecretStore", artifact.Yaml);
        Assert.Contains("ExternalSecret/test-app-dev/test-app-env", artifact.ResourceNames);
    }

    [Fact]
    public async Task SealedSecretsStrategy_GeneratesV1SecretWithAnnotation()
    {
        var plan = CreatePlan("sealed-secrets");
        var artifact = await _plugin.RenderAsync(plan);

        Assert.Contains("kind: Secret", artifact.Yaml);
        Assert.Contains("type: Opaque", artifact.Yaml);
        Assert.Contains("sealedsecrets.bitnami.com/managed", artifact.Yaml);
        Assert.DoesNotContain("ExternalSecret", artifact.Yaml);
        Assert.Contains("Secret/test-app-dev/test-app-env", artifact.ResourceNames);
    }

    [Fact]
    public async Task DefaultStrategy_FallsBackToOpaque()
    {
        var plan = CreatePlan(); // defaults to "opaque"
        var artifact = await _plugin.RenderAsync(plan);

        Assert.Contains("kind: Secret", artifact.Yaml);
        Assert.Contains("type: Opaque", artifact.Yaml);
    }

    [Fact]
    public async Task NoEnvVars_GeneratesNoSecretOrExternalSecret()
    {
        var plan = new WorkloadPlan
        {
            AppName = "test-app",
            Environment = "dev",
            Namespace = "test-app-dev",
            Image = "nginx:latest",
            EnvironmentVariables = new(),
            SecretsStrategy = "external-secrets",
            ExternalSecretsStore = "azure-keyvault"
        };

        var artifact = await _plugin.RenderAsync(plan);

        Assert.DoesNotContain("Secret", artifact.Yaml);
        Assert.DoesNotContain("ExternalSecret", artifact.Yaml);
    }
}
