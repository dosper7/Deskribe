using Deskribe.Core.Tests.Support;

namespace Deskribe.Core.Tests;

public class DestroyAndStatusTests : IDisposable
{
    private readonly EngineHarness _h = new();

    private const string Manifest = """
        {
          "name": "shop",
          "resources": [ { "type": "postgres" }, { "type": "redis" }, { "type": "kafka.messaging" } ],
          "services": [ { "env": { "DB": "@resource(postgres).connectionString" }, "overrides": { "dev": { "replicas": 50 } } } ]
        }
        """;

    public DestroyAndStatusTests()
    {
        _h.WriteManifest(Manifest);
        // A policy-limit violation must not block status or destroy.
        _h.WriteBase(EngineHarness.DefaultBase.TrimEnd().TrimEnd('}') + """, "policies": { "limits": { "maxReplicas": 3 } } }""");
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task DestroyCallsRuntimeThenEachProvisionerOnceWithItsResources()
    {
        await _h.Engine.DestroyAsync(_h.ManifestPath, _h.PlatformPath, "dev");

        var runtimeCtx = Assert.Single(_h.Runtime.DestroyCalls);
        Assert.Equal("shop-dev", runtimeCtx.Namespace);
        Assert.Empty(runtimeCtx.Workloads);
        Assert.Equal(["postgres", "redis"], Assert.Single(_h.PgProv.DestroyCalls).Resources.Select(r => r.ResourceKey));
        Assert.Equal(["kafka.messaging"], Assert.Single(_h.KafkaProv.DestroyCalls).Resources.Select(r => r.ResourceKey));
    }

    [Fact]
    public async Task DestroyContinuesAfterFailuresAndReportsThemAll()
    {
        _h.Runtime.ThrowOnDestroy = true;
        _h.PgProv.FailDestroy = true;
        _h.KafkaProv.ThrowOnDestroy = true;

        var ex = await Assert.ThrowsAsync<DeskribeException>(
            () => _h.Engine.DestroyAsync(_h.ManifestPath, _h.PlatformPath, "dev"));

        Assert.Single(_h.PgProv.DestroyCalls);
        Assert.Single(_h.KafkaProv.DestroyCalls);
        Assert.Contains("runtime exploded", ex.Message);
        Assert.Contains("pg-prov could not destroy", ex.Message);
        Assert.Contains("kafka-prov exploded", ex.Message);
    }

    [Fact]
    public async Task DestroyRequiresDefinedEnvironment()
    {
        await Assert.ThrowsAsync<DeskribeException>(
            () => _h.Engine.DestroyAsync(_h.ManifestPath, _h.PlatformPath, "qa"));
        Assert.Empty(_h.Runtime.DestroyCalls);
    }

    [Fact]
    public async Task DestroyRequiresResolvablePluginsBeforeDoingAnything()
    {
        _h.WriteBase("""{ "runtime": { "name": "kubernetes" }, "provisioners": { "postgres": "pg-prov", "redis": "pg-prov", "kafka.messaging": "missing" } }""");

        var ex = await Assert.ThrowsAsync<DeskribeException>(
            () => _h.Engine.DestroyAsync(_h.ManifestPath, _h.PlatformPath, "dev"));

        Assert.Contains("missing", ex.Message);
        Assert.Empty(_h.Runtime.DestroyCalls);
        Assert.Empty(_h.PgProv.DestroyCalls);
    }

    [Fact]
    public async Task StatusWorksWithoutImagesAndDespitePolicyViolations()
    {
        var status = await _h.Engine.StatusAsync(_h.ManifestPath, _h.PlatformPath, "dev");

        Assert.True(status.Exists);
        Assert.Equal("shop-dev", status.Namespace);
        Assert.Empty(Assert.Single(_h.Runtime.StatusCalls).Workloads);
    }

    [Fact]
    public async Task StatusRequiresRuntimePlugin()
    {
        _h.WriteBase("""{ "runtime": { "name": "nomad" } }""");
        await Assert.ThrowsAsync<DeskribeException>(
            () => _h.Engine.StatusAsync(_h.ManifestPath, _h.PlatformPath, "dev"));
    }
}
