using System.Text.Json;
using Deskribe.Core.Config;
using Deskribe.Core.Merging;
using Deskribe.Core.Plugins;
using Deskribe.Core.Resolution;
using Deskribe.Core.Validation;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Engine;

public class DeskribeEngine
{
    public const string BindingsFileName = "bindings.json";

    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConfigLoader _configLoader;
    private readonly MergeEngine _mergeEngine;
    private readonly ManifestValidator _validator;
    private readonly PluginRegistry _pluginRegistry;
    private readonly ILogger<DeskribeEngine> _logger;

    public DeskribeEngine(
        ConfigLoader configLoader,
        MergeEngine mergeEngine,
        ManifestValidator validator,
        PluginRegistry pluginRegistry,
        ILogger<DeskribeEngine> logger)
    {
        _configLoader = configLoader;
        _mergeEngine = mergeEngine;
        _validator = validator;
        _pluginRegistry = pluginRegistry;
        _logger = logger;
    }

    // ───────────────────────────── validate / plan ─────────────────────────────

    public async Task<ValidationResult> ValidateAsync(
        string manifestPath, string platformPath, string environment, CancellationToken ct = default)
    {
        var (cfg, loadErrors) = await TryLoadAsync(manifestPath, platformPath, environment, ct);
        if (cfg is null)
            return new ValidationResult { IsValid = false, Errors = loadErrors };
        return await _validator.ValidateAsync(cfg, ct);
    }

    /// <summary>Validates, then plans. Throws <see cref="DeskribeValidationException"/> when invalid.</summary>
    public async Task<DeskribePlan> PlanAsync(
        string manifestPath,
        string platformPath,
        string environment,
        Dictionary<string, string>? images = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Planning {Manifest} for environment {Env}", LogText.Clean(manifestPath), LogText.Clean(environment));

        var (cfg, loadErrors) = await TryLoadAsync(manifestPath, platformPath, environment, ct);
        if (cfg is null)
            throw new DeskribeValidationException(new ValidationResult { IsValid = false, Errors = loadErrors });

        var validation = await _validator.ValidateAsync(cfg, ct);
        if (!validation.IsValid)
            throw new DeskribeValidationException(validation);

        var resourcePlans = await PlanResourcesAsync(cfg, ct);

        var manifest = cfg.Manifest;
        var resourcesByKey = manifest.Resources.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var workloads = new List<WorkloadPlan>();
        var bindings = new List<Binding>();

        foreach (var service in manifest.Services)
        {
            var workload = _mergeEngine.BuildWorkload(manifest, service, cfg.Defaults, cfg.Environment, cfg.Namespace, images);
            foreach (var (envVar, value) in service.Env)
            {
                foreach (var reference in ResourceReferences.Extract(envVar, value))
                {
                    var resource = resourcesByKey[reference.ResourceKey];
                    var schema = RequireProvider(resource.Type).GetSchema();
                    var sensitive = schema.SensitiveOutputs.Contains(reference.Output, StringComparer.Ordinal);
                    bindings.Add(new Binding
                    {
                        Workload = workload.Name,
                        EnvVar = envVar,
                        ResourceKey = resource.Key,
                        ResourceType = resource.Type,
                        Output = reference.Output,
                        Sensitive = sensitive
                    });
                    if (sensitive)
                        workload.SecretKeys.Add(envVar);
                }
            }
            workloads.Add(workload);
        }

        return new DeskribePlan
        {
            AppName = manifest.Name,
            Team = manifest.Team,
            Environment = cfg.Environment,
            Namespace = cfg.Namespace,
            Platform = cfg.Platform,
            EnvironmentConfig = cfg.EnvironmentConfig,
            Defaults = cfg.Defaults,
            ResourcePlans = resourcePlans,
            Workloads = workloads,
            Bindings = bindings,
            Warnings = [.. validation.Warnings]
        };
    }

    // ───────────────────────────── apply ─────────────────────────────

