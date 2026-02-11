using System.Text.Json.Serialization;
using Deskribe.Sdk.Resources;

namespace Deskribe.Sdk.Models;

public sealed record DeskribeManifest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("resources")]
    public List<DeskribeResource> Resources { get; init; } = [];

    [JsonPropertyName("services")]
    public List<ServiceDefinition> Services { get; init; } = [];
}
