using System.CommandLine;
using System.Text.Json;
using Deskribe.Core.Engine;

namespace Deskribe.Cli.Commands;

public static class PlanCommand
{
    public static Command Create(DeskribeEngine engine)
    {
        var command = new Command("plan", "Generate an execution plan for infrastructure and deployment");

        var fileOption = new Option<string>(["-f", "--file"], () => "deskribe.json", "Path to deskribe.json");
        var envOption = new Option<string>("--env", "Target environment") { IsRequired = true };
        var platformOption = new Option<string>("--platform", "Path to platform config directory") { IsRequired = true };
        var imageOption = new Option<string[]>("--image", "Image mapping in format name=image:tag (can be specified multiple times)");

        command.AddOption(fileOption);
        command.AddOption(envOption);
        command.AddOption(platformOption);
        command.AddOption(imageOption);

        command.SetHandler(async (file, env, platform, images) =>
        {
            try
            {
                var imageMap = ParseImages(images);
                var plan = await engine.PlanAsync(file, platform, env, imageMap);

                Console.WriteLine($"Plan for '{plan.AppName}' in '{plan.Environment}':");
                Console.WriteLine();

                Console.WriteLine("Resources:");
                foreach (var rp in plan.ResourcePlans)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write($"  [{rp.Action.ToUpperInvariant()}] ");
                    Console.ResetColor();
                    Console.WriteLine(rp.ResourceType);

                    foreach (var (key, value) in rp.Configuration)
                    {
                        Console.WriteLine($"    {key}: {FormatValue(value)}");
                    }
                }

                if (plan.Workload is not null)
                {
                    Console.WriteLine();
                    Console.WriteLine("Workload:");
                    Console.WriteLine($"  Namespace: {plan.Workload.Namespace}");
                    Console.WriteLine($"  Image:     {plan.Workload.Image ?? "(not set)"}");
                    Console.WriteLine($"  Replicas:  {plan.Workload.Replicas}");
                    Console.WriteLine($"  CPU:       {plan.Workload.Cpu}");
                    Console.WriteLine($"  Memory:    {plan.Workload.Memory}");

                    if (plan.Workload.EnvironmentVariables.Count > 0)
                    {
                        Console.WriteLine("  Environment:");
                        foreach (var (key, value) in plan.Workload.EnvironmentVariables)
                        {
                            Console.WriteLine($"    {key}: {value}");
                        }
                    }
                }

                foreach (var warning in plan.Warnings)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Warning: {warning}");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
                Environment.ExitCode = 1;
            }
        }, fileOption, envOption, platformOption, imageOption);

        return command;
    }

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
        }
        return map.Count > 0 ? map : null;
    }

    private static string FormatValue(object? value)
    {
        if (value is null) return "null";
        if (value is JsonElement element) return element.ToString();
        if (value is string s) return s;
        return JsonSerializer.Serialize(value);
    }
}
