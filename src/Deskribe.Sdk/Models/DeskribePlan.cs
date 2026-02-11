namespace Deskribe.Sdk.Models;

public sealed record DeskribePlan
{
    public required string AppName { get; init; }
    public required string Environment { get; init; }
    public required PlatformConfig Platform { get; init; }
    public required EnvironmentConfig EnvironmentConfig { get; init; }
    public List<ResourcePlanResult> ResourcePlans { get; init; } = [];
    public WorkloadPlan? Workload { get; init; }
    public List<string> Warnings { get; init; } = [];
}
