using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;

namespace Deskribe.Sdk;

public interface IResourceProvider
{
    string ResourceType { get; }
    Task<ValidationResult> ValidateAsync(DeskribeResource resource, ValidationContext ctx, CancellationToken ct = default);
    Task<ResourcePlanResult> PlanAsync(DeskribeResource resource, PlanContext ctx, CancellationToken ct = default);
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
    public required string Environment { get; init; }
    public required string AppName { get; init; }
}
