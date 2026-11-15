using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Resources.Postgres;

public class PostgresResourceProvider : IResourceProvider
{
    public string ResourceType => "postgres";

    // Defaults; platform teams replace them via platform.resources.postgres
    // (falling back to provisionerConfigs.postgres): validSizes, validVersions, defaultVersion, port.
    private static readonly HashSet<string> DefaultValidSizes = ["xs", "s", "m", "l", "xl"];
    private static readonly HashSet<string> DefaultValidVersions = ["14", "15", "16", "17"];
    private const string DefaultVersion = "16";
    private const string DefaultPort = "5432";

    private static readonly string[] AllowedProperties = ["version", "ha", "sku"];

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
        ProvidedOutputs = ["connectionString", "host", "port", "database"],
        SensitiveOutputs = ["connectionString"]
    };

    public Task<ValidationResult> ValidateAsync(ResourceDescriptor resource, ValidationContext ctx, CancellationToken ct)
    {
        var errors = new List<string>();
        var validSizes = ProviderSupport.StringSet(ctx.Platform, "postgres", "validSizes") ?? DefaultValidSizes;
        var validVersions = ProviderSupport.StringSet(ctx.Platform, "postgres", "validVersions") ?? DefaultValidVersions;

        ProviderSupport.RejectUnknown(resource, AllowedProperties, "postgres", errors);

        if (resource.Size is not null && !validSizes.Contains(resource.Size))
            errors.Add($"Invalid Postgres size '{resource.Size}'. Valid sizes: {string.Join(", ", validSizes)}");

        ProviderSupport.CheckBool(resource, "ha", errors);
        ProviderSupport.CheckKind(resource, "sku", JsonValueKind.String, "a string", errors);

        if (resource.Properties.TryGetValue("version", out var v))
        {
            if (v.ValueKind != JsonValueKind.String)
                errors.Add($"Property 'version' on resource '{resource.Key}' must be a string");
            else if (!validVersions.Contains(v.GetString()!))
                errors.Add($"Invalid Postgres version '{v.GetString()}'. Valid versions: {string.Join(", ", validVersions)}");
        }

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(ResourceDescriptor resource, PlanContext ctx, CancellationToken ct)
    {
        var defaultVersion = ProviderSupport.String(ctx.Platform, "postgres", "defaultVersion") ?? DefaultVersion;
        var configuration = new Dictionary<string, object?>
        {
            ["version"] = ProviderSupport.ReadString(resource, "version") ?? defaultVersion,
            ["size"] = resource.Size ?? "s",
            ["ha"] = ProviderSupport.ReadBool(resource, "ha") ?? ctx.Defaults.Ha ?? false,
            ["port"] = ProviderSupport.Port(ctx.Platform, "postgres") ?? DefaultPort,
            ["appName"] = ctx.AppName,
            ["environment"] = ctx.Environment,
            ["region"] = ctx.Defaults.Region
        };
        if (ProviderSupport.ReadString(resource, "sku") is { } sku)
            configuration["sku"] = sku;

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceKey = resource.Key,
            ResourceType = "postgres",
            Configuration = configuration
        });
    }
}
