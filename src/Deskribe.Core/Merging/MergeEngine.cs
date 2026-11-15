using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Merging;

public class MergeEngine
{
    private readonly ILogger<MergeEngine> _logger;
    private readonly EngineDefaults _defaults;

    public MergeEngine(ILogger<MergeEngine> logger, EngineDefaults? defaults = null)
    {
        _logger = logger;
        _defaults = defaults ?? new EngineDefaults();
    }

    /// <summary>
    /// Effective defaults for one environment: environment value, else platform value,
    /// else engine default, for every field.
    /// </summary>
    public PlatformDefaults MergeDefaults(PlatformConfig platform, EnvironmentConfig? envConfig)
    {
        var env = envConfig?.Defaults;
        var plat = platform.Defaults;
        return new PlatformDefaults
        {
            Runtime = env?.Runtime ?? plat.Runtime ?? _defaults.Runtime,
            Region = env?.Region ?? plat.Region ?? _defaults.Region,
            Replicas = env?.Replicas ?? plat.Replicas ?? _defaults.Replicas,
            Cpu = env?.Cpu ?? plat.Cpu ?? _defaults.Cpu,
            Memory = env?.Memory ?? plat.Memory ?? _defaults.Memory,
            NamespacePattern = env?.NamespacePattern ?? plat.NamespacePattern ?? _defaults.NamespacePattern,
            Ha = env?.Ha ?? plat.Ha ?? _defaults.Ha,
            SecretsStrategy = env?.SecretsStrategy ?? plat.SecretsStrategy ?? _defaults.SecretsStrategy,
            ExternalSecretsStore = env?.ExternalSecretsStore ?? plat.ExternalSecretsStore ?? _defaults.ExternalSecretsStore
        };
    }

    public static string ResolveNamespace(PlatformDefaults defaults, string? team, string app, string environment) =>
        NamespaceInterpolator.Resolve(defaults.NamespacePattern ?? "{app}-{env}", team, app, environment);

    /// <summary>
    /// The workload name for a service: the app name when the manifest has exactly one
    /// service, otherwise <c>{app}-{service}</c>. Null when a name cannot be derived.
    /// </summary>
    public static string? GetWorkloadName(DeskribeManifest manifest, ServiceDefinition service)
    {
        if (manifest.Services.Count == 1)
            return manifest.Name;
        return string.IsNullOrEmpty(service.Name) ? null : $"{manifest.Name}-{service.Name}";
    }

    /// <summary>Replicas/cpu/memory for a service: per-environment override, else effective default.</summary>
    public static (int Replicas, string Cpu, string Memory) ResolveSizing(
        ServiceDefinition service, PlatformDefaults defaults, string environment)
    {
        service.Overrides.TryGetValue(environment, out var o);
        return (
            o?.Replicas ?? defaults.Replicas ?? 2,
            o?.Cpu ?? defaults.Cpu ?? "250m",
            o?.Memory ?? defaults.Memory ?? "512Mi");
    }

    /// <summary>
    /// Image lookup: by service name, then by workload name; with exactly one service and
    /// exactly one image supplied, that image whatever its key.
    /// </summary>
    public static string? FindImage(
        DeskribeManifest manifest, ServiceDefinition service, string workloadName, IReadOnlyDictionary<string, string>? images)
    {
        if (images is null || images.Count == 0)
            return null;
        if (service.Name is not null && images.TryGetValue(service.Name, out var byService))
            return byService;
        if (images.TryGetValue(workloadName, out var byWorkload))
            return byWorkload;
        if (manifest.Services.Count == 1 && images.Count == 1)
            return images.Values.First();
        return null;
    }

    /// <summary>Builds the workload for one service. Secret keys are classified by the caller.</summary>
    public WorkloadPlan BuildWorkload(
        DeskribeManifest manifest,
        ServiceDefinition service,
        PlatformDefaults defaults,
        string environment,
        string ns,
        IReadOnlyDictionary<string, string>? images)
    {
        var name = GetWorkloadName(manifest, service)
            ?? throw new DeskribeException($"Service in app '{manifest.Name}' has no name; every service needs one when the app has several.");
        var (replicas, cpu, memory) = ResolveSizing(service, defaults, environment);

        _logger.LogDebug("Merged workload {Workload} for {Env}", name, environment);

        return new WorkloadPlan
        {
            Name = name,
            AppName = manifest.Name,
            ServiceName = service.Name,
            Environment = environment,
            Namespace = ns,
            Team = manifest.Team,
            Image = FindImage(manifest, service, name, images),
            Replicas = replicas,
            Cpu = cpu,
            Memory = memory,
            EnvironmentVariables = new Dictionary<string, string>(service.Env),
            SecretsStrategy = defaults.SecretsStrategy ?? SecretsStrategies.Opaque,
            ExternalSecretsStore = defaults.ExternalSecretsStore
        };
    }
}
