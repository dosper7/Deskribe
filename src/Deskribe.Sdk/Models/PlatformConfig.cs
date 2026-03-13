using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deskribe.Sdk.Models;

public sealed record PlatformConfig
{
    [JsonPropertyName("organization")]
    public string? Organization { get; init; }

    [JsonPropertyName("defaults")]
    public PlatformDefaults Defaults { get; init; } = new();

    [JsonPropertyName("runtime")]
    public RuntimeConfig Runtime { get; init; } = new();

    [JsonPropertyName("provisionerConfigs")]
    public Dictionary<string, Dictionary<string, JsonElement>> ProvisionerConfigs { get; init; } = new();

    [JsonPropertyName("provisioners")]
    public Dictionary<string, string> Provisioners { get; init; } = new();

    [JsonPropertyName("policies")]
    public PlatformPolicies Policies { get; init; } = new();

    [JsonPropertyName("environments")]
    public Dictionary<string, EnvironmentConfig>? Environments { get; init; }
}

public sealed record RuntimeConfig
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "kubernetes";

    [JsonPropertyName("config")]
    public Dictionary<string, JsonElement> Config { get; init; } = new();
}

public sealed record PlatformDefaults
{
    [JsonPropertyName("runtime")]
    public string? Runtime { get; init; }

    [JsonPropertyName("region")]
    public string? Region { get; init; }

    [JsonPropertyName("replicas")]
    public int? Replicas { get; init; }

    [JsonPropertyName("cpu")]
    public string? Cpu { get; init; }

    [JsonPropertyName("memory")]
    public string? Memory { get; init; }

    [JsonPropertyName("namespacePattern")]
    public string? NamespacePattern { get; init; }

    [JsonPropertyName("ha")]
    public bool? Ha { get; init; }

    [JsonPropertyName("secretsStrategy")]
    public string? SecretsStrategy { get; init; }

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
