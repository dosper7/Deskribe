using System.Text.Json;
using System.Text.Json.Serialization;
using Deskribe.Sdk.Models;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Config;

/// <summary>
/// Loads the developer manifest and the platform configuration. The platform path is
/// either a single <c>.json</c> file with an <c>environments</c> map, or a directory with
/// <c>base.json</c> and <c>envs/{env}.json</c>. Every failure is a <see cref="DeskribeException"/>
/// with a message that names the offending file.
/// </summary>
public class ConfigLoader
{
    private readonly ILogger<ConfigLoader> _logger;

    internal static readonly JsonSerializerOptions JsonOptions = new()
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
        return await ReadJsonAsync<DeskribeManifest>(path, "manifest", ct);
    }

    public async Task<PlatformConfig> LoadPlatformConfigAsync(string platformPath, CancellationToken ct = default)
    {
        if (IsSingleFile(platformPath))
        {
            _logger.LogInformation("Loading single-file platform config from {Path}", platformPath);
            return await ReadJsonAsync<PlatformConfig>(platformPath, "platform config", ct);
        }

        var basePath = Path.Combine(EnsureDirectory(platformPath), "base.json");
        _logger.LogInformation("Loading platform config from {Path}", basePath);
        return await ReadJsonAsync<PlatformConfig>(basePath, "platform config", ct);
    }

    /// <summary>The environments the platform defines, sorted by name.</summary>
    public async Task<IReadOnlyList<string>> GetEnvironmentNamesAsync(string platformPath, CancellationToken ct = default)
    {
        if (IsSingleFile(platformPath))
        {
            var config = await LoadPlatformConfigAsync(platformPath, ct);
            return (config.Environments?.Keys ?? Enumerable.Empty<string>()).Order(StringComparer.Ordinal).ToList();
        }

        var envsDir = Path.Combine(EnsureDirectory(platformPath), "envs");
        if (!Directory.Exists(envsDir))
            return [];

        return Directory.GetFiles(envsDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Loads an environment the platform defines; throws when it is not defined.</summary>
    public async Task<EnvironmentConfig> LoadEnvironmentConfigAsync(string platformPath, string environment, CancellationToken ct = default)
    {
        var known = await GetEnvironmentNamesAsync(platformPath, ct);
        if (!known.Contains(environment, StringComparer.Ordinal))
        {
            var list = known.Count == 0 ? "none" : string.Join(", ", known);
            throw new DeskribeException(
                $"Environment '{environment}' is not defined by the platform config at '{platformPath}'. Known environments: {list}.");
        }

        if (IsSingleFile(platformPath))
        {
            var config = await LoadPlatformConfigAsync(platformPath, ct);
            var envConfig = config.Environments![environment];
            return envConfig with { Name = envConfig.Name ?? environment };
        }

        var envPath = Path.Combine(platformPath, "envs", $"{environment}.json");
        _logger.LogInformation("Loading environment config from {Path}", envPath);
        var loaded = await ReadJsonAsync<EnvironmentConfig>(envPath, "environment config", ct);
        return loaded with { Name = loaded.Name ?? environment };
    }

    private static bool IsSingleFile(string platformPath) =>
        File.Exists(platformPath) && platformPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static string EnsureDirectory(string platformPath)
    {
        if (!Directory.Exists(platformPath))
            throw new DeskribeException(
                $"Platform config not found at '{platformPath}'. Expected a .json file or a directory containing base.json.");
        return platformPath;
    }

    private static async Task<T> ReadJsonAsync<T>(string path, string what, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new DeskribeException($"The {what} file '{path}' does not exist.");

        var json = await File.ReadAllTextAsync(path, ct);
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new DeskribeException($"The {what} file '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new DeskribeException($"The {what} file '{path}' is invalid: {ex.Message}", ex);
        }
    }
}
