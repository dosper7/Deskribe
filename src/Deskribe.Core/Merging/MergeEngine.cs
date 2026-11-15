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

    public WorkloadPlan MergeWorkloadPlan(
        DeskribeManifest manifest,
        PlatformConfig platform,
        EnvironmentConfig envConfig,
        string environment,
        string? image)
    {
        _logger.LogInformation("Merging workload plan for {App} in {Env}", manifest.Name, environment);

        // Nullable-based 3-tier merge: platform -> environment -> developer
        var replicas = envConfig.Defaults?.Replicas ?? platform.Defaults.Replicas ?? _defaults.Replicas;
        var cpu = envConfig.Defaults?.Cpu ?? platform.Defaults.Cpu ?? _defaults.Cpu;
        var memory = envConfig.Defaults?.Memory ?? platform.Defaults.Memory ?? _defaults.Memory;

        // Developer per-env overrides win
        var service = manifest.Services.FirstOrDefault();
        if (service?.Overrides.TryGetValue(environment, out var devOverride) == true)
        {
            if (devOverride.Replicas.HasValue)
                replicas = devOverride.Replicas.Value;
            if (devOverride.Cpu is not null)
                cpu = devOverride.Cpu;
            if (devOverride.Memory is not null)
                memory = devOverride.Memory;
        }

        var nsPattern = platform.Defaults.NamespacePattern ?? _defaults.NamespacePattern;
        var ns = NamespaceInterpolator.Resolve(nsPattern, manifest.Team, manifest.Name, environment);

        return new WorkloadPlan
        {
            AppName = manifest.Name,
            Environment = environment,
            Team = manifest.Team,
            Namespace = ns,
            Image = image,
            Replicas = replicas,
            Cpu = cpu,
            Memory = memory,
            EnvironmentVariables = service?.Env ?? new(),
            SecretsStrategy = platform.Defaults.SecretsStrategy ?? _defaults.SecretsStrategy,
            ExternalSecretsStore = platform.Defaults.ExternalSecretsStore
        };
    }

    public PlatformDefaults MergeDefaults(PlatformConfig platform, EnvironmentConfig envConfig)
    {
        return new PlatformDefaults
        {
            Runtime = envConfig.Defaults?.Runtime ?? platform.Defaults.Runtime ?? _defaults.Runtime,
            Region = envConfig.Defaults?.Region ?? platform.Defaults.Region ?? _defaults.Region,
            Replicas = envConfig.Defaults?.Replicas ?? platform.Defaults.Replicas ?? _defaults.Replicas,
            Cpu = envConfig.Defaults?.Cpu ?? platform.Defaults.Cpu ?? _defaults.Cpu,
            Memory = envConfig.Defaults?.Memory ?? platform.Defaults.Memory ?? _defaults.Memory,
            NamespacePattern = envConfig.Defaults?.NamespacePattern ?? platform.Defaults.NamespacePattern ?? _defaults.NamespacePattern,
            Ha = envConfig.Defaults?.Ha ?? platform.Defaults.Ha
        };
    }
}
