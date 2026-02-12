# Deskribe Plugin Development Guide

This guide walks you through building plugins for Deskribe, the Intent-as-Code platform.
Every working example is based on the real SDK interfaces and follows the same patterns
used by the built-in Postgres, Redis, Kafka, Pulumi, and Kubernetes plugins.

---

## 1. Plugin Architecture Overview

Deskribe uses a **registrar pattern**: each plugin implements `IPlugin`, and the engine
calls `Register()` at startup, passing an `IPluginRegistrar` that the plugin uses to
hand over its providers and adapters.

```
  deskribe.json                  platform-config/
  (app manifest)                 (org policies)
       |                              |
       v                              v
 +------------------------------------------+
 |            DeskribeEngine                 |
 |  validate -> plan -> apply -> destroy     |
 +-----+----------+-----------+-------------+
       |          |           |
       v          v           v
 +-----------+ +---------+ +-----------+
 | Resource  | | Backend | | Runtime   |
 | Providers | | Adapters| | Adapters  |
 +-----------+ +---------+ +-----------+
 | postgres  | | pulumi  | | kubernetes|
 | redis     | | terraform| | aca      |
 | kafka     | |         | |           |
 | mongodb   | |         | |           |
 +-----------+ +---------+ +-----------+
       ^          ^           ^
       |          |           |
 +------------------------------------------+
 |     PluginHost (implements IPluginRegistrar)    |
 |  Stores providers/adapters in dictionaries      |
 |  Engine looks them up by string key             |
 +------------------------------------------+
```

**How it works at startup:**

```csharp
// In Program.cs — the engine asks each plugin to register itself
var pluginHost = serviceProvider.GetRequiredService<PluginHost>();
pluginHost.RegisterPlugin(new MongoDbPlugin());   // your new plugin
```

Inside `RegisterPlugin`, the host calls `plugin.Register(this)`, and your plugin
calls whichever `registrar.RegisterXxx()` methods apply. The host stores them in
dictionaries keyed by `ResourceType` or adapter `Name`, and the engine looks them
up at validate/plan/apply time.

---

## 2. Creating a Resource Provider (Step-by-Step)

We will build a **MongoDB resource provider** from scratch. When done, developers
can declare MongoDB in their `deskribe.json` and Deskribe will validate, plan, and
provision it.

### 2.1 Define the Resource Record

Resource records extend `DeskribeResource` and carry the fields a developer can set
in their manifest.

```csharp
// MongoDbResource.cs
namespace Deskribe.Sdk.Resources;

public sealed record MongoDbResource : DeskribeResource
{
    public string? Version { get; init; }
    public bool? ReplicaSet { get; init; }
    public int? StorageGb { get; init; }
}
```

The `Type` and `Size` properties come from the base class. `Version`, `ReplicaSet`,
and `StorageGb` are MongoDB-specific options.

### 2.2 Create the Resource Provider

The provider implements `IResourceProvider`. It has two jobs: **validate** the
developer's config and **plan** what infrastructure to create.

```csharp
// MongoDbResourceProvider.cs
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;

namespace Deskribe.Plugins.Resources.MongoDB;

public class MongoDbResourceProvider : IResourceProvider
{
    public string ResourceType => "mongodb";

    private static readonly HashSet<string> ValidSizes = ["xs", "s", "m", "l", "xl"];
    private static readonly HashSet<string> ValidVersions = ["6.0", "7.0", "8.0"];

    public Task<ValidationResult> ValidateAsync(
        DeskribeResource resource, ValidationContext ctx, CancellationToken ct)
    {
        if (resource is not MongoDbResource mongo)
            return Task.FromResult(
                ValidationResult.Invalid($"Expected MongoDbResource but got {resource.GetType().Name}"));

        var errors = new List<string>();

        if (mongo.Size is not null && !ValidSizes.Contains(mongo.Size))
            errors.Add($"Invalid MongoDB size '{mongo.Size}'. Valid sizes: {string.Join(", ", ValidSizes)}");

        if (mongo.Version is not null && !ValidVersions.Contains(mongo.Version))
            errors.Add($"Invalid MongoDB version '{mongo.Version}'. Valid versions: {string.Join(", ", ValidVersions)}");

        if (mongo.StorageGb is < 1 or > 16384)
            errors.Add("storageGb must be between 1 and 16384");

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(
        DeskribeResource resource, PlanContext ctx, CancellationToken ct)
    {
        var mongo = resource as MongoDbResource;
        var size = mongo?.Size ?? "s";
        var version = mongo?.Version ?? "7.0";
        var replicaSet = mongo?.ReplicaSet ?? ctx.EnvironmentConfig.Defaults.Ha ?? false;
        var storageGb = mongo?.StorageGb ?? 10;

        var releaseName = $"{ctx.AppName}-mongodb";
        var ns = ctx.Platform.Defaults.NamespacePattern
            .Replace("{app}", ctx.AppName)
            .Replace("{env}", ctx.Environment);

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceType = "mongodb",
            Action = "create",
            PlannedOutputs = new Dictionary<string, string>
            {
                ["connectionString"] = $"mongodb://{releaseName}.{ns}.svc.cluster.local:27017/{ctx.AppName}",
                ["host"] = $"{releaseName}.{ns}.svc.cluster.local",
                ["port"] = "27017"
            },
            Configuration = new Dictionary<string, object?>
            {
                ["helmRelease"] = releaseName,
                ["helmChart"] = "oci://registry-1.docker.io/bitnamicharts/mongodb",
                ["version"] = version,
                ["size"] = size,
                ["replicaSet"] = replicaSet,
                ["storageGb"] = storageGb,
                ["namespace"] = ns
            }
        });
    }
}
```

