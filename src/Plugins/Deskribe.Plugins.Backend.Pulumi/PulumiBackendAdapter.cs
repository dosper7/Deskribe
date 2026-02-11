using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Backend.Pulumi;

public class PulumiBackendAdapter : IBackendAdapter
{
    public string Name => "pulumi";

    public Task<BackendApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct)
    {
        // MVP: Log what would be deployed via Pulumi Automation API
        // In production, this would use Pulumi.Automation.LocalWorkspace
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

        return Task.FromResult(new BackendApplyResult
        {
            Success = true,
            ResourceOutputs = outputs
        });
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
