using System.Text.Json;
using Deskribe.Core.Tests.Support;
using Deskribe.Sdk;

namespace Deskribe.Core.Tests;

public class GenerateTests : IDisposable
{
    private readonly EngineHarness _h = new();

    private const string Manifest = """
        {
          "name": "shop",
          "team": "payments",
          "resources": [ { "type": "postgres" }, { "type": "redis" } ],
          "services": [ {
            "env": {
              "DB": "@resource(postgres).connectionString",
              "CACHE": "@resource(redis).endpoint",
              "MODE": "fast"
            }
          } ]
        }
        """;

    private static readonly Dictionary<string, string> Images = new() { ["shop"] = "shop:1" };

    public GenerateTests()
    {
        _h.WriteManifest(Manifest);
        _h.WriteEnv("dev", """{ "defaults": { "secretsStrategy": "external-secrets", "externalSecretsStore": "vault" } }""");
    }

    public void Dispose() => _h.Dispose();

    private string WriteOutputs() => _h.WriteFile("outputs.json", """
        { "redis": { "endpoint": "redis:6379", "port": 6379 }, "postgres": { "host": "pg" } }
        """);

    [Fact]
    public async Task OpaqueStrategyWithSecretsIsRefused()
    {
        _h.WriteEnv("dev", """{ "defaults": { "secretsStrategy": "opaque" } }""");

        var ex = await Assert.ThrowsAsync<DeskribeException>(() => _h.GenerateAsync(images: Images, outputsPath: WriteOutputs()));

        Assert.Contains("external-secrets", ex.Message);
        Assert.Contains("deskribe apply", ex.Message);
        Assert.False(Directory.Exists(_h.OutputDir));
        Assert.Empty(_h.PgProv.GenerateCalls);
    }

    [Fact]
    public async Task OpaqueStrategyWithoutSecretsIsAllowed()
    {
        _h.WriteEnv("dev", """{ "defaults": { "secretsStrategy": "opaque" } }""");
        _h.WriteManifest("""
            { "name": "shop", "resources": [ { "type": "redis" } ], "services": [ { "env": { "CACHE": "@resource(redis).endpoint" } } ] }
            """);

        await _h.GenerateAsync(images: Images, outputsPath: WriteOutputs());

        Assert.Equal("redis:6379", _h.Runtime.GenerateCalls.Single().Workloads[0].EnvironmentVariables["CACHE"]);
    }

    [Fact]
    public async Task OutputsFileResolvesReferencesAndSecretsStayExternal()
    {
        var files = await _h.GenerateAsync(images: Images, outputsPath: WriteOutputs());

        var w = Assert.Single(Assert.Single(_h.Runtime.GenerateCalls).Workloads);
        Assert.Equal("redis:6379", w.EnvironmentVariables["CACHE"]);
        Assert.Equal("@resource(postgres).connectionString", w.EnvironmentVariables["DB"]);
        Assert.All(files, f => Assert.True(File.Exists(f.Path), f.Path));
    }

    [Fact]
    public async Task UnresolvedNonSecretReferenceFailsWithoutOutputs()
    {
        var ex = await Assert.ThrowsAsync<DeskribeException>(() => _h.GenerateAsync(images: Images));
        Assert.Contains("@resource(redis).endpoint", ex.Message);
        Assert.Contains("--outputs", ex.Message);
    }

    [Fact]
    public async Task MalformedOutputsFileIsReadableError()
    {
        var path = _h.WriteFile("outputs.json", "[1,2]");
        var ex = await Assert.ThrowsAsync<DeskribeException>(() => _h.GenerateAsync(images: Images, outputsPath: path));
        Assert.Contains("outputs.json", ex.Message);
    }

    [Fact]
    public async Task ImagesRequiredForRuntimeArtifacts()
    {
        var ex = await Assert.ThrowsAsync<DeskribeException>(() => _h.GenerateAsync(outputsPath: WriteOutputs()));
        Assert.Contains("'shop'", ex.Message);
    }

    [Fact]
    public async Task BindingsJsonIsWrittenWithoutValues()
    {
        await _h.GenerateAsync(images: Images, outputsPath: WriteOutputs());

        var path = Path.Combine(_h.OutputDir, "bindings.json");
        var text = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Assert.Equal("shop", root.GetProperty("app").GetString());
        Assert.Equal("payments", root.GetProperty("team").GetString());
        Assert.Equal("dev", root.GetProperty("environment").GetString());
        Assert.Equal("shop-dev", root.GetProperty("namespace").GetString());
        var bindings = root.GetProperty("bindings").EnumerateArray().ToList();
        Assert.Equal(2, bindings.Count);
        Assert.Contains(bindings, b => b.GetProperty("envVar").GetString() == "DB" && b.GetProperty("sensitive").GetBoolean());
        Assert.DoesNotContain("redis:6379", text);
    }

    [Fact]
    public async Task InfraOnlyCallsProvisionersOnceAndNeedsNoImages()
    {
        _h.PgProv.FilesToGenerate.Add(new GeneratedFile { Path = "infra/pg.tfvars.json", Content = "{}" });

        var files = await _h.GenerateAsync(format: "terraform-only");

        var call = Assert.Single(_h.PgProv.GenerateCalls);
        Assert.Equal(["postgres", "redis"], call.Resources.Select(r => r.ResourceKey));
        Assert.Empty(_h.Runtime.GenerateCalls);
        Assert.True(File.Exists(Path.Combine(_h.OutputDir, "infra", "pg.tfvars.json")));
        Assert.Contains(files, f => f.Path.EndsWith("bindings.json"));
    }

    [Fact]
    public async Task RuntimeOnlySkipsProvisioners()
    {
        await _h.GenerateAsync(images: Images, format: "k8s-only", outputsPath: WriteOutputs());

        Assert.Empty(_h.PgProv.GenerateCalls);
        Assert.Single(_h.Runtime.GenerateCalls);
        Assert.True(File.Exists(Path.Combine(_h.OutputDir, "shop", "deployment.yaml")));
    }

    [Fact]
    public async Task UnknownFormatIsAnError()
    {
        await Assert.ThrowsAsync<DeskribeException>(() => _h.GenerateAsync(images: Images, format: "helm"));
    }

    [Theory]
    [InlineData("../escape.yaml")]
    [InlineData("/etc/passwd-deskribe")]
    [InlineData("a/../../escape.yaml")]
    public async Task PluginPathsEscapingOutputDirAreRejected(string path)
    {
        _h.Runtime.GenerateFiles = (_, _) => [new GeneratedFile { Path = path, Content = "x" }];

        var ex = await Assert.ThrowsAsync<DeskribeException>(() => _h.GenerateAsync(images: Images, outputsPath: WriteOutputs()));

        Assert.Contains("outside the output directory", ex.Message);
        Assert.False(Directory.Exists(_h.OutputDir));
    }

    [Fact]
    public async Task SiblingDirectoryWithSamePrefixIsRejected()
    {
        _h.Runtime.GenerateFiles = (_, dir) => [new GeneratedFile { Path = dir + "-evil/x.yaml", Content = "x" }];

        await Assert.ThrowsAsync<DeskribeException>(() => _h.GenerateAsync(images: Images, outputsPath: WriteOutputs()));
    }

    [Fact]
    public async Task InvalidManifestThrowsValidationException()
    {
        _h.WriteManifest("""{ "name": "Shop" }""");
        await Assert.ThrowsAsync<DeskribeValidationException>(() => _h.GenerateAsync(images: Images));
    }
}
