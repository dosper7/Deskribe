using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deskribe.Sdk.Models;

public sealed record ResourceDescriptor
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Optional name, required when an app declares more than one resource of the same type.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("size")]
    public string? Size { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Properties { get; init; } = new();

    /// <summary>
    /// Identifies this resource within the app: <see cref="Name"/> when set, otherwise
    /// <see cref="Type"/>. This is what <c>@resource(key).output</c> refers to.
    /// </summary>
    [JsonIgnore]
    public string Key => Name ?? Type;
}