**What ValidateAsync checks:**

- The resource is actually a `MongoDbResource` (guards against a misconfigured engine).
- `Size` is one of the allowed T-shirt sizes.
- `Version` is a supported MongoDB major version.
- `StorageGb` falls within an acceptable range.

**What PlanAsync returns:**

- `PlannedOutputs` -- connection details that other services will consume via
  `@resource(mongodb).connectionString` references.
- `Configuration` -- the Helm chart details, sizing, and namespace the backend
  adapter will use to actually create the resource.

### 2.3 Create the Plugin Entry Point

```csharp
// MongoDbPlugin.cs
using Deskribe.Sdk;

namespace Deskribe.Plugins.Resources.MongoDB;

public class MongoDbPlugin : IPlugin
{
    public string Name => "MongoDB Resource Provider";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterResourceProvider(new MongoDbResourceProvider());
    }
}
```

### 2.4 Manifest Usage (deskribe.json)

Developers declare MongoDB in their application manifest:

```json
{
  "name": "orders-api",
  "resources": [
    {
      "type": "mongodb",
      "size": "m",
      "replicaSet": true,
      "storageGb": 50,
      "version": "7.0"
    },
    { "type": "redis" }
  ],
  "services": [
    {
      "env": {
        "ConnectionStrings__Mongo": "@resource(mongodb).connectionString",
        "Redis__Endpoint": "@resource(redis).endpoint"
      }
    }
  ]
}
```

### 2.5 Wire Up the JSON Deserializer

The `ConfigLoader` and the Aspire `ManifestResourceJsonConverter` both need to know
how to deserialize `"type": "mongodb"` into a `MongoDbResource`. Add a case to the
polymorphic switch in `ConfigLoader.cs` (Core) and `DeskribeAspireExtensions.cs`
(Aspire):

```csharp
// In the type-switch that maps "type" strings to record types:
"mongodb" => JsonSerializer.Deserialize<MongoDbResource>(rawJson, innerOptions),
```

### 2.6 Register in Program.cs

```csharp
using Deskribe.Plugins.Resources.MongoDB;

// ... after building the service provider:
pluginHost.RegisterPlugin(new MongoDbPlugin());
```

---

## 3. Creating a Backend Adapter (Step-by-Step)

Backend adapters turn resource plans into real infrastructure. The built-in Pulumi
adapter uses the Pulumi Automation API. Here is a **Terraform backend adapter** that
shells out to the `terraform` CLI instead.

### 3.1 The Adapter

