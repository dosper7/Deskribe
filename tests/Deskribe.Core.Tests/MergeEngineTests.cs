using Deskribe.Core.Merging;
using Deskribe.Sdk.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deskribe.Core.Tests;

public class MergeEngineTests
{
    private readonly MergeEngine _merge = new(NullLogger<MergeEngine>.Instance);

    private static readonly PlatformDefaults FullBase = new()
    {
        Runtime = "base-rt", Region = "base-region", Replicas = 2, Cpu = "100m", Memory = "128Mi",
        NamespacePattern = "base-{app}", Ha = false, SecretsStrategy = "opaque", ExternalSecretsStore = "base-store"
    };

    [Fact]
    public void EnvironmentValueWinsForEveryField()
    {
        var env = new EnvironmentConfig
        {
            Defaults = new PlatformDefaults
            {
                Runtime = "env-rt", Region = "env-region", Replicas = 5, Cpu = "1", Memory = "1Gi",
                NamespacePattern = "{team}-{app}-{env}", Ha = true, SecretsStrategy = "external-secrets", ExternalSecretsStore = "vault"
            }
        };

        var merged = _merge.MergeDefaults(new PlatformConfig { Defaults = FullBase }, env);

        Assert.Equal(env.Defaults, merged);
    }

    [Fact]
    public void BaseValueUsedWhenEnvironmentSilent()
    {
        var merged = _merge.MergeDefaults(new PlatformConfig { Defaults = FullBase }, new EnvironmentConfig());
        Assert.Equal(FullBase, merged);
    }

    [Fact]
    public void EngineDefaultsUsedWhenNothingSet()
    {
        var engineDefaults = new EngineDefaults { ExternalSecretsStore = "engine-store" };
        var merge = new MergeEngine(NullLogger<MergeEngine>.Instance, engineDefaults);

        var merged = merge.MergeDefaults(new PlatformConfig(), new EnvironmentConfig());

        Assert.Equal("kubernetes", merged.Runtime);
        Assert.Equal("westeurope", merged.Region);
        Assert.Equal(2, merged.Replicas);
        Assert.Equal("250m", merged.Cpu);
        Assert.Equal("512Mi", merged.Memory);
        Assert.Equal("{app}-{env}", merged.NamespacePattern);
        Assert.False(merged.Ha);
        Assert.Equal("opaque", merged.SecretsStrategy);
        Assert.Equal("engine-store", merged.ExternalSecretsStore);
    }

    [Fact]
    public void EnvironmentLevelRegionSecretsStrategyAndNamespacePatternAreHonoured()
    {
        var env = new EnvironmentConfig
        {
            Defaults = new PlatformDefaults { Region = "eastus2", SecretsStrategy = "external-secrets", NamespacePattern = "{env}-{app}" }
        };

        var merged = _merge.MergeDefaults(new PlatformConfig { Defaults = FullBase }, env);

        Assert.Equal("eastus2", merged.Region);
        Assert.Equal("external-secrets", merged.SecretsStrategy);
        Assert.Equal("{env}-{app}", merged.NamespacePattern);
        Assert.Equal("dev-shop", MergeEngine.ResolveNamespace(merged, null, "shop", "dev"));
    }

    [Fact]
    public void NamespaceUsesDefaultTeamWhenTeamMissing()
    {
        var d = new PlatformDefaults { NamespacePattern = "{team}-{app}-{env}" };
        Assert.Equal("default-shop-dev", MergeEngine.ResolveNamespace(d, null, "shop", "dev"));
        Assert.Equal("pay-shop-dev", MergeEngine.ResolveNamespace(d, "pay", "shop", "dev"));
    }