    public async Task<ApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct = default)
    {
        _logger.LogInformation("Applying plan for {App} in {Env}", plan.AppName, plan.Environment);

        RequireImages(plan.Workloads);
        var runtime = RequireRuntime(plan.Platform);
        var groups = GroupByProvisioner(plan.Platform, plan.ResourcePlans);

        // Let the runtime reject what it cannot deploy (names, runtime settings) before any
        // infrastructure is created. References are still unresolved here, so no values are involved.
        await CallRuntimeAsync(runtime, "accept the deployment",
            () => runtime.RenderAsync(CreateRuntimeContext(plan, plan.Workloads), ct));

        var outputs = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            _logger.LogInformation("Provisioning {Count} resource(s) with {Provisioner}", group.Resources.Count, group.Name);
            var result = await group.Provisioner.ApplyAsync(CreateProvisionContext(plan, group), ct);
            if (!result.Success)
                throw new DeskribeException(
                    $"Provisioner '{group.Name}' failed: {string.Join("; ", result.Errors.DefaultIfEmpty("no error details given"))}");
            foreach (var (key, values) in result.ResourceOutputs)
                outputs[key] = values;
        }

        var resolved = ResolveWorkloads(plan.Workloads, outputs, hint: null);

        _logger.LogInformation("Deploying {Count} workload(s) with runtime {Runtime}", resolved.Count, runtime.Name);
        var runtimeResult = await CallRuntimeAsync(runtime, "deploy",
            () => runtime.ApplyAsync(CreateRuntimeContext(plan, resolved), ct));

        return new ApplyResult
        {
            Namespace = runtimeResult.Namespace,
            Applied = [.. runtimeResult.Applied],
            Pruned = [.. runtimeResult.Pruned],
            Warnings = [.. plan.Warnings]
        };
    }

    // ───────────────────────────── generate ─────────────────────────────

    /// <summary>
    /// Writes GitOps artifacts under <paramref name="outputDir"/>. Plugins receive the full
    /// output directory; relative paths they return are taken relative to it.
    /// </summary>
    public async Task<List<GeneratedFile>> GenerateAsync(
        string manifestPath,
        string platformPath,
        string environment,
        string outputDir,
        Dictionary<string, string>? images = null,
        string? outputFormat = null,
        string? outputsPath = null,
        CancellationToken ct = default)
    {
        var format = GenerateFormats.Parse(outputFormat);
        var outputRoot = Path.GetFullPath(outputDir);
        _logger.LogInformation("Generating {Format} artifacts for {Env} into {OutputDir}", format, environment, outputRoot);

        var plan = await PlanAsync(manifestPath, platformPath, environment, images, ct);
        var files = new List<GeneratedFile>();

        IRuntimePlugin? runtime = null;
        List<WorkloadPlan>? resolvedWorkloads = null;
        if (format != GenerateFormat.InfraOnly)
        {
            RequireImages(plan.Workloads);
            runtime = RequireRuntime(plan.Platform);

            var opaqueWithSecrets = plan.Workloads
                .Where(w => w.SecretKeys.Count > 0 && w.SecretsStrategy == SecretsStrategies.Opaque)
                .Select(w => w.Name)
                .ToList();
            if (opaqueWithSecrets.Count > 0)
                throw new DeskribeException(
                    $"Cannot generate artifacts for workload(s) {string.Join(", ", opaqueWithSecrets.Select(n => $"'{n}'"))}: " +
                    "they use secret values and the 'opaque' secrets strategy would write them to disk. " +
                    "Use the 'external-secrets' strategy for GitOps, or deploy with 'deskribe apply'.");

            var outputs = outputsPath is null
                ? new Dictionary<string, Dictionary<string, string>>()
                : await LoadOutputsAsync(outputsPath, ct);
            resolvedWorkloads = ResolveWorkloads(plan.Workloads, outputs,
                hint: outputsPath is null ? "Supply a resource outputs file to resolve them (CLI: --outputs <file>)." : null);
        }

        if (format != GenerateFormat.RuntimeOnly)
        {
            foreach (var group in GroupByProvisioner(plan.Platform, plan.ResourcePlans))
            {
                var result = await group.Provisioner.GenerateArtifactsAsync(CreateProvisionContext(plan, group), outputRoot, ct);
                if (!result.Success)
                    throw new DeskribeException(
                        $"Provisioner '{group.Name}' failed to generate artifacts: {string.Join("; ", result.Errors.DefaultIfEmpty("no error details given"))}");
                files.AddRange(result.Files);
            }
        }

        if (runtime is not null && resolvedWorkloads is not null)
        {
            var runtimeFiles = await CallRuntimeAsync(runtime, "generate artifacts",
                () => runtime.GenerateArtifactsAsync(CreateRuntimeContext(plan, resolvedWorkloads), outputRoot, ct));
            files.AddRange(runtimeFiles);
        }

        files.Add(new GeneratedFile
        {
            Path = Path.Combine(outputRoot, BindingsFileName),
            Content = JsonSerializer.Serialize(new BindingsDocument
            {
                App = plan.AppName,
                Team = plan.Team,
                Environment = plan.Environment,
                Namespace = plan.Namespace,
                Bindings = plan.Bindings
            }, OutputJsonOptions) + "\n",
            Format = "json"
        });

        var placed = PlaceFiles(files, outputRoot);

        foreach (var file in placed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            await File.WriteAllTextAsync(file.Path, file.Content, ct);
            _logger.LogInformation("Generated {Path}", file.Path);
        }

        return placed;
    }

    // ───────────────────────────── status / destroy ─────────────────────────────

    public async Task<RuntimeStatus> StatusAsync(
        string manifestPath, string platformPath, string environment, CancellationToken ct = default)
    {
        var cfg = await LoadAsync(manifestPath, platformPath, environment, ct);
        var runtime = RequireRuntime(cfg.Platform);
        return await CallRuntimeAsync(runtime, "report status",
            () => runtime.GetStatusAsync(CreateRuntimeContext(cfg), ct));
    }

    /// <summary>
    /// Removes the app's runtime objects, then its infrastructure. Every step is attempted;
    /// failures are reported together afterwards.
    /// </summary>
    public async Task DestroyAsync(
        string manifestPath, string platformPath, string environment, CancellationToken ct = default)
    {
        _logger.LogInformation("Destroying {Manifest} in {Env}", LogText.Clean(manifestPath), LogText.Clean(environment));

        var cfg = await LoadAsync(manifestPath, platformPath, environment, ct);
        var runtime = RequireRuntime(cfg.Platform);
        foreach (var resource in cfg.Manifest.Resources)
            RequireProvider(resource.Type);
        var resourcePlans = await PlanResourcesAsync(cfg, ct);
        var groups = GroupByProvisioner(cfg.Platform, resourcePlans);

        var failures = new List<string>();
        try
        {
            await runtime.DestroyAsync(CreateRuntimeContext(cfg), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Runtime {Runtime} failed to destroy", runtime.Name);
            failures.Add($"Runtime '{runtime.Name}': {ex.Message}");
        }

        foreach (var group in groups)
        {
            var ctx = new ProvisionContext
            {
                AppName = cfg.Manifest.Name,
                Team = cfg.Manifest.Team,
                Environment = cfg.Environment,
                Platform = cfg.Platform,
                Defaults = cfg.Defaults,
                Resources = group.Resources,
                Config = ProvisionerConfig(cfg.Platform, group.Name)
            };
            try
            {
                var result = await group.Provisioner.DestroyAsync(ctx, ct);
                if (!result.Success)
                    failures.Add($"Provisioner '{group.Name}': {string.Join("; ", result.Errors.DefaultIfEmpty("no error details given"))}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Provisioner {Provisioner} failed to destroy", group.Name);
                failures.Add($"Provisioner '{group.Name}': {ex.Message}");
            }
        }

        if (failures.Count > 0)
            throw new DeskribeException(
                "Destroy did not complete:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(f => "  - " + f)));

        _logger.LogInformation("Destroy complete for {App} in {Env}", LogText.Clean(cfg.Manifest.Name), LogText.Clean(cfg.Environment));
    }

    // ───────────────────────────── loading ─────────────────────────────

    private async Task<(LoadedConfiguration? Config, List<string> Errors)> TryLoadAsync(
        string manifestPath, string platformPath, string environment, CancellationToken ct)
    {
        var errors = new List<string>();
        DeskribeManifest? manifest = null;
        PlatformConfig? platform = null;
        EnvironmentConfig? envConfig = null;
        IReadOnlyList<string> known = [];

        try { manifest = await _configLoader.LoadManifestAsync(manifestPath, ct); }
        catch (DeskribeException ex) { errors.Add(ex.Message); }

        try { platform = await _configLoader.LoadPlatformConfigAsync(platformPath, ct); }
        catch (DeskribeException ex) { errors.Add(ex.Message); }

        if (platform is not null)
        {
            try
            {
                known = await _configLoader.GetEnvironmentNamesAsync(platformPath, ct);
                envConfig = await _configLoader.LoadEnvironmentConfigAsync(platformPath, environment, ct);
            }
            catch (DeskribeException ex) { errors.Add(ex.Message); }
        }

        if (manifest is null || platform is null || envConfig is null)
            return (null, errors);

        var defaults = _mergeEngine.MergeDefaults(platform, envConfig);
        return (new LoadedConfiguration
        {
            Manifest = manifest,
            Platform = platform,
            EnvironmentConfig = envConfig,
            Environment = environment,
            KnownEnvironments = known,
            Defaults = defaults,
            Namespace = MergeEngine.ResolveNamespace(defaults, manifest.Team, manifest.Name, environment)
        }, errors);
    }

    private async Task<LoadedConfiguration> LoadAsync(
        string manifestPath, string platformPath, string environment, CancellationToken ct)
    {
        var (cfg, errors) = await TryLoadAsync(manifestPath, platformPath, environment, ct);
        return cfg ?? throw new DeskribeException(string.Join(Environment.NewLine, errors));
    }

    private async Task<List<ResourcePlanResult>> PlanResourcesAsync(LoadedConfiguration cfg, CancellationToken ct)
    {
        var planCtx = new PlanContext
        {
            Platform = cfg.Platform,
            EnvironmentConfig = cfg.EnvironmentConfig,
            Defaults = cfg.Defaults,
            Environment = cfg.Environment,
            AppName = cfg.Manifest.Name
        };

        var plans = new List<ResourcePlanResult>();
        foreach (var resource in cfg.Manifest.Resources)
        {
            var provider = RequireProvider(resource.Type);
            var result = await provider.PlanAsync(resource, planCtx, ct);
            if (result.ResourceKey != resource.Key)
                throw new DeskribeException(
                    $"Resource provider for '{resource.Type}' returned a plan for key '{result.ResourceKey}' instead of '{resource.Key}'. This is a plugin bug.");
            plans.Add(result);
        }
        return plans;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private sealed record ProvisionerGroup(string Name, IProvisioner Provisioner, IReadOnlyList<ResourcePlanResult> Resources);

    private List<ProvisionerGroup> GroupByProvisioner(PlatformConfig platform, IEnumerable<ResourcePlanResult> resourcePlans)
    {
        var groups = new List<ProvisionerGroup>();
        var errors = new List<string>();
        foreach (var group in resourcePlans.GroupBy(rp => platform.Provisioners.GetValueOrDefault(rp.ResourceType)))
        {
            if (group.Key is null)
            {
                errors.AddRange(group.Select(rp =>
                    $"Resource '{rp.ResourceKey}' of type '{rp.ResourceType}' is not mapped to a provisioner in the platform config."));
                continue;
            }
            var provisioner = _pluginRegistry.GetProvisioner(group.Key);
            if (provisioner is null)
            {
                errors.Add($"Provisioner '{group.Key}' is not registered.");
                continue;
            }
            groups.Add(new ProvisionerGroup(group.Key, provisioner, group.ToList()));
        }

        if (errors.Count > 0)
            throw new DeskribeException(string.Join(Environment.NewLine, errors));
        return groups;
    }

    private IResourceProvider RequireProvider(string resourceType) =>
        _pluginRegistry.GetResourceProvider(resourceType)
        ?? throw new DeskribeException($"Resource type '{resourceType}' has no registered resource provider.");

    private IRuntimePlugin RequireRuntime(PlatformConfig platform) =>
        _pluginRegistry.GetRuntimePlugin(platform.Runtime.Name)
        ?? throw new DeskribeException($"Runtime '{platform.Runtime.Name}' (platform runtime.name) has no registered runtime plugin.");

    /// <summary>Turns a runtime plugin failure into a user-facing error.</summary>
    private static async Task<T> CallRuntimeAsync<T>(IRuntimePlugin runtime, string action, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DeskribeException)
        {
            throw new DeskribeException($"Runtime '{runtime.Name}' failed to {action}: {ex.Message}", ex);
        }
    }

    private static void RequireImages(IEnumerable<WorkloadPlan> workloads)
    {
        var missing = workloads.Where(w => string.IsNullOrWhiteSpace(w.Image)).Select(w => $"'{w.Name}'").ToList();
        if (missing.Count > 0)
            throw new DeskribeException(
                $"No image supplied for workload(s) {string.Join(", ", missing)}. Provide an image for each workload (keyed by service name or workload name).");
    }

    private static IReadOnlyDictionary<string, JsonElement> ProvisionerConfig(PlatformConfig platform, string name) =>
        platform.ProvisionerConfigs.TryGetValue(name, out var config) ? config : new Dictionary<string, JsonElement>();

    private static ProvisionContext CreateProvisionContext(DeskribePlan plan, ProvisionerGroup group) => new()
    {
        AppName = plan.AppName,
        Team = plan.Team,
        Environment = plan.Environment,
        Platform = plan.Platform,
        Defaults = plan.Defaults,
        Resources = group.Resources,
        Config = ProvisionerConfig(plan.Platform, group.Name)
    };

    private static RuntimeContext CreateRuntimeContext(DeskribePlan plan, IReadOnlyList<WorkloadPlan> workloads) => new()
    {
        AppName = plan.AppName,
        Team = plan.Team,
        Environment = plan.Environment,
        Namespace = plan.Namespace,
        Platform = plan.Platform,
        Defaults = plan.Defaults,
        Workloads = workloads
    };

    private static RuntimeContext CreateRuntimeContext(LoadedConfiguration cfg) => new()
    {
        AppName = cfg.Manifest.Name,
        Team = cfg.Manifest.Team,
        Environment = cfg.Environment,
        Namespace = cfg.Namespace,
        Platform = cfg.Platform,
        Defaults = cfg.Defaults
    };

    /// <summary>
    /// Substitutes resource outputs into every workload's env vars. Unresolved references
    /// are an error, except in secret variables under the external-secrets strategy, whose
    /// values the cluster pulls from the external store.
    /// </summary>
    private static List<WorkloadPlan> ResolveWorkloads(
        IEnumerable<WorkloadPlan> workloads,
        IReadOnlyDictionary<string, Dictionary<string, string>> outputs,
        string? hint)
    {
        var problems = new List<string>();
        var resolved = new List<WorkloadPlan>();

        foreach (var workload in workloads)
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (envVar, value) in workload.EnvironmentVariables)
            {
                var unresolved = new List<ResourceReference>();
                env[envVar] = ResourceReferences.Resolve(envVar, value, outputs, unresolved);

                var mayStayUnresolved = workload.SecretKeys.Contains(envVar) &&
                                        workload.SecretsStrategy == SecretsStrategies.ExternalSecrets;
                if (mayStayUnresolved)
                    continue;

                problems.AddRange(unresolved.Select(r => $"{workload.Name}: {envVar} -> {r.Expression}"));
                problems.AddRange(ResourceReferences.FindMalformed(value)
                    .Select(m => $"{workload.Name}: {envVar} -> malformed reference '{m}'"));
            }
            resolved.Add(workload with { EnvironmentVariables = env, SecretKeys = [.. workload.SecretKeys] });
        }

        if (problems.Count > 0)
        {
            var message = "Unresolved resource references (no provisioner supplied these outputs):" + Environment.NewLine +
                          string.Join(Environment.NewLine, problems.Select(p => "  - " + p));
            if (hint is not null)
                message += Environment.NewLine + hint;
            throw new DeskribeException(message);
        }

        return resolved;
    }

    private static async Task<Dictionary<string, Dictionary<string, string>>> LoadOutputsAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new DeskribeException($"The resource outputs file '{path}' does not exist.");

        Dictionary<string, Dictionary<string, JsonElement>>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(
                await File.ReadAllTextAsync(path, ct));
        }
        catch (JsonException ex)
        {
            throw new DeskribeException(
                $"The resource outputs file '{path}' is invalid: {ex.Message}. Expected {{ \"<resourceKey>\": {{ \"<output>\": \"<value>\" }} }}.", ex);
        }

        var outputs = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (key, values) in raw ?? [])
        {
            var converted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (output, element) in values ?? [])
            {
                converted[output] = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString()!,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
                    _ => throw new DeskribeException(
                        $"The resource outputs file '{path}': '{key}.{output}' must be a string, number or boolean.")
                };
            }
            outputs[key] = converted;
        }
        return outputs;
    }

    /// <summary>
    /// Resolves every file path against <paramref name="outputRoot"/> and rejects any that
    /// would land outside it (plugins can be third-party code).
    /// </summary>
    private static List<GeneratedFile> PlaceFiles(IEnumerable<GeneratedFile> files, string outputRoot)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootWithSeparator = Path.EndsInDirectorySeparator(outputRoot) ? outputRoot : outputRoot + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var placed = new List<GeneratedFile>();
        var errors = new List<string>();

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                errors.Add("A plugin returned a generated file with an empty path.");
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(outputRoot, file.Path));
            if (!fullPath.StartsWith(rootWithSeparator, comparison))
            {
                errors.Add($"Generated file '{file.Path}' resolves outside the output directory '{outputRoot}'.");
                continue;
            }
            if (!seen.Add(fullPath))
            {
                errors.Add($"More than one generated file targets '{fullPath}'.");
                continue;
            }
            placed.Add(file with { Path = fullPath });
        }

        if (errors.Count > 0)
            throw new DeskribeException("Refusing to write generated files:" + Environment.NewLine +
                                        string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));
        return placed;
    }

    private sealed record BindingsDocument
    {
        public required string App { get; init; }
        public string? Team { get; init; }
        public required string Environment { get; init; }
        public required string Namespace { get; init; }
        public required List<Binding> Bindings { get; init; }
    }
}
