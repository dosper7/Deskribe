using System.Text.Json.Serialization;

namespace Deskribe.Sdk.Models;

public sealed record PlatformConfig
{
    [JsonPropertyName("organization")]
    public string? Organization { get; init; }

    [JsonPropertyName("defaults")]
    public PlatformDefaults Defaults { get; init; } = new();

    [JsonPropertyName("backends")]
    public Dictionary<string, string> Backends { get; init; } = new();

    [JsonPropertyName("policies")]
    public PlatformPolicies Policies { get; init; } = new();
}

public sealed record PlatformDefaults
{
    [JsonPropertyName("runtime")]
    public string Runtime { get; init; } = "kubernetes";

    [JsonPropertyName("region")]
    public string Region { get; init; } = "westeurope";

    [JsonPropertyName("replicas")]
    public int Replicas { get; init; } = 2;

    [JsonPropertyName("cpu")]
    public string Cpu { get; init; } = "250m";

    [JsonPropertyName("memory")]
    public string Memory { get; init; } = "512Mi";

    [JsonPropertyName("namespacePattern")]
    public string NamespacePattern { get; init; } = "{app}-{env}";

    [JsonPropertyName("ha")]
    public bool? Ha { get; init; }

    [JsonPropertyName("secretsStrategy")]
    public string SecretsStrategy { get; init; } = "opaque";

    [JsonPropertyName("externalSecretsStore")]
    public string? ExternalSecretsStore { get; init; }

    [JsonPropertyName("pulumiProjectDir")]
    public string? PulumiProjectDir { get; init; }
}

public sealed record PlatformPolicies
{
    [JsonPropertyName("allowedRegions")]
    public List<string> AllowedRegions { get; init; } = [];

    [JsonPropertyName("enforceTLS")]
    public bool EnforceTls { get; init; }
}
