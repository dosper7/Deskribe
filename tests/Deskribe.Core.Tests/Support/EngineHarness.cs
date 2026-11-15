using Deskribe.Core.Config;
using Deskribe.Core.Engine;
using Deskribe.Core.Merging;
using Deskribe.Core.Plugins;
using Deskribe.Core.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deskribe.Core.Tests.Support;

/// <summary>
/// A temp directory with a manifest and a directory-style platform config, plus an engine
/// wired to fake plugins: postgres (pg-prov), redis (pg-prov), kafka (kafka-prov), runtime "kubernetes".
/// </summary>
public sealed class EngineHarness : IDisposable
{
    public string Dir { get; }
    public string ManifestPath => Path.Combine(Dir, "deskribe.json");
    public string PlatformPath => Path.Combine(Dir, "platform");
    public string OutputDir => Path.Combine(Dir, "out");

    public PluginRegistry Registry { get; }
    public FakeResourceProvider Postgres { get; } =
        new("postgres", ["connectionString", "host", "port", "password"], ["connectionString", "password"]);
    public FakeResourceProvider Redis { get; } = new("redis", ["endpoint", "password"], ["password"]);
    public FakeResourceProvider Kafka { get; } = new("kafka.messaging", ["endpoint"]);
    public FakeProvisioner PgProv { get; } = new("pg-prov");
    public FakeProvisioner KafkaProv { get; } = new("kafka-prov");
    public FakeRuntime Runtime { get; } = new();
    public DeskribeEngine Engine { get; }

    public const string DefaultBase = """
        {
          "defaults": { "region": "westeurope", "replicas": 2, "cpu": "250m", "memory": "512Mi", "namespacePattern": "{app}-{env}" },
          "runtime": { "name": "kubernetes" },
          "provisioners": { "postgres": "pg-prov", "redis": "pg-prov", "kafka.messaging": "kafka-prov" },
          "provisionerConfigs": { "pg-prov": { "server": "db.example" } }
        }
        """;

    public EngineHarness(bool registerPlugins = true)
    {
        Dir = Path.Combine(AppContext.BaseDirectory, "test-temp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(PlatformPath, "envs"));

        Registry = new PluginRegistry(NullLogger<PluginRegistry>.Instance);
        if (registerPlugins)
        {
            Registry.RegisterResourceProvider(Postgres);
            Registry.RegisterResourceProvider(Redis);
            Registry.RegisterResourceProvider(Kafka);
            Registry.RegisterProvisioner(PgProv);
            Registry.RegisterProvisioner(KafkaProv);
            Registry.RegisterRuntimePlugin(Runtime);
        }

        Engine = new DeskribeEngine(
            new ConfigLoader(NullLogger<ConfigLoader>.Instance),
            new MergeEngine(NullLogger<MergeEngine>.Instance),
            new ManifestValidator(Registry, NullLogger<ManifestValidator>.Instance),
            Registry,
            NullLogger<DeskribeEngine>.Instance);

        WriteBase(DefaultBase);
        WriteEnv("dev", """{ "defaults": { "replicas": 1 } }""");
        WriteEnv("prod", """{ "defaults": { "replicas": 3 } }""");
    }

    public void WriteManifest(string json) => File.WriteAllText(ManifestPath, json);
    public void WriteBase(string json) => File.WriteAllText(Path.Combine(PlatformPath, "base.json"), json);
    public void WriteEnv(string env, string json) => File.WriteAllText(Path.Combine(PlatformPath, "envs", env + ".json"), json);

    public string WriteFile(string name, string content)
    {
        var path = Path.Combine(Dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public Task<Sdk.Models.ValidationResult> ValidateAsync(string env = "dev") =>
        Engine.ValidateAsync(ManifestPath, PlatformPath, env);

    public Task<Sdk.Models.DeskribePlan> PlanAsync(string env = "dev", Dictionary<string, string>? images = null) =>
        Engine.PlanAsync(ManifestPath, PlatformPath, env, images);

    public Task<List<Sdk.GeneratedFile>> GenerateAsync(
        string env = "dev", Dictionary<string, string>? images = null, string? format = null, string? outputsPath = null) =>
        Engine.GenerateAsync(ManifestPath, PlatformPath, env, OutputDir, images, format, outputsPath);

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}
