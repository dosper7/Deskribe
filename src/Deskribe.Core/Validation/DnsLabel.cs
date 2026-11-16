using System.Text.RegularExpressions;

namespace Deskribe.Core.Validation;

/// <summary>DNS labels as Kubernetes uses them for names.</summary>
public static partial class DnsLabel
{
    public const int MaxLength = 63;

    // \z, not $: $ would also accept a trailing newline.
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?\z")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^[a-z]([a-z0-9-]*[a-z0-9])?\z")]
    private static partial Regex ObjectNameRegex();

    /// <summary>RFC 1123 label: what namespaces require.</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxLength && LabelRegex().IsMatch(value);

    /// <summary>RFC 1035 label (must start with a letter): what workload objects such as Services require.</summary>
    public static bool IsValidObjectName(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxLength && ObjectNameRegex().IsMatch(value);
}
