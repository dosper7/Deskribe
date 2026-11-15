namespace Deskribe.Cli.Commands;

internal static class CommandHelpers
{
    internal static Dictionary<string, string>? ParseImages(string[]? images)
    {
        if (images is null || images.Length == 0)
            return null;

        var map = new Dictionary<string, string>();
        foreach (var img in images)
        {
            var eqIndex = img.IndexOf('=');
            if (eqIndex > 0)
            {
                map[img[..eqIndex]] = img[(eqIndex + 1)..];
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: skipping invalid --image entry '{img}' (expected format name=image:tag)");
                Console.ResetColor();
            }
        }
        return map.Count > 0 ? map : null;
    }

    internal static bool ValidateInputs(string file, string platform)
    {
        var valid = true;

        if (!File.Exists(file))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: manifest file not found: {Path.GetFullPath(file)}");
            Console.ResetColor();
            valid = false;
        }

        if (!File.Exists(platform) && !Directory.Exists(platform))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: platform config path not found: {Path.GetFullPath(platform)}");
            Console.ResetColor();
            valid = false;
        }

        return valid;
    }
}
