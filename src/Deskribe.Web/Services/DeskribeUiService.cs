using System.Text.Json;
using Deskribe.Core.Config;
using Deskribe.Core.Engine;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Web.Services;

public class DeskribeUiService
{
    private readonly DeskribeEngine _engine;
    private readonly ConfigLoader _configLoader;
    private readonly AppStateStore _stateStore;
    private readonly ILogger<DeskribeUiService> _logger;
    private readonly string _solutionRoot;
    private readonly string _examplesDir;
    private readonly string _defaultPlatformConfigPath;
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
        _stateStore = stateStore;
        _logger = logger;

        // Resolve solution root: walk up from the assembly location to find Deskribe.slnx
        _solutionRoot = configuration["Deskribe:SolutionRoot"]
            ?? FindSolutionRoot()
            ?? Directory.GetCurrentDirectory();

        _examplesDir = Path.Combine(_solutionRoot, "examples");
        _defaultPlatformConfigPath = Path.Combine(_solutionRoot, "examples", "platform-config");

        _logger.LogInformation("Deskribe solution root: {Root}", _solutionRoot);
        _logger.LogInformation("Examples directory: {Dir}", _examplesDir);

        RefreshApps();
    }

    public record AppInfo(string Name, string ManifestPath, string PlatformPath, string[] Environments);

    public AppStateStore StateStore => _stateStore;

    public IReadOnlyList<AppInfo> GetApplications() => _discoveredApps;

    /// <summary>
    /// Scans the examples directory for deskribe.json files and discovers all apps.
    /// </summary>
    public void RefreshApps()
    {
        var apps = new List<AppInfo>();

        if (!Directory.Exists(_examplesDir))
        {
            _logger.LogWarning("Examples directory not found: {Dir}", _examplesDir);
            _discoveredApps = apps;
            return;
        }

        var manifestFiles = Directory.GetFiles(_examplesDir, "deskribe.json", SearchOption.AllDirectories);
        _logger.LogInformation("Found {Count} manifest(s) in {Dir}", manifestFiles.Length, _examplesDir);

        foreach (var manifestPath in manifestFiles)
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<DeskribeManifest>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (manifest is null) continue;

                // Discover environments from service overrides + platform env files
                var environments = DiscoverEnvironments(manifest, _defaultPlatformConfigPath);

                var app = new AppInfo(
                    manifest.Name,
                    manifestPath,
                    _defaultPlatformConfigPath,
                    environments);

                apps.Add(app);
                _logger.LogInformation("Discovered app: {Name} with environments: [{Envs}]",
                    app.Name, string.Join(", ", environments));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse manifest at {Path}", manifestPath);
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

    public async Task<DeskribePlan> PlanAsync(string appName, string environment, string? image = null, CancellationToken ct = default)
    {
        var app = FindApp(appName);
        var images = image != null ? new Dictionary<string, string> { ["api"] = image } : null;

        try
        {
            var plan = await _engine.PlanAsync(app.ManifestPath, app.PlatformPath, environment, images, ct);
            _stateStore.SetStatus(appName, environment, AppStatus.Planned, "Plan generated successfully");
            return plan;
        }
        catch (Exception ex)
        {
            _stateStore.SetStatus(appName, environment, AppStatus.Error, ex.Message);
            throw;
        }
    }

    public async Task ApplyAsync(DeskribePlan plan, CancellationToken ct = default)
    {
        try
        {
            await _engine.ApplyAsync(plan, ct);
            _stateStore.SetStatus(plan.AppName, plan.Environment, AppStatus.Applied, "Applied successfully");
        }
        catch (Exception ex)
        {
            _stateStore.SetStatus(plan.AppName, plan.Environment, AppStatus.Error, ex.Message);
            throw;
        }
    }

    public async Task DestroyAsync(string appName, string environment, CancellationToken ct = default)
    {
        var app = FindApp(appName);
        try
        {
            await _engine.DestroyAsync(app.ManifestPath, app.PlatformPath, environment, ct);
            _stateStore.SetStatus(appName, environment, AppStatus.Destroyed, "Destroyed successfully");
        }
        catch (Exception ex)
        {
            _stateStore.SetStatus(appName, environment, AppStatus.Error, ex.Message);
            throw;
        }
    }

    public async Task<List<GeneratedFile>> GenerateAsync(
        string appName, string environment, string outputDir,
        string? image = null, string? outputFormat = null, CancellationToken ct = default)
    {
        var app = FindApp(appName);
        var images = image != null ? new Dictionary<string, string> { ["api"] = image } : null;
        return await _engine.GenerateAsync(app.ManifestPath, app.PlatformPath, environment, outputDir, images, outputFormat, ct);
    }

    private AppInfo FindApp(string appName)
    {
        return _discoveredApps.FirstOrDefault(a => a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Application '{appName}' not found. Known apps: [{string.Join(", ", _discoveredApps.Select(a => a.Name))}]. " +
                $"Ensure a deskribe.json exists under {_examplesDir}.");
    }

    private static string[] DiscoverEnvironments(DeskribeManifest manifest, string platformConfigPath)
    {
        var envs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // From service overrides in the manifest
        foreach (var service in manifest.Services)
        {
            foreach (var key in service.Overrides.Keys)
                envs.Add(key);
        }

        // From platform env files
        var envsDir = Path.Combine(platformConfigPath, "envs");
        if (Directory.Exists(envsDir))
        {
            foreach (var file in Directory.GetFiles(envsDir, "*.json"))
            {
                envs.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        // Always include dev if nothing else
        if (envs.Count == 0) envs.Add("dev");

        return envs.OrderBy(e => e switch
        {
            "dev" => 0,
            "staging" => 1,
            "prod" => 2,
            _ => 3
        }).ToArray();
    }

    private static string? FindSolutionRoot()
    {
        // Walk up from assembly location
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Deskribe.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        // Try from current directory
        dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Deskribe.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }
}
