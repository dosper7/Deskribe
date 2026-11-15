using Deskribe.Core.Config;
using Deskribe.Core.Merging;
using Deskribe.Core.Plugins;
using Deskribe.Core.Resolution;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Validation;

/// <summary>
/// Validates a loaded manifest against the platform's rules and the registered plugins.
/// Reports every problem found rather than stopping at the first.
/// </summary>
public class ManifestValidator
{
    private readonly PluginRegistry _registry;
    private readonly ILogger<ManifestValidator> _logger;

    public ManifestValidator(PluginRegistry registry, ILogger<ManifestValidator> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task<ValidationResult> ValidateAsync(LoadedConfiguration cfg, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var manifest = cfg.Manifest;
        var platform = cfg.Platform;
        var defaults = cfg.Defaults;

        CheckLabel(errors, $"App name '{manifest.Name}'", manifest.Name);
        if (manifest.Team is not null)
            CheckLabel(errors, $"Team '{manifest.Team}'", manifest.Team);
        CheckLabel(errors, $"Namespace '{cfg.Namespace}' (from pattern '{defaults.NamespacePattern}')", cfg.Namespace);

        if (_registry.GetRuntimePlugin(platform.Runtime.Name) is null)
            errors.Add($"Runtime '{platform.Runtime.Name}' (platform runtime.name) has no registered runtime plugin.");

        var resourcesByKey = ValidateResources(manifest, platform, errors);
        ValidateServices(cfg, resourcesByKey, errors);
        ValidatePolicies(defaults, platform, errors, warnings);

        var validationCtx = new ValidationContext { Platform = platform, Environment = cfg.Environment };
        foreach (var resource in manifest.Resources)
        {
            var provider = _registry.GetResourceProvider(resource.Type);
            if (provider is null)
                continue;
            var result = await provider.ValidateAsync(resource, validationCtx, ct);
            errors.AddRange(result.Errors.Select(e => $"Resource '{resource.Key}': {e}"));
            warnings.AddRange(result.Warnings.Select(w => $"Resource '{resource.Key}': {w}"));
        }

        errors = errors.Distinct().ToList();
        _logger.LogInformation("Validation: {ErrorCount} errors, {WarningCount} warnings", errors.Count, warnings.Count);

        return new ValidationResult { IsValid = errors.Count == 0, Errors = errors, Warnings = warnings };
    }

    private Dictionary<string, ResourceDescriptor> ValidateResources(
        DeskribeManifest manifest, PlatformConfig platform, List<string> errors)
    {
        var byKey = new Dictionary<string, ResourceDescriptor>(StringComparer.Ordinal);
        foreach (var resource in manifest.Resources)
        {
            var key = resource.Key;
            if (!ResourceReferences.IsValidKey(key))
                errors.Add($"Resource key '{key}' is invalid; keys may only contain letters, digits, '.', '_' and '-'.");
            else if (!byKey.TryAdd(key, resource))
                errors.Add($"Resource key '{key}' is declared more than once; give each resource of the same type a distinct 'name'.");

            if (_registry.GetResourceProvider(resource.Type) is null)
                errors.Add($"Resource type '{resource.Type}' has no registered resource provider.");

            if (!platform.Provisioners.TryGetValue(resource.Type, out var provisionerName))
                errors.Add($"Resource type '{resource.Type}' is not mapped to a provisioner in the platform 'provisioners' config.");
            else if (_registry.GetProvisioner(provisionerName) is null)
                errors.Add($"Provisioner '{provisionerName}' (mapped for resource type '{resource.Type}') is not registered.");
        }
        return byKey;
    }

    private void ValidateServices(
        LoadedConfiguration cfg, Dictionary<string, ResourceDescriptor> resourcesByKey, List<string> errors)
    {
        var manifest = cfg.Manifest;
        var limits = cfg.Platform.Policies.Limits;

        decimal? maxCpu = null, maxMemory = null;
        if (limits.MaxCpu is not null)
        {
            if (Quantity.TryParseCpu(limits.MaxCpu, out var c)) maxCpu = c;
            else errors.Add($"Platform policy limits.maxCpu '{limits.MaxCpu}' is not a valid CPU quantity.");
        }
        if (limits.MaxMemory is not null)
        {
            if (Quantity.TryParseMemory(limits.MaxMemory, out var m)) maxMemory = m;
            else errors.Add($"Platform policy limits.maxMemory '{limits.MaxMemory}' is not a valid memory quantity.");
        }

        if (manifest.Services.Count > 1)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < manifest.Services.Count; i++)
            {
                var name = manifest.Services[i].Name;
                if (string.IsNullOrEmpty(name))
                    errors.Add($"Service #{i + 1} has no name; every service needs a unique name when the app has several.");
                else if (!seen.Add(name))
                    errors.Add($"Service name '{name}' is used more than once.");
            }
        }

