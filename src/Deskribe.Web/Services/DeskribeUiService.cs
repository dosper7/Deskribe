using Deskribe.Core.Config;
using Deskribe.Core.Engine;
using Deskribe.Sdk.Models;

namespace Deskribe.Web.Services;

public class DeskribeUiService
{
    private readonly DeskribeEngine _engine;
    private readonly ConfigLoader _configLoader;
    private readonly ILogger<DeskribeUiService> _logger;

    public DeskribeUiService(DeskribeEngine engine, ConfigLoader configLoader, ILogger<DeskribeUiService> logger)
    {
        _engine = engine;
        _configLoader = configLoader;
        _logger = logger;
    }

    public record AppInfo(string Name, string ManifestPath, string PlatformPath, string[] Environments);

    private static readonly List<AppInfo> KnownApps =
    [
        new("payments-api", "examples/payments-api/deskribe.json", "examples/platform-config", ["dev", "prod"])
    ];

    public IReadOnlyList<AppInfo> GetApplications() => KnownApps;

    public async Task<DeskribeManifest> GetManifestAsync(string appName, CancellationToken ct = default)
    {
        var app = KnownApps.First(a => a.Name == appName);
        return await _configLoader.LoadManifestAsync(app.ManifestPath, ct);
    }

    public async Task<PlatformConfig> GetPlatformConfigAsync(string appName, CancellationToken ct = default)
    {
        var app = KnownApps.First(a => a.Name == appName);
        return await _configLoader.LoadPlatformConfigAsync(app.PlatformPath, ct);
    }

    public async Task<ValidationResult> ValidateAsync(string appName, string environment, CancellationToken ct = default)
    {
        var app = KnownApps.First(a => a.Name == appName);
        return await _engine.ValidateAsync(app.ManifestPath, app.PlatformPath, environment, ct);
    }

    public async Task<DeskribePlan> PlanAsync(string appName, string environment, string? image = null, CancellationToken ct = default)
    {
        var app = KnownApps.First(a => a.Name == appName);
        var images = image != null ? new Dictionary<string, string> { ["api"] = image } : null;
        return await _engine.PlanAsync(app.ManifestPath, app.PlatformPath, environment, images, ct);
    }

    public async Task ApplyAsync(DeskribePlan plan, CancellationToken ct = default)
    {
        await _engine.ApplyAsync(plan, ct);
    }

    public async Task DestroyAsync(string appName, string environment, CancellationToken ct = default)
    {
        var app = KnownApps.First(a => a.Name == appName);
        await _engine.DestroyAsync(app.ManifestPath, app.PlatformPath, environment, ct);
    }
}
