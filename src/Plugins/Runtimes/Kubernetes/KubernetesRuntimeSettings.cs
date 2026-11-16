using System.Text.Json;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Runtime.Kubernetes;

/// <summary>
/// Settings read from the platform's <c>runtime.config</c> section on every call.
/// All keys are optional; unknown keys are ignored, but a known key with an invalid value is an error.
/// </summary>
public sealed record KubernetesRuntimeSettings
{
    public int ContainerPort { get; init; } = 8080;
    public int ServicePort { get; init; } = 80;
    public string PortName { get; init; } = "http";
    public string? HealthPath { get; init; }
    public bool CreateNamespace { get; init; } = true;
    public int RolloutTimeoutSeconds { get; init; } = 300;
    public string ExternalSecretsStore { get; init; } = "default";
    public string ExternalSecretsStoreKind { get; init; } = "ClusterSecretStore";
    public string ExternalSecretsRefreshInterval { get; init; } = "1h";
    public string ExternalSecretsKeyTemplate { get; init; } = "{app}-{env}-{var}";

    public static KubernetesRuntimeSettings From(PlatformConfig? platform)
    {
        var defaults = new KubernetesRuntimeSettings();
        if (platform?.Runtime?.Config is not { Count: > 0 } config)
            return defaults;

        var settings = defaults with
        {
            ContainerPort = ReadInt(config, "containerPort") ?? defaults.ContainerPort,
            ServicePort = ReadInt(config, "servicePort") ?? defaults.ServicePort,
            PortName = ReadString(config, "portName") ?? defaults.PortName,
            HealthPath = ReadString(config, "healthPath") ?? defaults.HealthPath,
            CreateNamespace = ReadBool(config, "createNamespace") ?? defaults.CreateNamespace,
            RolloutTimeoutSeconds = ReadInt(config, "rolloutTimeoutSeconds") ?? defaults.RolloutTimeoutSeconds,
            ExternalSecretsStore = ReadString(config, "externalSecretsStore") ?? defaults.ExternalSecretsStore,
            ExternalSecretsStoreKind = ReadString(config, "externalSecretsStoreKind") ?? defaults.ExternalSecretsStoreKind,
            ExternalSecretsRefreshInterval = ReadString(config, "externalSecretsRefreshInterval") ?? defaults.ExternalSecretsRefreshInterval,
            ExternalSecretsKeyTemplate = ReadString(config, "externalSecretsKeyTemplate") ?? defaults.ExternalSecretsKeyTemplate
        };

        settings.Validate();
        return settings;
    }

    private void Validate()
    {
        if (ContainerPort is < 1 or > 65535)
            throw Invalid("containerPort", "must be between 1 and 65535");
        if (ServicePort is < 1 or > 65535)
            throw Invalid("servicePort", "must be between 1 and 65535");
        if (!KubernetesNames.IsValidPortName(PortName))
            throw Invalid("portName", "must be an IANA service name (1-15 lowercase letters, digits or '-', with at least one letter)");
        if (HealthPath is not null && !HealthPath.StartsWith('/'))
            throw Invalid("healthPath", "must start with '/'");
        if (RolloutTimeoutSeconds < 1)
            throw Invalid("rolloutTimeoutSeconds", "must be a positive number of seconds");
        if (string.IsNullOrWhiteSpace(ExternalSecretsStore))
            throw Invalid("externalSecretsStore", "must not be empty");
        if (ExternalSecretsStoreKind is not ("ClusterSecretStore" or "SecretStore"))
            throw Invalid("externalSecretsStoreKind", "must be 'ClusterSecretStore' or 'SecretStore'");
        if (string.IsNullOrWhiteSpace(ExternalSecretsRefreshInterval))
            throw Invalid("externalSecretsRefreshInterval", "must not be empty");
        if (string.IsNullOrWhiteSpace(ExternalSecretsKeyTemplate))
            throw Invalid("externalSecretsKeyTemplate", "must not be empty");
    }

    private static InvalidOperationException Invalid(string key, string reason) =>
        new($"Invalid Kubernetes runtime setting runtime.config.{key}: {reason}.");

    private static string? ReadString(Dictionary<string, JsonElement> config, string key)
    {
        if (!config.TryGetValue(key, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : throw Invalid(key, $"expected a string but got {element.ValueKind}");
    }

    private static int? ReadInt(Dictionary<string, JsonElement> config, string key)
    {
        if (!config.TryGetValue(key, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(element.GetString(), out var value) => value,
            _ => throw Invalid(key, "expected an integer")
        };
    }

    private static bool? ReadBool(Dictionary<string, JsonElement> config, string key)
    {
        if (!config.TryGetValue(key, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(element.GetString(), out var value) => value,
            _ => throw Invalid(key, "expected true or false")
        };
    }
}
