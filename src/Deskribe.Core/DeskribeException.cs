using Deskribe.Sdk.Models;

namespace Deskribe.Core;

/// <summary>A failure the user can act on. The message is meant to be shown as-is.</summary>
public class DeskribeException : Exception
{
    public DeskribeException(string message) : base(message) { }

    public DeskribeException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown when the manifest or platform configuration fails validation.</summary>
public class DeskribeValidationException : DeskribeException
{
    public ValidationResult Result { get; }

    public DeskribeValidationException(ValidationResult result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    private static string BuildMessage(ValidationResult result) =>
        "Validation failed:" + Environment.NewLine +
        string.Join(Environment.NewLine, result.Errors.Select(e => "  - " + e));
}
