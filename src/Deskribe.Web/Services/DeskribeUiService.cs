using Deskribe.Core;
using Deskribe.Core.Config;
using Deskribe.Core.Engine;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Web.Services;

public class AppNotFoundException(string message) : Exception(message);

/// <summary>Thrown by mutating operations while the dashboard is read-only.</summary>
public class MutationsDisabledException()
    : Exception(DeskribeUiService.ReadOnlyReason);

/// <summary>Deployment status that never throws; <see cref="Error"/> is set when the cluster could not be queried.</summary>
public record StatusResult(RuntimeStatus? Status, string? Error);

public class DeskribeUiService
{
    public const string ReadOnlyReason =
        "This dashboard is read-only. Apply and destroy are disabled unless 'Deskribe:EnableMutations' is set to true.";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(10);

    private readonly DeskribeEngine _engine;
    private readonly ConfigLoader _configLoader;
    private readonly ILogger<DeskribeUiService> _logger;
    private readonly string _manifestsRoot;
    private readonly string _platformPath;
    private List<AppInfo> _discoveredApps = [];

    public DeskribeUiService(
        DeskribeEngine engine,
        ConfigLoader configLoader,
        AppStateStore stateStore,
        IConfiguration configuration,
        ILogger<DeskribeUiService> logger)
    {
        _engine = engine;
        _configLoader = configLoader;
        StateStore = stateStore;
        _logger = logger;

        MutationsEnabled = configuration.GetValue("Deskribe:EnableMutations", false);

        var configuredRoot = configuration["Deskribe:ManifestsRoot"];
        var configuredPlatform = configuration["Deskribe:PlatformPath"];
        if (string.IsNullOrWhiteSpace(configuredRoot) || string.IsNullOrWhiteSpace(configuredPlatform))
        {
            // Fallback: the repository's examples, located via Deskribe.slnx.
            var solutionRoot = configuration["Deskribe:SolutionRoot"]
                ?? FindSolutionRoot()
                ?? Directory.GetCurrentDirectory();
            configuredRoot = string.IsNullOrWhiteSpace(configuredRoot) ? Path.Combine(solutionRoot, "examples") : configuredRoot;
            configuredPlatform = string.IsNullOrWhiteSpace(configuredPlatform)
                ? Path.Combine(solutionRoot, "examples", "platform-config")
                : configuredPlatform;
        }
        _manifestsRoot = Path.GetFullPath(configuredRoot);
        _platformPath = Path.GetFullPath(configuredPlatform);

        _logger.LogInformation("Manifests root: {Root}; platform config: {Platform}; mutations {State}",
            _manifestsRoot, _platformPath, MutationsEnabled ? "ENABLED" : "disabled");

        RefreshApps();
    }

    public record AppInfo(string Name, string ManifestPath, string PlatformPath, string[] Environments);

    public AppStateStore StateStore { get; }

    public bool MutationsEnabled { get; }

    public IReadOnlyList<AppInfo> GetApplications() => _discoveredApps;

