using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Resources.Postgres;

public class PostgresResourceProvider : IResourceProvider
{
    public string ResourceType => "postgres";

    private static readonly HashSet<string> ValidSizes = ["xs", "s", "m", "l", "xl"];
    private static readonly HashSet<string> ValidVersions = ["14", "15", "16", "17"];

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

        if (resource.Size is not null && !ValidSizes.Contains(resource.Size))
            errors.Add($"Invalid Postgres size '{resource.Size}'. Valid sizes: {string.Join(", ", ValidSizes)}");

        var version = resource.Properties.TryGetValue("version", out var v) ? v.GetString() : null;
        if (version is not null && !ValidVersions.Contains(version))
            errors.Add($"Invalid Postgres version '{version}'. Valid versions: {string.Join(", ", ValidVersions)}");

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(ResourceDescriptor resource, PlanContext ctx, CancellationToken ct)
    {
        var size = resource.Size ?? "s";
        var version = resource.Properties.TryGetValue("version", out var v) ? v.GetString() ?? "16" : "16";
        var ha = resource.Properties.TryGetValue("ha", out var h) ? h.GetBoolean() : ctx.EnvironmentConfig.Defaults?.Ha ?? false;

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceType = "postgres",
            Action = "create",
            PlannedOutputs = new Dictionary<string, string>
            {
                ["connectionString"] = $"<pending:{ctx.AppName}-postgres>",
                ["host"] = $"<pending:{ctx.AppName}-postgres-host>",
                ["port"] = "5432"
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
