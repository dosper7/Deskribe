using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Deskribe.Core.Config;
using Deskribe.Core.Merging;
using Deskribe.Core.Plugins;
using Deskribe.Core.Resolution;
using Deskribe.Core.Validation;
using Microsoft.Extensions.Logging;

using SdkValidationResult = Deskribe.Sdk.Models.ValidationResult;

namespace Deskribe.Core.Engine;

public class DeskribeEngine
{
    private readonly ConfigLoader _configLoader;
    private readonly MergeEngine _mergeEngine;
    private readonly ResourceReferenceResolver _resolver;
    private readonly PolicyValidator _validator;
    private readonly PluginHost _pluginHost;
    private readonly ILogger<DeskribeEngine> _logger;

    public DeskribeEngine(
        ConfigLoader configLoader,
        MergeEngine mergeEngine,
        ResourceReferenceResolver resolver,
        PolicyValidator validator,
        PluginHost pluginHost,
        ILogger<DeskribeEngine> logger)
    {
        _configLoader = configLoader;
        _mergeEngine = mergeEngine;
        _resolver = resolver;
        _validator = validator;
        _pluginHost = pluginHost;
        _logger = logger;
    }

    public async Task<SdkValidationResult> ValidateAsync(
        string manifestPath,
        string platformPath,
        string environment,
        CancellationToken ct = default)
    {
        var manifest = await _configLoader.LoadManifestAsync(manifestPath, ct);
        var platform = await _configLoader.LoadPlatformConfigAsync(platformPath, ct);

        var policyResult = _validator.Validate(manifest, platform);
        if (!policyResult.IsValid)
            return policyResult;

        // Validate resource references
        var service = manifest.Services.FirstOrDefault();
        if (service is not null)
        {
            var refs = _resolver.ExtractReferences(service.Env);
            var resourceTypes = manifest.Resources.Select(r => r.Type).ToHashSet();
            var refValidation = _resolver.ValidateReferences(refs, resourceTypes);
            if (!refValidation.IsValid)
            {
                return new SdkValidationResult
                {
                    IsValid = false,
                    Errors = refValidation.Errors,
                    Warnings = policyResult.Warnings
                };
            }
        }

        // Validate each resource with its provider
        var envConfig = await _configLoader.LoadEnvironmentConfigAsync(platformPath, environment, ct);
        var validationCtx = new ValidationContext { Platform = platform, Environment = environment };
        var allErrors = new List<string>();

        foreach (var resource in manifest.Resources)
        {
            var provider = _pluginHost.GetResourceProvider(resource.Type);
            if (provider is null)
            {
                allErrors.Add($"No resource provider registered for type '{resource.Type}'");
                continue;
            }

            var result = await provider.ValidateAsync(resource, validationCtx, ct);
            if (!result.IsValid)
                allErrors.AddRange(result.Errors);
        }

        return new SdkValidationResult
        {
            IsValid = allErrors.Count == 0,
            Errors = allErrors,
            Warnings = policyResult.Warnings
        };
    }

    public async Task<DeskribePlan> PlanAsync(
        string manifestPath,
        string platformPath,
        string environment,
        Dictionary<string, string>? images = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Planning for environment: {Env}", environment);

        var manifest = await _configLoader.LoadManifestAsync(manifestPath, ct);
        var platform = await _configLoader.LoadPlatformConfigAsync(platformPath, ct);
        var envConfig = await _configLoader.LoadEnvironmentConfigAsync(platformPath, environment, ct);

        // Determine image
        string? image = null;
        if (images is not null)
        {
            // Try service name first, then "api" as default
            var service = manifest.Services.FirstOrDefault();
            var serviceName = service?.Name ?? "api";
            images.TryGetValue(serviceName, out image);
        }

        // Merge workload plan
        var workload = _mergeEngine.MergeWorkloadPlan(manifest, platform, envConfig, environment, image);

        // Plan resources
        var planCtx = new PlanContext
        {
            Platform = platform,
            EnvironmentConfig = envConfig,
            Environment = environment,
            AppName = manifest.Name
        };

        var resourcePlans = new List<ResourcePlanResult>();
        var warnings = new List<string>();

        foreach (var resource in manifest.Resources)
        {
            var provider = _pluginHost.GetResourceProvider(resource.Type);
            if (provider is null)
            {
                warnings.Add($"No provider for resource type '{resource.Type}', skipping");
                continue;
            }

            var plan = await provider.PlanAsync(resource, planCtx, ct);
            resourcePlans.Add(plan);
        }

        return new DeskribePlan
        {
            AppName = manifest.Name,
            Environment = environment,
            Platform = platform,
            EnvironmentConfig = envConfig,
            ResourcePlans = resourcePlans,
            Workload = workload,
            Warnings = warnings
        };
    }

