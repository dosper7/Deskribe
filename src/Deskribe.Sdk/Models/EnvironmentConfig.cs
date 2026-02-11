using System.Text.Json.Serialization;

namespace Deskribe.Sdk.Models;

public sealed record EnvironmentConfig
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("defaults")]
    public PlatformDefaults Defaults { get; init; } = new();

    [JsonPropertyName("alertRouting")]
    public Dictionary<string, List<string>> AlertRouting { get; init; } = new();
}
