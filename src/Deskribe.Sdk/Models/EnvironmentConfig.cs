using System.Text.Json.Serialization;

namespace Deskribe.Sdk.Models;

public sealed record EnvironmentConfig
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("defaults")]
    public PlatformDefaults? Defaults { get; init; }

    [JsonPropertyName("alertRouting")]
    public Dictionary<string, List<string>> AlertRouting { get; init; } = new();
}
