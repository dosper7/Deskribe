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

        var releaseName = $"{ctx.AppName}-redis";
        var ns = ctx.Platform.Defaults.NamespacePattern
            .Replace("{app}", ctx.AppName)
            .Replace("{env}", ctx.Environment);

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceType = "redis",
            Action = "create",
            PlannedOutputs = new Dictionary<string, string>
            {
                ["endpoint"] = $"{releaseName}-master.{ns}.svc.cluster.local:6379",
                ["host"] = $"{releaseName}-master.{ns}.svc.cluster.local",
                ["port"] = "6379"
            },
            Configuration = new Dictionary<string, object?>
            {
                ["helmRelease"] = releaseName,
                ["helmChart"] = "oci://registry-1.docker.io/bitnamicharts/redis",
                ["ha"] = ha,
                ["namespace"] = ns
            }
        });
    }
}
