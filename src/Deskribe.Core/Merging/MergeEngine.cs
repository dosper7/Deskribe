using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Merging;

public class MergeEngine
{
    private readonly ILogger<MergeEngine> _logger;

    // Sensible defaults when nothing is specified
    private const string DefaultRuntime = "kubernetes";
    private const string DefaultRegion = "westeurope";
    private const int DefaultReplicas = 2;
    private const string DefaultCpu = "250m";
    private const string DefaultMemory = "512Mi";
    private const string DefaultNamespacePattern = "{app}-{env}";
    private const string DefaultSecretsStrategy = "opaque";

    public MergeEngine(ILogger<MergeEngine> logger)
    {
        _logger = logger;
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
        var replicas = envConfig.Defaults?.Replicas ?? platform.Defaults.Replicas ?? DefaultReplicas;
        var cpu = envConfig.Defaults?.Cpu ?? platform.Defaults.Cpu ?? DefaultCpu;
        var memory = envConfig.Defaults?.Memory ?? platform.Defaults.Memory ?? DefaultMemory;

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

        var nsPattern = platform.Defaults.NamespacePattern ?? DefaultNamespacePattern;
        var ns = nsPattern
            .Replace("{app}", manifest.Name)
            .Replace("{env}", environment);

        return new WorkloadPlan
        {
            AppName = manifest.Name,
            Environment = environment,
            Namespace = ns,
            Image = image,
            Replicas = replicas,
            Cpu = cpu,
            Memory = memory,
            EnvironmentVariables = service?.Env ?? new(),
            SecretsStrategy = platform.Defaults.SecretsStrategy ?? DefaultSecretsStrategy,
            ExternalSecretsStore = platform.Defaults.ExternalSecretsStore
        };
    }

    public PlatformDefaults MergeDefaults(PlatformConfig platform, EnvironmentConfig envConfig)
    {
        return new PlatformDefaults
        {
            Runtime = envConfig.Defaults?.Runtime ?? platform.Defaults.Runtime ?? DefaultRuntime,
            Region = envConfig.Defaults?.Region ?? platform.Defaults.Region ?? DefaultRegion,
            Replicas = envConfig.Defaults?.Replicas ?? platform.Defaults.Replicas ?? DefaultReplicas,
            Cpu = envConfig.Defaults?.Cpu ?? platform.Defaults.Cpu ?? DefaultCpu,
            Memory = envConfig.Defaults?.Memory ?? platform.Defaults.Memory ?? DefaultMemory,
            NamespacePattern = envConfig.Defaults?.NamespacePattern ?? platform.Defaults.NamespacePattern ?? DefaultNamespacePattern,
            Ha = envConfig.Defaults?.Ha ?? platform.Defaults.Ha
        };
    }
}
