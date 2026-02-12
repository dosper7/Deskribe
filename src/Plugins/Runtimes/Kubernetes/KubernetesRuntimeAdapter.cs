using Deskribe.Sdk;
using k8s;
using k8s.Models;

namespace Deskribe.Plugins.Runtime.Kubernetes;

public class KubernetesRuntimeAdapter : IRuntimeAdapter
{
    private readonly KubernetesClientConfiguration? _config;

    public KubernetesRuntimeAdapter(KubernetesClientConfiguration? config = null)
    {
        _config = config;
    }

    public string Name => "kubernetes";

    public Task<WorkloadManifest> RenderAsync(WorkloadPlan workload, CancellationToken ct = default)
    {
        var resources = new List<object>();
        var resourceNames = new List<string>();

        // --- Namespace ---
        var ns = new V1Namespace
        {
            ApiVersion = "v1",
            Kind = "Namespace",
            Metadata = new V1ObjectMeta
            {
                Name = workload.Namespace,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "deskribe"
                }
            }
        };
        resources.Add(ns);
        resourceNames.Add($"Namespace/{workload.Namespace}");

        // --- Secret / ExternalSecret (if env vars exist) ---
        if (workload.EnvironmentVariables.Count > 0)
        {
            switch (workload.SecretsStrategy)
            {
                case "external-secrets":
                    var externalSecretYaml = RenderExternalSecret(workload);
                    resources.Add(externalSecretYaml);
                    resourceNames.Add($"ExternalSecret/{workload.Namespace}/{workload.AppName}-env");
                    break;

                case "sealed-secrets":
                    var sealedSecret = new V1Secret
                    {
                        ApiVersion = "v1",
                        Kind = "Secret",
                        Metadata = new V1ObjectMeta
                        {
                            Name = $"{workload.AppName}-env",
                            NamespaceProperty = workload.Namespace,
                            Annotations = new Dictionary<string, string>
                            {
                                ["sealedsecrets.bitnami.com/managed"] = "true"
                            }
                        },
                        Type = "Opaque",
                        StringData = new Dictionary<string, string>(workload.EnvironmentVariables)
                    };
                    resources.Add(sealedSecret);
                    resourceNames.Add($"Secret/{workload.Namespace}/{workload.AppName}-env");
                    break;

                default: // "opaque"
                    var secret = new V1Secret
                    {
                        ApiVersion = "v1",
                        Kind = "Secret",
                        Metadata = new V1ObjectMeta
                        {
                            Name = $"{workload.AppName}-env",
                            NamespaceProperty = workload.Namespace
                        },
                        Type = "Opaque",
                        StringData = new Dictionary<string, string>(workload.EnvironmentVariables)
                    };
                    resources.Add(secret);
                    resourceNames.Add($"Secret/{workload.Namespace}/{workload.AppName}-env");
                    break;
            }
        }

        // --- Deployment ---
        var appLabels = new Dictionary<string, string>
        {
            ["app"] = workload.AppName
        };