```csharp
// TerraformBackendAdapter.cs
using System.Diagnostics;
using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Backend.Terraform;

public class TerraformBackendAdapter : IBackendAdapter
{
    public string Name => "terraform";

    public async Task<BackendApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct)
    {
        var outputs = new Dictionary<string, Dictionary<string, string>>();
        var errors = new List<string>();

        // Generate a Terraform working directory per app+env
        var workDir = Path.Combine(
            Path.GetTempPath(), "deskribe-tf", $"{plan.AppName}-{plan.Environment}");
        Directory.CreateDirectory(workDir);

        // Write main.tf.json from resource plans
        var tfConfig = BuildTerraformConfig(plan);
        var configPath = Path.Combine(workDir, "main.tf.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(tfConfig), ct);

        // terraform init
        var initResult = await RunTerraformAsync(workDir, "init -input=false", ct);
        if (initResult.ExitCode != 0)
        {
            errors.Add($"terraform init failed: {initResult.StdErr}");
            return new BackendApplyResult { Success = false, Errors = errors };
        }

        // terraform apply -auto-approve
        var applyResult = await RunTerraformAsync(workDir, "apply -auto-approve -input=false", ct);
        if (applyResult.ExitCode != 0)
        {
            errors.Add($"terraform apply failed: {applyResult.StdErr}");
            return new BackendApplyResult { Success = false, Errors = errors };
        }

        // Collect outputs from the plan (real implementation would parse terraform output -json)
        foreach (var resourcePlan in plan.ResourcePlans)
        {
            outputs[resourcePlan.ResourceType] = resourcePlan.PlannedOutputs;
        }

        return new BackendApplyResult { Success = true, ResourceOutputs = outputs };
    }

    public async Task DestroyAsync(string appName, string environment, PlatformConfig platform, CancellationToken ct)
    {
        var workDir = Path.Combine(
            Path.GetTempPath(), "deskribe-tf", $"{appName}-{environment}");

        if (!Directory.Exists(workDir))
        {
            Console.WriteLine($"[Terraform] No state directory for {appName}-{environment}, nothing to destroy.");
            return;
        }

        await RunTerraformAsync(workDir, "destroy -auto-approve -input=false", ct);
        Console.WriteLine($"[Terraform] Destroyed {appName}-{environment}");
    }

    private static Dictionary<string, object> BuildTerraformConfig(DeskribePlan plan)
    {
        // Convert resource plans into Terraform resource blocks.
        // A real implementation would map each ResourceType to the
        // appropriate Terraform provider resource (e.g., helm_release,
        // azurerm_cosmosdb_mongo_database, aws_docdb_cluster, etc.)
        var resources = new Dictionary<string, object>();

        foreach (var rp in plan.ResourcePlans)
        {
            var resourceKey = $"helm_release_{rp.ResourceType.Replace(".", "_")}";
            resources[resourceKey] = new
            {
                name = rp.Configuration.GetValueOrDefault("helmRelease"),
                chart = rp.Configuration.GetValueOrDefault("helmChart"),
                @namespace = rp.Configuration.GetValueOrDefault("namespace")
            };
        }

        return new Dictionary<string, object>
        {
            ["resource"] = new Dictionary<string, object>
            {
                ["helm_release"] = resources
            }
        };
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunTerraformAsync(
        string workDir, string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "terraform",
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdout, stderr);
    }
}
```

### 3.2 The Plugin Entry Point

```csharp
// TerraformPlugin.cs
using Deskribe.Sdk;

namespace Deskribe.Plugins.Backend.Terraform;

public class TerraformPlugin : IPlugin
{
    public string Name => "Terraform Backend Adapter";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterBackendAdapter(new TerraformBackendAdapter());
    }
}
```

### 3.3 Terraform vs. Pulumi -- Key Differences

| Concern            | Pulumi Adapter (built-in)           | Terraform Adapter (this example)        |
|--------------------|-------------------------------------|-----------------------------------------|
| Provisioning model | Pulumi Automation API (in-process)  | `terraform` CLI (out-of-process)        |
| State storage      | Pulumi Cloud / self-managed backend | `.tfstate` file or remote backend       |
| Language           | C# inline program                   | JSON config generated from resource plan|
| Destroy            | `stack.DestroyAsync()`              | `terraform destroy -auto-approve`       |

### 3.4 How Resource Plans Map to IaC Resources

The engine calls each resource provider's `PlanAsync`, which returns a
`ResourcePlanResult`. The backend adapter receives the full `DeskribePlan`
(containing all `ResourcePlanResult` entries) and translates each one into the
corresponding IaC construct:

```
ResourcePlanResult                     IaC Resource
--------------------                   ----------------------------
ResourceType: "postgres"         -->   helm_release.myapp_postgres
Configuration["helmChart"]       -->   chart = "bitnami/postgresql"
Configuration["namespace"]       -->   namespace = "myapp-dev"
Configuration["version"]         -->   set { name = "image.tag" }
```

### 3.5 Selecting a Backend per Resource Type

The platform config maps resource types to backend adapters:

```json
{
  "backends": {
    "postgres": "terraform",
    "redis": "pulumi",
    "mongodb": "terraform"
  }
}
```

The engine reads this mapping in `ApplyAsync` to route each resource plan to the
correct backend adapter.

---

## 4. Creating a Runtime Adapter (Step-by-Step)

Runtime adapters deploy workloads to a compute platform. The built-in adapter
targets Kubernetes. Here is an **Azure Container Apps** runtime adapter.

### 4.1 The Adapter

