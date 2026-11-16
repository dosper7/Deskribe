using Deskribe.Plugins.Resources.Kafka;
using Deskribe.Plugins.Resources.Postgres;
using Deskribe.Plugins.Resources.Redis;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using static Deskribe.Plugins.Tests.T;

namespace Deskribe.Plugins.Tests;

public class ResourceProviderTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly PostgresResourceProvider _pg = new();
    private readonly RedisResourceProvider _redis = new();
    private readonly KafkaResourceProvider _kafka = new();

    // ---- schemas
    [Fact]
    public void Schemas_DeclareOutputsHonestly()
    {
        var pg = _pg.GetSchema();
        Assert.Equal(["connectionString", "host", "port", "database"], pg.ProvidedOutputs);
        Assert.Equal(["connectionString"], pg.SensitiveOutputs);

        var redis = _redis.GetSchema();
        Assert.Equal(["connectionString", "endpoint", "host", "port"], redis.ProvidedOutputs);
        Assert.Equal(["connectionString"], redis.SensitiveOutputs);

        var kafka = _kafka.GetSchema();
        Assert.Equal(["bootstrapServers", "endpoint"], kafka.ProvidedOutputs);
        Assert.Empty(kafka.SensitiveOutputs);
    }

    // ---- planning
    [Fact]
    public async Task Postgres_Plan_UsesKeyAndMergedDefaults()
    {
        var plan = await _pg.PlanAsync(Res("postgres", "orders-db", "m"), Pctx(), Ct);
        Assert.Equal("orders-db", plan.ResourceKey);
        Assert.Equal("postgres", plan.ResourceType);
        Assert.Equal("northeurope", plan.Configuration["region"]);
        Assert.Equal(false, plan.Configuration["ha"]);
        Assert.Equal("m", plan.Configuration["size"]);
        Assert.Equal("16", plan.Configuration["version"]);
    }

    [Fact]
    public async Task Postgres_Plan_ResourceHaOverridesDefault()
    {
        var plan = await _pg.PlanAsync(Res("postgres", props: ("ha", true)), Pctx(), Ct);
        Assert.Equal("postgres", plan.ResourceKey);
        Assert.Equal(true, plan.Configuration["ha"]);
    }

    [Fact]
    public async Task Postgres_Plan_DefaultHaComesFromMergedDefaults()
    {
        var plan = await _pg.PlanAsync(Res("postgres"), Pctx(defaults: new PlatformDefaults { Ha = true }), Ct);
        Assert.Equal(true, plan.Configuration["ha"]);
    }

    [Fact]
    public async Task Postgres_CatalogConstraints_AndLegacyFallback()
    {
        var catalog = Catalog("postgres", new { validVersions = new[] { "18" }, defaultVersion = "18", port = 6543, validSizes = new[] { "s" } });
        var legacy = Catalog("postgres", new { validVersions = new[] { "18" }, defaultVersion = "18", port = 6543, validSizes = new[] { "s" } }, legacy: true);

        foreach (var platform in new[] { catalog, legacy })
        {
            var plan = await _pg.PlanAsync(Res("postgres"), Pctx(platform), Ct);
            Assert.Equal("18", plan.Configuration["version"]);
            Assert.Equal("6543", plan.Configuration["port"]);

            Assert.False((await _pg.ValidateAsync(Res("postgres", size: "m"), Vctx(platform), Ct)).IsValid);
            Assert.False((await _pg.ValidateAsync(Res("postgres", props: ("version", "16")), Vctx(platform), Ct)).IsValid);
            Assert.True((await _pg.ValidateAsync(Res("postgres", size: "s", props: ("version", "18")), Vctx(platform), Ct)).IsValid);
        }
    }

    [Fact]
    public async Task Postgres_CatalogWinsOverLegacy()
    {
        var platform = new PlatformConfig
        {
            Resources = Catalog("postgres", new { defaultVersion = "17" }).Resources,
            ProvisionerConfigs = Catalog("postgres", new { defaultVersion = "15", port = 1111 }, legacy: true).ProvisionerConfigs
        };
        var plan = await _pg.PlanAsync(Res("postgres"), Pctx(platform), Ct);
        Assert.Equal("17", plan.Configuration["version"]);
        Assert.Equal("1111", plan.Configuration["port"]);
    }

    [Fact]
    public async Task Redis_Plan_UsesKeyRegionAndCatalogPort()
    {
        var plan = await _redis.PlanAsync(Res("redis", "cache"), Pctx(Catalog("redis", new { port = 7000 })), Ct);
        Assert.Equal("cache", plan.ResourceKey);
        Assert.Equal("northeurope", plan.Configuration["region"]);
        Assert.Equal("7000", plan.Configuration["port"]);
        Assert.Equal(false, plan.Configuration["ha"]);
    }

    [Fact]
    public async Task Redis_Catalog_ValidSizes()
    {
        var platform = Catalog("redis", new { validSizes = new[] { "xs" } });
        Assert.False((await _redis.ValidateAsync(Res("redis", size: "m"), Vctx(platform), Ct)).IsValid);
        Assert.True((await _redis.ValidateAsync(Res("redis", size: "xs"), Vctx(platform), Ct)).IsValid);
    }

    [Fact]
    public async Task Kafka_Plan_UsesKeyAndRegion()
    {
        var resource = Res("kafka.messaging", "events", props: ("topics", new object[] { new { name = "a", owners = new[] { "t" } } }));
        var plan = await _kafka.PlanAsync(resource, Pctx(), Ct);
        Assert.Equal("events", plan.ResourceKey);
        Assert.Equal("northeurope", plan.Configuration["region"]);
        var topics = Assert.IsType<List<Dictionary<string, object?>>>(plan.Configuration["topics"]);
        Assert.Equal(3, topics[0]["partitions"]);
    }

    [Fact]
    public async Task Plans_ContainNoPlaceholders()
    {
        var plans = new[]
        {
            await _pg.PlanAsync(Res("postgres"), Pctx(), Ct),
            await _redis.PlanAsync(Res("redis"), Pctx(), Ct),
            await _kafka.PlanAsync(Res("kafka.messaging", props: ("topics", new object[] { new { name = "a", owners = new[] { "t" } } })), Pctx(), Ct)
        };
        foreach (var p in plans)
            Assert.DoesNotContain("pending", System.Text.Json.JsonSerializer.Serialize(p), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Plan_DoesNotThrow_OnWrongTypes()
    {
        await _pg.PlanAsync(Res("postgres", props: [("ha", "yes"), ("version", 5)]), Pctx(), Ct);
        await _redis.PlanAsync(Res("redis", props: [("ha", "yes"), ("maxMemoryMb", "x")]), Pctx(), Ct);
        await _kafka.PlanAsync(Res("kafka.messaging", props: ("topics", "x")), Pctx(), Ct);
        await _kafka.PlanAsync(Res("kafka.messaging", props: ("topics", new object[] { 1, new { name = 3, partitions = "a", owners = "x" } })), Pctx(), Ct);
    }

    // ---- strict validation
    private static async Task<ValidationResult> V(IResourceProvider p, ResourceDescriptor r) => await p.ValidateAsync(r, Vctx(), Ct);

    [Theory]
    [InlineData("postgres")]
    [InlineData("redis")]
    public async Task UnknownProperty_IsError(string type)
    {
        IResourceProvider p = type == "postgres" ? _pg : _redis;
        var result = await V(p, Res(type, props: ("bogus", 1)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("bogus"));
    }

    [Fact]
    public async Task Kafka_UnknownProperty_IsError()
    {
        var topics = new object[] { new { name = "a", owners = new[] { "t" } } };
        var result = await V(_kafka, Res("kafka.messaging", props: [("topics", topics), ("bogus", 1)]));
        Assert.Contains(result.Errors, e => e.Contains("bogus"));
    }

    [Fact]
    public async Task Kafka_UnknownTopicProperty_IsError()
    {
        var result = await V(_kafka, Res("kafka.messaging", props: ("topics", new object[] { new { name = "a", owners = new[] { "t" }, oops = 1 } })));
        Assert.Contains(result.Errors, e => e.Contains("oops"));
    }

    [Fact]
    public async Task Postgres_WrongTypes_AreValidationErrors()
    {
        Assert.False((await V(_pg, Res("postgres", props: ("ha", "yes")))).IsValid);
        Assert.False((await V(_pg, Res("postgres", props: ("version", 16)))).IsValid);
        Assert.False((await V(_pg, Res("postgres", props: ("sku", true)))).IsValid);
    }

    [Fact]
    public async Task Redis_WrongTypes_AreValidationErrors()
    {
        Assert.False((await V(_redis, Res("redis", props: ("ha", "yes")))).IsValid);
        Assert.False((await V(_redis, Res("redis", props: ("version", 7)))).IsValid);
        Assert.False((await V(_redis, Res("redis", props: ("maxMemoryMb", "big")))).IsValid);
        Assert.False((await V(_redis, Res("redis", props: ("maxMemoryMb", 1.5)))).IsValid);
        Assert.True((await V(_redis, Res("redis", props: [("maxMemoryMb", 256), ("ha", true), ("version", "7")]))).IsValid);
    }

    [Fact]
    public async Task Kafka_Valid()
    {
        var r = Res("kafka.messaging", props: ("topics", new object[] { new { name = "e", partitions = 6, retentionHours = 24, owners = new[] { "a" }, consumers = new[] { "b" } } }));
        Assert.True((await V(_kafka, r)).IsValid);
    }

    [Fact]
    public async Task Kafka_MissingOrEmptyTopics_Invalid()
    {
        Assert.Contains("at least one topic", (await V(_kafka, Res("kafka.messaging"))).Errors[0]);
        Assert.False((await V(_kafka, Res("kafka.messaging", props: ("topics", Array.Empty<object>())))).IsValid);
    }

    [Fact]
    public async Task Kafka_TopicWithoutOwner_Invalid()
    {
        Assert.False((await V(_kafka, Res("kafka.messaging", props: ("topics", new object[] { new { name = "e" } })))).IsValid);
    }

    [Fact]
    public async Task Kafka_WrongTypes_AreErrorsNotExceptions()
    {
        var cases = new object[]
        {
            "x",
            new object[] { "x" },
            new object[] { 5 },
            new object[] { new { name = 3, owners = new[] { "a" } } },
            new object[] { new { name = "a", partitions = "3", owners = new[] { "a" } } },
            new object[] { new { name = "a", partitions = 2.5, owners = new[] { "a" } } },
            new object[] { new { name = "a", retentionHours = true, owners = new[] { "a" } } },
            new object[] { new { name = "a", owners = "a" } },
            new object[] { new { name = "a", owners = new object[] { 1 } } },
            new object[] { new { name = "a", owners = new[] { "a" }, consumers = new object[] { 1 } } },
            new object[] { new { name = "a", partitions = 0, owners = new[] { "a" } } }
        };
        foreach (var topics in cases)
        {
            var result = await V(_kafka, Res("kafka.messaging", props: ("topics", topics)));
            Assert.False(result.IsValid, System.Text.Json.JsonSerializer.Serialize(topics));
        }
    }
}
