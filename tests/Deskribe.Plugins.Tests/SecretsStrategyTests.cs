using Deskribe.Sdk;
using Deskribe.Plugins.Runtime.Kubernetes;

namespace Deskribe.Plugins.Tests;

public class SecretsStrategyTests
{
    private readonly KubernetesRuntimeAdapter _adapter = new();

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
        var manifest = await _adapter.RenderAsync(plan);

        Assert.Contains("kind: Secret", manifest.Yaml);
        Assert.Contains("type: Opaque", manifest.Yaml);
        Assert.DoesNotContain("ExternalSecret", manifest.Yaml);
        Assert.DoesNotContain("sealedsecrets.bitnami.com", manifest.Yaml);
        Assert.Contains("Secret/test-app-dev/test-app-env", manifest.ResourceNames);
    }

    [Fact]
    public async Task ExternalSecretsStrategy_GeneratesExternalSecretCrd()
    {
        var plan = CreatePlan("external-secrets", "azure-keyvault");
        var manifest = await _adapter.RenderAsync(plan);

        Assert.Contains("kind: ExternalSecret", manifest.Yaml);
        Assert.Contains("external-secrets.io/v1beta1", manifest.Yaml);
        Assert.Contains("azure-keyvault", manifest.Yaml);
        Assert.Contains("ClusterSecretStore", manifest.Yaml);
        Assert.Contains("ExternalSecret/test-app-dev/test-app-env", manifest.ResourceNames);
    }

    [Fact]
    public async Task SealedSecretsStrategy_GeneratesV1SecretWithAnnotation()
    {
        var plan = CreatePlan("sealed-secrets");
        var manifest = await _adapter.RenderAsync(plan);

        Assert.Contains("kind: Secret", manifest.Yaml);
        Assert.Contains("type: Opaque", manifest.Yaml);
        Assert.Contains("sealedsecrets.bitnami.com/managed", manifest.Yaml);
        Assert.DoesNotContain("ExternalSecret", manifest.Yaml);
        Assert.Contains("Secret/test-app-dev/test-app-env", manifest.ResourceNames);
    }

    [Fact]
    public async Task DefaultStrategy_FallsBackToOpaque()
    {
        var plan = CreatePlan(); // defaults to "opaque"
        var manifest = await _adapter.RenderAsync(plan);

        Assert.Contains("kind: Secret", manifest.Yaml);
        Assert.Contains("type: Opaque", manifest.Yaml);
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

        var manifest = await _adapter.RenderAsync(plan);

        Assert.DoesNotContain("Secret", manifest.Yaml);
        Assert.DoesNotContain("ExternalSecret", manifest.Yaml);
    }
}
