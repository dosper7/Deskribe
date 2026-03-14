using System.Text.Json.Serialization;

namespace Deskribe.Sdk.Models;

public sealed record DeskribeManifest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("team")]
    public string? Team { get; init; }

    [JsonPropertyName("resources")]
    public List<ResourceDescriptor> Resources { get; init; } = [];

    [JsonPropertyName("services")]
    public List<ServiceDefinition> Services { get; init; } = [];
}
