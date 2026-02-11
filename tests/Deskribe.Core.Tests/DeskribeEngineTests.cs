using Deskribe.Core.Config;
using Deskribe.Core.Engine;
using Deskribe.Core.Merging;
using Deskribe.Core.Plugins;
using Deskribe.Core.Resolution;
using Deskribe.Core.Validation;
using Deskribe.Plugins.Resources.Postgres;
using Deskribe.Plugins.Resources.Redis;
using Deskribe.Plugins.Resources.Kafka;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deskribe.Core.Tests;

public class DeskribeEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DeskribeEngine _engine;

    public DeskribeEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "deskribe-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "platform", "envs"));

        var configLoader = new ConfigLoader(NullLogger<ConfigLoader>.Instance);
        var mergeEngine = new MergeEngine(NullLogger<MergeEngine>.Instance);
        var resolver = new ResourceReferenceResolver(NullLogger<ResourceReferenceResolver>.Instance);
        var validator = new PolicyValidator(NullLogger<PolicyValidator>.Instance);
        var pluginHost = new PluginHost(NullLogger<PluginHost>.Instance);

        pluginHost.RegisterPlugin(new PostgresPlugin());
        pluginHost.RegisterPlugin(new RedisPlugin());
        pluginHost.RegisterPlugin(new KafkaPlugin());

        _engine = new DeskribeEngine(configLoader, mergeEngine, resolver, validator, pluginHost,
            NullLogger<DeskribeEngine>.Instance);
    }

    private async Task WriteJson(string path, string json)
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, path), json);
    }

    [Fact]
    public async Task ValidateAsync_PassesForValidManifest()
    {
        await WriteJson("deskribe.json", """
        {
            "name": "test-app",
            "resources": [
                { "type": "postgres", "size": "m" }
            ],
            "services": [
                {
                    "env": {
                        "DB": "@resource(postgres).connectionString"
                    }
                }
            ]
        }
        """);

        await WriteJson("platform/base.json", """
        {
            "organization": "acme",
            "defaults": { "replicas": 2, "cpu": "250m", "memory": "512Mi", "namespacePattern": "{app}-{env}" },
            "backends": { "postgres": "pulumi" },
            "policies": { "allowedRegions": ["westeurope"], "enforceTLS": true }
        }
        """);

        await WriteJson("platform/envs/dev.json", """
        { "name": "dev", "defaults": {} }
        """);

        var result = await _engine.ValidateAsync(
            Path.Combine(_tempDir, "deskribe.json"),
            Path.Combine(_tempDir, "platform"),
            "dev");

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_FailsForInvalidResourceReference()
    {
        await WriteJson("deskribe.json", """
        {
            "name": "test-app",
            "resources": [
                { "type": "postgres", "size": "m" }
            ],
            "services": [
                {
                    "env": {
                        "CACHE": "@resource(redis).endpoint"
                    }
                }
            ]
        }
        """);

        await WriteJson("platform/base.json", """
        {
            "organization": "acme",
            "defaults": { "replicas": 2, "cpu": "250m", "memory": "512Mi", "namespacePattern": "{app}-{env}" },
            "backends": { "postgres": "pulumi" },
            "policies": {}
        }
        """);

        var result = await _engine.ValidateAsync(
            Path.Combine(_tempDir, "deskribe.json"),
            Path.Combine(_tempDir, "platform"),
            "dev");

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task PlanAsync_CreatesValidPlan()
    {
        await WriteJson("deskribe.json", """
        {
            "name": "my-api",
            "resources": [
                { "type": "postgres", "size": "m" },
                { "type": "redis" }
            ],
            "services": [
                {
                    "env": {
                        "DB": "@resource(postgres).connectionString",
                        "CACHE": "@resource(redis).endpoint"
                    },
                    "overrides": {
                        "dev": { "replicas": 1 }
                    }
                }
            ]
        }
        """);

        await WriteJson("platform/base.json", """
        {
            "organization": "acme",
            "defaults": { "replicas": 2, "cpu": "250m", "memory": "512Mi", "namespacePattern": "{app}-{env}" },
            "backends": { "postgres": "pulumi", "redis": "pulumi" },
            "policies": {}
        }
        """);

        await WriteJson("platform/envs/dev.json", """
        { "name": "dev", "defaults": {} }
        """);

        var plan = await _engine.PlanAsync(
            Path.Combine(_tempDir, "deskribe.json"),
            Path.Combine(_tempDir, "platform"),
            "dev",
            new Dictionary<string, string> { ["api"] = "nginx:latest" });

        Assert.Equal("my-api", plan.AppName);
        Assert.Equal("dev", plan.Environment);
        Assert.Equal(2, plan.ResourcePlans.Count);
        Assert.NotNull(plan.Workload);
        Assert.Equal("my-api-dev", plan.Workload.Namespace);
        Assert.Equal(1, plan.Workload.Replicas);
        Assert.Equal("nginx:latest", plan.Workload.Image);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}
