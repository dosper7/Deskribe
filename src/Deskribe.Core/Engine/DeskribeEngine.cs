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
    private readonly PluginRegistry _pluginRegistry;
    private readonly ILogger<DeskribeEngine> _logger;

    public DeskribeEngine(
        ConfigLoader configLoader,
        MergeEngine mergeEngine,
        ResourceReferenceResolver resolver,
        PolicyValidator validator,
        PluginRegistry pluginRegistry,
        ILogger<DeskribeEngine> logger)
    {
        _configLoader = configLoader;
        _mergeEngine = mergeEngine;
        _resolver = resolver;
        _validator = validator;
        _pluginRegistry = pluginRegistry;
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
            var provider = _pluginRegistry.GetResourceProvider(resource.Type);
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
            var provider = _pluginRegistry.GetResourceProvider(resource.Type);
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
            Team = manifest.Team,
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

        // 1. Apply infra via provisioners
        var resourceOutputs = new Dictionary<string, Dictionary<string, string>>();

        var applyWarnings = new List<string>();

        foreach (var resourcePlan in plan.ResourcePlans)
        {
            var provisionerName = plan.Platform.Provisioners.GetValueOrDefault(resourcePlan.ResourceType);
            if (provisionerName is null)
            {
                _logger.LogWarning("No provisioner configured for resource type '{Type}'. Skipping — handle manually.", resourcePlan.ResourceType);
                applyWarnings.Add($"No provisioner configured for '{resourcePlan.ResourceType}'. This resource must be provisioned manually.");
                continue;
            }

            var provisioner = _pluginRegistry.GetProvisioner(provisionerName);

            if (provisioner is null)
            {
                _logger.LogWarning("No provisioner '{Provisioner}' for resource '{Type}', skipping apply",
                    provisionerName, resourcePlan.ResourceType);
                applyWarnings.Add($"Provisioner '{provisionerName}' not found for '{resourcePlan.ResourceType}'. This resource must be provisioned manually.");
                continue;
            }

            var result = await provisioner.ApplyAsync(plan, ct);

            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Provisioner apply failed for {resourcePlan.ResourceType}: {string.Join(", ", result.Errors)}");
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
            var runtimeName = plan.Platform.Runtime.Name;
            var runtime = _pluginRegistry.GetRuntimePlugin(runtimeName);

            if (runtime is null)
            {
                _logger.LogWarning("No runtime plugin '{Runtime}', skipping deployment", runtimeName);
                return;
            }

            var artifact = await runtime.RenderAsync(resolvedWorkload, ct);
            await runtime.ApplyAsync(artifact, ct);

            _logger.LogInformation("Deployment complete for {App} in {Env}", plan.AppName, plan.Environment);
        }
    }

    public async Task<List<GeneratedFile>> GenerateAsync(
        string manifestPath,
        string platformPath,
        string environment,
        string outputDir,
        Dictionary<string, string>? images = null,
        string? outputFormat = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Generating artifacts for {Env} to {OutputDir} (format: {Format})", environment, outputDir, outputFormat ?? "all");

        var plan = await PlanAsync(manifestPath, platformPath, environment, images, ct);
        var allFiles = new List<GeneratedFile>();
        var format = outputFormat ?? "all";

        // Generate provisioner artifacts (terraform/pulumi) unless k8s-only
        if (format is "all" or "terraform-only")
        {
            var provisionerGroups = plan.ResourcePlans
                .Where(rp => plan.Platform.Provisioners.ContainsKey(rp.ResourceType))
                .GroupBy(rp => plan.Platform.Provisioners[rp.ResourceType]);

            // Warn about resources with no provisioner configured
            foreach (var rp in plan.ResourcePlans.Where(rp => !plan.Platform.Provisioners.ContainsKey(rp.ResourceType)))
            {
                _logger.LogWarning("No provisioner configured for resource type '{Type}'. Skipping artifact generation — handle manually.", rp.ResourceType);
                plan.Warnings.Add($"No provisioner configured for '{rp.ResourceType}'. This resource must be provisioned manually.");
            }

            foreach (var group in provisionerGroups)
            {
                var provisioner = _pluginRegistry.GetProvisioner(group.Key);
                if (provisioner is null)
                {
                    _logger.LogWarning("No provisioner '{Name}' for artifact generation", group.Key);
                    continue;
                }

                var result = await provisioner.GenerateArtifactsAsync(plan, outputDir, ct);
                if (!result.Success)
                {
                    _logger.LogWarning("Artifact generation failed for provisioner '{Name}': {Errors}",
                        group.Key, string.Join(", ", result.Errors));
                    continue;
                }

                allFiles.AddRange(result.Files);
            }
        }

        // Generate K8s YAML + Kustomize structure unless terraform-only
        if (format is "all" or "k8s-only")
        {
            if (plan.Workload is not null)
            {
                var runtimeName = plan.Platform.Runtime.Name;
                var runtime = _pluginRegistry.GetRuntimePlugin(runtimeName);

                if (runtime is not null)
                {
                    var artifact = await runtime.RenderAsync(plan.Workload, ct);
                    var k8sFiles = GenerateKustomizeStructure(plan, artifact, outputDir);
                    allFiles.AddRange(k8sFiles);
                }
                else
                {
                    _logger.LogWarning("No runtime plugin '{Runtime}' for K8s artifact generation", runtimeName);
                }
            }
        }

        // Write files to disk
        Directory.CreateDirectory(outputDir);
        foreach (var file in allFiles)
        {
            var dir = Path.GetDirectoryName(file.Path);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(file.Path, file.Content, ct);
            _logger.LogInformation("Generated: {Path}", file.Path);
        }

        return allFiles;
    }

    private static List<GeneratedFile> GenerateKustomizeStructure(DeskribePlan plan, RuntimeArtifact artifact, string outputDir)
    {
        var files = new List<GeneratedFile>();
        var appDir = plan.Team is not null
            ? Path.Combine(outputDir, plan.Team, plan.AppName)
            : Path.Combine(outputDir, plan.AppName);
        var baseDir = Path.Combine(appDir, "base");
        var overlayDir = Path.Combine(appDir, "overlays", plan.Environment);

        // Split the YAML into deployment and secrets
        var yamlDocuments = artifact.Yaml.Split("---\n", StringSplitOptions.RemoveEmptyEntries);
        var deploymentYaml = new List<string>();
        var secretYaml = new List<string>();

        foreach (var doc in yamlDocuments)
        {
            var trimmed = doc.Trim();
            if (trimmed.Contains("kind: Secret") || trimmed.Contains("kind: ExternalSecret"))
                secretYaml.Add(trimmed);
            else
                deploymentYaml.Add(trimmed);
        }

        // base/deployment.yaml — Namespace + Deployment + Service
        files.Add(new GeneratedFile
        {
            Path = Path.Combine(baseDir, "deployment.yaml"),
            Content = string.Join("\n---\n", deploymentYaml) + "\n",
            Format = "yaml"
        });

        // base/secrets.yaml — Secret/ExternalSecret
        if (secretYaml.Count > 0)
        {
            files.Add(new GeneratedFile
            {
                Path = Path.Combine(baseDir, "secrets.yaml"),
                Content = string.Join("\n---\n", secretYaml) + "\n",
                Format = "yaml"
            });
        }

        // base/kustomization.yaml
        var baseResources = new List<string> { "- deployment.yaml" };
        if (secretYaml.Count > 0) baseResources.Add("- secrets.yaml");

        files.Add(new GeneratedFile
        {
            Path = Path.Combine(baseDir, "kustomization.yaml"),
            Content = $"""
                apiVersion: kustomize.config.k8s.io/v1beta1
                kind: Kustomization

                resources:
                {string.Join("\n", baseResources)}
                """.Replace("                ", "") + "\n",
            Format = "yaml"
        });

        // overlays/{env}/kustomization.yaml
        var workload = plan.Workload!;
        files.Add(new GeneratedFile
        {
            Path = Path.Combine(overlayDir, "kustomization.yaml"),
            Content = $"""
                apiVersion: kustomize.config.k8s.io/v1beta1
                kind: Kustomization

                resources:
                - ../../base

                namespace: {workload.Namespace}

                patches:
                - target:
                    kind: Deployment
                    name: {plan.AppName}
                  patch: |
                    - op: replace
                      path: /spec/replicas
                      value: {workload.Replicas}
                    - op: replace
                      path: /spec/template/spec/containers/0/image
                      value: {workload.Image ?? "nginx:latest"}
                    - op: replace
                      path: /spec/template/spec/containers/0/resources/requests/cpu
                      value: {workload.Cpu}
                    - op: replace
                      path: /spec/template/spec/containers/0/resources/requests/memory
                      value: {workload.Memory}
                    - op: replace
                      path: /spec/template/spec/containers/0/resources/limits/cpu
                      value: {workload.Cpu}
                    - op: replace
                      path: /spec/template/spec/containers/0/resources/limits/memory
                      value: {workload.Memory}
                """.Replace("                ", "") + "\n",
            Format = "yaml"
        });

        return files;
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
        var runtimeName = platform.Runtime.Name;
        var runtime = _pluginRegistry.GetRuntimePlugin(runtimeName);
        if (runtime is not null)
        {
            var nsPattern = platform.Defaults.NamespacePattern ?? "{app}-{env}";
            var ns = nsPattern
                .Replace("{app}", manifest.Name)
                .Replace("{env}", environment);
            await runtime.DestroyAsync(ns, ct);
        }

        // Destroy infra via provisioners
        foreach (var (resourceType, provisionerName) in platform.Provisioners)
        {
            var provisioner = _pluginRegistry.GetProvisioner(provisionerName);
            if (provisioner is not null)
            {
                await provisioner.DestroyAsync(manifest.Name, environment, platform, ct);
            }
        }

        _logger.LogInformation("Destroy complete for {App} in {Env}", manifest.Name, environment);
    }
}
