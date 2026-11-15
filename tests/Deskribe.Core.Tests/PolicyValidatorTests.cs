using Deskribe.Core.Plugins;
using Deskribe.Core.Validation;
using Deskribe.Sdk.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deskribe.Core.Tests;

public class PolicyValidatorTests
{
    private readonly PolicyValidator _validator = new(NullLogger<PolicyValidator>.Instance);

    private static PlatformConfig CreatePlatform() => new()
    {
        Organization = "acme",
        Defaults = new PlatformDefaults(),
        Provisioners = new Dictionary<string, string> { ["postgres"] = "pulumi" }
    };

    private static DeskribeManifest CreateManifest(Dictionary<string, string> env) => new()
    {
        Name = "test-app",
        Resources = [new ResourceDescriptor { Type = "postgres", Size = "m" }],
        Services = [new ServiceDefinition { Env = env }]
    };

    [Fact]
    public void Validate_MalformedResourceReference_ProducesError()
    {
        // "@resource(postgres)" without the .property suffix does not match the canonical pattern
        var manifest = CreateManifest(new Dictionary<string, string>
        {
            ["DB"] = "@resource(postgres)"
        });

        var result = _validator.Validate(manifest, CreatePlatform());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("DB") && e.Contains("malformed"));
    }

    [Fact]
    public void Validate_WellFormedReferenceToDeclaredResource_IsValid()
    {
        var manifest = CreateManifest(new Dictionary<string, string>
        {
            ["DB"] = "@resource(postgres).connectionString"
        });

        var result = _validator.Validate(manifest, CreatePlatform());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ReferenceToUndeclaredResourceType_ProducesError()
    {
        var manifest = CreateManifest(new Dictionary<string, string>
        {
            ["CACHE"] = "@resource(redis).endpoint"
        });

        var result = _validator.Validate(manifest, CreatePlatform());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("CACHE") && e.Contains("redis"));
    }
}

public class PluginRegistryThreadSafetyTests
{
    [Fact]
    public void ConcurrentRegistrations_DoNotThrow()
    {
        var registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);

        var exceptions = new List<Exception>();
        Parallel.For(0, 100, i =>
        {
            try
            {
                registry.RegisterProvisioner(new FakeProvisioner($"prov-{i % 10}"));
                _ = registry.GetProvisioner($"prov-{i % 10}");
            }
            catch (Exception ex)
            {
                lock (exceptions) { exceptions.Add(ex); }
            }
        });

        Assert.Empty(exceptions);
        Assert.NotNull(registry.GetProvisioner("prov-0"));
    }

    private sealed class FakeProvisioner(string name) : Deskribe.Sdk.IProvisioner
    {
        public string Name => name;
        public Task<Deskribe.Sdk.ProvisionResult> ApplyAsync(Deskribe.Sdk.Models.DeskribePlan plan, CancellationToken ct = default) =>
            Task.FromResult(new Deskribe.Sdk.ProvisionResult { Success = true });
        public Task<Deskribe.Sdk.ArtifactResult> GenerateArtifactsAsync(Deskribe.Sdk.Models.DeskribePlan plan, string outputDir, CancellationToken ct = default) =>
            Task.FromResult(new Deskribe.Sdk.ArtifactResult { Success = true });
        public Task DestroyAsync(string appName, string environment, Deskribe.Sdk.Models.PlatformConfig platform, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