```csharp
// AcaRuntimeAdapter.cs
using System.Text.Json;
using Deskribe.Sdk;

namespace Deskribe.Plugins.Runtime.Aca;

public class AcaRuntimeAdapter : IRuntimeAdapter
{
    public string Name => "aca";

    public Task<WorkloadManifest> RenderAsync(WorkloadPlan workload, CancellationToken ct = default)
    {
        // Build an ARM / Bicep-compatible JSON spec for a Container App
        var containerApp = new
        {
            type = "Microsoft.App/containerApps",
            apiVersion = "2024-03-01",
            name = workload.AppName,
            location = "[resourceGroup().location]",
            properties = new
            {
                managedEnvironmentId = "[parameters('envId')]",
                configuration = new
                {
                    ingress = new { external = true, targetPort = 8080 }
                },
                template = new
                {
                    containers = new[]
                    {
                        new
                        {
                            name = workload.AppName,
                            image = workload.Image ?? "nginx:latest",
                            resources = new
                            {
                                cpu = ParseCpuCores(workload.Cpu),
                                memory = workload.Memory
                            },
                            env = workload.EnvironmentVariables.Select(kv => new
                            {
                                name = kv.Key,
                                value = kv.Value
                            }).ToArray()
                        }
                    },
                    scale = new
                    {
                        minReplicas = workload.Replicas,
                        maxReplicas = workload.Replicas * 2
                    }
                }
            }
        };

        var yaml = JsonSerializer.Serialize(containerApp, new JsonSerializerOptions { WriteIndented = true });

        return Task.FromResult(new WorkloadManifest
        {
            Namespace = workload.Namespace,
            Yaml = yaml,
            ResourceNames = [$"ContainerApp/{workload.AppName}"]
        });
    }

    public async Task ApplyAsync(WorkloadManifest manifest, CancellationToken ct = default)
    {
        // In production: use Azure.ResourceManager SDK to deploy.
        // Here we show the intent.
        Console.WriteLine($"[ACA] Deploying Container App to namespace '{manifest.Namespace}'");
        Console.WriteLine($"[ACA] Resources: {string.Join(", ", manifest.ResourceNames)}");

        // Example using Azure SDK (pseudo-code):
        // var client = new ArmClient(new DefaultAzureCredential());
        // var rg = client.GetResourceGroupResource(rgId);
        // await rg.GetContainerApps().CreateOrUpdateAsync(WaitUntil.Completed, name, data, ct);

        await Task.CompletedTask;
    }

    public async Task DestroyAsync(string namespaceName, CancellationToken ct = default)
    {
        Console.WriteLine($"[ACA] Would delete Container Apps environment: {namespaceName}");
        await Task.CompletedTask;
    }

    private static double ParseCpuCores(string cpu)
    {
        // Convert Kubernetes-style "250m" to ACA-style 0.25
        if (cpu.EndsWith('m'))
            return double.Parse(cpu[..^1]) / 1000.0;
        return double.Parse(cpu);
    }
}
```

### 4.2 The Plugin Entry Point

```csharp
// AcaPlugin.cs
using Deskribe.Sdk;

namespace Deskribe.Plugins.Runtime.Aca;

public class AcaPlugin : IPlugin
{
    public string Name => "Azure Container Apps Runtime Adapter";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterRuntimeAdapter(new AcaRuntimeAdapter());
    }
}
```

### 4.3 How the Engine Uses Runtime Adapters

1. **RenderAsync** -- takes a `WorkloadPlan` (app name, replicas, CPU, memory,
   environment variables with resolved `@resource()` references) and produces a
   `WorkloadManifest` containing the deployment spec.

2. **ApplyAsync** -- takes the rendered manifest and pushes it to the target
   platform. The Kubernetes adapter uses the `KubernetesClient` C# SDK to create
   or update Namespace, Secret, Deployment, and Service objects. Your ACA adapter
   would use `Azure.ResourceManager` to deploy a Container App.

3. **DestroyAsync** -- tears down the namespace / resource group.

### 4.4 A Note on the Built-in Kubernetes Adapter

The Kubernetes runtime adapter (`KubernetesRuntimeAdapter`) uses the official
`KubernetesClient` NuGet package (version 18.0.13). It renders four resource types
(Namespace, Secret, Deployment, Service) and applies them using a create-or-update
pattern with `HttpOperationException` catch for 404-based upserts. A valid kubeconfig
is required — the adapter will throw if no cluster is available.

---

## 5. Creating a Messaging Provider (Step-by-Step)

Messaging providers handle validation and ACL planning for message-oriented
resources. The built-in `KafkaMessagingProvider` validates topic configuration
and generates ACL entries. Here is a **RabbitMQ messaging provider**.

### 5.1 Define the Resource Record

```csharp
// RabbitMqMessagingResource.cs
namespace Deskribe.Sdk.Resources;

public sealed record RabbitMqMessagingResource : DeskribeResource
{
    public List<RabbitMqQueue> Queues { get; init; } = [];
    public List<RabbitMqExchange> Exchanges { get; init; } = [];
}

public sealed record RabbitMqQueue
{
    public required string Name { get; init; }
    public bool Durable { get; init; } = true;
    public int? MessageTtlMs { get; init; }
    public string? DeadLetterExchange { get; init; }
    public List<string> Producers { get; init; } = [];
    public List<string> Consumers { get; init; } = [];
}

public sealed record RabbitMqExchange
{
    public required string Name { get; init; }
    public string ExchangeType { get; init; } = "topic";
    public List<string> BoundQueues { get; init; } = [];
}
```

