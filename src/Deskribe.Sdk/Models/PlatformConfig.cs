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

    /// <summary>
    /// Capability catalog: per resource type, the constraints and defaults the platform
    /// allows (for example <c>validSizes</c>, <c>validVersions</c>, <c>defaultVersion</c>).
    /// </summary>
    [JsonPropertyName("resources")]
    public Dictionary<string, Dictionary<string, JsonElement>> Resources { get; init; } = new();

    /// <summary>Resource type → name of the provisioner that fulfils it.</summary>
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

}

public sealed record PlatformPolicies
{
    [JsonPropertyName("allowedRegions")]
    public List<string> AllowedRegions { get; init; } = [];

    [JsonPropertyName("enforceTLS")]
    public bool EnforceTls { get; init; }

    /// <summary>Upper bounds on what a developer's per-environment overrides may request.</summary>
    [JsonPropertyName("limits")]
    public WorkloadLimits Limits { get; init; } = new();
}

public sealed record WorkloadLimits
{
    [JsonPropertyName("maxReplicas")]
    public int? MaxReplicas { get; init; }

    /// <summary>Kubernetes CPU quantity, for example <c>2</c> or <c>1500m</c>.</summary>
    [JsonPropertyName("maxCpu")]
    public string? MaxCpu { get; init; }

    /// <summary>Kubernetes memory quantity, for example <c>4Gi</c>.</summary>
    [JsonPropertyName("maxMemory")]
    public string? MaxMemory { get; init; }
}
