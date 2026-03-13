# Deskribe Plugin Development Guide

This guide walks you through building plugins for Deskribe, the Intent-as-Code platform.
Every working example is based on the real SDK interfaces and follows the same patterns
used by the built-in Postgres, Redis, Kafka, Pulumi, and Kubernetes plugins.

---

## 1. Plugin Architecture Overview

Deskribe uses **assembly scanning** and a `PluginRegistry` to discover plugins at
startup. Each plugin class is annotated with `[DeskribePlugin("name")]` and implements
`IPlugin`. The registry scans assemblies, finds all plugin classes, and registers their
providers, provisioners, and runtime plugins automatically.

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
 | Resource  | | Provi-  | | Runtime   |
 | Providers | | sioners | | Plugins   |
 +-----------+ +---------+ +-----------+
 | postgres  | | pulumi  | | kubernetes|
 | redis     | | terraform| | aca      |
 | kafka     | |         | |           |
 | mongodb   | |         | |           |
 +-----------+ +---------+ +-----------+
       ^          ^           ^
       |          |           |
 +------------------------------------------+
 |  PluginRegistry (assembly scanning)           |
 |  Discovers [DeskribePlugin] classes           |
 |  Stores providers/provisioners by key         |
 +------------------------------------------+
```

**How it works at startup:**

```csharp
// In Program.cs — DI registration with assembly scanning
services.AddDeskribe(typeof(MongoDbPlugin).Assembly);

// Or discover and register all plugins from multiple assemblies:
var registry = serviceProvider.GetRequiredService<PluginRegistry>();
registry.DiscoverAndRegisterAll(assemblies);
```

The registry scans assemblies for classes annotated with `[DeskribePlugin]`,
instantiates them, and calls `Register()`. Providers and provisioners are stored
in dictionaries keyed by `ResourceType` or provisioner `Name`, and the engine
looks them up at validate/plan/apply time.

---

## 2. Creating a Resource Provider (Step-by-Step)

We will build a **MongoDB resource provider** from scratch. When done, developers
can declare MongoDB in their `deskribe.json` and Deskribe will validate, plan, and
provision it.

### 2.1 Resource Descriptors

Resources are no longer represented by concrete record types. Instead, every resource
is a `ResourceDescriptor` with a `Properties` dictionary that holds the resource-specific
configuration. Developers declare properties in their `deskribe.json`, and providers
extract them at runtime using `resource.Properties.TryGetValue(...)`.

### 2.2 Create the Resource Provider

The provider implements `IResourceProvider`. It has three jobs: declare a **schema**
for its accepted properties, **validate** the developer's config, and **plan** what
infrastructure to create.

```csharp
// MongoDbResourceProvider.cs
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Resources.MongoDB;

[DeskribePlugin("mongodb")]
public class MongoDbResourceProvider : IResourceProvider
{
    public string ResourceType => "mongodb";

    private static readonly HashSet<string> ValidSizes = ["xs", "s", "m", "l", "xl"];
    private static readonly HashSet<string> ValidVersions = ["6.0", "7.0", "8.0"];

    public ResourceSchema GetSchema() => new()
    {
        Properties =
        {
            ["version"] = new SchemaProperty { Type = "string", Description = "MongoDB major version" },
            ["replicaSet"] = new SchemaProperty { Type = "boolean", Description = "Enable replica set" },
            ["storageGb"] = new SchemaProperty { Type = "integer", Description = "Storage in GB (1-16384)" }
        }
    };