### 5.2 The Resource Provider (validates structure, plans Helm release)

```csharp
// RabbitMqResourceProvider.cs
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;

namespace Deskribe.Plugins.Resources.RabbitMQ;

public class RabbitMqResourceProvider : IResourceProvider
{
    public string ResourceType => "rabbitmq.messaging";

    public Task<ValidationResult> ValidateAsync(
        DeskribeResource resource, ValidationContext ctx, CancellationToken ct)
    {
        if (resource is not RabbitMqMessagingResource rmq)
            return Task.FromResult(
                ValidationResult.Invalid($"Expected RabbitMqMessagingResource but got {resource.GetType().Name}"));

        var errors = new List<string>();

        if (rmq.Queues.Count == 0 && rmq.Exchanges.Count == 0)
            errors.Add("RabbitMQ resource must declare at least one queue or exchange");

        foreach (var queue in rmq.Queues)
        {
            if (string.IsNullOrWhiteSpace(queue.Name))
                errors.Add("Queue name is required");

            if (queue.MessageTtlMs is < 1)
                errors.Add($"Queue '{queue.Name}': messageTtlMs must be positive");

            if (queue.Producers.Count == 0)
                errors.Add($"Queue '{queue.Name}': must have at least one producer");
        }

        foreach (var exchange in rmq.Exchanges)
        {
            if (string.IsNullOrWhiteSpace(exchange.Name))
                errors.Add("Exchange name is required");
        }

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(
        DeskribeResource resource, PlanContext ctx, CancellationToken ct)
    {
        var rmq = (RabbitMqMessagingResource)resource;
        var releaseName = $"{ctx.AppName}-rabbitmq";
        var ns = ctx.Platform.Defaults.NamespacePattern
            .Replace("{app}", ctx.AppName)
            .Replace("{env}", ctx.Environment);

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceType = "rabbitmq.messaging",
            Action = "create",
            PlannedOutputs = new Dictionary<string, string>
            {
                ["connectionString"] = $"amqp://guest:guest@{releaseName}.{ns}.svc.cluster.local:5672",
                ["host"] = $"{releaseName}.{ns}.svc.cluster.local",
                ["port"] = "5672"
            },
            Configuration = new Dictionary<string, object?>
            {
                ["helmRelease"] = releaseName,
                ["helmChart"] = "oci://registry-1.docker.io/bitnamicharts/rabbitmq",
                ["namespace"] = ns,
                ["queues"] = rmq.Queues.Select(q => q.Name).ToList(),
                ["exchanges"] = rmq.Exchanges.Select(e => e.Name).ToList()
            }
        });
    }
}
```

### 5.3 The Messaging Provider (validates policies, generates ACLs)

The `IMessagingProvider` interface currently accepts `KafkaMessagingResource`. For a
RabbitMQ provider you would either generalize this interface or create a
RabbitMQ-specific messaging interface. Here is how the pattern works using the
existing registrar while keeping the same ACL-generation approach:

```csharp
// RabbitMqMessagingProvider.cs
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;

namespace Deskribe.Plugins.Resources.RabbitMQ;

/// <summary>
/// Validates RabbitMQ-specific messaging policies and generates ACL/permission plans.
/// In practice, you would extend IMessagingProvider or create a parallel interface
/// for non-Kafka messaging. This example shows the ACL-generation pattern.
/// </summary>
public class RabbitMqMessagingProvider
{
    public string ProviderType => "rabbitmq.messaging";

    public ValidationResult ValidateMessaging(RabbitMqMessagingResource resource)
    {
        var errors = new List<string>();

        foreach (var queue in resource.Queues)
        {
            // Platform policy: queues must have a dead-letter exchange in production
            if (queue.DeadLetterExchange is null)
                errors.Add($"Queue '{queue.Name}': production policy requires a dead-letter exchange");

            // Platform policy: durable must be true
            if (!queue.Durable)
                errors.Add($"Queue '{queue.Name}': platform requires durable queues");
        }

        return errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]);
    }

    public List<Dictionary<string, object>> GenerateAcls(RabbitMqMessagingResource resource)
    {
        var acls = new List<Dictionary<string, object>>();

        foreach (var queue in resource.Queues)
        {
            foreach (var producer in queue.Producers)
            {
                acls.Add(new Dictionary<string, object>
                {
                    ["principal"] = producer,
                    ["vhost"] = "/",
                    ["resource"] = queue.Name,
                    ["permission"] = "write"
                });
            }

            foreach (var consumer in queue.Consumers)
            {
                acls.Add(new Dictionary<string, object>
                {
                    ["principal"] = consumer,
                    ["vhost"] = "/",
                    ["resource"] = queue.Name,
                    ["permission"] = "read"
                });
            }
        }

        return acls;
    }
}
```

