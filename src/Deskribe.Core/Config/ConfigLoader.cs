using System.Text.Json;
using System.Text.Json.Serialization;
using Deskribe.Sdk.Models;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Config;

public class ConfigLoader
{
    private readonly ILogger<ConfigLoader> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ConfigLoader(ILogger<ConfigLoader> logger)
    {
        _logger = logger;
    }

    public async Task<DeskribeManifest> LoadManifestAsync(string path, CancellationToken ct = default)
    {
        _logger.LogInformation("Loading manifest from {Path}", path);
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<DeskribeManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize manifest from {path}");
    }

    public async Task<PlatformConfig> LoadPlatformConfigAsync(string platformPath, CancellationToken ct = default)
    {
        // Auto-detect: if platformPath is a file, use single-file format
        // If it's a directory, use split-file format (base.json + envs/)
        if (File.Exists(platformPath) && platformPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Loading single-file platform config from {Path}", platformPath);
            var json = await File.ReadAllTextAsync(platformPath, ct);
            return JsonSerializer.Deserialize<PlatformConfig>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Failed to deserialize platform config from {platformPath}");
        }

        var basePath = Path.Combine(platformPath, "base.json");
        _logger.LogInformation("Loading platform config from {Path}", basePath);
        var baseJson = await File.ReadAllTextAsync(basePath, ct);
        return JsonSerializer.Deserialize<PlatformConfig>(baseJson, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize platform config from {basePath}");
    }

    public async Task<EnvironmentConfig> LoadEnvironmentConfigAsync(string platformPath, string environment, CancellationToken ct = default)
    {
        // Single-file format: environments are embedded in the platform config
        if (File.Exists(platformPath) && platformPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var config = await LoadPlatformConfigAsync(platformPath, ct);
            if (config.Environments?.TryGetValue(environment, out var envConfig) == true)
            {
                return envConfig with { Name = envConfig.Name ?? environment };
            }
            _logger.LogWarning("Environment '{Env}' not found in single-file config, using defaults", environment);
            return new EnvironmentConfig { Name = environment };
        }

        // Split-file format: envs/{env}.json
        var envPath = Path.Combine(platformPath, "envs", $"{environment}.json");
        _logger.LogInformation("Loading environment config from {Path}", envPath);

        if (!File.Exists(envPath))
        {
            _logger.LogWarning("Environment config not found at {Path}, using defaults", envPath);
            return new EnvironmentConfig { Name = environment };
        }

        var json = await File.ReadAllTextAsync(envPath, ct);
        return JsonSerializer.Deserialize<EnvironmentConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize environment config from {envPath}");
    }
}
