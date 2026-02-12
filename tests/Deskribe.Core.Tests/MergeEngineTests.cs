using Deskribe.Core.Merging;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deskribe.Core.Tests;

public class MergeEngineTests
{
    private readonly MergeEngine _engine = new(NullLogger<MergeEngine>.Instance);

    private static DeskribeManifest CreateManifest(ServiceOverride? devOverride = null, ServiceOverride? prodOverride = null)
    {
        var overrides = new Dictionary<string, ServiceOverride>();
        if (devOverride is not null) overrides["dev"] = devOverride;
        if (prodOverride is not null) overrides["prod"] = prodOverride;

        return new DeskribeManifest
        {
            Name = "test-app",
            Resources = [new PostgresResource { Type = "postgres", Size = "m" }],
            Services =
            [
                new ServiceDefinition
                {
                    Env = new Dictionary<string, string>
                    {
                        ["DB"] = "@resource(postgres).connectionString"
                    },
                    Overrides = overrides
                }
            ]
        };
    }

    private static PlatformConfig CreatePlatform() => new()
    {
        Organization = "acme",
        Defaults = new PlatformDefaults
        {
            Runtime = "kubernetes",
            Region = "westeurope",
            Replicas = 2,
            Cpu = "250m",
            Memory = "512Mi",
            NamespacePattern = "{app}-{env}"
        },
        Backends = new Dictionary<string, string> { ["postgres"] = "pulumi" }
    };

    [Fact]
    public void MergeWorkloadPlan_UsePlatformDefaults_WhenNoOverrides()
    {
        var manifest = CreateManifest();
        var platform = CreatePlatform();
        var envConfig = new EnvironmentConfig { Name = "dev" };

        var result = _engine.MergeWorkloadPlan(manifest, platform, envConfig, "dev", null);

        Assert.Equal("test-app", result.AppName);
        Assert.Equal("dev", result.Environment);
        Assert.Equal("test-app-dev", result.Namespace);
        Assert.Equal(2, result.Replicas);
        Assert.Equal("250m", result.Cpu);
        Assert.Equal("512Mi", result.Memory);
        Assert.Null(result.Image);
    }

    [Fact]
    public void MergeWorkloadPlan_EnvironmentOverridesPlatform()
    {
        var manifest = CreateManifest();
        var platform = CreatePlatform();
        var envConfig = new EnvironmentConfig
        {
            Name = "prod",
            Defaults = new PlatformDefaults
            {
                Replicas = 5,
                Cpu = "1000m",
                Memory = "2Gi"
            }
        };

        var result = _engine.MergeWorkloadPlan(manifest, platform, envConfig, "prod", null);

        Assert.Equal(5, result.Replicas);
        Assert.Equal("1000m", result.Cpu);
        Assert.Equal("2Gi", result.Memory);
    }

    [Fact]
    public void MergeWorkloadPlan_DeveloperOverridesEnvironment()
    {
        var manifest = CreateManifest(
            prodOverride: new ServiceOverride { Replicas = 3, Cpu = "500m", Memory = "1Gi" });
        var platform = CreatePlatform();
        var envConfig = new EnvironmentConfig
        {
            Name = "prod",
            Defaults = new PlatformDefaults
            {
                Replicas = 5,
                Cpu = "1000m",
                Memory = "2Gi"
            }
        };

        var result = _engine.MergeWorkloadPlan(manifest, platform, envConfig, "prod", null);

        // Developer override wins
        Assert.Equal(3, result.Replicas);
        Assert.Equal("500m", result.Cpu);
        Assert.Equal("1Gi", result.Memory);
    }

    [Fact]
    public void MergeWorkloadPlan_SetsImageWhenProvided()
    {
        var manifest = CreateManifest();
        var platform = CreatePlatform();
        var envConfig = new EnvironmentConfig { Name = "dev" };

        var result = _engine.MergeWorkloadPlan(manifest, platform, envConfig, "dev", "ghcr.io/acme/api:sha-123");

        Assert.Equal("ghcr.io/acme/api:sha-123", result.Image);
    }

    [Fact]
    public void MergeWorkloadPlan_NamespacePatternResolved()
    {
        var manifest = CreateManifest();
        var platform = CreatePlatform();
        var envConfig = new EnvironmentConfig { Name = "staging" };

        var result = _engine.MergeWorkloadPlan(manifest, platform, envConfig, "staging", null);

        Assert.Equal("test-app-staging", result.Namespace);
    }

    [Fact]
    public void MergeWorkloadPlan_PreservesEnvVars()
    {
        var manifest = CreateManifest();
        var platform = CreatePlatform();
        var envConfig = new EnvironmentConfig { Name = "dev" };

        var result = _engine.MergeWorkloadPlan(manifest, platform, envConfig, "dev", null);

        Assert.Contains("DB", result.EnvironmentVariables.Keys);
        Assert.Equal("@resource(postgres).connectionString", result.EnvironmentVariables["DB"]);
    }

    [Fact]
    public void MergeWorkloadPlan_PropagatesSecretsStrategy()
    {
        var manifest = CreateManifest();
        var platform = CreatePlatform() with
        {
            Defaults = CreatePlatform().Defaults with
            {
                SecretsStrategy = "external-secrets",
                ExternalSecretsStore = "azure-keyvault"
            }
        };
        var envConfig = new EnvironmentConfig { Name = "dev" };

        var result = _engine.MergeWorkloadPlan(manifest, platform, envConfig, "dev", null);

        Assert.Equal("external-secrets", result.SecretsStrategy);
        Assert.Equal("azure-keyvault", result.ExternalSecretsStore);
    }

    [Fact]
    public void MergeWorkloadPlan_DefaultsSecretsStrategyToOpaque()
    {
        var manifest = CreateManifest();
        var platform = CreatePlatform();
        var envConfig = new EnvironmentConfig { Name = "dev" };

        var result = _engine.MergeWorkloadPlan(manifest, platform, envConfig, "dev", null);

        Assert.Equal("opaque", result.SecretsStrategy);
        Assert.Null(result.ExternalSecretsStore);
    }
}
