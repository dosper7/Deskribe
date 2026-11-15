using Deskribe.Sdk.Models;

namespace Deskribe.Sdk;

/// <summary>
/// Deploys workloads to a runtime (Kubernetes is the supported target). The plugin owns
/// everything runtime-specific: manifest rendering, on-disk artifact layout, applying,
/// pruning, rollout tracking, and teardown.
/// </summary>
public interface IRuntimePlugin
{
    string Name { get; }

    /// <summary>Renders the manifests for <see cref="RuntimeContext.Workloads"/> without touching the runtime.</summary>
    Task<RuntimeArtifact> RenderAsync(RuntimeContext ctx, CancellationToken ct = default);

    /// <summary>Produces the files a GitOps pipeline would commit (layout is the plugin's decision).</summary>
    Task<IReadOnlyList<GeneratedFile>> GenerateArtifactsAsync(RuntimeContext ctx, string outputDir, CancellationToken ct = default);

    /// <summary>
    /// Applies the workloads idempotently, removes objects previously deployed for this
    /// app that are no longer declared, and waits for the rollout to become ready.
    /// Throws when the rollout does not complete.
    /// </summary>
    Task<RuntimeApplyResult> ApplyAsync(RuntimeContext ctx, CancellationToken ct = default);

    Task<RuntimeStatus> GetStatusAsync(RuntimeContext ctx, CancellationToken ct = default);

    /// <summary>Removes only what was deployed for this app/environment.</summary>
    Task DestroyAsync(RuntimeContext ctx, CancellationToken ct = default);
}

public sealed record RuntimeContext
{
    public required string AppName { get; init; }
    public string? Team { get; init; }
    public required string Environment { get; init; }
    public required string Namespace { get; init; }
    public required PlatformConfig Platform { get; init; }

    /// <summary>Platform defaults with the environment's overrides already applied.</summary>
    public required PlatformDefaults Defaults { get; init; }

    /// <summary>Workloads to deploy. Empty for status and destroy.</summary>
    public IReadOnlyList<WorkloadPlan> Workloads { get; init; } = [];
}

public sealed record WorkloadPlan
{
    /// <summary>Runtime object name, unique within the app's namespace.</summary>
    public required string Name { get; init; }
    public required string AppName { get; init; }

    /// <summary>Service name from the manifest; null for a single unnamed service.</summary>
    public string? ServiceName { get; init; }
    public required string Environment { get; init; }
    public required string Namespace { get; init; }
    public string? Team { get; init; }
    public string? Image { get; init; }
    public int Replicas { get; init; } = 2;
    public string Cpu { get; init; } = "250m";
    public string Memory { get; init; } = "512Mi";

    /// <summary>
    /// All environment variables. Values may still contain <c>@resource(key).output</c>
    /// references until the engine resolves them.
    /// </summary>
    public Dictionary<string, string> EnvironmentVariables { get; init; } = new();

    /// <summary>
    /// Names of the variables in <see cref="EnvironmentVariables"/> that carry sensitive
    /// values. These must be delivered through the secrets strategy, never inline.
    /// </summary>
    public HashSet<string> SecretKeys { get; init; } = [];

    /// <summary><c>opaque</c> or <c>external-secrets</c>.</summary>
    public string SecretsStrategy { get; init; } = SecretsStrategies.Opaque;
    public string? ExternalSecretsStore { get; init; }
}

public static class SecretsStrategies
{
    /// <summary>Deskribe writes the resolved values into a runtime secret on apply.</summary>
    public const string Opaque = "opaque";

    /// <summary>The runtime pulls values from an external store; Deskribe never sees them.</summary>
    public const string ExternalSecrets = "external-secrets";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Opaque, ExternalSecrets };
}

public sealed record RuntimeArtifact
{
    public required string Namespace { get; init; }
    public required string Yaml { get; init; }
    public List<string> ResourceNames { get; init; } = [];
}

public sealed record RuntimeApplyResult
{
    public required string Namespace { get; init; }
    public List<string> Applied { get; init; } = [];
    public List<string> Pruned { get; init; } = [];
}

public sealed record RuntimeStatus
{
    public required string Namespace { get; init; }

    /// <summary>False when nothing is deployed for this app/environment.</summary>
    public bool Exists { get; init; }
    public List<WorkloadStatus> Workloads { get; init; } = [];
}

public sealed record WorkloadStatus
{
    public required string Name { get; init; }
    public string? Image { get; init; }
    public int DesiredReplicas { get; init; }

    /// <summary>Replicas that are ready and run the currently requested version.</summary>
    public int ReadyReplicas { get; init; }
    public bool Ready => DesiredReplicas > 0 && ReadyReplicas >= DesiredReplicas;
}
