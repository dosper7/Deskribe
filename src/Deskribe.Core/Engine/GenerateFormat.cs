namespace Deskribe.Core.Engine;

/// <summary>Which artifacts <see cref="DeskribeEngine.GenerateAsync"/> produces.</summary>
public enum GenerateFormat
{
    All,
    RuntimeOnly,
    InfraOnly
}

public static class GenerateFormats
{
    public const string All = "all";
    public const string RuntimeOnly = "runtime-only";
    public const string InfraOnly = "infra-only";

    /// <summary>Parses a format name; <c>k8s-only</c> and <c>terraform-only</c> are accepted as legacy aliases.</summary>
    public static GenerateFormat Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or All => GenerateFormat.All,
        RuntimeOnly or "k8s-only" => GenerateFormat.RuntimeOnly,
        InfraOnly or "terraform-only" => GenerateFormat.InfraOnly,
        _ => throw new DeskribeException(
            $"Unknown output format '{value}'. Use '{All}', '{RuntimeOnly}' or '{InfraOnly}'.")
    };
}
