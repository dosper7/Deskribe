namespace Deskribe.Sdk.Models;

public sealed record ResourcePlanResult
{
    /// <summary>The resource's key within the app (see <see cref="ResourceDescriptor.Key"/>).</summary>
    public required string ResourceKey { get; init; }
    public required string ResourceType { get; init; }
    public Dictionary<string, object?> Configuration { get; init; } = new();
}
