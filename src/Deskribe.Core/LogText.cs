namespace Deskribe.Core;

/// <summary>Prepares caller-supplied text (environment names, paths) for log entries.</summary>
public static class LogText
{
    /// <summary>Removes line breaks so a value cannot start a forged log entry.</summary>
    public static string Clean(string? value) =>
        (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
}
