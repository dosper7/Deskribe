using System.Text.Json;
using Deskribe.Plugins.Provisioner.PlatformOutput;
using Deskribe.Plugins.Provisioner.Pulumi;
using Deskribe.Plugins.Provisioner.Static;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using static Deskribe.Plugins.Tests.T;

namespace Deskribe.Plugins.Tests;

public class PulumiProvisionerTests
{
    private sealed class FakeRunner : IPulumiStackRunner
    {
        public IReadOnlyDictionary<string, object?> Outputs { get; set; } = new Dictionary<string, object?>();
        public Dictionary<string, string>? Config;
        public string? Stack, Dir;
        public int Ups, Destroys;
        public Exception? Throw;

        public Task<IReadOnlyDictionary<string, object?>> UpAsync(string projectDir, string stackName, IReadOnlyDictionary<string, string> config, CancellationToken ct)
        {
            Ups++; Dir = projectDir; Stack = stackName; Config = new(config);
            if (Throw is not null) throw Throw;
            return Task.FromResult(Outputs);
        }

        public Task DestroyAsync(string projectDir, string stackName, CancellationToken ct)
        {
            Destroys++; Stack = stackName;
            if (Throw is not null) throw Throw;
            return Task.CompletedTask;
        }
    }

    private static readonly object Cfg = new { projectDir = "./infra" };

    [Fact]
    public async Task Apply_RunsOnce_WithConfigMapping()
    {
        var runner = new FakeRunner();
        var ctx = Ctx([Plan("orders-db", "postgres", ("version", "16"), ("ha", true)), Plan("cache", "redis", ("size", "s"))], Cfg);
        var result = await new PulumiProvisioner(runner).ApplyAsync(ctx);

        Assert.True(result.Success);
        Assert.Equal(1, runner.Ups);
        Assert.Equal("./infra", runner.Dir);
        Assert.Equal("myapp-dev", runner.Stack);
        Assert.Equal("myapp", runner.Config!["appName"]);
        Assert.Equal("dev", runner.Config["environment"]);
        Assert.Equal("northeurope", runner.Config["region"]);
        Assert.Equal("payments", runner.Config["team"]);
        Assert.Equal("16", runner.Config["orders-db:version"]);
        Assert.Equal("true", runner.Config["orders-db:ha"]);
        Assert.Equal("s", runner.Config["cache:size"]);
    }

    [Fact]
    public async Task Apply_NullTeam_IsOmitted()
    {
        var runner = new FakeRunner();
        await new PulumiProvisioner(runner).ApplyAsync(Ctx([], Cfg, team: null));
        Assert.DoesNotContain("team", runner.Config!.Keys);
    }

    [Fact]
    public async Task Apply_ReadsDottedAndUnderscoreOutputs_OnlyForOwnResources()
    {
        var runner = new FakeRunner
        {
            Outputs = new Dictionary<string, object?>
            {
                ["orders-db.connectionString"] = "cs",
                ["orders_db_host"] = "h",
                ["kafka.messaging.bootstrapServers"] = "b:9092",
                ["kafka_messaging_endpoint"] = "b:9092",
                ["other.thing"] = "nope",
                ["cache.port"] = 6379,
                ["cache.empty"] = null,
                ["orders-db."] = "bad"
            }
        };
        var ctx = Ctx([Plan("orders-db", "postgres"), Plan("kafka.messaging", "kafka.messaging"), Plan("cache", "redis")], Cfg);
        var result = await new PulumiProvisioner(runner).ApplyAsync(ctx);

        Assert.Equal(["cs", "h"], new[] { result.ResourceOutputs["orders-db"]["connectionString"], result.ResourceOutputs["orders-db"]["host"] });
        Assert.Equal(2, result.ResourceOutputs["orders-db"].Count);
        Assert.Equal("b:9092", result.ResourceOutputs["kafka.messaging"]["bootstrapServers"]);
        Assert.Equal("b:9092", result.ResourceOutputs["kafka.messaging"]["endpoint"]);
        Assert.Equal("6379", result.ResourceOutputs["cache"]["port"]);
        Assert.Single(result.ResourceOutputs["cache"]);
        Assert.DoesNotContain("other", result.ResourceOutputs.Keys);
    }

