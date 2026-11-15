using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deskribe.Sdk;
using k8s;
using k8s.Models;

namespace Deskribe.Plugins.Runtime.Kubernetes;

/// <summary>One rendered Kubernetes object. <see cref="Body"/> is a typed model or, for CRDs, a dictionary.</summary>
internal sealed record ManifestObject(string ApiVersion, string Kind, string Name, string? Namespace, object Body)
{
    public string Id => Namespace is null ? $"{Kind}/{Name}" : $"{Kind}/{Namespace}/{Name}";

    public string ToYaml() => ManifestRenderer.ToYaml(Body);
}

internal sealed record RenderedManifests(string Namespace, ManifestObject? NamespaceObject, IReadOnlyList<ManifestObject> Objects)
{
    /// <summary>The namespace object (if any) first, then every namespaced object.</summary>
    public IEnumerable<ManifestObject> All => NamespaceObject is null ? Objects : Objects.Prepend(NamespaceObject);

    public string ToYaml() => string.Join("---\n", All.Select(o => o.ToYaml()));
}

internal static class ManifestRenderer
{
    public const string ExternalSecretGroup = "external-secrets.io";
    public const string ExternalSecretVersion = "v1beta1";
    public const string ExternalSecretPlural = "externalsecrets";
    public const string ExternalSecretApiVersion = ExternalSecretGroup + "/" + ExternalSecretVersion;

    /// <summary>Serialises one object as a YAML document that always ends with a newline, so documents can be concatenated safely.</summary>
    public static string ToYaml(object body)
    {
        var yaml = KubernetesYaml.Serialize(body);
        return yaml.EndsWith('\n') ? yaml : yaml + "\n";
    }

    public static string EnvSecretName(string workload) => $"{workload}-env";

    public static RenderedManifests Render(RuntimeContext ctx, KubernetesRuntimeSettings settings)
    {
        KubernetesNames.RequireNamespace(ctx.Namespace);
        KubernetesNames.RequireLabelValue("App name", ctx.AppName);
        KubernetesNames.RequireLabelValue("Environment", ctx.Environment);
        if (!string.IsNullOrEmpty(ctx.Team))
            KubernetesNames.RequireLabelValue("Team", ctx.Team);

        var namespaceObject = settings.CreateNamespace ? RenderNamespace(ctx) : null;

        var objects = new List<ManifestObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workload in ctx.Workloads)
        {
            if (!seen.Add(workload.Name))
                throw new InvalidOperationException($"Workload name '{workload.Name}' is used more than once.");
            objects.AddRange(RenderWorkload(ctx, workload, settings));
        }

