using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Deskribe.Plugins.Runtime.Kubernetes.Tests;

internal static class TestData
{
    public static WorkloadPlan Workload(
        string name = "api",
        string? image = "ghcr.io/acme/api:1.0.0",
        Dictionary<string, string>? env = null,
        IEnumerable<string>? secretKeys = null,
        string strategy = SecretsStrategies.Opaque,
        string? store = null,
        string app = "orders",
        string environment = "dev",
        string ns = "orders-dev",
        string? team = "payments",
        int replicas = 2) => new()
    {
        Name = name,
        AppName = app,
        Environment = environment,
        Namespace = ns,
        Team = team,
        Image = image,
        Replicas = replicas,
        Cpu = "100m",
        Memory = "64Mi",
        EnvironmentVariables = env ?? new Dictionary<string, string>
        {
            ["LOG_LEVEL"] = "debug",
            ["ConnectionStrings__Postgres"] = "Host=pg;Database=orders;Token=tok-abc-42"
        },
        SecretKeys = secretKeys is null ? ["ConnectionStrings__Postgres"] : [.. secretKeys],
        SecretsStrategy = strategy,
        ExternalSecretsStore = store
    };

    public static RuntimeContext Context(
        IEnumerable<WorkloadPlan>? workloads = null,
        Dictionary<string, object>? config = null,
        string app = "orders",
        string environment = "dev",
        string ns = "orders-dev",
        string? team = "payments") => new()
    {
        AppName = app,
        Environment = environment,
        Namespace = ns,
        Team = team,
        Platform = Platform(config),
        Defaults = new PlatformDefaults(),
        Workloads = (workloads ?? [Workload(app: app, environment: environment, ns: ns, team: team)]).ToList()
    };

    public static PlatformConfig Platform(Dictionary<string, object>? config) => new()
    {
        Runtime = new RuntimeConfig
        {
            Config = (config ?? []).ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value))
        }
    };

    /// <summary>Parses a multi-document YAML stream into generic dictionaries.</summary>
    public static List<Dictionary<object, object>> ParseYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder().Build();
        var parser = new Parser(new StringReader(yaml));
        parser.Consume<StreamStart>();
        var docs = new List<Dictionary<object, object>>();
        while (parser.Accept<DocumentStart>(out _))
            docs.Add(deserializer.Deserialize<Dictionary<object, object>>(parser));
        return docs;
    }

    public static Dictionary<object, object> Doc(List<Dictionary<object, object>> docs, string kind, string name) =>
        docs.Single(d => (string)d["kind"] == kind && (string)Path(d, "metadata.name")! == name);

    public static object? Path(object node, string path)
    {
        object? current = node;
        foreach (var part in path.Split('.'))
        {
            current = current switch
            {
                Dictionary<object, object> map => map.TryGetValue(part, out var v) ? v : null,
                List<object> list when int.TryParse(part, out var i) => i < list.Count ? list[i] : null,
                _ => null
            };
            if (current is null)
                return null;
        }
        return current;
    }

    public static Dictionary<object, object> Container(Dictionary<object, object> deployment) =>
        (Dictionary<object, object>)Path(deployment, "spec.template.spec.containers.0")!;

    public static Dictionary<string, string> PlainEnv(Dictionary<object, object> deployment) =>
        ((List<object>?)Path(Container(deployment), "env") ?? [])
        .Cast<Dictionary<object, object>>()
        .ToDictionary(e => (string)e["name"], e => (string)e["value"]);
}
