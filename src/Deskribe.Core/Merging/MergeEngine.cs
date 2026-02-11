using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Merging;

public class MergeEngine
{
    private readonly ILogger<MergeEngine> _logger;

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

        // Start with platform defaults
        var replicas = platform.Defaults.Replicas;
        var cpu = platform.Defaults.Cpu;
        var memory = platform.Defaults.Memory;

        // Override with environment defaults
        if (envConfig.Defaults.Replicas != 0 && envConfig.Defaults.Replicas != platform.Defaults.Replicas)
            replicas = envConfig.Defaults.Replicas;
        if (envConfig.Defaults.Cpu != platform.Defaults.Cpu)
            cpu = envConfig.Defaults.Cpu;
        if (envConfig.Defaults.Memory != platform.Defaults.Memory)
            memory = envConfig.Defaults.Memory;

        // Override with developer per-env overrides
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

        var ns = platform.Defaults.NamespacePattern
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
            EnvironmentVariables = service?.Env ?? new()
        };
    }

    public PlatformDefaults MergeDefaults(PlatformConfig platform, EnvironmentConfig envConfig)
    {
        return new PlatformDefaults
        {
            Runtime = envConfig.Defaults.Runtime != "kubernetes" ? envConfig.Defaults.Runtime : platform.Defaults.Runtime,
            Region = envConfig.Defaults.Region != "westeurope" ? envConfig.Defaults.Region : platform.Defaults.Region,
            Replicas = envConfig.Defaults.Replicas != 0 ? envConfig.Defaults.Replicas : platform.Defaults.Replicas,
            Cpu = envConfig.Defaults.Cpu != "250m" ? envConfig.Defaults.Cpu : platform.Defaults.Cpu,
            Memory = envConfig.Defaults.Memory != "512Mi" ? envConfig.Defaults.Memory : platform.Defaults.Memory,
            NamespacePattern = envConfig.Defaults.NamespacePattern != "{app}-{env}" ? envConfig.Defaults.NamespacePattern : platform.Defaults.NamespacePattern,
            Ha = envConfig.Defaults.Ha ?? platform.Defaults.Ha
        };
    }
}
