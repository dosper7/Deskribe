using System.CommandLine;
using Deskribe.Core.Engine;

namespace Deskribe.Cli.Commands;

public static class DestroyCommand
{
    public static Command Create(DeskribeEngine engine)
    {
        var command = new Command("destroy", "Destroy all infrastructure and workloads for an environment");

        var fileOption = new Option<string>(["-f", "--file"], () => "deskribe.json", "Path to deskribe.json");
        var envOption = new Option<string>("--env", "Target environment") { IsRequired = true };
        var platformOption = new Option<string>("--platform", "Path to platform config directory") { IsRequired = true };

        command.AddOption(fileOption);
        command.AddOption(envOption);
        command.AddOption(platformOption);

        command.SetHandler(async (file, env, platform) =>
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Destroying all resources for environment '{env}'...");
                Console.ResetColor();

                await engine.DestroyAsync(file, platform, env);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Destroy complete for environment '{env}'!");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
                Environment.ExitCode = 1;
            }
        }, fileOption, envOption, platformOption);

        return command;
    }
}
