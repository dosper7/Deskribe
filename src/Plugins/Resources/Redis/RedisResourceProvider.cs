using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Resources.Redis;

public class RedisResourceProvider : IResourceProvider
{
    public string ResourceType => "redis";

    // Defaults; platform teams replace them via platform.resources.redis
    // (falling back to provisionerConfigs.redis): validSizes, port.
    private static readonly HashSet<string> DefaultValidSizes = ["xs", "s", "m", "l", "xl"];
    private const string DefaultPort = "6379";

    private static readonly string[] AllowedProperties = ["version", "ha", "maxMemoryMb"];

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
        ProvidedOutputs = ["connectionString", "endpoint", "host", "port"],
        SensitiveOutputs = ["connectionString"]
    };

    public Task<ValidationResult> ValidateAsync(ResourceDescriptor resource, ValidationContext ctx, CancellationToken ct)
    {
        var errors = new List<string>();
        var validSizes = ProviderSupport.StringSet(ctx.Platform, "redis", "validSizes") ?? DefaultValidSizes;

        ProviderSupport.RejectUnknown(resource, AllowedProperties, "redis", errors);

        if (resource.Size is not null && !validSizes.Contains(resource.Size))
            errors.Add($"Invalid Redis size '{resource.Size}'. Valid sizes: {string.Join(", ", validSizes)}");

        ProviderSupport.CheckBool(resource, "ha", errors);
        ProviderSupport.CheckKind(resource, "version", JsonValueKind.String, "a string", errors);

        if (resource.Properties.TryGetValue("maxMemoryMb", out var m)
            && !(m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var mb) && mb >= 1))
            errors.Add($"Property 'maxMemoryMb' on resource '{resource.Key}' must be a positive integer");

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(ResourceDescriptor resource, PlanContext ctx, CancellationToken ct)
    {
        var configuration = new Dictionary<string, object?>
        {
            ["size"] = resource.Size ?? "s",
            ["ha"] = ProviderSupport.ReadBool(resource, "ha") ?? ctx.Defaults.Ha ?? false,
            ["port"] = ProviderSupport.Port(ctx.Platform, "redis") ?? DefaultPort,
            ["appName"] = ctx.AppName,
            ["environment"] = ctx.Environment,
            ["region"] = ctx.Defaults.Region
        };
        if (ProviderSupport.ReadString(resource, "version") is { } version)
            configuration["version"] = version;
        if (resource.Properties.TryGetValue("maxMemoryMb", out var m)
            && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var mb))
            configuration["maxMemoryMb"] = mb;

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceKey = resource.Key,
            ResourceType = "redis",
            Configuration = configuration
        });
    }
}
