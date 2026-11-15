using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Resources.Redis;

public class RedisResourceProvider : IResourceProvider
{
    public string ResourceType => "redis";

    // Defaults; platform teams can replace them via provisionerConfigs.redis
    // (keys: validSizes, port).
    private static readonly HashSet<string> DefaultValidSizes = ["xs", "s", "m", "l", "xl"];
    private const string DefaultPort = "6379";

    private static ProviderConstraints GetConstraints(PlatformConfig platform) =>
        platform.ProvisionerConfigs.TryGetValue("redis", out var config)
            ? ProviderConstraints.From(config)
            : ProviderConstraints.Empty;

    private sealed record ProviderConstraints(HashSet<string>? ValidSizes, string? Port)
    {
        public static readonly ProviderConstraints Empty = new(null, null);

        public static ProviderConstraints From(Dictionary<string, JsonElement> config) => new(
            ReadValidSizes(config),
            ReadPort(config, "port"));

        private static HashSet<string>? ReadValidSizes(Dictionary<string, JsonElement> config)
        {
            if (!config.TryGetValue("validSizes", out var element) || element.ValueKind != JsonValueKind.Array)
                return null;
            return [.. element.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => s is not null)
                .Select(s => s!)];
        }

        private static string? ReadPort(Dictionary<string, JsonElement> config, string key)
        {
            if (!config.TryGetValue(key, out var element))
                return null;
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number when element.TryGetInt32(out var port) => port.ToString(),
                _ => null
            };
        }
    }

    public ResourceSchema GetSchema() => new()
    {
        ResourceType = "redis",
        Description = "Redis cache",
        Properties =
        [
            new() { Name = "version", ValueType = "string", Description = "Redis version" },
            new() { Name = "ha", ValueType = "bool", Description = "Enable high availability" },
            new() { Name = "maxMemoryMb", ValueType = "int", Description = "Maximum memory in MB" }
        ],
        ProvidedOutputs = ["endpoint", "host", "port"]
    };

    public Task<ValidationResult> ValidateAsync(ResourceDescriptor resource, ValidationContext ctx, CancellationToken ct)
    {
        var errors = new List<string>();
        var validSizes = GetConstraints(ctx.Platform).ValidSizes ?? DefaultValidSizes;

        if (resource.Size is not null && !validSizes.Contains(resource.Size))
            errors.Add($"Invalid Redis size '{resource.Size}'. Valid sizes: {string.Join(", ", validSizes)}");

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(ResourceDescriptor resource, PlanContext ctx, CancellationToken ct)
    {
        var constraints = GetConstraints(ctx.Platform);
        var ha = resource.Properties.TryGetValue("ha", out var h) ? h.GetBoolean() : ctx.EnvironmentConfig.Defaults?.Ha ?? false;
        var size = resource.Size ?? "s";

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceType = "redis",
            Action = "create",
            PlannedOutputs = new Dictionary<string, string>
            {
                ["endpoint"] = $"<pending:{ctx.AppName}-redis>",
                ["host"] = $"<pending:{ctx.AppName}-redis-host>",
                ["port"] = constraints.Port ?? DefaultPort
            },
            Configuration = new Dictionary<string, object?>
            {
                ["size"] = size,
                ["ha"] = ha,
                ["appName"] = ctx.AppName,
                ["environment"] = ctx.Environment,
                ["region"] = ctx.Platform.Defaults.Region
            }
        });
    }
}