    [Fact]
    public async Task Apply_NoMatchingOutputs_YieldsNoOutputs_NoFallback()
    {
        var result = await new PulumiProvisioner(new FakeRunner()).ApplyAsync(Ctx([Plan("db", "postgres")], Cfg));
        Assert.True(result.Success);
        Assert.Empty(result.ResourceOutputs);
    }

    [Fact]
    public void MapOutputs_LongestKeyPrefixWins()
    {
        var map = PulumiProvisioner.MapOutputs(
            [Plan("a", "x"), Plan("a-b", "x")],
            new Dictionary<string, object?> { ["a_b_host"] = "h", ["a_host"] = "h2" });
        Assert.Equal("h", map["a-b"]["host"]);
        Assert.Equal("h2", map["a"]["host"]);
        Assert.Single(map["a"]);
    }

    [Fact]
    public async Task Apply_MissingProjectDir_Fails()
    {
        var runner = new FakeRunner();
        var result = await new PulumiProvisioner(runner).ApplyAsync(Ctx([Plan("db", "postgres")]));
        Assert.False(result.Success);
        Assert.Equal(0, runner.Ups);
    }

    [Fact]
    public async Task Apply_RunnerFailure_IsReported()
    {
        var result = await new PulumiProvisioner(new FakeRunner { Throw = new InvalidOperationException("nope") }).ApplyAsync(Ctx([], Cfg));
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("nope"));
    }

    [Fact]
    public async Task Apply_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new FakeRunner { Throw = new OperationCanceledException(cts.Token) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PulumiProvisioner(runner).ApplyAsync(Ctx([], Cfg), cts.Token));
    }

    [Fact]
    public async Task Destroy_DestroysStack()
    {
        var runner = new FakeRunner();
        var result = await new PulumiProvisioner(runner).DestroyAsync(Ctx([], Cfg));
        Assert.True(result.Success);
        Assert.Equal(1, runner.Destroys);
        Assert.Equal("myapp-dev", runner.Stack);
        Assert.False((await new PulumiProvisioner(runner).DestroyAsync(Ctx([]))).Success);
    }

    [Fact]
    public void Name_IsPulumi() => Assert.Equal("pulumi", new PulumiProvisioner().Name);
}

public class PlatformOutputProvisionerTests
{
    private static readonly object Cfg = new
    {
        basePath = "{team}/{app}/{env}",
        modules = new Dictionary<string, object>
        {
            ["postgres"] = new
            {
                moduleName = "pg",
                fileName = "terraform.tfvars.json",
                mappings = new Dictionary<string, string>
                {
                    ["db_name"] = "{app}-db",
                    ["sku_name"] = "{size:map(s=B1,m=D2)}",
                    ["pg_version"] = "{version}",
                    ["ha_enabled"] = "{ha}",
                    ["label"] = "ha={ha}",
                    ["team"] = "{team}",
                    ["unknown"] = "{nope}"
                }
            },
            ["redis"] = new { moduleName = "redis", mappings = new Dictionary<string, string> { ["name"] = "{app}-redis" } }
        }
    };

    private readonly PlatformOutputProvisioner _p = new();

    [Fact]
    public async Task Generate_EmitsTypedValues_ForSingleTokenTemplates()
    {
        var pg = Plan("db", "postgres", ("version", "16"), ("size", "m"), ("ha", false), ("port", 5432));
        var result = await _p.GenerateArtifactsAsync(Ctx([pg], Cfg), "out");

        Assert.True(result.Success, string.Join(";", result.Errors));
        var file = Assert.Single(result.Files);
        Assert.Equal(Path.Combine("out", "payments/myapp/dev", "pg", "terraform.tfvars.json"), file.Path);
        var root = JsonDocument.Parse(file.Content).RootElement;
        Assert.Equal(JsonValueKind.False, root.GetProperty("ha_enabled").ValueKind);
        Assert.Equal("ha=false", root.GetProperty("label").GetString());
        Assert.Equal("16", root.GetProperty("pg_version").GetString());
        Assert.Equal("D2", root.GetProperty("sku_name").GetString());
        Assert.Equal("myapp-db", root.GetProperty("db_name").GetString());
        Assert.Equal("{nope}", root.GetProperty("unknown").GetString());
    }

