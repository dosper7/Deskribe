using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Provisioner.Terraform;

/// <summary>
/// Applies the platform team's Terraform root module with the Terraform CLI.
/// Config (provisionerConfigs.terraform): <c>workingDir</c> (required to apply/destroy),
/// <c>binary</c> (default "terraform"), <c>workspace</c> (template, default "{app}-{env}"),
/// <c>outputName</c> (default "deskribe_outputs"; a map of resource key → output name → string).
/// </summary>
public class TerraformProvisioner : IProvisioner
{
    private const string DefaultOutputName = "deskribe_outputs";
    private const int MaxErrorChars = 4000;

    public string Name => "terraform";

    public async Task<ProvisionResult> ApplyAsync(ProvisionContext ctx, CancellationToken ct = default)
    {
        var settings = ReadSettings(ctx, out var error);
        if (settings is null) return ProvisionResult.Failed(error!);

        var varFile = WriteVarFile(ctx);
        try
        {
            var failure = await RunSequenceAsync(settings, varFile.Path, "apply", ct);
            if (failure is not null) return failure;

            var output = await RunAsync(settings, ["output", "-json"], ct);
            if (output.Error is not null) return ProvisionResult.Failed(output.Error);

            return ParseOutputs(output.StdOut, settings.OutputName, ctx.Resources);
        }
        finally
        {
            varFile.Dispose();
        }
    }

    public async Task<ProvisionResult> DestroyAsync(ProvisionContext ctx, CancellationToken ct = default)
    {
        var settings = ReadSettings(ctx, out var error);
        if (settings is null) return ProvisionResult.Failed(error!);

        var varFile = WriteVarFile(ctx);
        try
        {
            return await RunSequenceAsync(settings, varFile.Path, "destroy", ct)
                   ?? new ProvisionResult { Success = true };
        }
        finally
        {
            varFile.Dispose();
        }
    }

    public Task<ArtifactResult> GenerateArtifactsAsync(ProvisionContext ctx, string outputDir, CancellationToken ct = default) =>
        Task.FromResult(new ArtifactResult
        {
            Success = true,
            Files =
            [
                new GeneratedFile
                {
                    Path = Path.Combine(outputDir, "infra", "terraform", "terraform.tfvars.json"),
                    Content = BuildTfVars(ctx),
                    Format = "json"
                }
            ]
        });

    internal static string BuildTfVars(ProvisionContext ctx)
    {
        var resources = new Dictionary<string, object>();
        foreach (var r in ctx.Resources)
            resources[r.ResourceKey] = new Dictionary<string, object?> { ["type"] = r.ResourceType, ["config"] = r.Configuration };

        var tfvars = new Dictionary<string, object?>
        {
            ["app_name"] = ctx.AppName,
            ["environment"] = ctx.Environment,
            ["region"] = ctx.Defaults.Region,
            ["team"] = ctx.Team,
            ["resources"] = resources
        };
        return JsonSerializer.Serialize(tfvars, new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed record Settings(string WorkingDir, string Binary, string Workspace, string OutputName);

    private static Settings? ReadSettings(ProvisionContext ctx, out string? error)
    {
        var workingDir = ReadString(ctx, "workingDir");
        if (string.IsNullOrWhiteSpace(workingDir))
        {
            error = "workingDir must be configured in provisionerConfigs.terraform to apply or destroy with Terraform";
            return null;
        }
        if (!Directory.Exists(workingDir))
        {
            error = $"Terraform workingDir '{workingDir}' does not exist";
            return null;
        }

        var workspace = (ReadString(ctx, "workspace") ?? "{app}-{env}")
            .Replace("{app}", ctx.AppName)
            .Replace("{env}", ctx.Environment)
            .Replace("{team}", ctx.Team ?? "default");

        error = null;
        return new Settings(
            workingDir,
            ReadString(ctx, "binary") ?? "terraform",
            workspace,
            ReadString(ctx, "outputName") ?? DefaultOutputName);
    }

    private static string? ReadString(ProvisionContext ctx, string key) =>
        ctx.Config.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;

    private sealed class VarFile(string directory, string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static VarFile WriteVarFile(ProvisionContext ctx)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "deskribe-tf-" + Guid.NewGuid().ToString("N"))).FullName;
        var path = Path.Combine(dir, "deskribe.tfvars.json");
        File.WriteAllText(path, BuildTfVars(ctx));
        return new VarFile(dir, path);
    }

    private static async Task<ProvisionResult?> RunSequenceAsync(Settings s, string varFile, string verb, CancellationToken ct)
    {
        string[][] steps =
        [
            ["init", "-input=false"],
            ["workspace", "select", "-or-create=true", s.Workspace],
            [verb, "-auto-approve", "-input=false", $"-var-file={varFile}"]
        ];

        foreach (var step in steps)
        {
            var run = await RunAsync(s, step, ct);
            if (run.Error is not null) return ProvisionResult.Failed(run.Error);
        }
        return null;
    }

    private sealed record RunResult(string StdOut, string? Error);

    private static async Task<RunResult> RunAsync(Settings s, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(s.Binary)
        {
            WorkingDirectory = s.WorkingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["TF_IN_AUTOMATION"] = "1";
        psi.Environment["TF_INPUT"] = "0";

        var label = $"terraform {args[0]}";
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("process did not start");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new RunResult("", $"Could not start '{s.Binary}': {ex.Message}");
        }

        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }

            var outText = await stdout;
            var errText = (await stderr).Trim();
            if (process.ExitCode == 0) return new RunResult(outText, null);

            if (errText.Length > MaxErrorChars) errText = "..." + errText[^MaxErrorChars..];
            var detail = errText.Length > 0 ? $": {errText}" : "";
            return new RunResult("", $"'{label}' failed with exit code {process.ExitCode}{detail}");
        }
    }

    private static ProvisionResult ParseOutputs(string json, string outputName, IReadOnlyList<ResourcePlanResult> resources)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(outputName, out var entry)
                || !entry.TryGetProperty("value", out var value))
            {
                return resources.Count == 0
                    ? new ProvisionResult { Success = true }
                    : ProvisionResult.Failed($"Terraform output '{outputName}' was not found after apply");
            }

            if (value.ValueKind != JsonValueKind.Object)
                return ProvisionResult.Failed($"Terraform output '{outputName}' must be a map of resource key to map of output name to string");

            var outputs = new Dictionary<string, Dictionary<string, string>>();
            foreach (var resource in resources)
            {
                if (!value.TryGetProperty(resource.ResourceKey, out var forResource)
                    || forResource.ValueKind != JsonValueKind.Object)
                    continue;

                var map = new Dictionary<string, string>();
                foreach (var prop in forResource.EnumerateObject())
                {
                    var text = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
                        _ => null
                    };
                    if (text is not null) map[prop.Name] = text;
                }
                outputs[resource.ResourceKey] = map;
            }
            return new ProvisionResult { Success = true, ResourceOutputs = outputs };
        }
        catch (JsonException)
        {
            return ProvisionResult.Failed("Terraform 'output -json' did not return valid JSON");
        }
    }
}
