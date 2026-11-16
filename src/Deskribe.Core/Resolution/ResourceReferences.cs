using System.Text.RegularExpressions;

namespace Deskribe.Core.Resolution;

/// <summary>
/// The single definition of the <c>@resource(key).output</c> reference syntax.
/// </summary>
public static partial class ResourceReferences
{
    public const string Marker = "@resource(";

    [GeneratedRegex(@"@resource\((?<key>[A-Za-z0-9._-]+)\)\.(?<output>[A-Za-z0-9_]+)")]
    private static partial Regex ReferenceRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z")]
    private static partial Regex KeyRegex();

    public static Regex Pattern => ReferenceRegex();

    /// <summary>Whether <paramref name="key"/> can be used as a resource key in a reference.</summary>
    public static bool IsValidKey(string? key) => key is not null && KeyRegex().IsMatch(key);

    public static bool ContainsMarker(string value) => value.Contains(Marker, StringComparison.Ordinal);

    public static IReadOnlyList<ResourceReference> Extract(string envVar, string value)
    {
        var refs = new List<ResourceReference>();
        foreach (Match match in ReferenceRegex().Matches(value))
        {
            refs.Add(new ResourceReference(envVar, match.Value, match.Groups["key"].Value, match.Groups["output"].Value));
        }
        return refs;
    }

    public static IReadOnlyList<ResourceReference> Extract(IReadOnlyDictionary<string, string> envVars) =>
        envVars.SelectMany(kv => Extract(kv.Key, kv.Value)).ToList();

    /// <summary>
    /// Returns a snippet for every occurrence of <c>@resource(</c> that does not start a
    /// well-formed reference.
    /// </summary>
    public static IReadOnlyList<string> FindMalformed(string value)
    {
        var wellFormedStarts = ReferenceRegex().Matches(value).Select(m => m.Index).ToHashSet();
        var malformed = new List<string>();
        var index = value.IndexOf(Marker, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (!wellFormedStarts.Contains(index))
            {
                var end = value.IndexOfAny([' ', ';', ',', '\t', '\n'], index);
                var snippet = end < 0 ? value[index..] : value[index..end];
                malformed.Add(snippet);
            }
            index = value.IndexOf(Marker, index + Marker.Length, StringComparison.Ordinal);
        }
        return malformed;
    }

    /// <summary>
    /// Replaces every reference whose value is known. References that cannot be resolved
    /// are left in place and appended to <paramref name="unresolved"/>.
    /// </summary>
    public static string Resolve(
        string envVar,
        string value,
        IReadOnlyDictionary<string, Dictionary<string, string>> outputs,
        List<ResourceReference> unresolved)
    {
        return ReferenceRegex().Replace(value, match =>
        {
            var key = match.Groups["key"].Value;
            var output = match.Groups["output"].Value;
            if (outputs.TryGetValue(key, out var resourceOutputs) &&
                resourceOutputs.TryGetValue(output, out var resolved))
            {
                return resolved;
            }

            unresolved.Add(new ResourceReference(envVar, match.Value, key, output));
            return match.Value;
        });
    }
}

public sealed record ResourceReference(string EnvVar, string Expression, string ResourceKey, string Output);
