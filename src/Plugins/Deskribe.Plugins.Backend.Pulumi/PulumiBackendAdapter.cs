using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Backend.Pulumi;

public class PulumiBackendAdapter : IBackendAdapter
{
    public string Name => "pulumi";

    public async Task<BackendApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct)
    {
        var pulumiProjectDir = plan.Platform.Defaults.PulumiProjectDir;

        if (!string.IsNullOrEmpty(pulumiProjectDir))
        {
            return await ApplyWithLocalProgramAsync(plan, pulumiProjectDir, ct);
        }

        return ApplyInlineMode(plan);
    }

    private static BackendApplyResult ApplyInlineMode(DeskribePlan plan)
    {
        // Inline mode: Log what would be deployed (backward compat for teams without Pulumi installed)
        var outputs = new Dictionary<string, Dictionary<string, string>>();

        foreach (var resourcePlan in plan.ResourcePlans)
        {
            outputs[resourcePlan.ResourceType] = resourcePlan.PlannedOutputs;

            Console.WriteLine($"[Pulumi] Would deploy {resourcePlan.ResourceType}:");
            Console.WriteLine($"  Action: {resourcePlan.Action}");
            foreach (var (key, value) in resourcePlan.Configuration)
            {
                Console.WriteLine($"  {key}: {FormatValue(value)}");
            }
        }

        return new BackendApplyResult
        {
            Success = true,
            ResourceOutputs = outputs
        };
    }

    private static async Task<BackendApplyResult> ApplyWithLocalProgramAsync(
        DeskribePlan plan, string projectDir, CancellationToken ct)
    {
        // Local Program mode: Use Pulumi Automation API with an existing Pulumi project
        Console.WriteLine($"[Pulumi] Using Local Program mode with project: {projectDir}");

        var stackName = $"{plan.AppName}-{plan.Environment}";
        var outputs = new Dictionary<string, Dictionary<string, string>>();

        try
        {
            var workspace = await global::Pulumi.Automation.LocalWorkspace.CreateOrSelectStackAsync(
                new global::Pulumi.Automation.LocalProgramArgs(stackName, projectDir),
                ct);

            // Set configuration from plan
            await workspace.SetConfigAsync("appName",
                new global::Pulumi.Automation.ConfigValue(plan.AppName), ct);
            await workspace.SetConfigAsync("environment",
                new global::Pulumi.Automation.ConfigValue(plan.Environment), ct);
            await workspace.SetConfigAsync("region",
                new global::Pulumi.Automation.ConfigValue(plan.Platform.Defaults.Region), ct);

            // Set resource-specific config
            foreach (var resourcePlan in plan.ResourcePlans)
            {
                foreach (var (key, value) in resourcePlan.Configuration)
                {
                    var configKey = $"{resourcePlan.ResourceType}:{key}";
                    await workspace.SetConfigAsync(configKey,
                        new global::Pulumi.Automation.ConfigValue(FormatValue(value)), ct);
                }
            }

            Console.WriteLine($"[Pulumi] Running 'pulumi up' for stack {stackName}...");
            var result = await workspace.UpAsync(
                new global::Pulumi.Automation.UpOptions
                {
                    OnStandardOutput = Console.WriteLine
                }, ct);

            Console.WriteLine($"[Pulumi] Stack update complete: {result.Summary.Result}");

            // Extract outputs from stack
            var stackOutputs = result.Outputs;
            foreach (var resourcePlan in plan.ResourcePlans)
            {
                var resourceOutputs = new Dictionary<string, string>();
                foreach (var (key, output) in stackOutputs)
                {
                    if (key.StartsWith(resourcePlan.ResourceType, StringComparison.OrdinalIgnoreCase))
                    {
                        var outputKey = key[(resourcePlan.ResourceType.Length + 1)..];
                        resourceOutputs[outputKey] = output.Value?.ToString() ?? "";
                    }
                }

                // Fall back to planned outputs if no stack outputs match
                if (resourceOutputs.Count == 0)
                    resourceOutputs = resourcePlan.PlannedOutputs;

                outputs[resourcePlan.ResourceType] = resourceOutputs;
            }

            return new BackendApplyResult { Success = true, ResourceOutputs = outputs };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Pulumi] Stack update failed: {ex.Message}");
            return new BackendApplyResult
            {
                Success = false,
                Errors = [$"Pulumi stack update failed: {ex.Message}"]
            };
        }
    }

    public Task DestroyAsync(string appName, string environment, CancellationToken ct)
    {
        Console.WriteLine($"[Pulumi] Would destroy stack: {appName}-{environment}");
        return Task.CompletedTask;
    }

    private static string FormatValue(object? value)
    {
        if (value is null) return "null";
        if (value is JsonElement element) return element.ToString();
        if (value is string s) return s;
        return JsonSerializer.Serialize(value);
    }
}
