using System.Text.Json.Serialization;

namespace Deskribe.Sdk.Models;

public sealed record ServiceDefinition
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("env")]
    public Dictionary<string, string> Env { get; init; } = new();

    [JsonPropertyName("overrides")]
    public Dictionary<string, ServiceOverride> Overrides { get; init; } = new();
}

public sealed record ServiceOverride
{
    [JsonPropertyName("replicas")]
    public int? Replicas { get; init; }

    [JsonPropertyName("cpu")]
    public string? Cpu { get; init; }

    [JsonPropertyName("memory")]
    public string? Memory { get; init; }
}
