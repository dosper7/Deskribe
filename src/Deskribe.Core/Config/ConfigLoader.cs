using System.Text.Json;
using System.Text.Json.Serialization;
using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;
using Microsoft.Extensions.Logging;

namespace Deskribe.Core.Config;

public class ConfigLoader
{
    private readonly ILogger<ConfigLoader> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ResourceJsonConverter() }
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
        var basePath = Path.Combine(platformPath, "base.json");
        _logger.LogInformation("Loading platform config from {Path}", basePath);
        var json = await File.ReadAllTextAsync(basePath, ct);
        return JsonSerializer.Deserialize<PlatformConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize platform config from {basePath}");
    }

    public async Task<EnvironmentConfig> LoadEnvironmentConfigAsync(string platformPath, string environment, CancellationToken ct = default)
    {
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

internal class ResourceJsonConverter : JsonConverter<DeskribeResource>
{
    public override DeskribeResource? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            throw new JsonException("Resource must have a 'type' property");

        var type = typeProp.GetString();
        var rawJson = root.GetRawText();

        return type switch
        {
            "postgres" => JsonSerializer.Deserialize<PostgresResource>(rawJson, options),
            "redis" => JsonSerializer.Deserialize<RedisResource>(rawJson, options),
            "kafka.messaging" => JsonSerializer.Deserialize<KafkaMessagingResource>(rawJson, options),
            _ => throw new JsonException($"Unknown resource type: {type}")
        };
    }

    public override void Write(Utf8JsonWriter writer, DeskribeResource value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
