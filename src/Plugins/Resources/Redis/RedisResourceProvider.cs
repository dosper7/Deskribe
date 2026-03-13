using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Resources.Redis;

public class RedisResourceProvider : IResourceProvider
{
    public string ResourceType => "redis";

    private static readonly HashSet<string> ValidSizes = ["xs", "s", "m", "l", "xl"];

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

        if (resource.Size is not null && !ValidSizes.Contains(resource.Size))
            errors.Add($"Invalid Redis size '{resource.Size}'. Valid sizes: {string.Join(", ", ValidSizes)}");

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(ResourceDescriptor resource, PlanContext ctx, CancellationToken ct)
    {
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
                ["port"] = "6379"
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