    [Fact]
    public void ServiceOverridesForTargetEnvironmentWin()
    {
        var service = new ServiceDefinition
        {
            Overrides = new()
            {
                ["prod"] = new ServiceOverride { Replicas = 4, Cpu = "1" },
                ["dev"] = new ServiceOverride { Memory = "2Gi" }
            }
        };
        var defaults = new PlatformDefaults { Replicas = 2, Cpu = "250m", Memory = "512Mi" };

        Assert.Equal((4, "1", "512Mi"), MergeEngine.ResolveSizing(service, defaults, "prod"));
        Assert.Equal((2, "250m", "2Gi"), MergeEngine.ResolveSizing(service, defaults, "dev"));
        Assert.Equal((2, "250m", "512Mi"), MergeEngine.ResolveSizing(service, defaults, "staging"));
    }

    [Fact]
    public void SingleServiceWorkloadIsNamedAfterApp()
    {
        var unnamed = new DeskribeManifest { Name = "shop", Services = [new ServiceDefinition()] };
        var named = new DeskribeManifest { Name = "shop", Services = [new ServiceDefinition { Name = "api" }] };

        Assert.Equal("shop", MergeEngine.GetWorkloadName(unnamed, unnamed.Services[0]));
        Assert.Equal("shop", MergeEngine.GetWorkloadName(named, named.Services[0]));
    }

    [Fact]
    public void MultiServiceWorkloadsAreAppDashService()
    {
        var m = new DeskribeManifest
        {
            Name = "shop",
            Services = [new ServiceDefinition { Name = "api" }, new ServiceDefinition { Name = "worker" }, new ServiceDefinition()]
        };

        Assert.Equal("shop-api", MergeEngine.GetWorkloadName(m, m.Services[0]));
        Assert.Equal("shop-worker", MergeEngine.GetWorkloadName(m, m.Services[1]));
        Assert.Null(MergeEngine.GetWorkloadName(m, m.Services[2]));
    }

    [Fact]
    public void ImageLookupByServiceNameThenWorkloadName()
    {
        var m = new DeskribeManifest
        {
            Name = "shop",
            Services = [new ServiceDefinition { Name = "api" }, new ServiceDefinition { Name = "worker" }]
        };
        var images = new Dictionary<string, string> { ["api"] = "api:1", ["shop-worker"] = "worker:1", ["other"] = "x" };

        Assert.Equal("api:1", MergeEngine.FindImage(m, m.Services[0], "shop-api", images));
        Assert.Equal("worker:1", MergeEngine.FindImage(m, m.Services[1], "shop-worker", images));
    }

    [Fact]
    public void ServiceNameTakesPrecedenceOverWorkloadName()
    {
        var m = new DeskribeManifest { Name = "shop", Services = [new ServiceDefinition { Name = "api" }] };
        var images = new Dictionary<string, string> { ["shop"] = "by-workload", ["api"] = "by-service" };
        Assert.Equal("by-service", MergeEngine.FindImage(m, m.Services[0], "shop", images));
    }

    [Fact]
    public void SingleServiceWithSingleImageUsesItWhateverTheKey()
    {
        var m = new DeskribeManifest { Name = "shop", Services = [new ServiceDefinition()] };
        Assert.Equal("img:1", MergeEngine.FindImage(m, m.Services[0], "shop", new Dictionary<string, string> { ["anything"] = "img:1" }));
    }

    [Fact]
    public void ImageIsNullWhenAmbiguousOrMissing()
    {
        var single = new DeskribeManifest { Name = "shop", Services = [new ServiceDefinition()] };
        var multi = new DeskribeManifest
        {
            Name = "shop",
            Services = [new ServiceDefinition { Name = "api" }, new ServiceDefinition { Name = "worker" }]
        };

        Assert.Null(MergeEngine.FindImage(single, single.Services[0], "shop", new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }));
        Assert.Null(MergeEngine.FindImage(single, single.Services[0], "shop", null));
        Assert.Null(MergeEngine.FindImage(multi, multi.Services[0], "shop-api", new Dictionary<string, string> { ["x"] = "1" }));
    }
}