    /// <summary>Scans the manifests root for deskribe.json files.</summary>
    public void RefreshApps()
    {
        var apps = new List<AppInfo>();

        if (!Directory.Exists(_manifestsRoot))
        {
            _logger.LogWarning("Manifests root not found: {Dir}", _manifestsRoot);
            _discoveredApps = apps;
            return;
        }

        string[] environments;
        try
        {
            environments = SortEnvironments(_configLoader.GetEnvironmentNamesAsync(_platformPath).GetAwaiter().GetResult());
        }
        catch (DeskribeException ex)
        {
            _logger.LogWarning("Platform config unavailable: {Message}", ex.Message);
            environments = [];
        }

        var manifestFiles = Directory.EnumerateFiles(_manifestsRoot, "deskribe.json", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .Order(StringComparer.Ordinal);

        foreach (var manifestPath in manifestFiles)
        {
            try
            {
                var manifest = _configLoader.LoadManifestAsync(manifestPath).GetAwaiter().GetResult();
                if (apps.Any(a => a.Name.Equals(manifest.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("Ignoring {Path}: an app named {Name} was already discovered", manifestPath, manifest.Name);
                    continue;
                }
                apps.Add(new AppInfo(manifest.Name, manifestPath, _platformPath, environments));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Skipping manifest {Path}: {Message}", manifestPath, ex.Message);
            }
        }

        _discoveredApps = apps;
    }

    public async Task<DeskribeManifest> GetManifestAsync(string appName, CancellationToken ct = default)
    {
        var app = FindApp(appName);
        return await _configLoader.LoadManifestAsync(app.ManifestPath, ct);
    }

    public async Task<PlatformConfig> GetPlatformConfigAsync(string appName, CancellationToken ct = default)
    {
        var app = FindApp(appName);
        return await _configLoader.LoadPlatformConfigAsync(app.PlatformPath, ct);
    }

    public async Task<ValidationResult> ValidateAsync(string appName, string environment, CancellationToken ct = default)
    {
        var app = FindApp(appName);
        return await _engine.ValidateAsync(app.ManifestPath, app.PlatformPath, environment, ct);
    }

    /// <summary>Plans; throws <see cref="DeskribeValidationException"/> when the manifest is invalid.</summary>
    public async Task<DeskribePlan> PlanAsync(
        string appName, string environment, Dictionary<string, string>? images = null, CancellationToken ct = default)
    {
        var app = FindApp(appName);
        try
        {
            var plan = await _engine.PlanAsync(app.ManifestPath, app.PlatformPath, environment, images, ct);
            StateStore.SetStatus(appName, environment, AppStatus.Planned, "Plan generated successfully");
            return plan;
        }
        catch (DeskribeException ex)
        {
            StateStore.SetStatus(appName, environment, AppStatus.Error, ex.Message);
            throw;
        }
    }

    public async Task<ApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct = default)
    {
        RequireMutations();
        try
        {
            var result = await _engine.ApplyAsync(plan, ct);
            StateStore.SetStatus(plan.AppName, plan.Environment, AppStatus.Applied, "Applied successfully");
            return result;
        }
        catch (Exception ex)
        {
            StateStore.SetStatus(plan.AppName, plan.Environment, AppStatus.Error, ex.Message);
            throw;
        }
    }

    public async Task DestroyAsync(string appName, string environment, CancellationToken ct = default)
    {
        RequireMutations();
        var app = FindApp(appName);
        try
        {
            await _engine.DestroyAsync(app.ManifestPath, app.PlatformPath, environment, ct);
            StateStore.SetStatus(appName, environment, AppStatus.Destroyed, "Destroyed successfully");
        }
        catch (Exception ex)
        {
            StateStore.SetStatus(appName, environment, AppStatus.Error, ex.Message);
            throw;
        }
    }

    /// <summary>Queries the runtime; an unreachable cluster is reported in the result, not thrown.</summary>
    public async Task<StatusResult> GetStatusAsync(string appName, string environment, CancellationToken ct = default)
    {
        var app = FindApp(appName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StatusTimeout);
        try
        {
            return new StatusResult(await _engine.StatusAsync(app.ManifestPath, app.PlatformPath, environment, timeout.Token), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new StatusResult(null, $"Timed out after {StatusTimeout.TotalSeconds:0}s while querying the cluster.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Status for {App}/{Env} unavailable: {Message}", appName, environment, ex.Message);
            return new StatusResult(null, ex.Message);
        }
    }

    /// <summary>
    /// Parses one image per line as <c>name=image</c>; a lone line without '=' is a single image
    /// (the engine applies it to a single-service manifest).
    /// </summary>
    public static Dictionary<string, string>? ParseImages(string? text)
    {
        var lines = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var eq = line.IndexOf('=');
            if (eq < 0 && lines.Length == 1)
                map["image"] = line;
            else if (eq > 0 && eq < line.Length - 1)
                map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            else
                throw new DeskribeException($"Invalid image '{line}': use one 'name=image:tag' per line, or a single image.");
        }
        return map;
    }

    private void RequireMutations()
    {
        if (!MutationsEnabled)
            throw new MutationsDisabledException();
    }

    private AppInfo FindApp(string appName)
    {
        return _discoveredApps.FirstOrDefault(a => a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppNotFoundException($"Application '{appName}' not found.");
    }

    private static bool IsBuildOutput(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj" or "node_modules");
    }

    private static string[] SortEnvironments(IEnumerable<string> envs) => envs
        .OrderBy(e => e switch { "dev" => 0, "staging" => 1, "prod" => 2, _ => 3 })
        .ThenBy(e => e, StringComparer.Ordinal)
        .ToArray();

    private static string? FindSolutionRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = start;
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir, "Deskribe.slnx")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
        }
        return null;
    }
}
