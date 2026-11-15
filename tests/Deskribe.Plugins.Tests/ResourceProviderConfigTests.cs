using System.Text.Json;
using Deskribe.Plugins.Resources.Postgres;
using Deskribe.Plugins.Resources.Redis;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Tests;

public class ResourceProviderConfigTests
{
    private static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static ValidationContext CreateValidationContext(Dictionary<string, JsonElement>? providerConfig) => new()
    {
        Platform = new PlatformConfig
        {
            ProvisionerConfigs = providerConfig is null
                ? new()
                : new Dictionary<string, Dictionary<string, JsonElement>> { ["postgres"] = providerConfig }
        },
        Environment = "dev"
    };

    private static PlanContext CreatePlanContext(string providerKey, Dictionary<string, JsonElement>? providerConfig) => new()
    {
        Platform = new PlatformConfig
        {
            ProvisionerConfigs = providerConfig is null
                ? new()
                : new Dictionary<string, Dictionary<string, JsonElement>> { [providerKey] = providerConfig }
        },
        EnvironmentConfig = new EnvironmentConfig { Name = "dev" },
        Environment = "dev",
        AppName = "myapp"
    };

    [Fact]
    public async Task Postgres_ConfiguredValidVersions_ReplaceDefaults()
    {
        var provider = new PostgresResourceProvider();
        var config = new Dictionary<string, JsonElement>
        {
            ["validVersions"] = ToElement(new[] { "16", "17", "18" })
        };
        var resource = new ResourceDescriptor
        {
            Type = "postgres",
            Properties = new Dictionary<string, JsonElement> { ["version"] = ToElement("14") }
        };

        // "14" is valid by default but excluded by the configured list
        var result = await provider.ValidateAsync(resource, CreateValidationContext(config), CancellationToken.None);
        Assert.False(result.IsValid);

        resource.Properties["version"] = ToElement("18");
        result = await provider.ValidateAsync(resource, CreateValidationContext(config), CancellationToken.None);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Postgres_ConfiguredValidSizes_ReplaceDefaults()
    {
        var provider = new PostgresResourceProvider();
        var config = new Dictionary<string, JsonElement>
        {
            ["validSizes"] = ToElement(new[] { "tiny", "huge" })
        };
        var resource = new ResourceDescriptor { Type = "postgres", Size = "m" };

        var result = await provider.ValidateAsync(resource, CreateValidationContext(config), CancellationToken.None);
        Assert.False(result.IsValid);

        resource = new ResourceDescriptor { Type = "postgres", Size = "huge" };
        result = await provider.ValidateAsync(resource, CreateValidationContext(config), CancellationToken.None);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Postgres_ConfiguredDefaultVersionAndPort_AreUsed()
    {
        var provider = new PostgresResourceProvider();
        var config = new Dictionary<string, JsonElement>
        {
            ["defaultVersion"] = ToElement("17"),
            ["port"] = ToElement(55432)
        };
        var resource = new ResourceDescriptor { Type = "postgres" };

        var result = await provider.PlanAsync(resource, CreatePlanContext("postgres", config), CancellationToken.None);

        Assert.Equal("17", result.Configuration["version"]);
        Assert.Equal("55432", result.PlannedOutputs["port"]);
    }

    [Fact]
    public async Task Postgres_WithoutConfig_UsesDefaults()
    {
        var provider = new PostgresResourceProvider();
        var resource = new ResourceDescriptor { Type = "postgres" };

        var result = await provider.PlanAsync(resource, CreatePlanContext("postgres", null), CancellationToken.None);

        Assert.Equal("16", result.Configuration["version"]);
        Assert.Equal("5432", result.PlannedOutputs["port"]);
    }

    [Fact]
    public async Task Redis_ConfiguredValidSizes_ReplaceDefaults()
    {
        var provider = new RedisResourceProvider();
        var config = new Dictionary<string, JsonElement>
        {
            ["validSizes"] = ToElement(new[] { "tiny", "huge" })
        };
        var ctx = CreateValidationContext(null);
        ctx = ctx with
        {
            Platform = ctx.Platform with
            {
                ProvisionerConfigs = new Dictionary<string, Dictionary<string, JsonElement>> { ["redis"] = config }
            }
        };

        var result = await provider.ValidateAsync(new ResourceDescriptor { Type = "redis", Size = "s" }, ctx, CancellationToken.None);
        Assert.False(result.IsValid);

        result = await provider.ValidateAsync(new ResourceDescriptor { Type = "redis", Size = "huge" }, ctx, CancellationToken.None);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Redis_ConfiguredPort_IsUsed()
    {
        var provider = new RedisResourceProvider();
        var config = new Dictionary<string, JsonElement>
        {
            ["port"] = ToElement(6380)
        };

        var result = await provider.PlanAsync(new ResourceDescriptor { Type = "redis" }, CreatePlanContext("redis", config), CancellationToken.None);

        Assert.Equal("6380", result.PlannedOutputs["port"]);
    }

    [Fact]
    public async Task Redis_WithoutConfig_UsesDefaults()
    {
        var provider = new RedisResourceProvider();

        var result = await provider.PlanAsync(new ResourceDescriptor { Type = "redis" }, CreatePlanContext("redis", null), CancellationToken.None);

        Assert.Equal("6379", result.PlannedOutputs["port"]);
    }
}