    public async Task ApplyAsync(
        DeskribePlan plan,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Applying plan for {App} in {Env}", plan.AppName, plan.Environment);

        // 1. Apply infra via backend adapters
        var resourceOutputs = new Dictionary<string, Dictionary<string, string>>();

        // Merge environment-level backends over platform-level backends
        var mergedBackends = new Dictionary<string, string>(plan.Platform.Backends);
        foreach (var (key, value) in plan.EnvironmentConfig.Backends)
        {
            mergedBackends[key] = value;
        }

        foreach (var resourcePlan in plan.ResourcePlans)
        {
            var backendName = mergedBackends.GetValueOrDefault(resourcePlan.ResourceType, "pulumi");
            var backend = _pluginHost.GetBackendAdapter(backendName);

            if (backend is null)
            {
                _logger.LogWarning("No backend adapter '{Backend}' for resource '{Type}', skipping apply",
                    backendName, resourcePlan.ResourceType);
                continue;
            }

            var result = await backend.ApplyAsync(plan, ct);

            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Backend apply failed for {resourcePlan.ResourceType}: {string.Join(", ", result.Errors)}");
            }

            foreach (var (key, outputs) in result.ResourceOutputs)
            {
                resourceOutputs[key] = outputs;
            }
        }

        // 2. Resolve resource references in env vars
        if (plan.Workload is not null)
        {
            var resolvedEnv = _resolver.ResolveReferences(plan.Workload.EnvironmentVariables, resourceOutputs);
            var resolvedWorkload = plan.Workload with { EnvironmentVariables = resolvedEnv };

            // 3. Deploy to runtime
            var runtimeName = plan.Platform.Defaults.Runtime;
            var runtime = _pluginHost.GetRuntimeAdapter(runtimeName);

            if (runtime is null)
            {
                _logger.LogWarning("No runtime adapter '{Runtime}', skipping deployment", runtimeName);
                return;
            }

            var manifest = await runtime.RenderAsync(resolvedWorkload, ct);
            await runtime.ApplyAsync(manifest, ct);

            _logger.LogInformation("Deployment complete for {App} in {Env}", plan.AppName, plan.Environment);
        }
    }

    public async Task DestroyAsync(
        string manifestPath,
        string platformPath,
        string environment,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Destroying {Env}", environment);

        var manifest = await _configLoader.LoadManifestAsync(manifestPath, ct);
        var platform = await _configLoader.LoadPlatformConfigAsync(platformPath, ct);

        // Destroy runtime resources
        var runtimeName = platform.Defaults.Runtime;
        var runtime = _pluginHost.GetRuntimeAdapter(runtimeName);
        if (runtime is not null)
        {
            var ns = platform.Defaults.NamespacePattern
                .Replace("{app}", manifest.Name)
                .Replace("{env}", environment);
            await runtime.DestroyAsync(ns, ct);
        }

        // Destroy infra via backend adapters
        foreach (var (resourceType, backendName) in platform.Backends)
        {
            var backend = _pluginHost.GetBackendAdapter(backendName);
            if (backend is not null)
            {
                await backend.DestroyAsync(manifest.Name, environment, ct);
            }
        }

        _logger.LogInformation("Destroy complete for {App} in {Env}", manifest.Name, environment);
    }
}
