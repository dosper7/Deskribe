using Deskribe.Sdk.Models;

namespace Deskribe.Sdk;

public interface IResourceProvider
{
    string ResourceType { get; }
    ResourceSchema GetSchema();
    Task<ValidationResult> ValidateAsync(ResourceDescriptor resource, ValidationContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Translates the developer's descriptor into the configuration a provisioner needs.
    /// The result's <see cref="ResourcePlanResult.ResourceKey"/> must be <see cref="ResourceDescriptor.Key"/>.
    /// </summary>
    Task<ResourcePlanResult> PlanAsync(ResourceDescriptor resource, PlanContext ctx, CancellationToken ct = default);
}

public record ValidationContext
{
    public required PlatformConfig Platform { get; init; }
    public required string Environment { get; init; }
}

public record PlanContext
{
    public required PlatformConfig Platform { get; init; }
    public required EnvironmentConfig EnvironmentConfig { get; init; }

    /// <summary>Platform defaults with the environment's overrides already applied. Use this, not <c>Platform.Defaults</c>.</summary>
    public required PlatformDefaults Defaults { get; init; }
    public required string Environment { get; init; }
    public required string AppName { get; init; }
}
