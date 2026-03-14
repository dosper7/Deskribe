using System.Text.Json;
using System.Text.RegularExpressions;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Provisioner.PlatformOutput;

public partial class PlatformOutputProvisioner : IProvisioner
{
    public string Name => "platform-output";

    public Task<ProvisionResult> ApplyAsync(DeskribePlan plan, CancellationToken ct)
    {
        Console.WriteLine($"[platform-output] This is an output-only provisioner. Run the generated artifacts through your platform tooling (e.g., Terraform).");

        return Task.FromResult(new ProvisionResult
        {
            Success = true,
            Errors = ["platform-output is an output-only provisioner. Infrastructure must be applied externally."]
        });
    }

    public Task<ArtifactResult> GenerateArtifactsAsync(DeskribePlan plan, string outputDir, CancellationToken ct)
    {
        var files = new List<GeneratedFile>();

        if (!plan.Platform.ProvisionerConfigs.TryGetValue("platform-output", out var rawConfig))
        {
            return Task.FromResult(new ArtifactResult
            {
                Success = false,
                Errors = ["No 'platform-output' entry found in provisionerConfigs."]
            });
        }

        var config = DeserializeConfig(rawConfig);

        var contextTokens = new Dictionary<string, string>
        {
            ["app"] = plan.AppName,
            ["env"] = plan.Environment,
            ["team"] = plan.Team ?? "default",
            ["org"] = plan.Platform.Organization ?? "default"
        };

        var basePath = ResolveTemplate(config.BasePath, contextTokens);

        foreach (var rp in plan.ResourcePlans)
        {
            var resourceKey = rp.ResourceType.Replace(".", "_");

            if (!config.Modules.TryGetValue(rp.ResourceType, out var moduleConfig)
                && !config.Modules.TryGetValue(resourceKey, out moduleConfig))
            {
                continue;
            }

            // Build resource-specific tokens from configuration
            var resourceTokens = new Dictionary<string, string>(contextTokens);
            foreach (var (key, value) in rp.Configuration)
            {
                resourceTokens[key] = value?.ToString() ?? "";
            }

            // Add size from resource plans if available
            if (rp.Configuration.TryGetValue("size", out var sizeVal))
                resourceTokens["size"] = sizeVal?.ToString() ?? "";

            var resolvedMappings = new Dictionary<string, object>();
            foreach (var (targetField, template) in moduleConfig.Mappings)
            {
                resolvedMappings[targetField] = ResolveTemplate(template, resourceTokens);
            }

            var filePath = Path.Combine(outputDir, basePath, moduleConfig.ModuleName, moduleConfig.FileName);
            var content = JsonSerializer.Serialize(resolvedMappings, new JsonSerializerOptions { WriteIndented = true });

            files.Add(new GeneratedFile
            {
                Path = filePath,
                Content = content,
                Format = "json"
            });
        }

        return Task.FromResult(new ArtifactResult
        {
            Success = true,
            Files = files
        });
    }

    public Task DestroyAsync(string appName, string environment, PlatformConfig platform, CancellationToken ct)
    {
        Console.WriteLine($"[platform-output] Destroy is a no-op for output-only provisioner. Remove the generated files from your platform repo manually.");
        return Task.CompletedTask;
    }

    private static PlatformOutputProvisionerConfig DeserializeConfig(Dictionary<string, JsonElement> rawConfig)
    {
        var json = JsonSerializer.Serialize(rawConfig);
        return JsonSerializer.Deserialize<PlatformOutputProvisionerConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new PlatformOutputProvisionerConfig();
    }

    internal static string ResolveTemplate(string template, Dictionary<string, string> tokens)
    {
        return MapExpressionRegex().Replace(template, match =>
        {
            var field = match.Groups[1].Value;
            var mapExpr = match.Groups[2].Value;

            // Handle {field:map(a=x,b=y)} syntax
            if (!string.IsNullOrEmpty(mapExpr))
            {
                var fieldValue = tokens.GetValueOrDefault(field, "");
                var mappings = ParseMapExpression(mapExpr);
                return mappings.GetValueOrDefault(fieldValue, fieldValue);
            }

            // Simple {field} replacement
            return tokens.GetValueOrDefault(field, match.Value);
        });
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
