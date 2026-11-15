using Deskribe.Core.Tests.Support;
using Deskribe.Sdk;

namespace Deskribe.Core.Tests;

public class ApplyTests : IDisposable
{
    private readonly EngineHarness _h = new();

    private const string Manifest = """
        {
          "name": "shop",
          "resources": [ { "type": "postgres" }, { "type": "redis" }, { "type": "kafka.messaging" } ],
          "services": [ {
            "env": {
              "DB": "@resource(postgres).connectionString",
              "CACHE": "@resource(redis).endpoint",
              "KAFKA": "@resource(kafka.messaging).endpoint",
              "MODE": "fast"
            }
          } ]
        }
        """;

    private static readonly Dictionary<string, string> Images = new() { ["shop"] = "shop:1" };

    public ApplyTests()
    {
        _h.WriteManifest(Manifest);
        _h.PgProv.Outputs["postgres"] = new() { ["connectionString"] = "Host=pg;Token=topsecret-123" };
        _h.PgProv.Outputs["redis"] = new() { ["endpoint"] = "redis:6379" };
        _h.KafkaProv.Outputs["kafka.messaging"] = new() { ["endpoint"] = "kafka:9092" };
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task CallsEachProvisionerOnceWithOnlyItsResources()
    {
        var plan = await _h.PlanAsync(images: Images);
        await _h.Engine.ApplyAsync(plan);

        var pg = Assert.Single(_h.PgProv.ApplyCalls);
        Assert.Equal(["postgres", "redis"], pg.Resources.Select(r => r.ResourceKey));
        Assert.Equal("db.example", pg.Config["server"].GetString());
        Assert.Same(plan.Defaults, pg.Defaults);

        var kafka = Assert.Single(_h.KafkaProv.ApplyCalls);
        Assert.Equal(["kafka.messaging"], kafka.Resources.Select(r => r.ResourceKey));
        Assert.Empty(kafka.Config);
    }

    [Fact]
    public async Task ResolvesReferencesAndDeploysAllWorkloads()
    {
        var plan = await _h.PlanAsync(images: Images);
        var result = await _h.Engine.ApplyAsync(plan);

        var ctx = Assert.Single(_h.Runtime.ApplyCalls);
        var w = Assert.Single(ctx.Workloads);
        Assert.Equal("Host=pg;Token=topsecret-123", w.EnvironmentVariables["DB"]);
        Assert.Equal("redis:6379", w.EnvironmentVariables["CACHE"]);
        Assert.Equal("kafka:9092", w.EnvironmentVariables["KAFKA"]);
        Assert.Equal("fast", w.EnvironmentVariables["MODE"]);
        Assert.Equal(["DB"], w.SecretKeys);
        Assert.Same(plan.Defaults, ctx.Defaults);

        Assert.Equal("shop-dev", result.Namespace);
        Assert.Equal(["Deployment/shop"], result.Applied);
        Assert.Equal(["Deployment/old"], result.Pruned);

        Assert.Contains("@resource(", plan.Workloads[0].EnvironmentVariables["DB"]);
    }

    [Fact]
    public async Task ResultDoesNotCarryResolvedValues()
    {
        var plan = await _h.PlanAsync(images: Images);
        var result = await _h.Engine.ApplyAsync(plan);

        var serialized = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("topsecret", serialized);
    }

    [Fact]
    public async Task MissingImageFailsBeforeAnything()
    {
        var plan = await _h.PlanAsync();

        var ex = await Assert.ThrowsAsync<DeskribeException>(() => _h.Engine.ApplyAsync(plan));

        Assert.Contains("'shop'", ex.Message);
        Assert.Empty(_h.PgProv.ApplyCalls);
        Assert.Empty(_h.Runtime.ApplyCalls);
    }

    [Fact]
    public async Task UnresolvedReferencesFailAndAreAllListed()
    {
        _h.PgProv.Outputs.Remove("postgres");
        _h.KafkaProv.Outputs.Clear();
        var plan = await _h.PlanAsync(images: Images);

        var ex = await Assert.ThrowsAsync<DeskribeException>(() => _h.Engine.ApplyAsync(plan));

        Assert.Contains("@resource(postgres).connectionString", ex.Message);
        Assert.Contains("@resource(kafka.messaging).endpoint", ex.Message);
        Assert.Empty(_h.Runtime.ApplyCalls);
    }

    [Fact]
    public async Task ExternalSecretsMayLeaveSecretReferencesUnresolved()
    {
        _h.WriteEnv("dev", """{ "defaults": { "secretsStrategy": "external-secrets", "externalSecretsStore": "vault" } }""");
        _h.PgProv.Outputs.Remove("postgres");
        var plan = await _h.PlanAsync(images: Images);

        await _h.Engine.ApplyAsync(plan);

        var w = Assert.Single(Assert.Single(_h.Runtime.ApplyCalls).Workloads);
        Assert.Equal("@resource(postgres).connectionString", w.EnvironmentVariables["DB"]);
        Assert.Equal("redis:6379", w.EnvironmentVariables["CACHE"]);
    }

    [Fact]
    public async Task ExternalSecretsStillRequiresNonSecretReferences()
    {
        _h.WriteEnv("dev", """{ "defaults": { "secretsStrategy": "external-secrets" } }""");
        _h.PgProv.Outputs.Remove("redis");
        var plan = await _h.PlanAsync(images: Images);

        var ex = await Assert.ThrowsAsync<DeskribeException>(() => _h.Engine.ApplyAsync(plan));
        Assert.Contains("@resource(redis).endpoint", ex.Message);
        Assert.Empty(_h.Runtime.ApplyCalls);
    }

    [Fact]
    public async Task OpaqueStrategyRequiresSecretReferences()
    {
        _h.PgProv.Outputs.Remove("postgres");
        var plan = await _h.PlanAsync(images: Images);

        await Assert.ThrowsAsync<DeskribeException>(() => _h.Engine.ApplyAsync(plan));
        Assert.Empty(_h.Runtime.ApplyCalls);
    }

    [Fact]
    public async Task FailedProvisionerThrowsWithItsErrors()
    {
        _h.KafkaProv.ApplyErrors.Add("quota exceeded");
        var plan = await _h.PlanAsync(images: Images);

        var ex = await Assert.ThrowsAsync<DeskribeException>(() => _h.Engine.ApplyAsync(plan));

        Assert.Contains("kafka-prov", ex.Message);
        Assert.Contains("quota exceeded", ex.Message);
        Assert.Empty(_h.Runtime.ApplyCalls);
    }

    [Fact]
    public async Task MissingRuntimePluginIsAnError()
    {
        var plan = await _h.PlanAsync(images: Images);
        var other = plan with { Platform = plan.Platform with { Runtime = new() { Name = "nomad" } } };

        var ex = await Assert.ThrowsAsync<DeskribeException>(() => _h.Engine.ApplyAsync(other));

        Assert.Contains("nomad", ex.Message);
        Assert.Empty(_h.PgProv.ApplyCalls);
    }

    [Fact]
    public async Task MultipleWorkloadsAreAllDeployed()
    {
        _h.WriteManifest("""
            {
              "name": "shop",
              "resources": [ { "type": "redis" } ],
              "services": [
                { "name": "api", "env": { "CACHE": "@resource(redis).endpoint" } },
                { "name": "worker", "env": { "CACHE": "@resource(redis).endpoint" } }
              ]
            }
            """);
        var plan = await _h.PlanAsync(images: new() { ["api"] = "a:1", ["worker"] = "w:1" });

        var result = await _h.Engine.ApplyAsync(plan);

        var ctx = Assert.Single(_h.Runtime.ApplyCalls);
        Assert.Equal(["shop-api", "shop-worker"], ctx.Workloads.Select(w => w.Name));
        Assert.All(ctx.Workloads, w => Assert.Equal("redis:6379", w.EnvironmentVariables["CACHE"]));
        Assert.Equal(2, result.Applied.Count);
    }

    [Fact]
    public async Task MultiServiceWithMissingImageNamesTheWorkload()
    {
        _h.WriteManifest("""
            { "name": "shop", "services": [ { "name": "api" }, { "name": "worker" } ] }
            """);
        var plan = await _h.PlanAsync(images: new() { ["api"] = "a:1" });

        var ex = await Assert.ThrowsAsync<DeskribeException>(() => _h.Engine.ApplyAsync(plan));
        Assert.Contains("'shop-worker'", ex.Message);
        Assert.DoesNotContain("'shop-api'", ex.Message);
    }
}
