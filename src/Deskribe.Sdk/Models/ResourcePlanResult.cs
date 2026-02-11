namespace Deskribe.Sdk.Models;

public sealed record ResourcePlanResult
{
    public required string ResourceType { get; init; }
    public required string Action { get; init; } // "create", "update", "no-change"
    public Dictionary<string, string> PlannedOutputs { get; init; } = new();
    public Dictionary<string, object?> Configuration { get; init; } = new();
}
