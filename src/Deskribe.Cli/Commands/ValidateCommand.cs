using System.CommandLine;
using Deskribe.Core.Engine;

namespace Deskribe.Cli.Commands;

public static class ValidateCommand
{
    public static Command Create(DeskribeEngine engine)
    {
        var command = new Command("validate", "Validate a deskribe.json manifest against platform config");

        var fileOption = new Option<string>(["-f", "--file"], "Path to deskribe.json") { IsRequired = true };
        var envOption = new Option<string>("--env", "Target environment") { IsRequired = true };
        var platformOption = new Option<string>("--platform", "Path to platform config directory") { IsRequired = true };

        command.AddOption(fileOption);
        command.AddOption(envOption);
        command.AddOption(platformOption);

        command.SetHandler(async (file, env, platform) =>
        {
            try
            {
                var result = await engine.ValidateAsync(file, platform, env);

                if (result.IsValid)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("Validation passed!");
                    Console.ResetColor();

                    foreach (var warning in result.Warnings)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  Warning: {warning}");
                        Console.ResetColor();
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Validation failed:");
                    Console.ResetColor();

                    foreach (var error in result.Errors)
                        Console.WriteLine($"  Error: {error}");

                    foreach (var warning in result.Warnings)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  Warning: {warning}");
                        Console.ResetColor();
                    }

                    Environment.ExitCode = 1;
                }
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
