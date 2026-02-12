using Deskribe.Sdk.Models;

namespace Deskribe.Sdk;

public interface IBackendAdapter
{
    string Name { get; }
    Task<BackendApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct = default);
    Task DestroyAsync(string appName, string environment, PlatformConfig platform, CancellationToken ct = default);
}

public record BackendApplyResult
{
    public bool Success { get; init; }
    public Dictionary<string, Dictionary<string, string>> ResourceOutputs { get; init; } = new();
    public List<string> Errors { get; init; } = [];
}
