namespace Deskribe.Sdk.Models;

public sealed record ValidationResult
{
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];

    public static ValidationResult Valid() => new() { IsValid = true };

    public static ValidationResult Invalid(params string[] errors) =>
        new() { IsValid = false, Errors = [.. errors] };
}
