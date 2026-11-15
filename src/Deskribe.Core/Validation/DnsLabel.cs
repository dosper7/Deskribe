using System.Text.RegularExpressions;

namespace Deskribe.Core.Validation;

/// <summary>RFC 1123 label as Kubernetes uses it for names and namespaces.</summary>
public static partial class DnsLabel
{
    public const int MaxLength = 63;

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex LabelRegex();

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxLength && LabelRegex().IsMatch(value);
}
