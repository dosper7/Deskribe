using System.Text.Json;
using Deskribe.Plugins.Provisioner.Terraform;
using static Deskribe.Plugins.Tests.T;

namespace Deskribe.Plugins.Tests;

public sealed class TerraformProvisionerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("deskribe-tf-test-").FullName;
    private readonly TerraformProvisioner _provisioner = new();

    public void Dispose() => Directory.Delete(_dir, true);

    private string WriteFakeTerraform(string? outputsJson, string? failVerb = null)
    {
        var work = Directory.CreateDirectory(Path.Combine(_dir, "module")).FullName;
        if (outputsJson is not null) File.WriteAllText(Path.Combine(_dir, "outputs.json"), outputsJson);
        var script = Path.Combine(_dir, "fake-terraform");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            D="{{_dir}}"
            echo "$*" >> "$D/calls.log"
            pwd >> "$D/cwd.log"
            for a in "$@"; do case "$a" in -var-file=*) cp "${a#-var-file=}" "$D/captured.json";; esac; done
            if [ "$1" = "{{failVerb}}" ]; then echo "boom: bad credentials" >&2; exit 3; fi
            if [ "$1" = "output" ]; then cat "$D/outputs.json"; fi
            exit 0
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return work;
    }

    private static string Outputs(object value, string name = "deskribe_outputs") =>
        JsonSerializer.Serialize(new Dictionary<string, object> { [name] = new { sensitive = true, type = "dynamic", value } });

    private object Config(string work, object? extra = null) => new Dictionary<string, object?>
    {
        ["workingDir"] = work,
        ["binary"] = Path.Combine(_dir, "fake-terraform")
    }.Concat(extra is null ? [] : JsonSerializer.SerializeToElement(extra).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value))
     .ToDictionary(k => k.Key, v => v.Value);

    private static readonly ResourcePlanResultsHolder Resources = new();
    private sealed class ResourcePlanResultsHolder
    {
        public Deskribe.Sdk.Models.ResourcePlanResult Pg => Plan("orders-db", "postgres", ("version", "16"), ("ha", true));
        public Deskribe.Sdk.Models.ResourcePlanResult Redis => Plan("cache", "redis", ("size", "s"));
    }

    private string Calls => File.ReadAllText(Path.Combine(_dir, "calls.log"));

    [Fact]
    public async Task Apply_RunsCliAndReturnsOnlyOutputsPerResource()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = WriteFakeTerraform(Outputs(new
        {
            orders_db = new { connectionString = "x" },
            cache = new { endpoint = "redis:6379", port = 6379, tls = true, nested = new { a = 1 }, nothing = (string?)null },
            unrelated = new { a = "b" }
        }).Replace("orders_db", "orders-db"));

        var ctx = Ctx([Resources.Pg, Resources.Redis], Config(work));
        var result = await _provisioner.ApplyAsync(ctx);

        Assert.True(result.Success, string.Join(";", result.Errors));
        Assert.Equal(["cache", "orders-db"], result.ResourceOutputs.Keys.Order());
        Assert.Equal("x", result.ResourceOutputs["orders-db"]["connectionString"]);
        var cache = result.ResourceOutputs["cache"];
        Assert.Equal("redis:6379", cache["endpoint"]);
        Assert.Equal("6379", cache["port"]);
        Assert.Equal("true", cache["tls"]);
        Assert.DoesNotContain("nested", cache.Keys);
        Assert.DoesNotContain("nothing", cache.Keys);

        var calls = Calls.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("init -input=false", calls[0]);
        Assert.Equal("workspace select -or-create=true myapp-dev", calls[1]);
        Assert.StartsWith("apply -auto-approve -input=false -var-file=", calls[2]);
        Assert.Equal("output -json", calls[3]);
        Assert.All(File.ReadAllLines(Path.Combine(_dir, "cwd.log")), l => Assert.EndsWith("module", l));
        Assert.DoesNotContain("x", string.Join("", result.Errors));
    }

    [Fact]
    public async Task Apply_WritesTfVars_AndRemovesTempFile()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = WriteFakeTerraform(Outputs(new { }));
        await _provisioner.ApplyAsync(Ctx([Resources.Pg], Config(work)));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "captured.json")));
        var root = doc.RootElement;
        Assert.Equal("myapp", root.GetProperty("app_name").GetString());
        Assert.Equal("dev", root.GetProperty("environment").GetString());
        Assert.Equal("northeurope", root.GetProperty("region").GetString());
        Assert.Equal("payments", root.GetProperty("team").GetString());
        var r = root.GetProperty("resources").GetProperty("orders-db");
        Assert.Equal("postgres", r.GetProperty("type").GetString());
        Assert.True(r.GetProperty("config").GetProperty("ha").GetBoolean());

        var varFile = Calls.Split('\n').First(l => l.StartsWith("apply")).Split("-var-file=")[1].Trim();
        Assert.False(File.Exists(varFile));
    }

    [Fact]
    public async Task Apply_HonoursWorkspaceTemplateBinaryAndOutputName()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = WriteFakeTerraform(Outputs(new { cache = new { endpoint = "e" } }, "custom_out"));
        var result = await _provisioner.ApplyAsync(Ctx([Resources.Redis], Config(work, new { workspace = "ws-{env}-{app}", outputName = "custom_out" })));

        Assert.True(result.Success, string.Join(";", result.Errors));
        Assert.Contains("workspace select -or-create=true ws-dev-myapp", Calls);
        Assert.Equal("e", result.ResourceOutputs["cache"]["endpoint"]);
    }

    [Fact]
    public async Task Apply_OutputsForSomeResourcesOnly_OmitsTheRest()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = WriteFakeTerraform(Outputs(new { cache = new { endpoint = "e" } }));
        var result = await _provisioner.ApplyAsync(Ctx([Resources.Pg, Resources.Redis], Config(work)));

        Assert.True(result.Success);
        Assert.Equal(["cache"], result.ResourceOutputs.Keys);
    }

    [Fact]
    public async Task Apply_MissingOutput_Fails()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = WriteFakeTerraform(Outputs(new { }, "something_else"));
        var result = await _provisioner.ApplyAsync(Ctx([Resources.Pg], Config(work)));
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("deskribe_outputs"));
    }

    [Fact]
    public async Task Apply_FailingCommand_ReportsStderrAndStops()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = WriteFakeTerraform(Outputs(new { }), failVerb: "apply");
        var result = await _provisioner.ApplyAsync(Ctx([Resources.Pg], Config(work)));

        Assert.False(result.Success);
        Assert.Empty(result.ResourceOutputs);
        Assert.Contains(result.Errors, e => e.Contains("apply") && e.Contains("exit code 3") && e.Contains("bad credentials"));
        Assert.DoesNotContain("output -json", Calls);
    }

    [Fact]
    public async Task Apply_MissingWorkingDir_Fails()
    {
        var result = await _provisioner.ApplyAsync(Ctx([Resources.Pg], new { binary = "terraform" }));
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("workingDir"));
        Assert.False((await _provisioner.DestroyAsync(Ctx([Resources.Pg]))).Success);
    }

    [Fact]
    public async Task Apply_MissingBinary_Fails()
    {
        var result = await _provisioner.ApplyAsync(Ctx([Resources.Pg], new { workingDir = _dir, binary = Path.Combine(_dir, "nope") }));
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("Could not start"));
    }

    [Fact]
    public async Task Apply_Cancellation_Propagates()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = WriteFakeTerraform(Outputs(new { }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _provisioner.ApplyAsync(Ctx([Resources.Pg], Config(work)), cts.Token));
    }

    [Fact]
    public async Task Destroy_RunsDestroy()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = WriteFakeTerraform(null);
        var result = await _provisioner.DestroyAsync(Ctx([], Config(work)));
        Assert.True(result.Success, string.Join(";", result.Errors));
        Assert.Contains("destroy -auto-approve -input=false -var-file=", Calls);
        Assert.Empty(result.ResourceOutputs);
    }

    [Fact]
    public async Task Destroy_Failure_Reported()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = WriteFakeTerraform(null, failVerb: "destroy");
        var result = await _provisioner.DestroyAsync(Ctx([], Config(work)));
        Assert.False(result.Success);
    }

    [Fact]
    public async Task GenerateArtifacts_WritesOnlyTfVars()
    {
        var result = await _provisioner.GenerateArtifactsAsync(Ctx([Resources.Pg]), "out");
        Assert.True(result.Success);
        var file = Assert.Single(result.Files);
        Assert.Equal(Path.Combine("out", "infra", "terraform", "terraform.tfvars.json"), file.Path);
        using var doc = JsonDocument.Parse(file.Content);
        Assert.Equal("northeurope", doc.RootElement.GetProperty("region").GetString());
        Assert.Equal("postgres", doc.RootElement.GetProperty("resources").GetProperty("orders-db").GetProperty("type").GetString());
    }

    [Fact]
    public async Task GenerateArtifacts_NullTeamAndRegion_AreNull()
    {
        var result = await _provisioner.GenerateArtifactsAsync(Ctx([], team: null, region: null), "out");
        using var doc = JsonDocument.Parse(result.Files[0].Content);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("team").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("region").ValueKind);
    }
}
