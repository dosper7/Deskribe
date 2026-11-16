using System.Text.RegularExpressions;

namespace Deskribe.Plugins.Runtime.Kubernetes;

/// <summary>Label and annotation names Deskribe puts on the objects it manages.</summary>
public static class KubernetesLabels
{
    public const string ManagedBy = "app.kubernetes.io/managed-by";
    public const string ManagedByValue = "deskribe";
    public const string Name = "app.kubernetes.io/name";
    public const string App = "deskribe.dev/app";
    public const string Environment = "deskribe.dev/environment";
    public const string Team = "deskribe.dev/team";

    /// <summary>Namespace annotation, value <c>{app}/{env}</c>: Deskribe created the namespace for that app and environment.</summary>
    public const string OwnerAnnotation = "deskribe.dev/owner";

    /// <summary>
    /// Pod-template annotation holding a hash of the workload's non-secret environment (plain variables, the
    /// names of secret variables and how they are delivered). Never derived from secret values.
    /// </summary>
    public const string EnvHashAnnotation = "deskribe.dev/env-hash";

    /// <summary>
    /// Pod-template annotation set on apply to the <c>resourceVersion</c> of the workload's opaque Secret, so a
    /// changed secret value rolls the pods. Server-side apply leaves the resourceVersion unchanged when the
    /// Secret's content does not change.
    /// </summary>
    public const string SecretVersionAnnotation = "deskribe.dev/secret-version";

    public static string Owner(string app, string environment) => $"{app}/{environment}";

    /// <summary>Selects every object Deskribe deployed for one app and environment.</summary>
    public static string Selector(string app, string environment) =>
        $"{ManagedBy}={ManagedByValue},{App}={app},{Environment}={environment}";
}

internal static partial class KubernetesNames
{
    // RFC 1035 label: what Service names require.
    [GeneratedRegex("^[a-z]([-a-z0-9]{0,61}[a-z0-9])?\\z")]
    private static partial Regex Dns1035Label();

    // RFC 1123 label: what Namespace names require.
    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?\\z")]
    private static partial Regex Dns1123Label();

    [GeneratedRegex("^(([A-Za-z0-9][-A-Za-z0-9_.]{0,61})?[A-Za-z0-9])?\\z")]
    private static partial Regex LabelValue();

    [GeneratedRegex("^[-._a-zA-Z0-9]+\\z")]
    private static partial Regex ConfigMapKey();

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,13}[a-z0-9])?\\z")]
    private static partial Regex PortName();

    [GeneratedRegex("^[a-zA-Z0-9]([-a-zA-Z0-9_.]*[a-zA-Z0-9])?\\z")]
    private static partial Regex PathSegment();

    public static bool IsValidPortName(string value) =>
        PortName().IsMatch(value) && value.Any(char.IsLetter) && !value.Contains("--");

    public static void RequireWorkloadName(string value) =>
        Require(Dns1035Label().IsMatch(value ?? ""), $"Workload name '{value}' is not a valid Kubernetes object name (lowercase letters, digits and '-', starting with a letter, at most 63 characters).");

    public static void RequireNamespace(string value) =>
        Require(Dns1123Label().IsMatch(value ?? ""), $"Namespace '{value}' is not a valid Kubernetes namespace name (lowercase letters, digits and '-', at most 63 characters).");

    public static void RequireLabelValue(string what, string value) =>
        Require(!string.IsNullOrEmpty(value) && LabelValue().IsMatch(value) && value.Length <= 63,
            $"{what} '{value}' cannot be used as a Kubernetes label value (at most 63 characters: letters, digits, '-', '_' or '.', starting and ending with a letter or digit).");

    public static void RequireSecretKey(string workload, string key) =>
        Require(ConfigMapKey().IsMatch(key ?? ""), $"Workload '{workload}': secret variable name '{key}' is not a valid Kubernetes Secret key (letters, digits, '-', '_' or '.').");

    public static void RequirePathSegment(string what, string value) =>
        Require(PathSegment().IsMatch(value ?? "") && value != "." && value != "..",
            $"{what} '{value}' cannot be used as a directory name.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
