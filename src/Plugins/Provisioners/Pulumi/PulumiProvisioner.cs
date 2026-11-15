using System.Text.Json;
using System.Text.RegularExpressions;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Provisioner.Pulumi;

/// <summary>
/// Runs the platform team's Pulumi program once per operation. Config: <c>projectDir</c> (required).
/// Stack outputs are read as <c>&lt;resourceKey&gt;.&lt;output&gt;</c> or
/// <c>&lt;resourceKey&gt;_&lt;output&gt;</c> (non-alphanumerics in the key replaced by <c>_</c>).
/// </summary>
public partial class PulumiProvisioner : IProvisioner
{
    private readonly IPulumiStackRunner _runner;

    public PulumiProvisioner() : this(new AutomationApiStackRunner()) { }

    internal PulumiProvisioner(IPulumiStackRunner runner) => _runner = runner;

    public string Name => "pulumi";

    public async Task<ProvisionResult> ApplyAsync(ProvisionContext ctx, CancellationToken ct = default)
    {
        if (GetProjectDir(ctx) is not { } projectDir)
            return ProvisionResult.Failed(MissingProjectDir);

        try
        {
            var stackOutputs = await _runner.UpAsync(projectDir, StackName(ctx), BuildConfig(ctx), ct);
            return new ProvisionResult { Success = true, ResourceOutputs = MapOutputs(ctx.Resources, stackOutputs) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProvisionResult.Failed($"Pulumi stack update failed: {ex.Message}");
        }
    }

    public Task<ArtifactResult> GenerateArtifactsAsync(ProvisionContext ctx, string outputDir, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(BuildConfig(ctx), new JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(new ArtifactResult
        {
            Success = true,
            Files =
            [
                new GeneratedFile
                {
                    Path = Path.Combine(outputDir, $"Pulumi.{StackName(ctx)}.json"),
                    Content = json,
                    Format = "json"
                }
            ]
        });
    }

    public async Task<ProvisionResult> DestroyAsync(ProvisionContext ctx, CancellationToken ct = default)
    {
        if (GetProjectDir(ctx) is not { } projectDir)
            return ProvisionResult.Failed(MissingProjectDir);

        try
        {
            await _runner.DestroyAsync(projectDir, StackName(ctx), ct);
            return new ProvisionResult { Success = true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProvisionResult.Failed($"Pulumi stack destroy failed: {ex.Message}");
        }
    }

    private const string MissingProjectDir =
        "projectDir must be configured in provisionerConfigs.pulumi to use the Pulumi provisioner";

    private static string StackName(ProvisionContext ctx) => $"{ctx.AppName}-{ctx.Environment}";

    private static string? GetProjectDir(ProvisionContext ctx) =>
        ctx.Config.TryGetValue("projectDir", out var dir) && dir.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(dir.GetString())
            ? dir.GetString()
            : null;

    internal static Dictionary<string, string> BuildConfig(ProvisionContext ctx)
    {
        var config = new Dictionary<string, string>
        {
            ["appName"] = ctx.AppName,
            ["environment"] = ctx.Environment
        };
        if (ctx.Defaults.Region is { } region) config["region"] = region;
        if (ctx.Team is { } team) config["team"] = team;

        foreach (var resource in ctx.Resources)
        {
            foreach (var (key, value) in resource.Configuration)
                config[$"{resource.ResourceKey}:{key}"] = FormatValue(value);
        }
        return config;
    }

    internal static Dictionary<string, Dictionary<string, string>> MapOutputs(
        IReadOnlyList<ResourcePlanResult> resources, IReadOnlyDictionary<string, object?> stackOutputs)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();

        foreach (var (name, raw) in stackOutputs)
        {
            var value = ToText(raw);
            if (value is null) continue;

            // Longest matching key prefix wins so keys that are prefixes of each other do not collide.
            (ResourcePlanResult Resource, string Output)? best = null;
            var bestLength = -1;
            foreach (var resource in resources)
            {
                foreach (var prefix in new[] { resource.ResourceKey + ".", Sanitize(resource.ResourceKey) + "_" })
                {
                    if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal)
                        && prefix.Length > bestLength)
                    {
                        best = (resource, name[prefix.Length..]);
                        bestLength = prefix.Length;
                    }
                }
            }

            if (best is not { } match) continue;
            if (!result.TryGetValue(match.Resource.ResourceKey, out var outputs))
                result[match.Resource.ResourceKey] = outputs = new Dictionary<string, string>();
            outputs[match.Output] = value;
        }
        return result;
    }

    private static string Sanitize(string key) => NonAlphanumeric().Replace(key, "_");

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();

    private static string? ToText(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value)
    };

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
        _ => JsonSerializer.Serialize(value)
    };
}
