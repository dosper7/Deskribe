using System.CommandLine;
using Deskribe.Core.Engine;

namespace Deskribe.Cli.Commands;

public static class ApplyCommand
{
    public static Command Create(DeskribeEngine engine)
    {
        var command = new Command("apply", "Apply infrastructure and deploy workloads");

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
                var imageMap = PlanCommand.ParseImages(images);

                Console.WriteLine($"Planning...");
                var plan = await engine.PlanAsync(file, platform, env, imageMap);

                Console.WriteLine($"Applying plan for '{plan.AppName}' in '{plan.Environment}'...");
                Console.WriteLine();

                await engine.ApplyAsync(plan);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine();
                Console.WriteLine($"Apply complete for '{plan.AppName}' in '{plan.Environment}'!");
                Console.ResetColor();
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
}
