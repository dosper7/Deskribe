using System.CommandLine;
using System.Text.Json;
using Deskribe.Core.Engine;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Cli.Commands;

public static class Commands
{
    public static IEnumerable<Command> CreateAll(DeskribeEngine engine) =>
    [
        Validate(engine), Plan(engine), Apply(engine), Generate(engine), Status(engine), Destroy(engine)
    ];

    private static Command Validate(DeskribeEngine engine)
    {
        var command = new Command("validate", "Validate a manifest against the platform config");
        var common = new CommonOptions();
        common.Add(command, withImages: false);

        command.SetHandler(async ctx =>
        {
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await Cli.RunAsync(async () =>
            {
                var args = common.Bind(ctx);
                args.RequireInputs();
                var result = await engine.ValidateAsync(args.File, args.Platform, args.Env, ct);
                if (!result.IsValid)
                {
                    Cli.Failure("Validation failed:");
                    Cli.PrintValidation(result.Errors, result.Warnings);
                    return 1;
                }
                Cli.Success("Validation passed.");
                Cli.PrintValidation([], result.Warnings);
                return 0;
            });
        });
        return command;
    }

    private static Command Plan(DeskribeEngine engine)
    {
        var command = new Command("plan", "Show what would be provisioned and deployed (nothing is changed)");
        var common = new CommonOptions();
        common.Add(command, withImages: true);

        command.SetHandler(async ctx =>
        {
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await Cli.RunAsync(async () =>
            {
                var args = common.Bind(ctx);
                args.RequireInputs();
                var plan = await engine.PlanAsync(args.File, args.Platform, args.Env, args.Images, ct);
                PrintPlan(plan);
                return 0;
            });
        });
        return command;
    }

    private static Command Apply(DeskribeEngine engine)
    {
        var command = new Command("apply", "Provision resources and deploy workloads");
        var common = new CommonOptions();
        common.Add(command, withImages: true);

        command.SetHandler(async ctx =>
        {
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await Cli.RunAsync(async () =>
            {
                var args = common.Bind(ctx);
                args.RequireInputs();
                var plan = await engine.PlanAsync(args.File, args.Platform, args.Env, args.Images, ct);
                Console.WriteLine($"Applying '{plan.AppName}' to '{plan.Environment}' (namespace {plan.Namespace}): " +
                                  $"{plan.ResourcePlans.Count} resource(s), {plan.Workloads.Count} workload(s)...");

                var result = await engine.ApplyAsync(plan, ct);

                Cli.Success($"Applied '{plan.AppName}' in '{plan.Environment}'.");
                Console.WriteLine($"  Namespace: {result.Namespace}");
                PrintList("Applied", result.Applied);
                PrintList("Pruned", result.Pruned);
                Cli.PrintValidation([], result.Warnings);
                return 0;
            });
        });
        return command;
    }

    private static Command Generate(DeskribeEngine engine)
    {
        var command = new Command("generate", "Write GitOps artifacts (runtime manifests, infrastructure files, bindings.json)");
        var common = new CommonOptions();
        common.Add(command, withImages: true);
        var output = new Option<string>(["-o", "--output-dir"], () => "./generated", "Output directory");
        var format = new Option<string>("--output-format", () => GenerateFormats.All,
            "all, runtime-only or infra-only (legacy: k8s-only, terraform-only)");
        format.FromAmong(GenerateFormats.All, GenerateFormats.RuntimeOnly, GenerateFormats.InfraOnly, "k8s-only", "terraform-only");
        var outputs = new Option<string?>("--outputs", "JSON file with resource outputs used to resolve @resource references");
        command.AddOption(output);
        command.AddOption(format);
        command.AddOption(outputs);

        command.SetHandler(async ctx =>
        {
            var dir = ctx.ParseResult.GetValueForOption(output)!;
            var fmt = ctx.ParseResult.GetValueForOption(format);
            var outputsPath = ctx.ParseResult.GetValueForOption(outputs);
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await Cli.RunAsync(async () =>
            {
                var args = common.Bind(ctx);
                args.RequireInputs();
                var files = await engine.GenerateAsync(args.File, args.Platform, args.Env, dir, args.Images, fmt, outputsPath, ct);
                var root = Path.GetFullPath(dir);
                Cli.Success($"Generated {files.Count} file(s) in {root}");
                foreach (var f in files)
                    Console.WriteLine($"  {Path.GetRelativePath(root, f.Path)} ({f.Format})");
                return 0;
            });
        });
        return command;
    }

