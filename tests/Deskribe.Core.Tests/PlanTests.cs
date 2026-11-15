using Deskribe.Core.Tests.Support;

namespace Deskribe.Core.Tests;

public class PlanTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task PlanThrowsValidationExceptionWithAllErrors()
    {
        _h.WriteManifest("""{ "name": "Shop", "resources": [ { "type": "postgres" }, { "type": "postgres" } ] }""");

        var ex = await Assert.ThrowsAsync<DeskribeValidationException>(() => _h.PlanAsync());

        Assert.False(ex.Result.IsValid);
        Assert.True(ex.Result.Errors.Count >= 2);
    }

    [Fact]
    public async Task PlanThrowsValidationExceptionForUnknownEnvironment()
    {
        _h.WriteManifest(ValidationTests.ValidManifest);
        var ex = await Assert.ThrowsAsync<DeskribeValidationException>(() => _h.PlanAsync("qa"));
        Assert.Contains(ex.Result.Errors, e => e.Contains("'qa'"));
    }

    [Fact]
    public async Task PlanCarriesMergedDefaultsEverywhere()
    {
        _h.WriteEnv("dev", """{ "defaults": { "region": "northeurope", "namespacePattern": "{team}-{app}-{env}", "secretsStrategy": "external-secrets", "externalSecretsStore": "vault" } }""");
        _h.WriteManifest(ValidationTests.ValidManifest);

        var plan = await _h.PlanAsync();

        Assert.Equal("northeurope", plan.Defaults.Region);
        Assert.Equal("payments-shop-dev", plan.Namespace);
        Assert.Same(plan.Defaults, _h.Postgres.PlanContexts.Single().Defaults);
        var w = Assert.Single(plan.Workloads);
        Assert.Equal("payments-shop-dev", w.Namespace);
        Assert.Equal("external-secrets", w.SecretsStrategy);
        Assert.Equal("vault", w.ExternalSecretsStore);
    }

    [Fact]
    public async Task SingleServiceWorkloadNamedAfterAppWithOverrides()
    {
        _h.WriteManifest(ValidationTests.ValidManifest);

        var dev = Assert.Single((await _h.PlanAsync("dev")).Workloads);
        var prod = Assert.Single((await _h.PlanAsync("prod")).Workloads);

        Assert.Equal("shop", dev.Name);
        Assert.Equal(1, dev.Replicas);
        Assert.Equal(4, prod.Replicas);
        Assert.Equal("250m", prod.Cpu);
    }

    [Fact]
    public async Task MultiServiceProducesOneWorkloadPerServiceSharingNamespace()
    {
        _h.WriteManifest("""
            {
              "name": "shop",
              "resources": [ { "type": "postgres" } ],
              "services": [
                { "name": "api", "env": { "DB": "@resource(postgres).connectionString" } },
                { "name": "worker", "env": { "HOST": "@resource(postgres).host" }, "overrides": { "dev": { "replicas": 3 } } }
              ]
            }
            """);

        var plan = await _h.PlanAsync(images: new() { ["api"] = "api:1", ["shop-worker"] = "worker:1" });

        Assert.Equal(["shop-api", "shop-worker"], plan.Workloads.Select(w => w.Name));
        Assert.All(plan.Workloads, w => Assert.Equal("shop-dev", w.Namespace));
        Assert.Equal("api:1", plan.Workloads[0].Image);
        Assert.Equal("worker:1", plan.Workloads[1].Image);
        Assert.Equal(1, plan.Workloads[0].Replicas);
        Assert.Equal(3, plan.Workloads[1].Replicas);
        Assert.Equal("api", plan.Workloads[0].ServiceName);
    }

    [Fact]
    public async Task ImageIsNullAtPlanTimeWhenNotSupplied()
    {
        _h.WriteManifest(ValidationTests.ValidManifest);
        var plan = await _h.PlanAsync();
        Assert.Null(plan.Workloads[0].Image);
    }

    [Fact]
    public async Task BindingsAndSecretClassification()
    {
        _h.WriteManifest("""
            {
              "name": "shop",
              "resources": [ { "type": "postgres", "name": "orders-db" }, { "type": "redis" } ],
              "services": [ {
                "env": {
                  "DB": "@resource(orders-db).connectionString",
                  "MIXED": "redis://:@resource(redis).password@@resource(redis).endpoint",
                  "CACHE": "@resource(redis).endpoint",
                  "MODE": "fast"
                }
              } ]
            }
            """);

        var plan = await _h.PlanAsync();
        var w = plan.Workloads.Single();

        Assert.Equal(["DB", "MIXED"], w.SecretKeys.Order());
        Assert.Equal(4, plan.Bindings.Count);
        var db = plan.Bindings.Single(b => b.EnvVar == "DB");
        Assert.Equal(("shop", "orders-db", "postgres", "connectionString", true),
            (db.Workload, db.ResourceKey, db.ResourceType, db.Output, db.Sensitive));
        Assert.False(plan.Bindings.Single(b => b.EnvVar == "CACHE").Sensitive);
        Assert.DoesNotContain(plan.Bindings, b => b.EnvVar == "MODE");
        Assert.Equal("fast", w.EnvironmentVariables["MODE"]);
    }

    [Fact]
    public async Task ResourcePlansAreKeyed()
    {
        _h.WriteManifest("""
            { "name": "shop", "resources": [ { "type": "postgres", "name": "a" }, { "type": "kafka.messaging" } ] }
            """);
        var plan = await _h.PlanAsync();
        Assert.Equal(["a", "kafka.messaging"], plan.ResourcePlans.Select(r => r.ResourceKey));
    }

    [Fact]
    public async Task ProviderReturningWrongKeyIsAPluginBug()
    {
        _h.Postgres.KeyOverride = _ => "wrong";
        _h.WriteManifest(ValidationTests.ValidManifest);

        var ex = await Assert.ThrowsAsync<DeskribeException>(() => _h.PlanAsync());
        Assert.Contains("plugin bug", ex.Message);
    }

    [Fact]
    public async Task ValidationWarningsGoIntoPlan()
    {
        _h.WriteBase(EngineHarness.DefaultBase.TrimEnd().TrimEnd('}') + """, "policies": { "enforceTLS": true } }""");
        _h.WriteManifest(ValidationTests.ValidManifest);

        var plan = await _h.PlanAsync();

        Assert.Contains(plan.Warnings, w => w.Contains("enforceTLS"));
    }
}