    [Fact]
    public void ResolveTemplate_NumbersStayNumbers()
    {
        var tokens = new Dictionary<string, object?> { ["n"] = 5, ["j"] = JsonSerializer.SerializeToElement(7), ["b"] = JsonSerializer.SerializeToElement(true) };
        Assert.Equal(5, PlatformOutputProvisioner.ResolveTemplate("{n}", tokens));
        Assert.Equal(7, ((JsonElement)PlatformOutputProvisioner.ResolveTemplate("{j}", tokens)!).GetInt32());
        Assert.Equal(true, PlatformOutputProvisioner.ResolveTemplate("{b}", tokens));
        Assert.Equal("n=5", PlatformOutputProvisioner.ResolveTemplate("n={n}", tokens));
    }

    [Fact]
    public async Task Generate_NullTeam_UsesDefault_AndOnlyOwnResources()
    {
        var result = await _p.GenerateArtifactsAsync(Ctx([Plan("cache", "redis")], Cfg, team: null), "out");
        var file = Assert.Single(result.Files);
        Assert.Equal(Path.Combine("out", "default/myapp/dev", "redis", "terraform.tfvars.json"), file.Path);
    }

    [Fact]
    public async Task Generate_SameTypeTwice_DoesNotCollide()
    {
        var result = await _p.GenerateArtifactsAsync(Ctx([Plan("a", "redis"), Plan("b", "redis")], Cfg), "out");
        Assert.Equal(2, result.Files.Select(f => f.Path).Distinct().Count());
    }

    [Fact]
    public async Task Generate_WithoutConfig_Fails()
    {
        Assert.False((await _p.GenerateArtifactsAsync(Ctx([Plan("a", "redis")]), "out")).Success);
    }

    [Fact]
    public async Task ApplyAndDestroy_SucceedWithNoOutputsOrErrors()
    {
        var ctx = Ctx([Plan("a", "redis")], Cfg);
        foreach (var r in new[] { await _p.ApplyAsync(ctx), await _p.DestroyAsync(ctx) })
        {
            Assert.True(r.Success);
            Assert.Empty(r.Errors);
            Assert.Empty(r.ResourceOutputs);
        }
    }

    [Fact]
    public async Task ExampleConfig_Parses()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "../../../../../examples/platform-config/base.json");
        Assert.True(File.Exists(path), path);
        var platform = JsonSerializer.Deserialize<PlatformConfig>(File.ReadAllText(path))!;
        var ctx = Ctx([Plan("postgres", "postgres", ("size", "m"), ("version", "16"), ("ha", true))],
            platform.ProvisionerConfigs["platform-output"], platform: platform);
        var result = await _p.GenerateArtifactsAsync(ctx, "out");
        var root = JsonDocument.Parse(result.Files.Single().Content).RootElement;
        Assert.Equal(JsonValueKind.True, root.GetProperty("ha_enabled").ValueKind);
        Assert.Equal("GP_Standard_D2s_v3", root.GetProperty("sku_name").GetString());
    }
}

public class StaticProvisionerTests
{
    private static readonly object Cfg = new
    {
        outputs = new Dictionary<string, object>
        {
            ["kafka.messaging"] = new { bootstrapServers = "kafka.{region}.corp:9092", endpoint = "kafka.{region}.corp:9092" },
            ["cache"] = new { endpoint = "redis-{env}-{app}.corp:6379", who = "{team}/{key}" },
            ["redis"] = new { endpoint = "type-level", host = "shared" }
        },
        environments = new Dictionary<string, object>
        {
            ["dev"] = new { outputs = new Dictionary<string, object> { ["kafka.messaging"] = new { bootstrapServers = "dev-kafka:9092" } } },
            ["prod"] = new { outputs = new Dictionary<string, object> { ["kafka.messaging"] = new { bootstrapServers = "prod-kafka:9092" } } }
        }
    };

    private readonly StaticProvisioner _p = new(name => name == "REDIS_PW" ? "s3cret" : null);