    private static Command Status(DeskribeEngine engine)
    {
        var command = new Command("status", "Show the deployed state; exits non-zero if nothing is deployed or a workload is not ready");
        var common = new CommonOptions();
        common.Add(command, withImages: false);

        command.SetHandler(async ctx =>
        {
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await Cli.RunAsync(async () =>
            {
                var args = common.Bind(ctx);
                args.RequireInputs();
                var status = await engine.StatusAsync(args.File, args.Platform, args.Env, ct);
                Console.WriteLine($"Namespace: {status.Namespace}");
                if (!status.Exists || status.Workloads.Count == 0)
                {
                    Cli.Error("Nothing is deployed.");
                    return 1;
                }
                foreach (var w in status.Workloads)
                    Console.WriteLine($"  {w.Name}: {w.Image ?? "(unknown image)"}  {w.ReadyReplicas}/{w.DesiredReplicas} ready" +
                                      (w.Ready ? "" : "  NOT READY"));
                return status.Workloads.All(w => w.Ready) ? 0 : 1;
            });
        });
        return command;
    }

    private static Command Destroy(DeskribeEngine engine)
    {
        var command = new Command("destroy", "Tear down the workloads AND the provisioned infrastructure for an environment");
        var common = new CommonOptions();
        common.Add(command, withImages: false);
        var yes = new Option<bool>(["-y", "--yes"], "Skip the confirmation prompt");
        command.AddOption(yes);

        command.SetHandler(async ctx =>
        {
            var skipPrompt = ctx.ParseResult.GetValueForOption(yes);
            var ct = ctx.GetCancellationToken();
            ctx.ExitCode = await Cli.RunAsync(async () =>
            {
                var args = common.Bind(ctx);
                args.RequireInputs();
                if (!skipPrompt && !Confirm(Cli.ReadAppName(args.File), args.Env))
                    return 1;

                await engine.DestroyAsync(args.File, args.Platform, args.Env, ct);
                Cli.Success($"Destroyed '{args.Env}'.");
                return 0;
            });
        });
        return command;
    }

    private static bool Confirm(string appName, string env)
    {
        if (Console.IsInputRedirected)
        {
            Cli.Error("Refusing to destroy without confirmation on non-interactive input. Pass --yes to proceed.");
            return false;
        }
        Cli.Warn($"This permanently destroys the workloads and infrastructure of '{appName}' in '{env}'.");
        Console.Write($"Type the app name ({appName}) to confirm: ");
        if (Console.ReadLine()?.Trim() == appName)
            return true;
        Cli.Error("Confirmation did not match; nothing was destroyed.");
        return false;
    }

    private static void PrintList(string title, List<string> items)
    {
        if (items.Count == 0)
        {
            Console.WriteLine($"  {title}: (none)");
            return;
        }
        Console.WriteLine($"  {title}:");
        foreach (var item in items)
            Console.WriteLine($"    {item}");
    }

    private static void PrintPlan(DeskribePlan plan)
    {
        Console.WriteLine($"Plan for '{plan.AppName}' in '{plan.Environment}' (namespace {plan.Namespace})");

        Console.WriteLine();
        Console.WriteLine("Resources:");
        if (plan.ResourcePlans.Count == 0)
            Console.WriteLine("  (none)");
        foreach (var rp in plan.ResourcePlans)
        {
            Console.WriteLine($"  {rp.ResourceKey} ({rp.ResourceType})");
            foreach (var (key, value) in rp.Configuration)
                Console.WriteLine($"    {key}: {FormatValue(value)}");
        }

        Console.WriteLine();
        Console.WriteLine("Workloads:");
        foreach (var w in plan.Workloads)
        {
            Console.WriteLine($"  {w.Name}");
            Console.WriteLine($"    Image:    {w.Image ?? "(not set; required for apply)"}");
            Console.WriteLine($"    Replicas: {w.Replicas}");
            Console.WriteLine($"    CPU:      {w.Cpu}");
            Console.WriteLine($"    Memory:   {w.Memory}");
            Console.WriteLine($"    Secrets:  {w.SecretsStrategy}");
            if (w.EnvironmentVariables.Count > 0)
            {
                Console.WriteLine("    Environment:");
                foreach (var (key, value) in w.EnvironmentVariables)
                    Console.WriteLine($"      {key}: {(w.SecretKeys.Contains(key) ? "(secret)" : value)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Bindings:");
        if (plan.Bindings.Count == 0)
            Console.WriteLine("  (none)");
        foreach (var b in plan.Bindings)
            Console.WriteLine($"  {b.Workload}: {b.EnvVar} <- {b.ResourceKey}({b.ResourceType}).{b.Output}{(b.Sensitive ? "  [sensitive]" : "")}");

        if (plan.Warnings.Count > 0)
        {
            Console.WriteLine();
            Cli.PrintValidation([], plan.Warnings);
        }
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        string s => s,
        JsonElement e => e.ToString(),
        _ => JsonSerializer.Serialize(value)
    };
}