### 5.4 The Plugin Entry Point

```csharp
// RabbitMqPlugin.cs
using Deskribe.Sdk;

namespace Deskribe.Plugins.Resources.RabbitMQ;

public class RabbitMqPlugin : IPlugin
{
    public string Name => "RabbitMQ Messaging Provider";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterResourceProvider(new RabbitMqResourceProvider());
        // When IMessagingProvider is generalized for non-Kafka types,
        // also register: registrar.RegisterMessagingProvider(new RabbitMqMessagingProvider());
    }
}
```

### 5.5 Manifest Usage

```json
{
  "name": "notifications-api",
  "resources": [
    {
      "type": "rabbitmq.messaging",
      "queues": [
        {
          "name": "email.send",
          "durable": true,
          "messageTtlMs": 86400000,
          "deadLetterExchange": "dlx.email",
          "producers": ["team-notifications"],
          "consumers": ["team-email-gateway"]
        }
      ],
      "exchanges": [
        {
          "name": "dlx.email",
          "exchangeType": "fanout",
          "boundQueues": ["email.send.dlq"]
        }
      ]
    }
  ],
  "services": [
    {
      "env": {
        "RabbitMQ__ConnectionString": "@resource(rabbitmq.messaging).connectionString"
      }
    }
  ]
}
```

---

## 6. Adding Aspire Support for a New Resource

Aspire integration lets developers run their declared resources locally as
containers. The pattern lives in `DeskribeAspireExtensions.cs`. Adding MongoDB
support means handling `"mongodb"` in the resource type switch and calling the
appropriate Aspire hosting API.

### 6.1 The Pattern

```
deskribe.json   --->   AddDeskribeManifest()   --->   switch(resource.Type)
                           reads manifest                  |
                           iterates resources               |
                                                     "mongodb" case
                                                           |
                                                     builder.AddMongoDB(...)
                                                           |
                                                     map.AddConnectionStringResource(...)
                                                     map.AddWaitForResource(...)
                                                     map.RegisterResource(...)
```

### 6.2 Adding MongoDB to DeskribeAspireExtensions

Add a new case to the switch in `AddDeskribeManifest` and a corresponding private
method:

```csharp
// Inside DeskribeAspireExtensions.AddDeskribeManifest():
case "mongodb":
    AddMongoDbResource(builder, manifest.Name, resource as MongoDbResource, map);
    break;
```

```csharp
// New private method:
private static void AddMongoDbResource(
    IDistributedApplicationBuilder builder,
    string appName,
    MongoDbResource? mongoResource,
    DeskribeResourceMap map)
{
    var serverName = $"{appName}-mongodb";
    var dbName = $"{appName}-db";

    var server = builder.AddMongoDB(serverName);

    if (mongoResource?.Version is { } version)
        server = server.WithImageTag(version);

    server = server.WithMongoExpress();
    server = server.WithLifetime(ContainerLifetime.Persistent);

    var db = server.AddDatabase(dbName);

    map.AddConnectionStringResource(db);
    map.AddWaitForResource(db);
    map.RegisterResource("mongodb", serverName, dbName);
}
```

### 6.3 Update the JSON Converter

In the `ManifestResourceJsonConverter.Read` method, add:

```csharp
"mongodb" => JsonSerializer.Deserialize<MongoDbResource>(rawJson, innerOptions),
```

Also add the corresponding `using` alias at the top of the file:

```csharp
using SdkMongoDbResource = Deskribe.Sdk.Resources.MongoDbResource;
```

---

## 7. Project Structure for a New Plugin

### 7.1 Directory Layout

```
src/
  Plugins/
    Deskribe.Plugins.Resources.MongoDB/
      Deskribe.Plugins.Resources.MongoDB.csproj
      MongoDbPlugin.cs
      MongoDbResourceProvider.cs
```

If you are building a backend adapter or runtime adapter, the layout is identical
but with a different naming prefix:

```
Deskribe.Plugins.Backend.Terraform/
Deskribe.Plugins.Runtime.Aca/
```

### 7.2 The .csproj File

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\..\Deskribe.Sdk\Deskribe.Sdk.csproj" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

If your plugin needs external packages (like the Kubernetes adapter needs
`KubernetesClient`, or a Terraform adapter might need `HashiCorp.Cdktf`), add them
under an `<ItemGroup>`:

```xml
  <ItemGroup>
    <PackageReference Include="MongoDB.Driver" Version="3.1.0" />
  </ItemGroup>
```

### 7.3 Naming Convention