        var container = new V1Container
        {
            Name = workload.AppName,
            Image = workload.Image ?? "nginx:latest",
            Ports = new List<V1ContainerPort>
            {
                new()
                {
                    ContainerPort = 8080,
                    Name = "http"
                }
            },
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new ResourceQuantity(workload.Cpu),
                    ["memory"] = new ResourceQuantity(workload.Memory)
                },
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new ResourceQuantity(workload.Cpu),
                    ["memory"] = new ResourceQuantity(workload.Memory)
                }
            }
        };

        if (workload.EnvironmentVariables.Count > 0)
        {
            container.EnvFrom = new List<V1EnvFromSource>
            {
                new()
                {
                    SecretRef = new V1SecretEnvSource
                    {
                        Name = $"{workload.AppName}-env"
                    }
                }
            };
        }

        var deployment = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta
            {
                Name = workload.AppName,
                NamespaceProperty = workload.Namespace,
                Labels = new Dictionary<string, string>
                {
                    ["app"] = workload.AppName,
                    ["app.kubernetes.io/managed-by"] = "deskribe"
                }
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = workload.Replicas,
                Selector = new V1LabelSelector
                {
                    MatchLabels = appLabels
                },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string>(appLabels)
                    },
                    Spec = new V1PodSpec
                    {
                        Containers = new List<V1Container> { container }
                    }
                }
            }
        };
        resources.Add(deployment);
        resourceNames.Add($"Deployment/{workload.Namespace}/{workload.AppName}");

        // --- Service ---
        var service = new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = new V1ObjectMeta
            {
                Name = workload.AppName,
                NamespaceProperty = workload.Namespace
            },
            Spec = new V1ServiceSpec
            {
                Selector = new Dictionary<string, string>(appLabels),
                Ports = new List<V1ServicePort>
                {
                    new()
                    {
                        Port = 80,
                        TargetPort = 8080,
                        Name = "http"
                    }
                }
            }
        };
        resources.Add(service);
        resourceNames.Add($"Service/{workload.Namespace}/{workload.AppName}");

        // Serialize all resources to YAML separated by ---
        var yamlDocuments = resources.Select(r =>
            r is string raw ? raw : KubernetesYaml.Serialize(r));
        var yaml = string.Join("---\n", yamlDocuments);

        return Task.FromResult(new WorkloadManifest
        {
            Namespace = workload.Namespace,
            Yaml = yaml,
            ResourceNames = resourceNames
        });
    }

    public async Task ApplyAsync(WorkloadManifest manifest, CancellationToken ct = default)
    {
        var config = _config ?? KubernetesClientConfiguration.BuildDefaultConfig();
        var client = new k8s.Kubernetes(config);

        var objects = KubernetesYaml.LoadAllFromString(manifest.Yaml);

        foreach (var obj in objects)
        {
            switch (obj)
            {
                case V1Namespace ns:
                    try
                    {
                        await client.ReadNamespaceAsync(ns.Metadata.Name, cancellationToken: ct);
                        await client.PatchNamespaceAsync(
                            new V1Patch(ns, V1Patch.PatchType.MergePatch),
                            ns.Metadata.Name,
                            cancellationToken: ct);
                        Console.WriteLine($"[Kubernetes] Updated Namespace/{ns.Metadata.Name}");
                    }
                    catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        await client.CreateNamespaceAsync(ns, cancellationToken: ct);
                        Console.WriteLine($"[Kubernetes] Created Namespace/{ns.Metadata.Name}");
                    }
                    break;

                case V1Secret secret:
                    try
                    {
                        await client.ReadNamespacedSecretAsync(secret.Metadata.Name, secret.Metadata.NamespaceProperty, cancellationToken: ct);
                        await client.ReplaceNamespacedSecretAsync(secret, secret.Metadata.Name, secret.Metadata.NamespaceProperty, cancellationToken: ct);
                        Console.WriteLine($"[Kubernetes] Updated Secret/{secret.Metadata.NamespaceProperty}/{secret.Metadata.Name}");
                    }
                    catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        await client.CreateNamespacedSecretAsync(secret, secret.Metadata.NamespaceProperty, cancellationToken: ct);
                        Console.WriteLine($"[Kubernetes] Created Secret/{secret.Metadata.NamespaceProperty}/{secret.Metadata.Name}");
                    }
                    break;

                case V1Deployment deployment:
                    try
                    {
                        await client.ReadNamespacedDeploymentAsync(deployment.Metadata.Name, deployment.Metadata.NamespaceProperty, cancellationToken: ct);
                        await client.ReplaceNamespacedDeploymentAsync(deployment, deployment.Metadata.Name, deployment.Metadata.NamespaceProperty, cancellationToken: ct);
                        Console.WriteLine($"[Kubernetes] Updated Deployment/{deployment.Metadata.NamespaceProperty}/{deployment.Metadata.Name}");
                    }
                    catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        await client.CreateNamespacedDeploymentAsync(deployment, deployment.Metadata.NamespaceProperty, cancellationToken: ct);
                        Console.WriteLine($"[Kubernetes] Created Deployment/{deployment.Metadata.NamespaceProperty}/{deployment.Metadata.Name}");
                    }
                    break;

                case V1Service service:
                    try
                    {
                        await client.ReadNamespacedServiceAsync(service.Metadata.Name, service.Metadata.NamespaceProperty, cancellationToken: ct);
                        await client.ReplaceNamespacedServiceAsync(service, service.Metadata.Name, service.Metadata.NamespaceProperty, cancellationToken: ct);
                        Console.WriteLine($"[Kubernetes] Updated Service/{service.Metadata.NamespaceProperty}/{service.Metadata.Name}");
                    }
                    catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        await client.CreateNamespacedServiceAsync(service, service.Metadata.NamespaceProperty, cancellationToken: ct);
                        Console.WriteLine($"[Kubernetes] Created Service/{service.Metadata.NamespaceProperty}/{service.Metadata.Name}");
                    }
                    break;

                default:
                    Console.WriteLine($"[Kubernetes] Skipping unknown resource type: {obj.GetType().Name}");
                    break;
            }
        }
    }

    public async Task DestroyAsync(string namespaceName, CancellationToken ct = default)
    {
        var config = _config ?? KubernetesClientConfiguration.BuildDefaultConfig();
        var client = new k8s.Kubernetes(config);

        try
        {
            await client.DeleteNamespaceAsync(namespaceName, cancellationToken: ct);
            Console.WriteLine($"[Kubernetes] Deleted Namespace/{namespaceName}");
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Console.WriteLine($"[Kubernetes] Namespace '{namespaceName}' not found, nothing to delete.");
        }
    }

    private static string RenderExternalSecret(WorkloadPlan workload)
    {
        var storeName = workload.ExternalSecretsStore ?? "default";
        var dataEntries = string.Join("\n", workload.EnvironmentVariables.Select(kv =>
            $"    - secretKey: {kv.Key}\n      remoteRef:\n        key: {workload.AppName}-{workload.Environment}-{kv.Key.ToLowerInvariant().Replace("__", "-")}"));

        return $"""
            apiVersion: external-secrets.io/v1beta1
            kind: ExternalSecret
            metadata:
              name: {workload.AppName}-env
              namespace: {workload.Namespace}
            spec:
              refreshInterval: 1h
              secretStoreRef:
                name: {storeName}
                kind: ClusterSecretStore
              target:
                name: {workload.AppName}-env
              data:
            {dataEntries}
            """;
    }
}
