using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Deskribe.Core;

namespace Deskribe.Cli.Commands;

/// <summary>Options shared by every command, plus the single place that parses and reports errors.</summary>
internal sealed class CommonOptions
{
    public Option<string> File { get; } = new(["-f", "--file"], () => "deskribe.json", "Path to deskribe.json");
    public Option<string> Env { get; } = new("--env", "Target environment") { IsRequired = true };
    public Option<string> Platform { get; } = new(["-p", "--platform"], "Path to the platform config directory or file") { IsRequired = true };
    public Option<string[]> Image { get; } = new("--image", "Image mapping name=image:tag (repeatable)")
    {
        AllowMultipleArgumentsPerToken = false
    };

    public void Add(Command command, bool withImages)
    {
        command.AddOption(File);
        command.AddOption(Env);
        command.AddOption(Platform);
        if (withImages)
            command.AddOption(Image);
    }

    public CommonArgs Bind(InvocationContext ctx) => new(
        ctx.ParseResult.GetValueForOption(File)!,
        ctx.ParseResult.GetValueForOption(Env)!,
        ctx.ParseResult.GetValueForOption(Platform)!,
        ParseImages(ctx.ParseResult.GetValueForOption(Image)));

    private static Dictionary<string, string>? ParseImages(string[]? images)
    {
        if (images is null || images.Length == 0)
            return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in images)
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0 || eq == entry.Length - 1)
                throw new DeskribeException($"Invalid --image '{entry}': expected name=image:tag.");
            map[entry[..eq]] = entry[(eq + 1)..];
        }
        return map;
    }
}

internal sealed record CommonArgs(string File, string Env, string Platform, Dictionary<string, string>? Images)
{
    public void RequireInputs()
    {
        if (!System.IO.File.Exists(File))
            throw new DeskribeException($"Manifest file not found: {Path.GetFullPath(File)}");
        if (!System.IO.File.Exists(Platform) && !Directory.Exists(Platform))
            throw new DeskribeException($"Platform config path not found: {Path.GetFullPath(Platform)}");
    }
}

internal static class Cli
{
    public static bool Verbose { get; set; }

    /// <summary>Runs a command body; returns the process exit code and presents any failure.</summary>
    public static async Task<int> RunAsync(Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (DeskribeValidationException ex)
        {
            Failure("Validation failed:");
            PrintValidation(ex.Result.Errors, ex.Result.Warnings);
            return 1;
        }
        catch (DeskribeException ex)
        {
            Error(ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Error("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Error(Verbose ? ex.ToString() : $"Unexpected error: {ex.Message} (run with --verbose for details)");
            return 1;
        }
    }

    public static void PrintValidation(IEnumerable<string> errors, IEnumerable<string> warnings)
    {
        foreach (var error in errors)
            Console.Error.WriteLine($"  Error: {error}");
        foreach (var warning in warnings)
            Warn($"  Warning: {warning}");
    }

    public static void Error(string message) => Colored(Console.Error, ConsoleColor.Red, "Error: " + message);

    public static void Failure(string message) => Colored(Console.Error, ConsoleColor.Red, message);

    public static void Warn(string message) => Colored(Console.Error, ConsoleColor.Yellow, message);

    public static void Success(string message) => Colored(Console.Out, ConsoleColor.Green, message);

    private static void Colored(TextWriter writer, ConsoleColor color, string message)
    {
        if (Console.IsOutputRedirected || Console.IsErrorRedirected)
        {
            writer.WriteLine(message);
            return;
        }
        Console.ForegroundColor = color;
        writer.WriteLine(message);
        Console.ResetColor();
    }

    /// <summary>Reads the app name from a manifest without validating it (used for destroy confirmation).</summary>
    public static string ReadAppName(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString()))
                return name.GetString()!;
        }
        catch (JsonException) { }
        throw new DeskribeException($"Could not read the app name from '{manifestPath}'. Run 'deskribe validate' first.");
    }
}
