using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deskribe.Sdk.Models;

public sealed record ResourceDescriptor
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("size")]
    public string? Size { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Properties { get; init; } = new();
}