| Plugin type       | Project name pattern                          | Example                                    |
|-------------------|-----------------------------------------------|--------------------------------------------|
| Resource provider | `Deskribe.Plugins.Resources.{Name}`           | `Deskribe.Plugins.Resources.MongoDB`       |
| Backend adapter   | `Deskribe.Plugins.Backend.{Name}`             | `Deskribe.Plugins.Backend.Terraform`       |
| Runtime adapter   | `Deskribe.Plugins.Runtime.{Name}`             | `Deskribe.Plugins.Runtime.Aca`             |

The plugin class is named `{Name}Plugin`, the provider `{Name}ResourceProvider`,
and the adapter `{Name}BackendAdapter` or `{Name}RuntimeAdapter`.

---

## 8. Testing Plugins

All plugin tests live under `tests/Deskribe.Plugins.Tests/` and use **xUnit**.
Below is a complete test class for the MongoDB resource provider.

### 8.1 Test Class

```csharp
// MongoDbProviderTests.cs
using Deskribe.Plugins.Resources.MongoDB;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;

namespace Deskribe.Plugins.Tests;

public class MongoDbProviderTests
{
    private readonly MongoDbResourceProvider _provider = new();

    private static ValidationContext CreateValidationContext() => new()
    {
        Platform = new PlatformConfig
        {
            Defaults = new PlatformDefaults { NamespacePattern = "{app}-{env}" },
            Backends = new Dictionary<string, string> { ["mongodb"] = "pulumi" }
        },
        Environment = "dev"
    };

    private static PlanContext CreatePlanContext(string appName = "myapp") => new()
    {
        Platform = new PlatformConfig
        {
            Defaults = new PlatformDefaults { NamespacePattern = "{app}-{env}" },
            Backends = new Dictionary<string, string> { ["mongodb"] = "pulumi" }
        },
        EnvironmentConfig = new EnvironmentConfig { Name = "dev" },
        Environment = "dev",
        AppName = appName
    };

    // --- Validation Tests ---

    [Fact]
    public async Task Validate_PassesForValidResource()
    {
        var resource = new MongoDbResource
        {
            Type = "mongodb",
            Size = "m",
            Version = "7.0",
            ReplicaSet = true
        };

        var result = await _provider.ValidateAsync(resource, CreateValidationContext(), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Validate_PassesWithDefaultsOnly()
    {
        var resource = new MongoDbResource { Type = "mongodb" };

        var result = await _provider.ValidateAsync(resource, CreateValidationContext(), CancellationToken.None);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Validate_FailsForInvalidSize()
    {
        var resource = new MongoDbResource { Type = "mongodb", Size = "mega" };

        var result = await _provider.ValidateAsync(resource, CreateValidationContext(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("size", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_FailsForInvalidVersion()
    {
        var resource = new MongoDbResource { Type = "mongodb", Version = "4.4" };

        var result = await _provider.ValidateAsync(resource, CreateValidationContext(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("version", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(20000)]
    public async Task Validate_FailsForInvalidStorageGb(int storageGb)
    {
        var resource = new MongoDbResource { Type = "mongodb", StorageGb = storageGb };

        var result = await _provider.ValidateAsync(resource, CreateValidationContext(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("storageGb", result.Errors[0]);
    }

    // --- Planning Tests ---

    [Fact]
    public async Task Plan_ReturnsCorrectResourceType()
    {
        var resource = new MongoDbResource { Type = "mongodb", Size = "m" };

        var result = await _provider.PlanAsync(resource, CreatePlanContext(), CancellationToken.None);

        Assert.Equal("mongodb", result.ResourceType);
        Assert.Equal("create", result.Action);
    }

    [Fact]
    public async Task Plan_OutputsContainConnectionString()
    {
        var resource = new MongoDbResource { Type = "mongodb" };

        var result = await _provider.PlanAsync(resource, CreatePlanContext("orders"), CancellationToken.None);

        Assert.Contains("connectionString", result.PlannedOutputs.Keys);
        Assert.Contains("orders-mongodb", result.PlannedOutputs["connectionString"]);
        Assert.Contains("orders-dev", result.PlannedOutputs["connectionString"]);
    }

    [Fact]
    public async Task Plan_ConfigurationContainsHelmDetails()
    {
        var resource = new MongoDbResource { Type = "mongodb", Version = "8.0", ReplicaSet = true };

        var result = await _provider.PlanAsync(resource, CreatePlanContext(), CancellationToken.None);

        Assert.Equal("8.0", result.Configuration["version"]);
        Assert.Equal(true, result.Configuration["replicaSet"]);
        Assert.Contains("bitnami", result.Configuration["helmChart"]?.ToString());
    }

    [Fact]
    public async Task Plan_UsesNamespacePattern()
    {
        var ctx = CreatePlanContext("payments");

        var resource = new MongoDbResource { Type = "mongodb" };
        var result = await _provider.PlanAsync(resource, ctx, CancellationToken.None);

        Assert.Equal("payments-dev", result.Configuration["namespace"]);
    }
}
```

