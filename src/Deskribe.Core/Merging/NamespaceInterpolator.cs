namespace Deskribe.Core.Merging;

/// <summary>
/// Resolves the {team}/{app}/{env} placeholders in a namespace pattern.
/// A null team falls back to "default".
/// </summary>
public static class NamespaceInterpolator
{
    public static string Resolve(string pattern, string? team, string app, string env) =>
        pattern
            .Replace("{team}", team ?? "default")
            .Replace("{app}", app)
            .Replace("{env}", env);
}
