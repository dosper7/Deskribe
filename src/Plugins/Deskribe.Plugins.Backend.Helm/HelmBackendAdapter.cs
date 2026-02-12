using System.Diagnostics;
using System.Text;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using k8s;
using k8s.Models;

namespace Deskribe.Plugins.Backend.Helm;

public class HelmBackendAdapter : IBackendAdapter
{
    public string Name => "helm";

    private static readonly Dictionary<string, HelmChartMapping> ChartMappings = new()
    {
        ["postgres"] = new HelmChartMapping
        {
            Chart = "oci://registry-1.docker.io/bitnamicharts/postgresql",
            BuildSetValues = (config, appName) =>
            {
                var sets = new List<string>();
                if (config.TryGetValue("version", out var version))
                    sets.Add($"image.tag={version}");
                sets.Add($"auth.database={appName}");
                return sets;
            },
            SecretName = release => $"{release}-postgresql",
            SecretKey = "postgres-password",
            BuildConnectionString = (release, ns, password, appName) =>
                $"Host={release}-postgresql.{ns}.svc.cluster.local;Port=5432;Database={appName};Username=postgres;Password={password}"
        },
        ["redis"] = new HelmChartMapping
        {
            Chart = "oci://registry-1.docker.io/bitnamicharts/redis",
            BuildSetValues = (config, appName) =>
            {
                var sets = new List<string>();
                if (config.TryGetValue("version", out var version))
                    sets.Add($"image.tag={version}");
                return sets;
            },
            SecretName = release => $"{release}-redis",
            SecretKey = "redis-password",
            BuildConnectionString = (release, ns, password, _) =>
                $"{release}-redis-master.{ns}.svc.cluster.local:6379,password={password}"
        },
        ["kafka.messaging"] = new HelmChartMapping
        {
            Chart = "oci://registry-1.docker.io/bitnamicharts/kafka",
            BuildSetValues = (_, _) => [],
            SecretName = _ => null!,
            SecretKey = null,
            BuildConnectionString = (release, ns, _, _) =>
                $"{release}-kafka.{ns}.svc.cluster.local:9092"
        }
    };

    public async Task<BackendApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct)
    {
        var outputs = new Dictionary<string, Dictionary<string, string>>();
        var ns = plan.Workload?.Namespace ?? $"{plan.AppName}-{plan.Environment}";

        foreach (var resourcePlan in plan.ResourcePlans)
        {
            var releaseName = resourcePlan.Configuration.GetValueOrDefault("helmRelease")?.ToString()
                ?? $"{plan.AppName}-{resourcePlan.ResourceType.Replace(".", "-")}";
            var chartOverride = resourcePlan.Configuration.GetValueOrDefault("helmChart")?.ToString();

            if (!ChartMappings.TryGetValue(resourcePlan.ResourceType, out var mapping))
            {
                Console.WriteLine($"[Helm] No chart mapping for {resourcePlan.ResourceType}, using planned outputs.");
                outputs[resourcePlan.ResourceType] = resourcePlan.PlannedOutputs;
                continue;
            }

            var chart = chartOverride ?? mapping.Chart;
            var setValues = mapping.BuildSetValues(resourcePlan.Configuration, plan.AppName);

            var helmArgs = BuildHelmArgs(releaseName, chart, ns, setValues);
            Console.WriteLine($"[Helm] Installing {resourcePlan.ResourceType}: helm {helmArgs}");

            await RunHelm(helmArgs, ct);

            var resourceOutputs = await ExtractOutputs(
                resourcePlan.ResourceType, mapping, releaseName, ns, plan.AppName, ct);
            outputs[resourcePlan.ResourceType] = resourceOutputs;
        }

        return new BackendApplyResult { Success = true, ResourceOutputs = outputs };
    }

    public async Task DestroyAsync(string appName, string environment, CancellationToken ct)
    {
        var ns = $"{appName}-{environment}";
        Console.WriteLine($"[Helm] Uninstalling all releases in namespace {ns}");

        try
        {
            var listOutput = await RunHelm($"list --namespace {ns} -q", ct);
            var releases = listOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var release in releases)
            {
                await RunHelm($"uninstall {release} --namespace {ns}", ct);
                Console.WriteLine($"[Helm] Uninstalled {release}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Helm] Destroy failed: {ex.Message}");
        }
    }

    public static string BuildHelmArgs(string releaseName, string chart, string ns, List<string> setValues)
    {
        var sb = new StringBuilder();
        sb.Append($"upgrade --install {releaseName} {chart} --namespace {ns} --create-namespace --wait");

        foreach (var setValue in setValues)
        {
            sb.Append($" --set {setValue}");
        }

        return sb.ToString();
    }

    private static async Task<string> RunHelm(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("helm", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"helm {args} failed (exit {process.ExitCode}): {error}");
        }

        return output;
    }

    private static async Task<Dictionary<string, string>> ExtractOutputs(
        string resourceType, HelmChartMapping mapping, string releaseName, string ns, string appName,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>();

        if (mapping.SecretKey is null)
        {
            // No secret to read (e.g., Kafka with no auth)
            var connStr = mapping.BuildConnectionString(releaseName, ns, "", appName);
            result["endpoint"] = connStr;
            result["connectionString"] = connStr;
            return result;
        }

        try
        {
            var config = KubernetesClientConfiguration.BuildDefaultConfig();
            var client = new k8s.Kubernetes(config);
            var secretName = mapping.SecretName(releaseName);

            var secret = await client.ReadNamespacedSecretAsync(secretName, ns, cancellationToken: ct);
            var password = Encoding.UTF8.GetString(secret.Data[mapping.SecretKey]);

            var connStr = mapping.BuildConnectionString(releaseName, ns, password, appName);
            result["connectionString"] = connStr;
            result["endpoint"] = $"{releaseName}-{resourceType}.{ns}.svc.cluster.local";
            result["password"] = password;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Helm] Could not read secret for {resourceType}: {ex.Message}");
            // Fall back to planned outputs pattern
            var connStr = mapping.BuildConnectionString(releaseName, ns, "<pending>", appName);
            result["connectionString"] = connStr;
            result["endpoint"] = $"{releaseName}-{resourceType}.{ns}.svc.cluster.local";
        }

        return result;
    }
}

internal record HelmChartMapping
{
    public required string Chart { get; init; }
    public required Func<Dictionary<string, object?>, string, List<string>> BuildSetValues { get; init; }
    public required Func<string, string> SecretName { get; init; }
    public required string? SecretKey { get; init; }
    public required Func<string, string, string, string, string> BuildConnectionString { get; init; }
}
