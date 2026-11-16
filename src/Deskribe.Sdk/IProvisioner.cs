using System.Text.Json;
using Deskribe.Sdk.Models;

namespace Deskribe.Sdk;

/// <summary>
/// Provisions backing infrastructure (databases, caches, messaging) for an application.
/// The engine calls each provisioner once per operation, passing only the resources
/// that the platform config maps to it.
/// </summary>
public interface IProvisioner
{
    string Name { get; }

    /// <summary>
    /// Provisions <see cref="ProvisionContext.Resources"/> and returns the real outputs,
    /// keyed by <see cref="ResourcePlanResult.ResourceKey"/>. Outputs that are not known
    /// must be omitted, never substituted with placeholders.
    /// </summary>
    Task<ProvisionResult> ApplyAsync(ProvisionContext ctx, CancellationToken ct = default);

    Task<ArtifactResult> GenerateArtifactsAsync(ProvisionContext ctx, string outputDir, CancellationToken ct = default);

    Task<ProvisionResult> DestroyAsync(ProvisionContext ctx, CancellationToken ct = default);
}

public sealed record ProvisionContext
{
    public required string AppName { get; init; }
    public string? Team { get; init; }
    public required string Environment { get; init; }
    public required PlatformConfig Platform { get; init; }

    /// <summary>Platform defaults with the environment's overrides already applied.</summary>
    public required PlatformDefaults Defaults { get; init; }

    /// <summary>Only the resources mapped to this provisioner.</summary>
    public required IReadOnlyList<ResourcePlanResult> Resources { get; init; }

    /// <summary>This provisioner's entry in the platform's <c>provisionerConfigs</c>; empty when absent.</summary>
    public IReadOnlyDictionary<string, JsonElement> Config { get; init; } = new Dictionary<string, JsonElement>();
}

public record ProvisionResult
{
    public bool Success { get; init; }

    /// <summary>Resource key → output name → value.</summary>
    public Dictionary<string, Dictionary<string, string>> ResourceOutputs { get; init; } = new();

    public List<string> Errors { get; init; } = [];

    public static ProvisionResult Failed(params string[] errors) => new() { Success = false, Errors = [.. errors] };
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
