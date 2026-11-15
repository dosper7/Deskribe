using System.Text.Json;
using Deskribe.Plugins.Runtime.Kubernetes;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Tests;

public class KubernetesRuntimeConfigTests
{
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

    private static PlatformConfig CreatePlatform(Dictionary<string, JsonElement> runtimeConfig) => new()
    {
        Runtime = new RuntimeConfig { Config = runtimeConfig }
    };

    [Fact]
    public async Task DefaultPorts_AreUsedWithoutConfig()
    {
        var plugin = new KubernetesRuntimePlugin();
        var artifact = await plugin.RenderAsync(CreatePlan());

        Assert.Contains("containerPort: 8080", artifact.Yaml);
        Assert.Contains("port: 80", artifact.Yaml);
        Assert.Contains("targetPort: 8080", artifact.Yaml);
    }

    [Fact]
    public async Task ConfiguredPorts_AndPortName_AreUsed()
    {
        var plugin = new KubernetesRuntimePlugin(CreatePlatform(new()
        {
            ["containerPort"] = JsonSerializer.SerializeToElement(3000),
            ["servicePort"] = JsonSerializer.SerializeToElement(8081),
            ["portName"] = JsonSerializer.SerializeToElement("web")
        }));

        var artifact = await plugin.RenderAsync(CreatePlan());

        Assert.Contains("containerPort: 3000", artifact.Yaml);
        Assert.Contains("port: 8081", artifact.Yaml);
        Assert.Contains("targetPort: 3000", artifact.Yaml);
        Assert.Contains("name: web", artifact.Yaml);
    }

    [Fact]
    public async Task ConfiguredExternalSecretsSettings_AreUsed()
    {
        var plugin = new KubernetesRuntimePlugin(CreatePlatform(new()
        {
            ["externalSecretsStore"] = JsonSerializer.SerializeToElement("platform-store"),
            ["externalSecretsRefreshInterval"] = JsonSerializer.SerializeToElement("15m"),
            ["externalSecretsStoreKind"] = JsonSerializer.SerializeToElement("SecretStore")
        }));

        var artifact = await plugin.RenderAsync(CreatePlan("external-secrets"));

        Assert.Contains("name: platform-store", artifact.Yaml);
        Assert.Contains("refreshInterval: 15m", artifact.Yaml);
        Assert.Contains("kind: SecretStore", artifact.Yaml);
        Assert.DoesNotContain("ClusterSecretStore", artifact.Yaml);
    }

    [Fact]
    public async Task WorkloadStore_TakesPrecedenceOverConfiguredStore()
    {
        var plugin = new KubernetesRuntimePlugin(CreatePlatform(new()
        {
            ["externalSecretsStore"] = JsonSerializer.SerializeToElement("platform-store")
        }));

        var artifact = await plugin.RenderAsync(CreatePlan("external-secrets", "workload-store"));

        Assert.Contains("name: workload-store", artifact.Yaml);
        Assert.DoesNotContain("platform-store", artifact.Yaml);
    }

    [Fact]
    public async Task DefaultExternalSecretsSettings_AreUsedWithoutConfig()
    {
        var plugin = new KubernetesRuntimePlugin();

        var artifact = await plugin.RenderAsync(CreatePlan("external-secrets"));

        Assert.Contains("name: default", artifact.Yaml);
        Assert.Contains("refreshInterval: 1h", artifact.Yaml);
        Assert.Contains("kind: ClusterSecretStore", artifact.Yaml);
    }
}
