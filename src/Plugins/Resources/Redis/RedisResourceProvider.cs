using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;

namespace Deskribe.Plugins.Resources.Redis;

public class RedisResourceProvider : IResourceProvider
{
    public string ResourceType => "redis";

    private static readonly HashSet<string> ValidSizes = ["xs", "s", "m", "l", "xl"];

    public Task<ValidationResult> ValidateAsync(DeskribeResource resource, ValidationContext ctx, CancellationToken ct)
    {
        if (resource is not RedisResource redis)
            return Task.FromResult(ValidationResult.Invalid($"Expected RedisResource but got {resource.GetType().Name}"));

        var errors = new List<string>();

        if (redis.Size is not null && !ValidSizes.Contains(redis.Size))
            errors.Add($"Invalid Redis size '{redis.Size}'. Valid sizes: {string.Join(", ", ValidSizes)}");

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(DeskribeResource resource, PlanContext ctx, CancellationToken ct)
    {
        var redis = resource as RedisResource;
        var ha = redis?.Ha ?? ctx.EnvironmentConfig.Defaults.Ha ?? false;

        var size = redis?.Size ?? "s";

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