### 8.2 Add the Project Reference

In `tests/Deskribe.Plugins.Tests/Deskribe.Plugins.Tests.csproj`, add:

```xml
<ProjectReference Include="..\..\src\Plugins\Deskribe.Plugins.Resources.MongoDB\Deskribe.Plugins.Resources.MongoDB.csproj" />
```

### 8.3 Run the Tests

```bash
dotnet test tests/Deskribe.Plugins.Tests/ --filter "FullyQualifiedName~MongoDb"
```

---

## 9. Registering Your Plugin

### 9.1 CLI (Program.cs)

In `src/Deskribe.Cli/Program.cs`, add the `using` and the registration call:

```csharp
using Deskribe.Plugins.Resources.MongoDB;

// ... alongside the other RegisterPlugin calls:
pluginHost.RegisterPlugin(new MongoDbPlugin());
```

Also add a `<ProjectReference>` to `Deskribe.Cli.csproj`:

```xml
<ProjectReference Include="..\Plugins\Deskribe.Plugins.Resources.MongoDB\Deskribe.Plugins.Resources.MongoDB.csproj" />
```

### 9.2 Web Project (Program.cs)

In `src/Deskribe.Web/Program.cs`, the same pattern applies inside the
`PluginHost` factory lambda:

```csharp
using Deskribe.Plugins.Resources.MongoDB;

// Inside the PluginHost singleton registration:
builder.Services.AddSingleton<PluginHost>(sp =>
{
    var host = new PluginHost(sp.GetRequiredService<ILogger<PluginHost>>());
    host.RegisterPlugin(new PostgresPlugin());
    host.RegisterPlugin(new RedisPlugin());
    host.RegisterPlugin(new KafkaPlugin());
    host.RegisterPlugin(new MongoDbPlugin());   // <-- add this
    host.RegisterPlugin(new PulumiPlugin());
    host.RegisterPlugin(new KubernetesPlugin());
    return host;
});
```

Also add a `<ProjectReference>` to `Deskribe.Web.csproj`:

```xml
<ProjectReference Include="..\Plugins\Deskribe.Plugins.Resources.MongoDB\Deskribe.Plugins.Resources.MongoDB.csproj" />
```

### 9.3 Aspire Support

In `src/Deskribe.Aspire/DeskribeAspireExtensions.cs`:

1. Add the type alias at the top:
   ```csharp
   using SdkMongoDbResource = Deskribe.Sdk.Resources.MongoDbResource;
   ```

2. Add the case to the switch in `AddDeskribeManifest`:
   ```csharp
   case "mongodb":
       AddMongoDbResource(builder, manifest.Name, resource as SdkMongoDbResource, map);
       break;
   ```

3. Add the case to `ManifestResourceJsonConverter.Read`:
   ```csharp
   "mongodb" => JsonSerializer.Deserialize<SdkMongoDbResource>(rawJson, innerOptions),
   ```

4. Add the `AddMongoDbResource` private method (shown in Section 6.2).

### 9.4 Add to the Solution

```bash
dotnet sln Deskribe.slnx add src/Plugins/Resources/MongoDB/Deskribe.Plugins.Resources.MongoDB.csproj
```

---

## Quick Reference: Interface Summary

| Interface            | Key property     | Methods                                          | Used for                     |
|----------------------|------------------|--------------------------------------------------|------------------------------|
| `IPlugin`            | `Name`           | `Register(IPluginRegistrar)`                     | Entry point for all plugins  |
| `IResourceProvider`  | `ResourceType`   | `ValidateAsync`, `PlanAsync`                     | Validating and planning infra|
| `IBackendAdapter`    | `Name`           | `ApplyAsync`, `DestroyAsync`                     | Provisioning infra via IaC   |
| `IRuntimeAdapter`    | `Name`           | `RenderAsync`, `ApplyAsync`, `DestroyAsync`      | Deploying workloads          |
| `IMessagingProvider`  | `ProviderType`   | `ValidateAsync`, `PlanAsync`                     | Messaging-specific policies  |

---

## Checklist for a New Plugin

1. Create the resource record in `Deskribe.Sdk/Resources/` (if it is a new resource type).
2. Create the plugin project under `src/Plugins/`.
3. Implement `IResourceProvider`, `IBackendAdapter`, or `IRuntimeAdapter`.
4. Implement `IPlugin` to register your providers/adapters.
5. Add the JSON deserialization case to `ConfigLoader` and the Aspire converter.
6. Register the plugin in both `Deskribe.Cli/Program.cs` and `Deskribe.Web/Program.cs`.
7. Add Aspire support in `DeskribeAspireExtensions.cs`.
8. Write xUnit tests in `tests/Deskribe.Plugins.Tests/`.
9. Add the project to the solution with `dotnet sln add`.
