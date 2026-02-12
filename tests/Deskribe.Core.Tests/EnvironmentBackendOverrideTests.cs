using Deskribe.Core.Config;
using Deskribe.Core.Engine;
using Deskribe.Core.Merging;
using Deskribe.Core.Plugins;
using Deskribe.Core.Resolution;
using Deskribe.Core.Validation;
using Deskribe.Plugins.Resources.Postgres;
using Deskribe.Plugins.Resources.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deskribe.Core.Tests;

public class EnvironmentBackendOverrideTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DeskribeEngine _engine;

    public EnvironmentBackendOverrideTests()
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

        _engine = new DeskribeEngine(configLoader, mergeEngine, resolver, validator, pluginHost,
            NullLogger<DeskribeEngine>.Instance);
    }

    private async Task WriteJson(string path, string json)
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, path), json);
    }

    [Fact]
    public async Task PlanAsync_EnvBackendsOverridePlatformBackends()
    {
        await WriteJson("deskribe.json", """
        {
            "name": "test-app",
            "resources": [
                { "type": "postgres", "size": "s" }
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
            "policies": {}
        }
        """);

        await WriteJson("platform/envs/local.json", """
        {
            "name": "local",
            "defaults": { "replicas": 1, "cpu": "250m", "memory": "256Mi" },
            "backends": { "postgres": "helm" }
        }
        """);

        var plan = await _engine.PlanAsync(
            Path.Combine(_tempDir, "deskribe.json"),
            Path.Combine(_tempDir, "platform"),
            "local");

        // The plan itself is created successfully with the env config that has backend overrides
        Assert.Equal("local", plan.Environment);
        Assert.Equal("test-app", plan.AppName);
        // Verify the environment config has the backends override
        Assert.Equal("helm", plan.EnvironmentConfig.Backends["postgres"]);
    }

    [Fact]
    public async Task PlanAsync_UnsetEnvBackendsFallThroughToPlatform()
    {
        await WriteJson("deskribe.json", """
        {
            "name": "test-app",
            "resources": [
                { "type": "postgres", "size": "s" }
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
            "policies": {}
        }
        """);

        await WriteJson("platform/envs/dev.json", """
        { "name": "dev", "defaults": {} }
        """);

        var plan = await _engine.PlanAsync(
            Path.Combine(_tempDir, "deskribe.json"),
            Path.Combine(_tempDir, "platform"),
            "dev");

        Assert.Equal("dev", plan.Environment);
        // No backend override in env config — should be empty
        Assert.Empty(plan.EnvironmentConfig.Backends);
        // Platform backends should still have pulumi
        Assert.Equal("pulumi", plan.Platform.Backends["postgres"]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}
