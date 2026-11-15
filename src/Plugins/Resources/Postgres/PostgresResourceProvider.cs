using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Resources.Postgres;

public class PostgresResourceProvider : IResourceProvider
{
    public string ResourceType => "postgres";

    // Defaults; platform teams can replace them via provisionerConfigs.postgres
    // (keys: validSizes, validVersions, defaultVersion, port).
    private static readonly HashSet<string> DefaultValidSizes = ["xs", "s", "m", "l", "xl"];
    private static readonly HashSet<string> DefaultValidVersions = ["14", "15", "16", "17"];
    private const string DefaultVersion = "16";
    private const string DefaultPort = "5432";

    private static ProviderConstraints GetConstraints(PlatformConfig platform) =>
        platform.ProvisionerConfigs.TryGetValue("postgres", out var config)
            ? ProviderConstraints.From(config)
            : ProviderConstraints.Empty;

    private sealed record ProviderConstraints(
        HashSet<string>? ValidSizes,
        HashSet<string>? ValidVersions,
        string? DefaultVersion,
        string? Port)
    {
        public static readonly ProviderConstraints Empty = new(null, null, null, null);

        public static ProviderConstraints From(Dictionary<string, JsonElement> config) => new(
            ReadStringSet(config, "validSizes"),
            ReadStringSet(config, "validVersions"),
            ReadString(config, "defaultVersion"),
            ReadPort(config, "port"));

        private static HashSet<string>? ReadStringSet(Dictionary<string, JsonElement> config, string key)
        {
            if (!config.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.Array)
                return null;
            return [.. element.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => s is not null)
                .Select(s => s!)];
        }

        private static string? ReadString(Dictionary<string, JsonElement> config, string key) =>
            config.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;

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
        ResourceType = "postgres",
        Description = "PostgreSQL database",
        Properties =
        [
            new() { Name = "version", ValueType = "string", Description = "PostgreSQL version (14-17)", Default = "16" },
            new() { Name = "ha", ValueType = "bool", Description = "Enable high availability" },
            new() { Name = "sku", ValueType = "string", Description = "Azure SKU tier" }
        ],
        ProvidedOutputs = ["connectionString", "host", "port"]
    };

    public Task<ValidationResult> ValidateAsync(ResourceDescriptor resource, ValidationContext ctx, CancellationToken ct)
    {
        var errors = new List<string>();
        var constraints = GetConstraints(ctx.Platform);
        var validSizes = constraints.ValidSizes ?? DefaultValidSizes;
        var validVersions = constraints.ValidVersions ?? DefaultValidVersions;

        if (resource.Size is not null && !validSizes.Contains(resource.Size))
            errors.Add($"Invalid Postgres size '{resource.Size}'. Valid sizes: {string.Join(", ", validSizes)}");

        var version = resource.Properties.TryGetValue("version", out var v) ? v.GetString() : null;
        if (version is not null && !validVersions.Contains(version))
            errors.Add($"Invalid Postgres version '{version}'. Valid versions: {string.Join(", ", validVersions)}");

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(ResourceDescriptor resource, PlanContext ctx, CancellationToken ct)
    {
        var constraints = GetConstraints(ctx.Platform);
        var defaultVersion = constraints.DefaultVersion ?? DefaultVersion;
        var size = resource.Size ?? "s";
        var version = resource.Properties.TryGetValue("version", out var v) ? v.GetString() ?? defaultVersion : defaultVersion;
        var ha = resource.Properties.TryGetValue("ha", out var h) ? h.GetBoolean() : ctx.EnvironmentConfig.Defaults?.Ha ?? false;

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceType = "postgres",
            Action = "create",
            PlannedOutputs = new Dictionary<string, string>
            {
                ["connectionString"] = $"<pending:{ctx.AppName}-postgres>",
                ["host"] = $"<pending:{ctx.AppName}-postgres-host>",
                ["port"] = constraints.Port ?? DefaultPort
            },
            Configuration = new Dictionary<string, object?>
            {
                ["version"] = version,
                ["size"] = size,
                ["ha"] = ha,
                ["appName"] = ctx.AppName,
                ["environment"] = ctx.Environment,
                ["region"] = ctx.Platform.Defaults.Region
            }
        });
    }
}