    public Task<ValidationResult> ValidateAsync(
        ResourceDescriptor resource, ValidationContext ctx, CancellationToken ct)
    {
        var errors = new List<string>();

        if (resource.Size is not null && !ValidSizes.Contains(resource.Size))
            errors.Add($"Invalid MongoDB size '{resource.Size}'. Valid sizes: {string.Join(", ", ValidSizes)}");

        if (resource.Properties.TryGetValue("version", out var v) && v is string version
            && !ValidVersions.Contains(version))
            errors.Add($"Invalid MongoDB version '{version}'. Valid versions: {string.Join(", ", ValidVersions)}");

        if (resource.Properties.TryGetValue("storageGb", out var sg) && sg is int storageGb
            && storageGb is < 1 or > 16384)
            errors.Add("storageGb must be between 1 and 16384");

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(
        ResourceDescriptor resource, PlanContext ctx, CancellationToken ct)
    {
        var size = resource.Size ?? "s";

        resource.Properties.TryGetValue("version", out var vObj);
        var version = vObj as string ?? "7.0";

        resource.Properties.TryGetValue("replicaSet", out var rsObj);
        var replicaSet = rsObj is bool rs ? rs : ctx.EnvironmentConfig.Defaults.Ha ?? false;

        resource.Properties.TryGetValue("storageGb", out var sgObj);
        var storageGb = sgObj is int sg ? sg : 10;

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

**What GetSchema returns:**

- A `ResourceSchema` declaring which properties the provider accepts, their types,
  and descriptions. The engine uses this for manifest validation and editor tooling.

**What ValidateAsync checks:**

- `Size` is one of the allowed T-shirt sizes (from the base `ResourceDescriptor`).
- `version` (from `Properties`) is a supported MongoDB major version.
- `storageGb` (from `Properties`) falls within an acceptable range.

**What PlanAsync returns:**

- `PlannedOutputs` -- connection details that other services will consume via
  `@resource(mongodb).connectionString` references.
- `Configuration` -- the Helm chart details, sizing, and namespace the provisioner
  will use to actually create the resource.

### 2.3 Create the Plugin Entry Point

```csharp
// MongoDbPlugin.cs
using Deskribe.Sdk;

namespace Deskribe.Plugins.Resources.MongoDB;

[DeskribePlugin("mongodb")]
public class MongoDbPlugin : IPlugin
{
    public string Name => "MongoDB Resource Provider";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterResourceProvider(new MongoDbResourceProvider());
    }
}
```

> **Note:** The `[DeskribePlugin("mongodb")]` attribute enables automatic discovery
> via assembly scanning. The registry finds this class when you call
> `registry.DiscoverAndRegisterAll(assemblies)`.

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

### 2.5 Register via DI

No custom JSON deserialization is needed. All resources are deserialized as
`ResourceDescriptor` with their extra fields stored in the `Properties` dictionary.
Register your plugin assembly with DI:

```csharp
// In Program.cs or Startup.cs:
services.AddDeskribe(typeof(MongoDbPlugin).Assembly);
```

---

## 3. Creating a Provisioner (Step-by-Step)

Provisioners turn resource plans into real infrastructure. The built-in Pulumi
provisioner uses the Pulumi Automation API. Here is a **Terraform provisioner** that
shells out to the `terraform` CLI instead.

### 3.1 The Provisioner

```csharp
// TerraformProvisioner.cs
using System.Diagnostics;
using System.Text.Json;
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

namespace Deskribe.Plugins.Provisioners.Terraform;

public class TerraformProvisioner : IProvisioner
{
    public string Name => "terraform";

    public async Task<ProvisionResult> ApplyAsync(DeskribePlan plan, CancellationToken ct)
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
            return new ProvisionResult { Success = false, Errors = errors };
        }

        // terraform apply -auto-approve
        var applyResult = await RunTerraformAsync(workDir, "apply -auto-approve -input=false", ct);
        if (applyResult.ExitCode != 0)
        {
            errors.Add($"terraform apply failed: {applyResult.StdErr}");
            return new ProvisionResult { Success = false, Errors = errors };
        }

        // Collect outputs from the plan (real implementation would parse terraform output -json)
        foreach (var resourcePlan in plan.ResourcePlans)
        {
            outputs[resourcePlan.ResourceType] = resourcePlan.PlannedOutputs;
        }

        return new ProvisionResult { Success = true, ResourceOutputs = outputs };
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

    public async Task<IReadOnlyList<ProvisionArtifact>> GenerateArtifactsAsync(
        DeskribePlan plan, CancellationToken ct)
    {
        // Generate Terraform config files as artifacts without applying them.
        // Useful for GitOps workflows where artifacts are committed to a repo.
        var artifacts = new List<ProvisionArtifact>();
        var tfConfig = BuildTerraformConfig(plan);

        artifacts.Add(new ProvisionArtifact
        {
            FileName = "main.tf.json",
            Content = JsonSerializer.Serialize(tfConfig, new JsonSerializerOptions { WriteIndented = true }),
            ArtifactType = "terraform-config"
        });

        return artifacts;
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

namespace Deskribe.Plugins.Provisioners.Terraform;

[DeskribePlugin("terraform")]
public class TerraformPlugin : IPlugin
{
    public string Name => "Terraform Provisioner";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterProvisioner(new TerraformProvisioner());
    }
}
```

### 3.3 Terraform vs. Pulumi -- Key Differences

| Concern            | Pulumi Provisioner (built-in)       | Terraform Provisioner (this example)    |
|--------------------|-------------------------------------|-----------------------------------------|
| Provisioning model | Pulumi Automation API (in-process)  | `terraform` CLI (out-of-process)        |
| State storage      | Pulumi Cloud / self-managed backend | `.tfstate` file or remote backend       |
| Language           | C# inline program                   | JSON config generated from resource plan|
| Destroy            | `stack.DestroyAsync()`              | `terraform destroy -auto-approve`       |

### 3.4 How Resource Plans Map to IaC Resources

The engine calls each resource provider's `PlanAsync`, which returns a
`ResourcePlanResult`. The provisioner receives the full `DeskribePlan`
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

### 3.5 Selecting a Provisioner per Resource Type

The platform config maps resource types to provisioners:

```json
{
  "provisioners": {
    "postgres": "terraform",
    "redis": "pulumi",
    "mongodb": "terraform"
  }
}
```

The engine reads this mapping in `ApplyAsync` to route each resource plan to the
correct provisioner.

---

## 4. Creating a Runtime Plugin (Step-by-Step)

Runtime plugins deploy workloads to a compute platform. The built-in plugin
targets Kubernetes. Here is an **Azure Container Apps** runtime plugin.

### 4.1 The Runtime Plugin

```csharp
// AcaRuntimePlugin.cs
using System.Text.Json;
using Deskribe.Sdk;

namespace Deskribe.Plugins.Runtime.Aca;

public class AcaRuntimePlugin : IRuntimePlugin
{
    public string Name => "aca";

    public Task<RuntimeArtifact> RenderAsync(WorkloadPlan workload, CancellationToken ct = default)
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

        return Task.FromResult(new RuntimeArtifact
        {
            Namespace = workload.Namespace,
            Yaml = yaml,
            ResourceNames = [$"ContainerApp/{workload.AppName}"]
        });
    }

    public async Task ApplyAsync(RuntimeArtifact manifest, CancellationToken ct = default)
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

[DeskribePlugin("aca")]
public class AcaPlugin : IPlugin
{
    public string Name => "Azure Container Apps Runtime Plugin";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterRuntimePlugin(new AcaRuntimePlugin());
    }
}
```

### 4.3 How the Engine Uses Runtime Plugins

1. **RenderAsync** -- takes a `WorkloadPlan` (app name, replicas, CPU, memory,
   environment variables with resolved `@resource()` references) and produces a
   `RuntimeArtifact` containing the deployment spec.

2. **ApplyAsync** -- takes the rendered manifest and pushes it to the target
   platform. The Kubernetes plugin uses the `KubernetesClient` C# SDK to create
   or update Namespace, Secret, Deployment, and Service objects. Your ACA plugin
   would use `Azure.ResourceManager` to deploy a Container App.

3. **DestroyAsync** -- tears down the namespace / resource group.

### 4.4 A Note on the Built-in Kubernetes Plugin

The Kubernetes runtime plugin (`KubernetesRuntimePlugin`) uses the official
`KubernetesClient` NuGet package (version 18.0.13). It renders four resource types
(Namespace, Secret, Deployment, Service) and applies them using a create-or-update
pattern with `HttpOperationException` catch for 404-based upserts. A valid kubeconfig
is required -- the plugin will throw if no cluster is available.

---

## 5. Creating a Messaging Resource Provider (Step-by-Step)

There is no longer a separate `IMessagingProvider` interface. Messaging resources
(Kafka, RabbitMQ, etc.) are handled by standard `IResourceProvider` implementations.
The built-in Kafka plugin registers an `IResourceProvider` that validates topic
configuration and generates ACL entries. Here is a **RabbitMQ resource provider**
that follows the same pattern.

### 5.1 The Resource Provider

Messaging-specific properties (queues, exchanges, ACLs) are stored in the
`ResourceDescriptor.Properties` dictionary, just like any other resource.

```csharp
// RabbitMqResourceProvider.cs
using Deskribe.Sdk;
using Deskribe.Sdk.Models;
using System.Text.Json;

namespace Deskribe.Plugins.Resources.RabbitMQ;

[DeskribePlugin("rabbitmq")]
public class RabbitMqResourceProvider : IResourceProvider
{
    public string ResourceType => "rabbitmq.messaging";

    public ResourceSchema GetSchema() => new()
    {
        Properties =
        {
            ["queues"] = new SchemaProperty { Type = "array", Description = "Queue definitions" },
            ["exchanges"] = new SchemaProperty { Type = "array", Description = "Exchange definitions" }
        }
    };

    public Task<ValidationResult> ValidateAsync(
        ResourceDescriptor resource, ValidationContext ctx, CancellationToken ct)
    {
        var errors = new List<string>();

        var queues = GetListProperty(resource, "queues");
        var exchanges = GetListProperty(resource, "exchanges");

        if (queues.Count == 0 && exchanges.Count == 0)
            errors.Add("RabbitMQ resource must declare at least one queue or exchange");

        foreach (var queue in queues)
        {
            if (!queue.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name?.ToString()))
                errors.Add("Queue name is required");

            if (queue.TryGetValue("messageTtlMs", out var ttl) && ttl is int ttlVal && ttlVal < 1)
                errors.Add($"Queue '{name}': messageTtlMs must be positive");

            if (!queue.TryGetValue("producers", out var producers)
                || producers is not JsonElement pArr || pArr.GetArrayLength() == 0)
                errors.Add($"Queue '{name}': must have at least one producer");
        }

        foreach (var exchange in exchanges)
        {
            if (!exchange.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name?.ToString()))
                errors.Add("Exchange name is required");
        }

        return Task.FromResult(errors.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid([.. errors]));
    }

    public Task<ResourcePlanResult> PlanAsync(
        ResourceDescriptor resource, PlanContext ctx, CancellationToken ct)
    {
        var releaseName = $"{ctx.AppName}-rabbitmq";
        var ns = ctx.Platform.Defaults.NamespacePattern
            .Replace("{app}", ctx.AppName)
            .Replace("{env}", ctx.Environment);

        var queues = GetListProperty(resource, "queues");
        var exchanges = GetListProperty(resource, "exchanges");

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
                ["queues"] = queues.Select(q => q["name"]).ToList(),
                ["exchanges"] = exchanges.Select(e => e["name"]).ToList()
            }
        });
    }

    private static List<Dictionary<string, object?>> GetListProperty(
        ResourceDescriptor resource, string key)
    {
        if (!resource.Properties.TryGetValue(key, out var val) || val is not JsonElement arr)
            return [];

        return arr.EnumerateArray()
            .Select(el => el.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value))
            .ToList();
    }
}
```

> **Note:** The old `IMessagingProvider` interface has been removed. Kafka, RabbitMQ,
> and other messaging systems now register as standard `IResourceProvider` implementations.
> ACL generation and messaging-specific validation belong in `ValidateAsync` and
> `PlanAsync`, just like any other resource.

### 5.2 The Plugin Entry Point

```csharp
// RabbitMqPlugin.cs
using Deskribe.Sdk;

namespace Deskribe.Plugins.Resources.RabbitMQ;

[DeskribePlugin("rabbitmq")]
public class RabbitMqPlugin : IPlugin
{
    public string Name => "RabbitMQ Resource Provider";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterResourceProvider(new RabbitMqResourceProvider());
    }
}
```

### 5.3 Manifest Usage

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
    AddMongoDbResource(builder, manifest.Name, resource, map);
    break;
```

```csharp
// New private method:
private static void AddMongoDbResource(
    IDistributedApplicationBuilder builder,
    string appName,
    ResourceDescriptor resource,
    ResourceDescriptorMap map)
{
    var serverName = $"{appName}-mongodb";
    var dbName = $"{appName}-db";

    var server = builder.AddMongoDB(serverName);

    if (resource.Properties.TryGetValue("version", out var v) && v is string version)
        server = server.WithImageTag(version);

    server = server.WithMongoExpress();
    server = server.WithLifetime(ContainerLifetime.Persistent);

    var db = server.AddDatabase(dbName);

    map.AddConnectionStringResource(db);
    map.AddWaitForResource(db);
    map.RegisterResource("mongodb", serverName, dbName);
}
```

### 6.3 JSON Deserialization

No custom JSON converter changes are needed. All resources are deserialized as
`ResourceDescriptor` objects with extra fields stored in the `Properties` dictionary.
The Aspire extension reads `resource.Properties` to extract MongoDB-specific config.

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

If you are building a provisioner or runtime plugin, the layout is identical
but with a different naming prefix:

```
Deskribe.Plugins.Provisioners.Terraform/
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

If your plugin needs external packages (like the Kubernetes plugin needs
`KubernetesClient`, or a Terraform provisioner might need `HashiCorp.Cdktf`), add them
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
| Provisioner       | `Deskribe.Plugins.Provisioners.{Name}`        | `Deskribe.Plugins.Provisioners.Terraform`  |
| Runtime plugin    | `Deskribe.Plugins.Runtime.{Name}`             | `Deskribe.Plugins.Runtime.Aca`             |

The plugin class is named `{Name}Plugin`, the provider `{Name}ResourceProvider`,
the provisioner `{Name}Provisioner`, or the runtime plugin `{Name}RuntimePlugin`.

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

namespace Deskribe.Plugins.Tests;

public class MongoDbProviderTests
{
    private readonly MongoDbResourceProvider _provider = new();

    private static ResourceDescriptor CreateResource(
        string? size = null,
        Dictionary<string, object>? properties = null) => new()
    {
        Type = "mongodb",
        Size = size,
        Properties = properties ?? new Dictionary<string, object>()
    };

    private static ValidationContext CreateValidationContext() => new()
    {
        Platform = new PlatformConfig
        {
            Defaults = new PlatformDefaults { NamespacePattern = "{app}-{env}" },
            Provisioners = new Dictionary<string, string> { ["mongodb"] = "pulumi" }
        },
        Environment = "dev"
    };

    private static PlanContext CreatePlanContext(string appName = "myapp") => new()
    {
        Platform = new PlatformConfig
        {
            Defaults = new PlatformDefaults { NamespacePattern = "{app}-{env}" },
            Provisioners = new Dictionary<string, string> { ["mongodb"] = "pulumi" }
        },
        EnvironmentConfig = new EnvironmentConfig { Name = "dev" },
        Environment = "dev",
        AppName = appName
    };

    // --- Validation Tests ---

    [Fact]
    public async Task Validate_PassesForValidResource()
    {
        var resource = CreateResource(size: "m", properties: new()
        {
            ["version"] = "7.0",
            ["replicaSet"] = true
        });

        var result = await _provider.ValidateAsync(resource, CreateValidationContext(), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Validate_PassesWithDefaultsOnly()
    {
        var resource = CreateResource();

        var result = await _provider.ValidateAsync(resource, CreateValidationContext(), CancellationToken.None);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Validate_FailsForInvalidSize()
    {
        var resource = CreateResource(size: "mega");

        var result = await _provider.ValidateAsync(resource, CreateValidationContext(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("size", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_FailsForInvalidVersion()
    {
        var resource = CreateResource(properties: new() { ["version"] = "4.4" });

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
        var resource = CreateResource(properties: new() { ["storageGb"] = storageGb });

        var result = await _provider.ValidateAsync(resource, CreateValidationContext(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("storageGb", result.Errors[0]);
    }

    // --- Schema Tests ---

    [Fact]
    public void GetSchema_ReturnsExpectedProperties()
    {
        var schema = _provider.GetSchema();

        Assert.Contains("version", schema.Properties.Keys);
        Assert.Contains("replicaSet", schema.Properties.Keys);
        Assert.Contains("storageGb", schema.Properties.Keys);
    }

    // --- Planning Tests ---

    [Fact]
    public async Task Plan_ReturnsCorrectResourceType()
    {
        var resource = CreateResource(size: "m");

        var result = await _provider.PlanAsync(resource, CreatePlanContext(), CancellationToken.None);

        Assert.Equal("mongodb", result.ResourceType);
        Assert.Equal("create", result.Action);
    }

    [Fact]
    public async Task Plan_OutputsContainConnectionString()
    {
        var resource = CreateResource();

        var result = await _provider.PlanAsync(resource, CreatePlanContext("orders"), CancellationToken.None);

        Assert.Contains("connectionString", result.PlannedOutputs.Keys);
        Assert.Contains("orders-mongodb", result.PlannedOutputs["connectionString"]);
        Assert.Contains("orders-dev", result.PlannedOutputs["connectionString"]);
    }

    [Fact]
    public async Task Plan_ConfigurationContainsHelmDetails()
    {
        var resource = CreateResource(properties: new()
        {
            ["version"] = "8.0",
            ["replicaSet"] = true
        });

        var result = await _provider.PlanAsync(resource, CreatePlanContext(), CancellationToken.None);

        Assert.Equal("8.0", result.Configuration["version"]);
        Assert.Equal(true, result.Configuration["replicaSet"]);
        Assert.Contains("bitnami", result.Configuration["helmChart"]?.ToString());
    }

    [Fact]
    public async Task Plan_UsesNamespacePattern()
    {
        var ctx = CreatePlanContext("payments");

        var resource = CreateResource();
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

### 9.1 DI Registration (Recommended)

The preferred way to register plugins is through DI with assembly scanning. In your
`Program.cs` (CLI or Web), call `AddDeskribe` with the assemblies containing your plugins:

```csharp
// Register all plugins from one or more assemblies:
services.AddDeskribe(
    typeof(MongoDbPlugin).Assembly,
    typeof(TerraformPlugin).Assembly,
    typeof(AcaPlugin).Assembly
);
```

This scans each assembly for classes annotated with `[DeskribePlugin]`, registers
them with the DI container, and wires up the `PluginRegistry` automatically.

Also add a `<ProjectReference>` to the consuming project's `.csproj`:

```xml
<ProjectReference Include="..\Plugins\Deskribe.Plugins.Resources.MongoDB\Deskribe.Plugins.Resources.MongoDB.csproj" />
```

### 9.2 Manual Registration (Alternative)

If you need fine-grained control, you can register plugins manually:

```csharp
var registry = serviceProvider.GetRequiredService<PluginRegistry>();
registry.DiscoverAndRegisterAll([
    typeof(MongoDbPlugin).Assembly,
    typeof(TerraformPlugin).Assembly
]);
```

### 9.3 Aspire Support

In `src/Deskribe.Aspire/DeskribeAspireExtensions.cs`:

1. Add the case to the switch in `AddDeskribeManifest`:
   ```csharp
   case "mongodb":
       AddMongoDbResource(builder, manifest.Name, resource, map);
       break;
   ```

2. Add the `AddMongoDbResource` private method (shown in Section 6.2).

### 9.4 Add to the Solution

```bash
dotnet sln Deskribe.slnx add src/Plugins/Resources/MongoDB/Deskribe.Plugins.Resources.MongoDB.csproj
```

---

## Quick Reference: Interface Summary

| Interface            | Key property     | Methods                                                          | Used for                     |
|----------------------|------------------|------------------------------------------------------------------|------------------------------|
| `IPlugin`            | `Name`           | `Register(IPluginRegistrar)`                                     | Entry point for all plugins  |
| `IResourceProvider`  | `ResourceType`   | `ValidateAsync`, `PlanAsync`, `GetSchema`                        | Validating and planning infra|
| `IProvisioner`       | `Name`           | `ApplyAsync`, `DestroyAsync`, `GenerateArtifactsAsync`           | Provisioning infra via IaC   |
| `IRuntimePlugin`     | `Name`           | `RenderAsync`, `ApplyAsync`, `DestroyAsync`                      | Deploying workloads          |

---

## Checklist for a New Plugin

1. Create the plugin project under `src/Plugins/`.
2. Implement `IResourceProvider`, `IProvisioner`, or `IRuntimePlugin`.
3. Add the `[DeskribePlugin("name")]` attribute to your plugin class.
4. Implement `GetSchema()` on resource providers to declare accepted properties.
5. Register via DI: `services.AddDeskribe(typeof(MyPlugin).Assembly)` (assembly scanning).
6. Add Aspire support in `DeskribeAspireExtensions.cs`.
7. Write xUnit tests in `tests/Deskribe.Plugins.Tests/`.
8. Add the project to the solution with `dotnet sln add`.
