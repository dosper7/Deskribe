using Deskribe.Sdk.Models;

namespace Deskribe.Sdk;

public interface IProvisioner
{
    string Name { get; }
    Task<ProvisionResult> ApplyAsync(DeskribePlan plan, CancellationToken ct = default);
    Task<ArtifactResult> GenerateArtifactsAsync(DeskribePlan plan, string outputDir, CancellationToken ct = default);
    Task DestroyAsync(string appName, string environment, PlatformConfig platform, CancellationToken ct = default);
}

public record ProvisionResult
{
    public bool Success { get; init; }
    public Dictionary<string, Dictionary<string, string>> ResourceOutputs { get; init; } = new();
    public List<string> Errors { get; init; } = [];
}

public record ArtifactResult
{
    public bool Success { get; init; }
    public List<GeneratedFile> Files { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}

public record GeneratedFile
{
    public required string Path { get; init; }
    public required string Content { get; init; }
    public string Format { get; init; } = "json";
}