        return new RenderedManifests(ctx.Namespace, namespaceObject, objects);
    }

    public static Dictionary<string, string> NamespaceLabels() => new() { [KubernetesLabels.ManagedBy] = KubernetesLabels.ManagedByValue };

    public static Dictionary<string, string> NamespaceAnnotations(RuntimeContext ctx) =>
        new() { [KubernetesLabels.OwnerAnnotation] = KubernetesLabels.Owner(ctx.AppName, ctx.Environment) };

    private static ManifestObject RenderNamespace(RuntimeContext ctx)
    {
        var ns = new V1Namespace
        {
            ApiVersion = "v1",
            Kind = "Namespace",
            Metadata = new V1ObjectMeta
            {
                Name = ctx.Namespace,
                Labels = NamespaceLabels(),
                Annotations = NamespaceAnnotations(ctx)
            }
        };
        return new ManifestObject("v1", "Namespace", ctx.Namespace, null, ns);
    }

    private static IEnumerable<ManifestObject> RenderWorkload(RuntimeContext ctx, WorkloadPlan workload, KubernetesRuntimeSettings settings)
    {
        KubernetesNames.RequireWorkloadName(workload.Name);
        if (string.IsNullOrWhiteSpace(workload.Image))
            throw new InvalidOperationException($"Workload '{workload.Name}' has no container image. Set an image for this service; Deskribe does not pick a default.");
        if (!SecretsStrategies.All.Contains(workload.SecretsStrategy))
            throw new InvalidOperationException(
                $"Workload '{workload.Name}' uses unknown secrets strategy '{workload.SecretsStrategy}'. Supported strategies: {string.Join(", ", SecretsStrategies.All.Order())}.");
        if (workload.Namespace != ctx.Namespace)
            throw new InvalidOperationException($"Workload '{workload.Name}' targets namespace '{workload.Namespace}' but the runtime context namespace is '{ctx.Namespace}'.");

        var ns = ctx.Namespace;
        var labels = CommonLabels(ctx, workload);
        var selector = SelectorLabels(ctx, workload);

        var secretKeys = workload.SecretKeys.Order(StringComparer.Ordinal).ToList();
        foreach (var key in secretKeys)
            KubernetesNames.RequireSecretKey(workload.Name, key);

        var plainEnv = workload.EnvironmentVariables
            .Where(kv => !workload.SecretKeys.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        var secretName = EnvSecretName(workload.Name);
        var storeName = string.IsNullOrWhiteSpace(workload.ExternalSecretsStore) ? settings.ExternalSecretsStore : workload.ExternalSecretsStore;
        var hashInput = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in plainEnv)
            hashInput[$"env:{key}"] = value;

        if (secretKeys.Count > 0)
        {
            if (workload.SecretsStrategy == SecretsStrategies.Opaque)
            {
                var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var key in secretKeys)
                {
                    if (!workload.EnvironmentVariables.TryGetValue(key, out var value))
                        throw new InvalidOperationException($"Workload '{workload.Name}': secret variable '{key}' has no value.");
                    data[key] = Encoding.UTF8.GetBytes(value);
                    hashInput[$"secret:{key}"] = value;
                }

                var secret = new V1Secret
                {
                    ApiVersion = "v1",
                    Kind = "Secret",
                    Metadata = new V1ObjectMeta { Name = secretName, NamespaceProperty = ns, Labels = new Dictionary<string, string>(labels) },
                    Type = "Opaque",
                    Data = data
                };
                yield return new ManifestObject("v1", "Secret", secretName, ns, secret);
            }
            else
            {
                var entries = new List<object>();
                foreach (var key in secretKeys)
                {
                    var remoteKey = RemoteKey(settings.ExternalSecretsKeyTemplate, ctx, workload, key);
                    hashInput[$"external:{key}"] = $"{settings.ExternalSecretsStoreKind}/{storeName}/{remoteKey}";
                    entries.Add(new Dictionary<string, object>
                    {
                        ["secretKey"] = key,
                        ["remoteRef"] = new Dictionary<string, object> { ["key"] = remoteKey }
                    });
                }

                var externalSecret = new Dictionary<string, object>
                {
                    ["apiVersion"] = ExternalSecretApiVersion,
                    ["kind"] = "ExternalSecret",
                    ["metadata"] = new Dictionary<string, object>
                    {
                        ["name"] = secretName,
                        ["namespace"] = ns,
                        ["labels"] = new Dictionary<string, string>(labels)
                    },
                    ["spec"] = new Dictionary<string, object>
                    {
                        ["refreshInterval"] = settings.ExternalSecretsRefreshInterval,
                        ["secretStoreRef"] = new Dictionary<string, object>
                        {
                            ["name"] = storeName!,
                            ["kind"] = settings.ExternalSecretsStoreKind
                        },
                        ["target"] = new Dictionary<string, object>
                        {
                            ["name"] = secretName,
                            ["creationPolicy"] = "Owner"
                        },
                        ["data"] = entries
                    }
                };
                yield return new ManifestObject(ExternalSecretApiVersion, "ExternalSecret", secretName, ns, externalSecret);
            }
        }

        var service = new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = new V1ObjectMeta { Name = workload.Name, NamespaceProperty = ns, Labels = new Dictionary<string, string>(labels) },
            Spec = new V1ServiceSpec
            {
                Selector = new Dictionary<string, string>(selector),
                Ports =
                [
                    new V1ServicePort
                    {
                        Name = settings.PortName,
                        Port = settings.ServicePort,
                        TargetPort = settings.ContainerPort,
                        Protocol = "TCP"
                    }
                ]
            }
        };
        yield return new ManifestObject("v1", "Service", workload.Name, ns, service);

        var container = new V1Container
        {
            Name = workload.Name,
            Image = workload.Image,
            Ports = [new V1ContainerPort { Name = settings.PortName, ContainerPort = settings.ContainerPort, Protocol = "TCP" }],
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new(workload.Cpu),
                    ["memory"] = new(workload.Memory)
                },
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new(workload.Cpu),
                    ["memory"] = new(workload.Memory)
                }
            }
        };

        if (plainEnv.Count > 0)
            container.Env = plainEnv.Select(kv => new V1EnvVar { Name = kv.Key, Value = kv.Value }).ToList();

        if (secretKeys.Count > 0)
            container.EnvFrom = [new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = secretName } }];

        if (settings.HealthPath is not null)
        {
            container.ReadinessProbe = new V1Probe
            {
                HttpGet = new V1HTTPGetAction { Path = settings.HealthPath, Port = settings.ContainerPort },
                PeriodSeconds = 5,
                FailureThreshold = 3
            };
        }

        var podLabels = new Dictionary<string, string>(labels);
        var deployment = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = workload.Name, NamespaceProperty = ns, Labels = new Dictionary<string, string>(labels) },
            Spec = new V1DeploymentSpec
            {
                Replicas = workload.Replicas,
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string>(selector) },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = podLabels,
                        Annotations = new Dictionary<string, string> { [KubernetesLabels.EnvHashAnnotation] = Hash(hashInput) }
                    },
                    Spec = new V1PodSpec { Containers = [container] }
                }
            }
        };
        yield return new ManifestObject("apps/v1", "Deployment", workload.Name, ns, deployment);
    }

    public static Dictionary<string, string> CommonLabels(RuntimeContext ctx, WorkloadPlan workload)
    {
        var labels = new Dictionary<string, string>
        {
            [KubernetesLabels.ManagedBy] = KubernetesLabels.ManagedByValue,
            [KubernetesLabels.Name] = workload.Name,
            [KubernetesLabels.App] = ctx.AppName,
            [KubernetesLabels.Environment] = ctx.Environment
        };
        if (!string.IsNullOrEmpty(ctx.Team))
            labels[KubernetesLabels.Team] = ctx.Team;
        return labels;
    }

    /// <summary>Deployment selectors are immutable, so this only uses labels that never change for a workload.</summary>
    public static Dictionary<string, string> SelectorLabels(RuntimeContext ctx, WorkloadPlan workload) => new()
    {
        [KubernetesLabels.App] = ctx.AppName,
        [KubernetesLabels.Name] = workload.Name
    };

    public static string RemoteKey(string template, RuntimeContext ctx, WorkloadPlan workload, string variable) =>
        template
            .Replace("{app}", ctx.AppName, StringComparison.Ordinal)
            .Replace("{env}", ctx.Environment, StringComparison.Ordinal)
            .Replace("{workload}", workload.Name, StringComparison.Ordinal)
            .Replace("{team}", ctx.Team ?? "", StringComparison.Ordinal)
            .Replace("{var}", variable.ToLowerInvariant().Replace("__", "-", StringComparison.Ordinal), StringComparison.Ordinal);

    private static string Hash(SortedDictionary<string, string> input)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(input);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
