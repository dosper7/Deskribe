using System.CommandLine;
using Deskribe.Core.Engine;

namespace Deskribe.Cli.Commands;

public static class GenerateCommand
{
    public static Command Create(DeskribeEngine engine)
    {
        var command = new Command("generate", "Generate deployment artifacts (terraform.tfvars.json, helm-values.yaml, bindings.json)");

        var fileOption = new Option<string>(["-f", "--file"], () => "deskribe.json", "Path to deskribe.json");
        var envOption = new Option<string>("--env", "Target environment") { IsRequired = true };
        var platformOption = new Option<string>(["-p", "--platform"], "Path to platform config directory or file") { IsRequired = true };
        var outputOption = new Option<string>(["-o", "--output-dir"], () => "./generated", "Output directory for generated artifacts");
        var imageOption = new Option<string[]>("--image", "Image mapping in format name=image:tag");

        command.AddOption(fileOption);
        command.AddOption(envOption);
        command.AddOption(platformOption);
        command.AddOption(outputOption);
        command.AddOption(imageOption);

        command.SetHandler(async (file, env, platform, outputDir, images) =>
        {
            try
            {
                var imageMap = PlanCommand.ParseImages(images);
                var files = await engine.GenerateAsync(file, platform, env, outputDir, imageMap);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Generated {files.Count} artifact(s) to {Path.GetFullPath(outputDir)}/");
                Console.ResetColor();

                foreach (var f in files)
                {
                    Console.WriteLine($"  {Path.GetFileName(f.Path)} ({f.Format})");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
                Environment.ExitCode = 1;
            }
        }, fileOption, envOption, platformOption, outputOption, imageOption);

        return command;
    }
}
