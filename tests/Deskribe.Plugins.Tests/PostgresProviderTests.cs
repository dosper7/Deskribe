using Deskribe.Plugins.Resources.Postgres;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;

namespace Deskribe.Plugins.Tests;

public class PostgresProviderTests
{
    private readonly PostgresResourceProvider _provider = new();

    private static ValidationContext CreateContext() => new()
    {
        Platform = new PlatformConfig
        {
            Defaults = new PlatformDefaults { NamespacePattern = "{app}-{env}" },
            Backends = new Dictionary<string, string> { ["postgres"] = "pulumi" }
        },
        Environment = "dev"
    };

    [Fact]
    public async Task Validate_PassesForValidResource()
    {
        var resource = new PostgresResource { Type = "postgres", Size = "m", Version = "16" };
        var result = await _provider.ValidateAsync(resource, CreateContext(), CancellationToken.None);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Validate_FailsForInvalidSize()
    {
        var resource = new PostgresResource { Type = "postgres", Size = "mega" };
        var result = await _provider.ValidateAsync(resource, CreateContext(), CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Contains("size", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_FailsForInvalidVersion()
    {
        var resource = new PostgresResource { Type = "postgres", Version = "9" };
        var result = await _provider.ValidateAsync(resource, CreateContext(), CancellationToken.None);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Plan_ReturnsCorrectOutputs()
    {
        var resource = new PostgresResource { Type = "postgres", Size = "m" };
        var ctx = new PlanContext
        {
            Platform = new PlatformConfig
            {
                Defaults = new PlatformDefaults { NamespacePattern = "{app}-{env}" },
                Backends = new Dictionary<string, string> { ["postgres"] = "pulumi" }
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
}