        for (var i = 0; i < manifest.Services.Count; i++)
        {
            var service = manifest.Services[i];
            var workloadName = MergeEngine.GetWorkloadName(manifest, service);
            var label = workloadName is not null ? $"Workload '{workloadName}'" : $"Service #{i + 1}";

            if (workloadName is not null)
                CheckLabel(errors, label + " name", workloadName);

            foreach (var envKey in service.Overrides.Keys)
            {
                if (!cfg.KnownEnvironments.Contains(envKey, StringComparer.Ordinal))
                    errors.Add($"{label}: override for environment '{envKey}' does not match any environment the platform defines ({FormatList(cfg.KnownEnvironments)}).");
            }

            foreach (var (envVar, value) in service.Env)
                ValidateReferences(label, envVar, value, resourcesByKey, errors);

            var (replicas, cpu, memory) = MergeEngine.ResolveSizing(service, cfg.Defaults, cfg.Environment);
            if (replicas < 1)
                errors.Add($"{label}: replicas must be at least 1 (got {replicas}).");
            else if (limits.MaxReplicas is { } maxReplicas && replicas > maxReplicas)
                errors.Add($"{label}: replicas {replicas} exceeds the platform limit of {maxReplicas}.");

            if (!Quantity.TryParseCpu(cpu, out var cpuCores))
                errors.Add($"{label}: cpu '{cpu}' is not a valid Kubernetes CPU quantity (for example 500m, 1, 0.5).");
            else if (maxCpu is { } mc && cpuCores > mc)
                errors.Add($"{label}: cpu '{cpu}' exceeds the platform limit of '{limits.MaxCpu}'.");

            if (!Quantity.TryParseMemory(memory, out var memBytes))
                errors.Add($"{label}: memory '{memory}' is not a valid Kubernetes memory quantity (for example 512Mi, 1Gi).");
            else if (maxMemory is { } mm && memBytes > mm)
                errors.Add($"{label}: memory '{memory}' exceeds the platform limit of '{limits.MaxMemory}'.");
        }
    }

    private void ValidateReferences(
        string label, string envVar, string value,
        Dictionary<string, ResourceDescriptor> resourcesByKey, List<string> errors)
    {
        foreach (var malformed in ResourceReferences.FindMalformed(value))
            errors.Add($"{label}: env var '{envVar}' has a malformed resource reference '{malformed}'. Expected @resource(<key>).<output>.");

        foreach (var reference in ResourceReferences.Extract(envVar, value))
        {
            if (!resourcesByKey.TryGetValue(reference.ResourceKey, out var resource))
            {
                errors.Add($"{label}: env var '{envVar}' references unknown resource '{reference.ResourceKey}' (declared: {FormatList(resourcesByKey.Keys)}).");
                continue;
            }

            var provider = _registry.GetResourceProvider(resource.Type);
            if (provider is null)
                continue;

            var outputs = provider.GetSchema().ProvidedOutputs;
            if (!outputs.Contains(reference.Output, StringComparer.Ordinal))
                errors.Add($"{label}: env var '{envVar}' references output '{reference.Output}' which resource '{reference.ResourceKey}' ({resource.Type}) does not provide (available: {FormatList(outputs)}).");
        }
    }

    private static void ValidatePolicies(
        PlatformDefaults defaults, PlatformConfig platform, List<string> errors, List<string> warnings)
    {
        var policies = platform.Policies;
        if (policies.AllowedRegions.Count > 0 &&
            (defaults.Region is null || !policies.AllowedRegions.Contains(defaults.Region, StringComparer.Ordinal)))
        {
            errors.Add($"Region '{defaults.Region}' is not allowed by platform policy (allowed: {FormatList(policies.AllowedRegions)}).");
        }

        if (defaults.SecretsStrategy is null || !SecretsStrategies.All.Contains(defaults.SecretsStrategy))
            errors.Add($"Secrets strategy '{defaults.SecretsStrategy}' is not supported (supported: {FormatList(SecretsStrategies.All.Order())}).");

        if (policies.EnforceTls)
            warnings.Add("Platform policy 'enforceTLS' is declared but not enforced by Deskribe.");
    }

    private static void CheckLabel(List<string> errors, string what, string? value)
    {
        if (!DnsLabel.IsValid(value))
            errors.Add($"{what} is not a valid DNS-1123 label (lowercase letters, digits and '-', starting and ending with a letter or digit, at most {DnsLabel.MaxLength} characters).");
    }

    private static string FormatList(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }
}
