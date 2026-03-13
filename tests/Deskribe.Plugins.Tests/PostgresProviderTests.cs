using System.Text.Json;
using Deskribe.Plugins.Resources.Postgres;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Tests;

public class PostgresProviderTests
{
    private readonly PostgresResourceProvider _provider = new();

    private static ValidationContext CreateContext() => new()
    {
        Platform = new PlatformConfig
        {
            Defaults = new PlatformDefaults { NamespacePattern = "{app}-{env}" },
            Provisioners = new Dictionary<string, string> { ["postgres"] = "pulumi" }
        },
        Environment = "dev"
    };

    private static ResourceDescriptor CreateResource(string? size = null, string? version = null)
    {
        var props = new Dictionary<string, JsonElement>();
        if (version is not null)
            props["version"] = JsonSerializer.SerializeToElement(version);

        return new ResourceDescriptor
        {
            Type = "postgres",
            Size = size,
            Properties = props
        };
    }

    [Fact]
    public async Task Validate_PassesForValidResource()
    {
        var resource = CreateResource(size: "m", version: "16");
        var result = await _provider.ValidateAsync(resource, CreateContext(), CancellationToken.None);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Validate_FailsForInvalidSize()
    {
        var resource = CreateResource(size: "mega");
        var result = await _provider.ValidateAsync(resource, CreateContext(), CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Contains("size", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_FailsForInvalidVersion()
    {
        var resource = CreateResource(version: "9");
        var result = await _provider.ValidateAsync(resource, CreateContext(), CancellationToken.None);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Plan_ReturnsCorrectOutputs()
    {
        var resource = CreateResource(size: "m");
        var ctx = new PlanContext
        {
            Platform = new PlatformConfig
            {
                Defaults = new PlatformDefaults { NamespacePattern = "{app}-{env}" },
                Provisioners = new Dictionary<string, string> { ["postgres"] = "pulumi" }
            },
            EnvironmentConfig = new EnvironmentConfig { Name = "dev" },
            Environment = "dev",
            AppName = "myapp"
        };

        var result = await _provider.PlanAsync(resource, ctx, CancellationToken.None);

        Assert.Equal("postgres", result.ResourceType);
        Assert.Equal("create", result.Action);
        Assert.Contains("connectionString", result.PlannedOutputs.Keys);
        Assert.Contains("host", result.PlannedOutputs.Keys);
    }

    [Fact]
    public void GetSchema_ReturnsCorrectMetadata()
    {
        var schema = _provider.GetSchema();

        Assert.Equal("postgres", schema.ResourceType);
        Assert.Contains(schema.ProvidedOutputs, o => o == "connectionString");
        Assert.Contains(schema.Properties, p => p.Name == "version");
    }
}