    [Fact]
    public async Task Apply_TemplatesOutputs_KeyedByResourceKey_OnlyOwnResources()
    {
        var result = await _p.ApplyAsync(Ctx([Plan("cache", "redis"), Plan("other", "postgres")], Cfg));

        Assert.True(result.Success, string.Join(";", result.Errors));
        Assert.Equal(["cache"], result.ResourceOutputs.Keys);
        var cache = result.ResourceOutputs["cache"];
        Assert.Equal("redis-dev-myapp.corp:6379", cache["endpoint"]);
        Assert.Equal("payments/cache", cache["who"]);
        Assert.Equal("shared", cache["host"]);
    }

    [Fact]
    public async Task Apply_KeyWinsOverType()
    {
        var result = await _p.ApplyAsync(Ctx([Plan("redis", "redis"), Plan("cache", "redis")], Cfg));
        Assert.Equal("type-level", result.ResourceOutputs["redis"]["endpoint"]);
        Assert.Equal("redis-dev-myapp.corp:6379", result.ResourceOutputs["cache"]["endpoint"]);
    }

    [Fact]
    public async Task Apply_EnvironmentOverrides()
    {
        var result = await _p.ApplyAsync(Ctx([Plan("kafka.messaging", "kafka.messaging")], Cfg));
        var kafka = result.ResourceOutputs["kafka.messaging"];
        Assert.Equal("dev-kafka:9092", kafka["bootstrapServers"]);
        Assert.Equal("kafka.northeurope.corp:9092", kafka["endpoint"]);
    }

    [Fact]
    public async Task Apply_EnvLookup_ReadsProcessEnvironment()
    {
        var cfg = new { outputs = new { redis = new { connectionString = "env:REDIS_PW" } } };
        var result = await _p.ApplyAsync(Ctx([Plan("redis", "redis")], cfg));
        Assert.Equal("s3cret", result.ResourceOutputs["redis"]["connectionString"]);
    }

    [Fact]
    public async Task Apply_MissingEnvVariable_FailsNamingVariable_WithoutOutputs()
    {
        var cfg = new { outputs = new { redis = new { connectionString = "env:MISSING_VAR" } } };
        var result = await _p.ApplyAsync(Ctx([Plan("redis", "redis")], cfg));
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("MISSING_VAR"));
        Assert.Empty(result.ResourceOutputs);
    }

    [Fact]
    public async Task Apply_RealProcessEnvironment_IsUsedByDefault()
    {
        Environment.SetEnvironmentVariable("DESKRIBE_TEST_STATIC", "v");
        try
        {
            var cfg = new { outputs = new { redis = new { connectionString = "env:DESKRIBE_TEST_STATIC" } } };
            var result = await new StaticProvisioner().ApplyAsync(Ctx([Plan("redis", "redis")], cfg));
            Assert.Equal("v", result.ResourceOutputs["redis"]["connectionString"]);
        }
        finally { Environment.SetEnvironmentVariable("DESKRIBE_TEST_STATIC", null); }
    }

    [Fact]
    public async Task Apply_ResourceWithoutDeclaredOutputs_IsOmitted()
    {
        var result = await _p.ApplyAsync(Ctx([Plan("db", "postgres")], Cfg));
        Assert.True(result.Success);
        Assert.Empty(result.ResourceOutputs);
    }

    [Fact]
    public async Task Apply_MalformedConfig_FailsWithoutThrowing()
    {
        Assert.False((await _p.ApplyAsync(Ctx([Plan("a", "redis")], new { outputs = "x" }))).Success);
        Assert.False((await _p.ApplyAsync(Ctx([Plan("a", "redis")], new { outputs = new { a = new { x = 1 } } }))).Success);
    }

    [Fact]
    public async Task Generate_ProducesNoFiles_AndDestroyIsNoOp()
    {
        var ctx = Ctx([Plan("cache", "redis")], Cfg);
        var art = await _p.GenerateArtifactsAsync(ctx, "out");
        Assert.True(art.Success);
        Assert.Empty(art.Files);
        var d = await _p.DestroyAsync(ctx);
        Assert.True(d.Success);
        Assert.Empty(d.ResourceOutputs);
    }

    [Fact]
    public void PluginRegistersProvisioner()
    {
        var attr = typeof(StaticPlugin).GetCustomAttributes(typeof(DeskribePluginAttribute), false).Cast<DeskribePluginAttribute>().Single();
        Assert.Equal("static", attr.Name);
        Assert.Equal("static", new StaticProvisioner().Name);
    }
}
