using System.Text.Json;
using System.Text.RegularExpressions;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Provisioner.PlatformOutput;

public partial class PlatformOutputProvisioner : IProvisioner
{
    public string Name => "platform-output";

    public Task<ProvisionResult> ApplyAsync(ProvisionContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new ProvisionResult { Success = true });

    public Task<ProvisionResult> DestroyAsync(ProvisionContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new ProvisionResult { Success = true });

    public Task<ArtifactResult> GenerateArtifactsAsync(ProvisionContext ctx, string outputDir, CancellationToken ct = default)
    {
        var files = new List<GeneratedFile>();

        if (!ctx.Config.Any())
        {
            return Task.FromResult(new ArtifactResult
            {
                Success = false,
                Errors = ["No 'platform-output' entry found in provisionerConfigs."]
            });
        }

        PlatformOutputProvisionerConfig config;
        try
        {
            config = DeserializeConfig(ctx.Config);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new ArtifactResult
            {
                Success = false,
                Errors = [$"Invalid 'platform-output' configuration: {ex.Message}"]
            });
        }

        var contextTokens = new Dictionary<string, object?>
        {
            ["app"] = ctx.AppName,
            ["env"] = ctx.Environment,
            ["team"] = ctx.Team ?? "default",
            ["org"] = ctx.Platform.Organization ?? "default"
        };

        var basePath = ResolveTemplate(config.BasePath, contextTokens)?.ToString() ?? "";
        var perType = ctx.Resources.GroupBy(r => r.ResourceType).ToDictionary(g => g.Key, g => g.Count());

        foreach (var rp in ctx.Resources)
        {
            var typeKey = rp.ResourceType.Replace(".", "_");

            if (!config.Modules.TryGetValue(rp.ResourceType, out var moduleConfig)
                && !config.Modules.TryGetValue(typeKey, out moduleConfig))
            {
                continue;
            }

            var resourceTokens = new Dictionary<string, object?>(contextTokens) { ["key"] = rp.ResourceKey };
            foreach (var (key, value) in rp.Configuration)
                resourceTokens[key] = value;

            var resolvedMappings = new Dictionary<string, object?>();
            foreach (var (targetField, template) in moduleConfig.Mappings)
                resolvedMappings[targetField] = ResolveTemplate(template, resourceTokens);

            var fileName = perType[rp.ResourceType] > 1
                ? $"{rp.ResourceKey}.{moduleConfig.FileName}"
                : moduleConfig.FileName;
            files.Add(new GeneratedFile
            {
                Path = Path.Combine(outputDir, basePath, moduleConfig.ModuleName, fileName),
                Content = JsonSerializer.Serialize(resolvedMappings, new JsonSerializerOptions { WriteIndented = true }),
                Format = "json"
            });
        }

        return Task.FromResult(new ArtifactResult { Success = true, Files = files });
    }

    private static PlatformOutputProvisionerConfig DeserializeConfig(IReadOnlyDictionary<string, JsonElement> rawConfig)
    {
        var json = JsonSerializer.Serialize(rawConfig.ToDictionary(kv => kv.Key, kv => kv.Value));
        return JsonSerializer.Deserialize<PlatformOutputProvisionerConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new PlatformOutputProvisionerConfig();
    }

    /// <summary>
    /// Resolves a template. When the template is exactly one plain <c>{token}</c> the token's
    /// underlying value is returned (bool, number, ...); mixed templates always yield strings.
    /// </summary>
    internal static object? ResolveTemplate(string template, Dictionary<string, object?> tokens)
    {
        var whole = MapExpressionRegex().Match(template);
        if (whole.Success && whole.Length == template.Length
            && string.IsNullOrEmpty(whole.Groups[2].Value)
            && tokens.TryGetValue(whole.Groups[1].Value, out var typed))
        {
            return typed is JsonElement je ? FromJson(je) : typed;
        }

        return MapExpressionRegex().Replace(template, match =>
        {
            var field = match.Groups[1].Value;
            var mapExpr = match.Groups[2].Value;

            // Handle {field:map(a=x,b=y)} syntax
            if (!string.IsNullOrEmpty(mapExpr))
            {
                var fieldValue = TokenText(tokens, field) ?? "";
                var mappings = ParseMapExpression(mapExpr);
                return mappings.GetValueOrDefault(fieldValue, fieldValue);
            }

            return TokenText(tokens, field) ?? match.Value;
        });
    }

    private static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Null => null,
        _ => e
    };

    private static string? TokenText(Dictionary<string, object?> tokens, string field)
    {
        if (!tokens.TryGetValue(field, out var v)) return null;
        return v switch
        {
            null => "",
            bool b => b ? "true" : "false",
            string s => s,
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => JsonSerializer.Serialize(v)
        };
    }

    private static Dictionary<string, string> ParseMapExpression(string mapExpr)
    {
        // Parse "map(a=x,b=y,c=z)"
        var inner = mapExpr;
        if (inner.StartsWith("map(") && inner.EndsWith(")"))
        {
            inner = inner[4..^1];
        }

        var result = new Dictionary<string, string>();
        foreach (var pair in inner.Split(','))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                result[parts[0].Trim()] = parts[1].Trim();
            }
        }
        return result;
    }

    [GeneratedRegex(@"\{(\w+)(?::([^}]+))?\}")]
    private static partial Regex MapExpressionRegex();
}
