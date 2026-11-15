using Deskribe.Core.Tests.Support;
using Deskribe.Sdk.Models;

namespace Deskribe.Core.Tests;

public class ValidationTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public const string ValidManifest = """
        {
          "name": "shop",
          "team": "payments",
          "resources": [ { "type": "postgres" }, { "type": "redis" } ],
          "services": [
            {
              "env": {
                "DB": "@resource(postgres).connectionString",
                "CACHE": "@resource(redis).endpoint",
                "MODE": "fast"
              },
              "overrides": { "prod": { "replicas": 4 } }
            }
          ]
        }
        """;

    public void Dispose() => _h.Dispose();

    private static void AssertError(ValidationResult result, params string[] fragments)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => fragments.All(f => e.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    private Task<ValidationResult> ValidateManifest(string manifest, string env = "dev")
    {
        _h.WriteManifest(manifest);
        return _h.ValidateAsync(env);
    }

    [Fact]
    public async Task ValidManifestPasses()
    {
        var result = await ValidateManifest(ValidManifest);
        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task MalformedManifestJsonIsReadableError()
    {
        var result = await ValidateManifest("{ \"name\": \"shop\", ");
        AssertError(result, "deskribe.json", "invalid");
        Assert.DoesNotContain(result.Errors, e => e.Contains("   at "));
    }

    [Fact]
    public async Task UnknownManifestPropertyIsRejected()
    {
        var result = await ValidateManifest("""{ "name": "shop", "servics": [] }""");
        AssertError(result, "deskribe.json", "servics");
    }

    [Fact]
    public async Task UnknownServicePropertyIsRejected()
    {
        var result = await ValidateManifest("""{ "name": "shop", "services": [ { "envs": {} } ] }""");
        AssertError(result, "deskribe.json", "envs");
    }

    [Fact]
    public async Task MalformedPlatformConfigIsReadableError()
    {
        _h.WriteBase("{ not json");
        var result = await ValidateManifest(ValidManifest);
        AssertError(result, "base.json", "invalid");
    }

    [Fact]
    public async Task UnknownEnvironmentInDirectoryConfigListsKnownEnvironments()
    {
        var result = await ValidateManifest(ValidManifest, "prdo");
        AssertError(result, "prdo", "dev, prod");
    }

    [Fact]
    public async Task UnknownEnvironmentInSingleFileConfigListsKnownEnvironments()
    {
        _h.WriteManifest(ValidManifest);
        var platform = _h.WriteFile("platform.json", """
            {
              "runtime": { "name": "kubernetes" },
              "provisioners": { "postgres": "pg-prov", "redis": "pg-prov" },
              "environments": { "dev": {}, "prod": {} }
            }
            """);

        var bad = await _h.Engine.ValidateAsync(_h.ManifestPath, platform, "staging");
        AssertError(bad, "staging", "dev, prod");

        var good = await _h.Engine.ValidateAsync(_h.ManifestPath, platform, "prod");
        Assert.True(good.IsValid, string.Join("\n", good.Errors));
    }

    [Fact]
    public async Task ReportsAllLoadErrorsTogether()
    {
        _h.WriteManifest("{");
        var result = await _h.ValidateAsync("nope");
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task InvalidAppTeamAndNamespaceNames()
    {
        _h.WriteEnv("dev", """{ "defaults": { "namespacePattern": "{team}_{app}" } }""");
        var result = await ValidateManifest("""{ "name": "Shop", "team": "Pay_Team" }""");

        AssertError(result, "App name 'Shop'", "DNS-1123");
        AssertError(result, "Team 'Pay_Team'");
        AssertError(result, "Namespace");
    }

    [Fact]
    public async Task WorkloadNameTooLongIsRejected()
    {
        var longName = new string('s', 60);
        var result = await ValidateManifest($$"""
            { "name": "shop", "services": [ { "name": "{{longName}}" }, { "name": "b" } ] }
            """);
        AssertError(result, $"shop-{longName}");
    }

    [Fact]
    public async Task DuplicateResourceKeys()
    {
        var result = await ValidateManifest("""
            { "name": "shop", "resources": [ { "type": "postgres" }, { "type": "postgres" } ] }
            """);
        AssertError(result, "'postgres'", "more than once");
    }

    [Fact]
    public async Task NamedResourcesOfSameTypeAreAllowed()
    {
        var result = await ValidateManifest("""
            {
              "name": "shop",
              "resources": [ { "type": "postgres", "name": "orders-db" }, { "type": "postgres", "name": "users-db" } ],
              "services": [ { "env": { "A": "@resource(orders-db).host", "B": "@resource(users-db).host" } } ]
            }
            """);
        Assert.True(result.IsValid, string.Join("\n", result.Errors));
    }

    [Fact]
    public async Task ResourceTypeWithoutProvider()
    {
        _h.WriteBase(EngineHarness.DefaultBase.Replace("\"redis\": \"pg-prov\"", "\"redis\": \"pg-prov\", \"mongo\": \"pg-prov\""));
        var result = await ValidateManifest("""{ "name": "shop", "resources": [ { "type": "mongo" } ] }""");
        AssertError(result, "'mongo'", "no registered resource provider");
    }

    [Fact]
    public async Task ResourceTypeNotMappedToProvisioner()
    {
        _h.WriteBase("""{ "runtime": { "name": "kubernetes" }, "provisioners": { "postgres": "pg-prov" } }""");
        var result = await ValidateManifest("""{ "name": "shop", "resources": [ { "type": "redis" } ] }""");
        AssertError(result, "'redis'", "not mapped");
    }

    [Fact]
    public async Task MappedProvisionerNotRegistered()
    {
        _h.WriteBase("""{ "runtime": { "name": "kubernetes" }, "provisioners": { "postgres": "pulumi" } }""");
        var result = await ValidateManifest("""{ "name": "shop", "resources": [ { "type": "postgres" } ] }""");
        AssertError(result, "'pulumi'", "not registered");
    }

    [Fact]
    public async Task RuntimeWithoutPlugin()
    {
        _h.WriteBase("""{ "runtime": { "name": "nomad" } }""");
        var result = await ValidateManifest("""{ "name": "shop" }""");
        AssertError(result, "'nomad'", "runtime plugin");
    }

    [Fact]
    public async Task MalformedUnknownKeyAndUnknownOutputReferences()
    {
        var result = await ValidateManifest("""
            {
              "name": "shop",
              "resources": [ { "type": "postgres" } ],
              "services": [ {
                "env": {
                  "A": "@resource(postgres)",
                  "B": "@resource(mysql).host",
                  "C": "@resource(postgres).hostname"
                }
              } ]
            }
            """);

        AssertError(result, "'A'", "malformed");
        AssertError(result, "'B'", "unknown resource 'mysql'");
        AssertError(result, "'C'", "'hostname'");
        Assert.Equal(3, result.Errors.Count);
    }

    [Fact]
    public async Task MultipleServicesNeedUniqueNames()
    {
        var result = await ValidateManifest("""
            { "name": "shop", "services": [ { "name": "api" }, { "name": "api" }, {} ] }
            """);
        AssertError(result, "'api'", "more than once");
        AssertError(result, "Service #3", "no name");
    }

    [Fact]
    public async Task OverrideForUnknownEnvironmentIsRejected()
    {
        var result = await ValidateManifest("""
            { "name": "shop", "services": [ { "overrides": { "prdo": { "replicas": 2 } } } ] }
            """);
        AssertError(result, "'prdo'", "dev, prod");
    }

    [Fact]
    public async Task ReplicasBelowOne()
    {
        var result = await ValidateManifest("""
            { "name": "shop", "services": [ { "overrides": { "dev": { "replicas": 0 } } } ] }
            """);
        AssertError(result, "replicas must be at least 1");
    }

    [Fact]
    public async Task InvalidCpuAndMemoryQuantities()
    {
        var result = await ValidateManifest("""
            { "name": "shop", "services": [ { "overrides": { "dev": { "cpu": "fast", "memory": "1GB" } } } ] }
            """);
        AssertError(result, "cpu 'fast'");
        AssertError(result, "memory '1GB'");
    }

    [Fact]
    public async Task ValuesAbovePolicyLimits()
    {
        _h.WriteBase(EngineHarness.DefaultBase.TrimEnd().TrimEnd('}') +
                     """, "policies": { "limits": { "maxReplicas": 3, "maxCpu": "1", "maxMemory": "1Gi" } } }""");
        var result = await ValidateManifest("""
            { "name": "shop", "services": [ { "overrides": { "dev": { "replicas": 5, "cpu": "1500m", "memory": "2Gi" } } } ] }
            """);

        AssertError(result, "replicas 5", "limit");
        AssertError(result, "cpu '1500m'", "limit");
        AssertError(result, "memory '2Gi'", "limit");
    }

    [Fact]
    public async Task LimitsOnlyApplyToTargetEnvironment()
    {
        _h.WriteBase(EngineHarness.DefaultBase.TrimEnd().TrimEnd('}') +
                     """, "policies": { "limits": { "maxReplicas": 3 } } }""");
        var result = await ValidateManifest("""
            { "name": "shop", "services": [ { "overrides": { "prod": { "replicas": 9 } } } ] }
            """);
        Assert.True(result.IsValid, string.Join("\n", result.Errors));
    }

    [Fact]
    public async Task EnvironmentRegionMustBeAllowed()
    {
        _h.WriteBase(EngineHarness.DefaultBase.TrimEnd().TrimEnd('}') +
                     """, "policies": { "allowedRegions": ["westeurope"] } }""");
        _h.WriteEnv("prod", """{ "defaults": { "region": "eastus2" } }""");
        _h.WriteManifest("""{ "name": "shop" }""");

        Assert.True((await _h.ValidateAsync("dev")).IsValid);
        AssertError(await _h.ValidateAsync("prod"), "'eastus2'", "westeurope");
    }

    [Fact]
    public async Task EnvironmentSecretsStrategyMustBeKnown()
    {
        _h.WriteEnv("dev", """{ "defaults": { "secretsStrategy": "vault-agent" } }""");
        var result = await ValidateManifest("""{ "name": "shop" }""");
        AssertError(result, "'vault-agent'", "external-secrets");
    }

    [Fact]
    public async Task ProviderValidationErrorsAreIncluded()
    {
        _h.Postgres.ValidationErrors.Add("size 'xxl' is not allowed");
        var result = await ValidateManifest(ValidManifest);
        AssertError(result, "postgres", "size 'xxl'");
    }

    [Fact]
    public async Task EnforceTlsIsAWarningNotAnError()
    {
        _h.WriteBase(EngineHarness.DefaultBase.TrimEnd().TrimEnd('}') + """, "policies": { "enforceTLS": true } }""");
        var result = await ValidateManifest(ValidManifest);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("enforceTLS") && w.Contains("not enforced"));
    }

    [Fact]
    public async Task ReportsErrorsFromEveryGroup()
    {
        _h.WriteEnv("dev", """{ "defaults": { "secretsStrategy": "nope" } }""");
        var result = await ValidateManifest("""
            {
              "name": "Shop",
              "resources": [ { "type": "postgres" }, { "type": "postgres" } ],
              "services": [ { "env": { "X": "@resource(cache).host" }, "overrides": { "qa": {} } } ]
            }
            """);

        AssertError(result, "App name");
        AssertError(result, "more than once");
        AssertError(result, "unknown resource 'cache'");
        AssertError(result, "'qa'");
        AssertError(result, "'nope'");
    }
}
