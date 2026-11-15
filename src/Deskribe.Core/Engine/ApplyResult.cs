namespace Deskribe.Core.Engine;

/// <summary>Outcome of <see cref="DeskribeEngine.ApplyAsync"/>. Never carries resolved values.</summary>
public sealed record ApplyResult
{
    public required string Namespace { get; init; }

    /// <summary>Runtime objects created or updated.</summary>
    public List<string> Applied { get; init; } = [];

    /// <summary>Runtime objects removed because they are no longer declared.</summary>
    public List<string> Pruned { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}
