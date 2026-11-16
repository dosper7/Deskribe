namespace Deskribe.Sdk.Models;

public sealed record DeskribePlan
{
    public required string AppName { get; init; }
    public string? Team { get; init; }
    public required string Environment { get; init; }
    public required string Namespace { get; init; }
    public required PlatformConfig Platform { get; init; }
    public required EnvironmentConfig EnvironmentConfig { get; init; }

    /// <summary>Platform defaults with the environment's overrides already applied.</summary>
    public required PlatformDefaults Defaults { get; init; }
    public List<ResourcePlanResult> ResourcePlans { get; init; } = [];
    public List<WorkloadPlan> Workloads { get; init; } = [];

    /// <summary>The contract between the app and its resources: one entry per reference.</summary>
    public List<Binding> Bindings { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// One <c>@resource(key).output</c> reference made by a service's environment variable.
/// </summary>
public sealed record Binding
{
    /// <summary>Workload name (see <see cref="WorkloadPlan.Name"/>).</summary>
    public required string Workload { get; init; }
    public required string EnvVar { get; init; }
    public required string ResourceKey { get; init; }
    public required string ResourceType { get; init; }
    public required string Output { get; init; }
    public bool Sensitive { get; init; }
}
