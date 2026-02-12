# Deskribe System Design

**Intent-as-Code: From manifest to running infrastructure in one command.**

This document explains how Deskribe works end-to-end. Every section answers
"when I do _this_, _that_ happens." If you read this top to bottom, you will
understand the entire data flow from a developer writing JSON to containers
running in Kubernetes.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [The Manifest (deskribe.json)](#2-the-manifest-deskribejson)
3. [Config Merge Pipeline](#3-config-merge-pipeline)
4. [Plugin Architecture](#4-plugin-architecture)
5. [Resource Resolution](#5-resource-resolution)
6. [Engine Pipeline](#6-engine-pipeline)
7. [Building Blocks](#7-building-blocks)
8. [Data Flow Example](#8-data-flow-example)

---

## 1. Architecture Overview

### The Big Picture

```
  DEVELOPER'S REPO                    PLATFORM TEAM'S REPO
  ================                    ====================

  payments-api/                       platform-config/
  +--------------------+              +---------------------+
  | deskribe.json      |              | base.json           |
  |                    |              |   org defaults      |
  | "I need postgres,  |              |   backend mappings  |
  |  redis, kafka.     |              |   policies          |
  |  Here are my env   |              |                     |
  |  var refs."        |              | envs/               |
  +--------+-----------+              |   dev.json          |
           |                          |   prod.json         |
           |                          +--------+------------+
           |                                   |
           +------------+    +-----------------+
                        |    |
                        v    v
              +---------------------+
              |                     |
              |   DESKRIBE ENGINE   |
              |   (Deskribe.Core)   |
              |                     |
              |  +---------------+  |
              |  | ConfigLoader  |  | -- Reads & deserializes JSON
              |  +-------+-------+  |
              |          |          |
              |  +-------v-------+  |
              |  | MergeEngine   |  | -- 3-layer merge (platform < env < dev)
              |  +-------+-------+  |
              |          |          |
              |  +-------v-------+  |
              |  | PolicyValid.  |  | -- Enforces platform governance
              |  +-------+-------+  |
              |          |          |
              |  +-------v-------+  |
              |  | RefResolver   |  | -- Parses @resource() syntax
              |  +-------+-------+  |
              |          |          |
              |  +-------v-------+  |
              |  | PluginHost    |  | -- Routes to registered providers
              |  +-------+-------+  |
              |          |          |
              +----------+----------+
                         |
              +----------+----------+
              |                     |
    +---------v--------+  +---------v---------+
    |  INFRASTRUCTURE  |  |  RUNTIME          |
    |                  |  |                   |
    |  Backend Adapter |  |  Runtime Adapter  |
    |  (Pulumi)        |  |  (Kubernetes)     |
    |                  |  |                   |
    |  Provisions:     |  |  Deploys:         |
    |  - Postgres      |  |  - Namespace      |
    |  - Redis         |  |  - Secret         |
    |  - Kafka         |  |  - Deployment     |
    |                  |  |  - Service        |
    +------------------+  +-------------------+
```

### What Each Component Does

| Component | Responsibility | When it runs |
|---|---|---|
| **ConfigLoader** | Reads `deskribe.json`, `base.json`, and `envs/{env}.json` from disk. Deserializes JSON into typed C# records. Uses a polymorphic converter to turn `"type": "postgres"` into `PostgresResource`. | First step of every command |
| **MergeEngine** | Takes all three config layers and produces a single `WorkloadPlan`. Platform defaults form the base, environment config overrides those, developer per-env overrides win last. | After loading, before validation |
| **PolicyValidator** | Checks that the manifest name is set, that all resource types have backends, and that `@resource()` references point to declared resources. | After merging |
| **ResourceReferenceResolver** | Extracts `@resource(type).property` expressions from env vars using regex. During apply, replaces them with real values from backend outputs. | Validation and Apply phases |
| **PluginHost** | In-process registry. Plugins register themselves at startup. The engine asks "give me the provider for `postgres`" and gets back `PostgresResourceProvider`. | Startup (registration), every command (lookup) |
| **Backend Adapter** | Takes a `DeskribePlan` and provisions infrastructure. Pulumi adapter would use Automation API. Returns resource outputs (connection strings, endpoints). | Apply phase |
| **Runtime Adapter** | Takes a `WorkloadPlan` with resolved env vars. Renders Kubernetes YAML (Namespace + Secret + Deployment + Service). Applies to cluster. | Apply phase, after infra is up |

---

## 2. The Manifest (deskribe.json)

The manifest is the developer's single file. Everything starts here.

### Full Annotated Example

```json
{
  // ---- Identity ----
  // Used for namespace generation, resource naming, K8s labels.
  // Pattern: {app}-{env} --> "payments-api-prod"
  "name": "payments-api",

  // ---- Resources ----
  // Each entry declares infrastructure the service needs.
  // Deskribe finds the right plugin for each "type".
  "resources": [
    {
      "type": "postgres",          // Matched to PostgresResourceProvider
      "size": "m",                 // Provider-specific: xs, s, m, l, xl
      "version": "16"             // Used by both prod (Azure DB v16) and local dev (image tag)
    },
    {
      "type": "redis"              // Minimal declaration -- all defaults
    },
    {
      "type": "kafka.messaging",   // Dotted type -- matches KafkaResourceProvider
      "topics": [
        {
          "name": "payments.transactions",
          "partitions": 6,
          "retentionHours": 168,   // 7 days
          "owners": ["team-payments"],
          "consumers": ["team-fraud", "team-notifications"]
        }
      ]
    }
  ],

  // ---- Services ----
  // Defines env vars and per-environment workload overrides.
  "services": [
    {
      "env": {
        // @resource(type).property references are resolved at apply time
        "ConnectionStrings__Postgres": "@resource(postgres).connectionString",
        "Redis__Endpoint": "@resource(redis).endpoint",
        "Kafka__BootstrapServers": "@resource(kafka.messaging).endpoint"
      },
      "overrides": {
        // Developer can override workload settings per environment
        "dev":  { "replicas": 2, "cpu": "250m", "memory": "512Mi" },
        "prod": { "replicas": 3, "cpu": "500m", "memory": "1Gi" }
      }
    }
  ]
}
```

### Field Reference

```
deskribe.json
|
+-- name (string, required)
|     Used in: namespace, resource naming, K8s labels
|     Example: "payments-api" --> namespace "payments-api-prod"
|
+-- resources[] (array of resource objects)
|   |
|   +-- type (string, required)
|   |     Determines which IResourceProvider handles this resource.
|   |     Built-in types: "postgres", "redis", "kafka.messaging"
|   |
|   +-- size (string, optional)
|   |     T-shirt size: "xs", "s", "m", "l", "xl"
|   |     Providers map this to concrete infra settings.
|   |
|   +-- [type-specific fields]
|         postgres: version, ha, sku
|         redis: version, ha, maxMemoryMb
|         kafka.messaging: topics[]
|
+-- services[] (array)
    |
    +-- name (string, optional)
    |     Used to match --image flag: --image api=ghcr.io/acme/api:v1
    |
    +-- env (dict<string, string>)
    |     Key-value pairs injected as env vars into the K8s deployment.
    |     Values can be literals or @resource() references.
    |
    +-- overrides (dict<string, ServiceOverride>)
          Per-environment workload overrides.
          Keys are environment names ("dev", "prod").
          Values: { replicas, cpu, memory }
```

### How `@resource(type).property` Works

The `@resource()` syntax is a forward reference. At write time, infrastructure
does not exist yet. The reference is a promise: "when infra is provisioned,
put the real value here."

```
  Developer writes:
    "ConnectionStrings__Postgres": "@resource(postgres).connectionString"

  What this means:
    @resource(  postgres  ) . connectionString
    |           |             |
    |           |             +-- The output property to extract.
    |           |                 Each provider declares what outputs
    |           |                 it produces (connectionString, host,
    |           |                 port, endpoint, bootstrapServers...).
    |           |
    |           +-- The resource "type" from the resources[] array.
    |               Must match a declared resource or validation fails.
    |
    +-- The @resource() prefix tells the resolver
        "this is not a literal value, resolve it."
```

The resolution happens at two different times depending on the context:

```
  +-------------------+---------------------------------------------+
  | Phase             | What happens                                |
  +-------------------+---------------------------------------------+
  | Validate          | Checks that "postgres" exists in resources. |
  |                   | Does NOT resolve the actual value.          |
  +-------------------+---------------------------------------------+
  | Plan              | Shows the reference as-is in the plan.      |
  |                   | "connectionString" is listed as a planned   |
  |                   | output with a placeholder value.            |
  +-------------------+---------------------------------------------+
  | Apply             | Backend provisions Postgres, returns        |
  |                   | actual connection string. Resolver replaces |
  |                   | @resource(postgres).connectionString with   |
  |                   | "Host=10.0.1.5;Port=5432;Database=..."      |
  +-------------------+---------------------------------------------+
  | Local Dev (Aspire)| Aspire handles this natively. Connection    |
  |                   | strings are injected via Aspire's own       |
  |                   | WithReference() mechanism.                  |
  +-------------------+---------------------------------------------+
```

---

## 3. Config Merge Pipeline

Deskribe merges configuration from three layers. This is how platform teams
set guardrails without blocking developers.

### The 3-Layer Merge Flow

```
  Layer 1: PLATFORM BASE                Layer 2: ENVIRONMENT             Layer 3: DEVELOPER
  (base.json)                           (envs/prod.json)                 (deskribe.json overrides.prod)
  =======================               ====================             ================================

  {                                     {                                {
    "defaults": {                         "defaults": {                    "overrides": {
      "runtime": "kubernetes",              "replicas": 3,                   "prod": {
      "region": "westeurope",               "cpu": "500m",                     "replicas": 5,
      "replicas": 2,                        "memory": "1Gi",                   "cpu": "1000m"
      "cpu": "250m",                        "ha": true                       }
      "memory": "512Mi",                  }                                }
      "namespacePattern":               }                                }
        "{app}-{env}"
    },
    "backends": {
      "postgres": "pulumi",
      ...
    }
  }

     |                                     |                                |
     |    lowest priority                  |    middle priority             |    highest priority
     +-------------------------------------+--------------------------------+
                                           |
                                           v

                               +------------------------+
                               |     MergeEngine        |
                               |                        |
                               |  For each scalar:      |
                               |  last non-default wins |
                               +----------+-------------+
                                          |
                                          v

                               MERGED WORKLOAD PLAN
                               ====================
                               {
                                 AppName:   "payments-api"
                                 Env:       "prod"
                                 Namespace: "payments-api-prod"
                                 Replicas:  5          <-- developer won
                                 Cpu:       "1000m"    <-- developer won
                                 Memory:    "1Gi"      <-- env won (dev didn't set)
                                 Image:     (from --image flag)
                               }
```

### Concrete Example: Who Wins?

Consider a scenario where the platform says 2 replicas, the environment
overlay says 3 for prod, and the developer says 5 for prod:

```
  Setting: "replicas"
  =====================================================================

  Step 1: Start with platform base
  +--------------------+
  | base.json          |       replicas = 2
  | replicas: 2        |       ==================>  current = 2
  +--------------------+

  Step 2: Apply environment overlay
  +--------------------+
  | envs/prod.json     |       envConfig.Replicas (3) != platform.Replicas (2)
  | replicas: 3        |       so override applies
  +--------------------+       ==================>  current = 3

  Step 3: Apply developer per-env override
  +--------------------+
  | deskribe.json      |       overrides["prod"].Replicas has value (5)
  | overrides.prod:    |       so override applies
  |   replicas: 5      |       ==================>  current = 5
  +--------------------+

  RESULT: replicas = 5 (developer wins)
```

### Merge Rules by Category

```
  +-------------------+------------------+---------------------------------------+
  | Category          | Merge Rule       | Why                                   |
  +-------------------+------------------+---------------------------------------+
  | Scalars           | Last wins        | replicas, cpu, memory -- developer    |
  | (replicas, cpu,   |                  | closest to the workload knows best.   |
  | memory)           |                  | Platform sets sane defaults, dev can  |
  |                   |                  | override per-env.                     |
  +-------------------+------------------+---------------------------------------+
  | Dictionaries      | Deep merge       | env vars from service definition are  |
  | (env vars)        |                  | passed through as-is. Platform does   |
  |                   |                  | not inject env vars.                  |
  +-------------------+------------------+---------------------------------------+
  | Resources         | Developer is     | Only the developer's deskribe.json    |
  |                   | authoritative    | declares resources. Platform config   |
  |                   |                  | does not add or remove resources.     |
  +-------------------+------------------+---------------------------------------+
  | Backends          | Platform only    | "postgres": "pulumi" comes only from  |
  |                   |                  | base.json. Developer cannot choose    |
  |                   |                  | Terraform vs Pulumi.                  |
  +-------------------+------------------+---------------------------------------+
  | Region, Runtime,  | Platform only    | Governance. Developer cannot deploy   |
  | TLS, Namespace    |                  | to us-east-1 if policy says           |
  | Pattern           |                  | westeurope/northeurope only.          |
  +-------------------+------------------+---------------------------------------+
```

### Data Flow Through Each Merge Step (Actual Code Path)

```csharp
// MergeEngine.MergeWorkloadPlan() -- simplified

// Step 1: Start with platform defaults
var replicas = platform.Defaults.Replicas;     // 2
var cpu      = platform.Defaults.Cpu;           // "250m"
var memory   = platform.Defaults.Memory;        // "512Mi"

// Step 2: Environment overlay (only if different from platform defaults)
if (envConfig.Defaults.Replicas != 0
    && envConfig.Defaults.Replicas != platform.Defaults.Replicas)
    replicas = envConfig.Defaults.Replicas;     // 3 (for prod)

if (envConfig.Defaults.Cpu != platform.Defaults.Cpu)
    cpu = envConfig.Defaults.Cpu;               // "500m" (for prod)

if (envConfig.Defaults.Memory != platform.Defaults.Memory)
    memory = envConfig.Defaults.Memory;         // "1Gi" (for prod)

// Step 3: Developer per-env overrides (highest priority)
var service = manifest.Services.FirstOrDefault();
if (service?.Overrides.TryGetValue(environment, out var devOverride) == true)
{
    if (devOverride.Replicas.HasValue)
        replicas = devOverride.Replicas.Value;  // 5 (developer said so)
    if (devOverride.Cpu is not null)
        cpu = devOverride.Cpu;                  // "1000m"
    if (devOverride.Memory is not null)
        memory = devOverride.Memory;            // stays "1Gi" (not set)
}

// Step 4: Generate namespace from platform pattern (not overridable)
var ns = platform.Defaults.NamespacePattern
    .Replace("{app}", manifest.Name)            // "payments-api"
    .Replace("{env}", environment);             // "prod"
// ns = "payments-api-prod"
```

---

## 4. Plugin Architecture

Plugins are how Deskribe stays extensible. Each infrastructure concern
(Postgres, Redis, Kafka, Pulumi, Kubernetes) is a separate plugin that
registers with the PluginHost at startup.

### Plugin Host and Registration

```
                       +---------------------+
                       |   CLI Startup       |
                       |   (Program.cs)      |
                       +----------+----------+
                                  |
                    Calls RegisterPlugin() for each:
                                  |
          +-----------+-----------+-----------+-----------+
          |           |           |           |           |
          v           v           v           v           v
  +-------+---+ +-----+-----+ +--+------+ +--+------+ +--+--------+
  | Postgres  | | Redis     | | Kafka   | | Pulumi  | | K8s       |
  | Plugin    | | Plugin    | | Plugin  | | Plugin  | | Plugin    |
  +-----------+ +-----------+ +---------+ +---------+ +-----------+
       |             |             |           |            |
       | Register    | Register   | Register  | Register   | Register
       | Resource    | Resource   | Resource  | Backend    | Runtime
       | Provider    | Provider   | Provider  | Adapter    | Adapter
       |             |             | +Messaging|           |
       v             v             v           v            v
  +----------------------------------------------------------------+
  |                       PLUGIN HOST                               |
  |                    (IPluginRegistrar)                           |
  |                                                                |
  |  _resourceProviders:                                           |
  |    "postgres"         --> PostgresResourceProvider              |
  |    "redis"            --> RedisResourceProvider                 |
  |    "kafka.messaging"  --> KafkaResourceProvider                 |
  |                                                                |
  |  _backendAdapters:                                             |
  |    "pulumi"           --> PulumiBackendAdapter                  |
  |                                                                |
  |  _runtimeAdapters:                                             |
  |    "kubernetes"       --> KubernetesRuntimeAdapter              |
  |                                                                |
  |  _messagingProviders:                                          |
  |    "kafka.messaging"  --> KafkaMessagingProvider                |
  +----------------------------------------------------------------+
```

### Interface Hierarchy

```
  IPlugin                              -- Base: has a Name, calls Register()
  |
  +-- Register(IPluginRegistrar)       -- Plugin tells the registrar what it provides
      |
      |  The registrar accepts four types of capability:
      |
      +-- IResourceProvider            -- Validates + plans a resource type
      |     ResourceType: string       -- "postgres", "redis", "kafka.messaging"
      |     ValidateAsync(resource, ctx)
      |     PlanAsync(resource, ctx)
      |
      +-- IBackendAdapter              -- Provisions/destroys infra
      |     Name: string               -- "pulumi", "terraform"
      |     ApplyAsync(plan)           -- Returns resource outputs
      |     DestroyAsync(app, env, platform)
      |
      +-- IRuntimeAdapter              -- Deploys workloads
      |     Name: string               -- "kubernetes", "ecs"
      |     RenderAsync(workload)      -- Returns YAML/manifest
      |     ApplyAsync(manifest)       -- Deploys to cluster
      |     DestroyAsync(namespace)
      |
      +-- IMessagingProvider           -- Specialized for topic/ACL management
            ProviderType: string       -- "kafka.messaging"
            ValidateAsync(resource, ctx)
            PlanAsync(resource, ctx)
```

### How Plugins Register and Get Discovered

At startup, `Program.cs` creates each plugin and calls `RegisterPlugin()`:

```csharp
// CLI startup -- Program.cs
var pluginHost = serviceProvider.GetRequiredService<PluginHost>();
pluginHost.RegisterPlugin(new PostgresPlugin());    // registers IResourceProvider
pluginHost.RegisterPlugin(new RedisPlugin());       // registers IResourceProvider
pluginHost.RegisterPlugin(new KafkaPlugin());       // registers IResourceProvider + IMessagingProvider
pluginHost.RegisterPlugin(new PulumiPlugin());      // registers IBackendAdapter
pluginHost.RegisterPlugin(new KubernetesPlugin());  // registers IRuntimeAdapter
```

Each plugin's `Register` method decides what to register:

```csharp
// PostgresPlugin.cs
public class PostgresPlugin : IPlugin
{
    public string Name => "Postgres Resource Provider";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterResourceProvider(new PostgresResourceProvider());
    }
}

// KafkaPlugin.cs -- registers TWO capabilities
public class KafkaPlugin : IPlugin
{
    public string Name => "Kafka Messaging Provider";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterResourceProvider(new KafkaResourceProvider());
        registrar.RegisterMessagingProvider(new KafkaMessagingProvider());
    }
}
```

When the engine processes a resource, it asks the PluginHost:

```
  Engine: "I have a resource with type = 'postgres'. Who handles it?"
  PluginHost: looks up _resourceProviders["postgres"]
  PluginHost: returns PostgresResourceProvider
  Engine: calls provider.ValidateAsync() or provider.PlanAsync()
```

### Plugin Lifecycle

```
  Phase 1: REGISTER (startup)
  ============================
  Plugin.Register(registrar)  -->  PluginHost stores provider in dictionary
  This happens once at application startup.

  Phase 2: VALIDATE (deskribe validate)
  =====================================
  For each resource in manifest:
    provider = PluginHost.GetResourceProvider(resource.Type)
    result = provider.ValidateAsync(resource, validationContext)
    if (!result.IsValid) --> collect errors

  Phase 3: PLAN (deskribe plan)
  =============================
  For each resource in manifest:
    provider = PluginHost.GetResourceProvider(resource.Type)
    plan = provider.PlanAsync(resource, planContext)
    --> Returns ResourcePlanResult with:
        - Action: "create" | "update" | "no-change"
        - PlannedOutputs: what the resource will produce
        - Configuration: version, size, region, etc.

  Phase 4: APPLY (deskribe apply)
  ===============================
  For each resource plan:
    backendName = platform.Backends[resource.Type]    // "pulumi"
    backend = PluginHost.GetBackendAdapter(backendName)
    result = backend.ApplyAsync(plan)
    --> Returns BackendApplyResult with actual resource outputs

  Then:
    runtimeName = platform.Defaults.Runtime           // "kubernetes"
    runtime = PluginHost.GetRuntimeAdapter(runtimeName)
    manifest = runtime.RenderAsync(workloadWithResolvedEnv)
    runtime.ApplyAsync(manifest)
```

---

## 5. Resource Resolution

This section walks through exactly what happens when the engine encounters
`@resource(postgres).connectionString` -- from raw string to resolved value.

### Step-by-Step Walkthrough

**Input**: The developer wrote this in their manifest:

```json
{
  "env": {
    "ConnectionStrings__Postgres": "@resource(postgres).connectionString"
  }
}
```

**Step 1: Regex Extraction**

The `ResourceReferenceResolver` uses a compiled regex to find references:

```
Pattern: @resource\((?<type>[a-zA-Z0-9_.]+)\)\.(?<property>[a-zA-Z0-9_]+)

Applied to: "@resource(postgres).connectionString"

Match:
  Full match:  "@resource(postgres).connectionString"
  Group "type":     "postgres"
  Group "property": "connectionString"
```

This produces a `ResourceReference` record:

```
ResourceReference {
    EnvVarName:    "ConnectionStrings__Postgres"
    RawExpression: "@resource(postgres).connectionString"
    ResourceType:  "postgres"
    Property:      "connectionString"
}
```

**Step 2: Validation (during `deskribe validate`)**

The resolver checks that `"postgres"` exists in the manifest's declared resources:

```
  Declared resource types: { "postgres", "redis", "kafka.messaging" }

  Check: does "postgres" exist in that set?
  Result: YES --> validation passes

  If the developer wrote @resource(mysql).connectionString:
  Check: does "mysql" exist?
  Result: NO --> "Environment variable 'ConnectionStrings__Postgres'
                  references unknown resource type 'mysql'"
```

**Step 3: Planning (during `deskribe plan`)**

The PostgresResourceProvider generates planned outputs:

```
ResourcePlanResult {
    ResourceType: "postgres"
    Action: "create"
    PlannedOutputs: {
        "connectionString": "Host=payments-api-postgres.payments-api-prod
                             .svc.cluster.local;Port=5432;
                             Database=payments-api;Username=app;
                             Password=<generated>"
        "host": "payments-api-postgres.payments-api-prod.svc.cluster.local"
        "port": "5432"
    }
}
```

At this point, the env var still holds the raw `@resource(...)` string.
The planned outputs show what the reference *will* resolve to.

**Step 4: Resolution (during `deskribe apply`)**

After the backend provisions the resource and returns real outputs:

```
  Backend returns:
  resourceOutputs = {
      "postgres": {
          "connectionString": "Host=10.0.1.5;Port=5432;Database=payments-api;
                               Username=app;Password=xK9!mR2@qZ",
          "host": "10.0.1.5",
          "port": "5432"
      }
  }

  Resolver processes each env var:

  Input:  "ConnectionStrings__Postgres" = "@resource(postgres).connectionString"

  Regex match found:
    type = "postgres"
    property = "connectionString"

  Lookup: resourceOutputs["postgres"]["connectionString"]
  Found:  "Host=10.0.1.5;Port=5432;Database=payments-api;Username=app;Password=xK9!mR2@qZ"

  Output: "ConnectionStrings__Postgres" = "Host=10.0.1.5;Port=5432;..."
```

**The full lifecycle visualized:**

```
  WRITE TIME           VALIDATE            PLAN                 APPLY
  ==========           ========            ====                 =====

  @resource            "Does               "Will produce:       "Actual value:
   (postgres)           postgres            Host=...svc         Host=10.0.1.5;
   .connection          exist in            .cluster.local;     Port=5432;
    String"             resources?"         Port=5432;..."       Database=...;
                                                                 Password=xK9..."
       |                    |                    |                    |
       v                    v                    v                    v
  Raw string         Type validated      Planned output       Injected into
  in manifest        against declared    shows expected       K8s Secret as
                     resources[]         shape                real env var
```

---

## 6. Engine Pipeline

The `DeskribeEngine` orchestrates the entire flow. Every CLI command
(validate, plan, apply, destroy) passes through it.

### Full Pipeline Diagram

```
  deskribe apply --env prod --platform ./platform-config --image api=ghcr.io/acme/api:v1
  |
  |
  v
  +===========================================================================+
  |                          DESKRIBE ENGINE PIPELINE                          |
  +===========================================================================+
  |                                                                           |
  |  STAGE 1: LOAD                                                            |
  |  +-----------+  +--------------+  +----------------+                      |
  |  | deskribe  |  | base.json    |  | envs/prod.json |                      |
  |  | .json     |  | (platform)   |  | (env overlay)  |                      |
  |  +-----+-----+  +------+-------+  +-------+--------+                      |
  |        |               |                   |                              |
  |        v               v                   v                              |
  |  ConfigLoader     ConfigLoader        ConfigLoader                        |
  |  .LoadManifest    .LoadPlatform       .LoadEnvironment                    |
  |  Async()          ConfigAsync()       ConfigAsync()                       |
  |        |               |                   |                              |
  |        v               v                   v                              |
  |  DeskribeManifest PlatformConfig    EnvironmentConfig                     |
  |  (typed records)  (typed record)    (typed record)                        |
  |                                                                           |
  |  -------  GATE: If any file missing or invalid JSON, throws  ----------  |
  |                                                                           |
  |  STAGE 2: MERGE                                                           |
  |  +-------------------------------------------------------------------+   |
  |  |  MergeEngine.MergeWorkloadPlan(manifest, platform, envConfig,     |   |
  |  |                                 environment, image)               |   |
  |  |                                                                   |   |
  |  |  platform.Defaults -> envConfig.Defaults -> dev overrides[env]    |   |
  |  |                                                                   |   |
  |  |  Output: WorkloadPlan {                                           |   |
  |  |    AppName, Environment, Namespace, Image,                        |   |
  |  |    Replicas, Cpu, Memory, EnvironmentVariables                    |   |
  |  |  }                                                                |   |
  |  +-------------------------------------------------------------------+   |
  |                                                                           |
  |  STAGE 3: VALIDATE (runs on each resource via its provider)               |
  |  +-------------------------------------------------------------------+   |
  |  |  For each resource in manifest.Resources:                         |   |
  |  |    provider = PluginHost.GetResourceProvider(resource.Type)        |   |
  |  |    result = provider.ValidateAsync(resource, context)             |   |
  |  |                                                                   |   |
  |  |  Also: PolicyValidator checks manifest-level rules                |   |
  |  |  Also: RefResolver validates @resource() references               |   |
  |  +-------------------------------------------------------------------+   |
  |                                                                           |
  |  -------  GATE: If validation errors > 0, stop. Report errors.  -------  |
  |                                                                           |
  |  STAGE 4: PLAN (runs on each resource via its provider)                   |
  |  +-------------------------------------------------------------------+   |
  |  |  For each resource in manifest.Resources:                         |   |
  |  |    provider = PluginHost.GetResourceProvider(resource.Type)        |   |
  |  |    plan = provider.PlanAsync(resource, planContext)                |   |
  |  |                                                                   |   |
  |  |  Output: DeskribePlan {                                           |   |
  |  |    AppName, Environment, Platform, EnvironmentConfig,             |   |
  |  |    ResourcePlans[], Workload, Warnings[]                          |   |
  |  |  }                                                                |   |
  |  +-------------------------------------------------------------------+   |
  |                                                                           |
  |  STAGE 5: APPLY INFRASTRUCTURE (via backend adapters)                     |
  |  +-------------------------------------------------------------------+   |
  |  |  For each resourcePlan in plan.ResourcePlans:                     |   |
  |  |    backendName = platform.Backends[resourcePlan.ResourceType]      |   |
  |  |    backend = PluginHost.GetBackendAdapter(backendName)            |   |
  |  |    result = backend.ApplyAsync(plan)                              |   |
  |  |                                                                   |   |
  |  |  Collects: resourceOutputs["postgres"]["connectionString"] = ...  |   |
  |  +-------------------------------------------------------------------+   |
  |                                                                           |
  |  -------  GATE: If backend fails, throw InvalidOperationException  -----  |
  |                                                                           |
  |  STAGE 6: RESOLVE REFERENCES                                              |
  |  +-------------------------------------------------------------------+   |
  |  |  resolvedEnv = RefResolver.ResolveReferences(                     |   |
  |  |    workload.EnvironmentVariables, resourceOutputs)                |   |
  |  |                                                                   |   |
  |  |  @resource(postgres).connectionString                             |   |
  |  |    --> "Host=10.0.1.5;Port=5432;Database=payments-api;..."        |   |
  |  +-------------------------------------------------------------------+   |
  |                                                                           |
  |  STAGE 7: DEPLOY (via runtime adapter)                                    |
  |  +-------------------------------------------------------------------+   |
  |  |  runtimeName = platform.Defaults.Runtime  // "kubernetes"         |   |
  |  |  runtime = PluginHost.GetRuntimeAdapter(runtimeName)              |   |
  |  |                                                                   |   |
  |  |  manifest = runtime.RenderAsync(resolvedWorkload)                 |   |
  |  |    --> Generates: Namespace, Secret, Deployment, Service YAML     |   |
  |  |                                                                   |   |
  |  |  runtime.ApplyAsync(manifest)                                     |   |
  |  |    --> Creates/updates K8s resources in cluster                   |   |
  |  +-------------------------------------------------------------------+   |
  |                                                                           |
  +===========================================================================+
```

### Error Handling and Validation Gates

The pipeline has three hard gates where execution stops on failure:

```
  Gate 1: LOAD
  +-----------------------------------------------------------------------+
  | Trigger: File not found, invalid JSON, unknown resource type          |
  | What happens: Exception thrown with descriptive message               |
  | Example: "Unknown resource type: dynamodb"                            |
  |          (ResourceJsonConverter doesn't know how to deserialize it)   |
  | Recovery: Fix the JSON and re-run                                     |
  +-----------------------------------------------------------------------+

  Gate 2: VALIDATE
  +-----------------------------------------------------------------------+
  | Trigger: Policy violations, invalid resource configs, bad references  |
  | What happens: ValidationResult with IsValid=false, errors collected   |
  | Examples:                                                             |
  |   - "Manifest 'name' is required"                                     |
  |   - "Invalid Postgres size 'xxl'. Valid: xs, s, m, l, xl"            |
  |   - "Env var 'DB' references resource type 'mysql' not in resources" |
  |   - "Kafka topic must have at least 1 partition"                      |
  |   - "Resource type 'redis' has no configured backend"  (warning)     |
  | Recovery: Fix manifest, run validate again                            |
  +-----------------------------------------------------------------------+

  Gate 3: APPLY
  +-----------------------------------------------------------------------+
  | Trigger: Backend provisioning fails                                   |
  | What happens: InvalidOperationException thrown                        |
  | Example: "Backend apply failed for postgres: Pulumi stack error..."   |
  | Recovery: Check backend logs, fix infrastructure issue, re-apply     |
  +-----------------------------------------------------------------------+
```

---

## 7. Building Blocks

### SDK (Deskribe.Sdk)

The SDK defines all contracts and types. It has zero implementation -- only
interfaces, records, and models. Any plugin author depends only on this package.

**Plugin Contracts (interfaces)**:

```csharp
// Every plugin implements this
public interface IPlugin
{
    string Name { get; }
    void Register(IPluginRegistrar registrar);
}

// The registrar that plugins call during Register()
public interface IPluginRegistrar
{
    void RegisterResourceProvider(IResourceProvider provider);
    void RegisterBackendAdapter(IBackendAdapter adapter);
    void RegisterRuntimeAdapter(IRuntimeAdapter adapter);
    void RegisterMessagingProvider(IMessagingProvider provider);
}

// Handles a specific resource type (postgres, redis, kafka.messaging)
public interface IResourceProvider
{
    string ResourceType { get; }
    Task<ValidationResult> ValidateAsync(DeskribeResource resource,
                                          ValidationContext ctx, CancellationToken ct);
    Task<ResourcePlanResult> PlanAsync(DeskribeResource resource,
                                        PlanContext ctx, CancellationToken ct);
}

// Provisions infrastructure (Pulumi, Terraform, etc.)
public interface IBackendAdapter
{
    string Name { get; }
    Task<BackendApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct);
    Task DestroyAsync(string appName, string environment, PlatformConfig platform, CancellationToken ct);
}

// Deploys workloads (Kubernetes, ECS, etc.)
public interface IRuntimeAdapter
{
    string Name { get; }
    Task<WorkloadManifest> RenderAsync(WorkloadPlan workload, CancellationToken ct);
    Task ApplyAsync(WorkloadManifest manifest, CancellationToken ct);
    Task DestroyAsync(string namespaceName, CancellationToken ct);
}

// Specialized for messaging systems with topics and ACLs
public interface IMessagingProvider
{
    string ProviderType { get; }
    Task<ValidationResult> ValidateAsync(KafkaMessagingResource resource,
                                          ValidationContext ctx, CancellationToken ct);
    Task<ResourcePlanResult> PlanAsync(KafkaMessagingResource resource,
                                        PlanContext ctx, CancellationToken ct);
}
```

**Resource Schemas (records)**:

```csharp
// Base class -- all resources have a type and optional size
public abstract record DeskribeResource
{
    public required string Type { get; init; }
    public string? Size { get; init; }
}

// Type-specific resources extend the base
public sealed record PostgresResource : DeskribeResource
{
    public string? Version { get; init; }        // "14", "15", "16", "17"
    public bool? Ha { get; init; }               // high availability
    public string? Sku { get; init; }            // cloud-specific SKU
}

public sealed record RedisResource : DeskribeResource
{
    public string? Version { get; init; }
    public bool? Ha { get; init; }
    public int? MaxMemoryMb { get; init; }
}

public sealed record KafkaMessagingResource : DeskribeResource
{
    public List<KafkaTopic> Topics { get; init; } = [];
}

public sealed record KafkaTopic
{
    public required string Name { get; init; }   // "payments.transactions"
    public int? Partitions { get; init; }        // minimum 3 per platform policy
    public int? RetentionHours { get; init; }    // 168 = 7 days
    public List<string> Owners { get; init; } = [];     // WRITE access
    public List<string> Consumers { get; init; } = [];  // READ access
}
```

**Model Types (manifest, platform config, plans)**:

```csharp
// Developer's manifest
public sealed record DeskribeManifest
{
    public required string Name { get; init; }
    public List<DeskribeResource> Resources { get; init; } = [];
    public List<ServiceDefinition> Services { get; init; } = [];
}

// Platform team's base config
public sealed record PlatformConfig
{
    public string? Organization { get; init; }
    public PlatformDefaults Defaults { get; init; } = new();
    public Dictionary<string, string> Backends { get; init; } = new();
    public PlatformPolicies Policies { get; init; } = new();
}

// Environment overlay
public sealed record EnvironmentConfig
{
    public required string Name { get; init; }
    public PlatformDefaults Defaults { get; init; } = new();
    public Dictionary<string, List<string>> AlertRouting { get; init; } = new();
}

// The output of the Plan phase
public sealed record DeskribePlan
{
    public required string AppName { get; init; }
    public required string Environment { get; init; }
    public required PlatformConfig Platform { get; init; }
    public required EnvironmentConfig EnvironmentConfig { get; init; }
    public List<ResourcePlanResult> ResourcePlans { get; init; } = [];
    public WorkloadPlan? Workload { get; init; }
    public List<string> Warnings { get; init; } = [];
}

// What a single resource will produce
public sealed record ResourcePlanResult
{
    public required string ResourceType { get; init; }
    public required string Action { get; init; }  // "create", "update", "no-change"
    public Dictionary<string, string> PlannedOutputs { get; init; } = new();
    public Dictionary<string, object?> Configuration { get; init; } = new();
}
```

---

### Core (Deskribe.Core)

The Core library contains all the orchestration logic. Here is what each
class does and how they connect.

**ConfigLoader** -- JSON deserialization with polymorphic resources:

```
  ConfigLoader reads three files:
  +--------------------------------------------------+
  |                                                  |
  |  LoadManifestAsync("deskribe.json")              |
  |    --> Reads file, deserializes with             |
  |        ResourceJsonConverter                     |
  |    --> Converter checks "type" field:            |
  |        "postgres"        -> PostgresResource     |
  |        "redis"           -> RedisResource        |
  |        "kafka.messaging" -> KafkaMessagingResource|
  |        anything else     -> JsonException        |
  |    --> Returns DeskribeManifest                   |
  |                                                  |
  |  LoadPlatformConfigAsync("platform-config/")     |
  |    --> Reads base.json from the directory        |
  |    --> Returns PlatformConfig                     |
  |                                                  |
  |  LoadEnvironmentConfigAsync("platform-config/",  |
  |                              "prod")             |
  |    --> Reads envs/prod.json                      |
  |    --> If file missing: returns empty defaults    |
  |    --> Returns EnvironmentConfig                  |
  +--------------------------------------------------+
```

**MergeEngine** -- 3-layer merge algorithm:

```
  Input:  DeskribeManifest + PlatformConfig + EnvironmentConfig + env + image
  Output: WorkloadPlan

  Algorithm:
    1. Start with platform.Defaults for replicas, cpu, memory
    2. Override with envConfig.Defaults if different from platform
    3. Override with manifest.Services[0].Overrides[env] if set
    4. Generate namespace: pattern.Replace("{app}",name).Replace("{env}",env)
    5. Attach env vars from manifest.Services[0].Env (still unresolved)
    6. Attach image from --image CLI flag
```

**ResourceReferenceResolver** -- `@resource()` syntax parsing:

```
  Regex: @resource\((?<type>[a-zA-Z0-9_.]+)\)\.(?<property>[a-zA-Z0-9_]+)

  Three methods:
    ExtractReferences(envVars)
      --> Scans all env var values for @resource() patterns
      --> Returns list of ResourceReference records

    ValidateReferences(refs, availableTypes)
      --> Checks each reference's type exists in declared resources
      --> Returns validation result with errors for unknown types

    ResolveReferences(envVars, resourceOutputs)
      --> Replaces each @resource(type).property with the actual value
          from resourceOutputs[type][property]
      --> Unresolvable references are left as-is (logged as warning)
```

**PolicyValidator** -- Platform policy enforcement:

```
  Checks:
    1. manifest.Name is not null or whitespace
    2. Each resource type has a backend in platform.Backends
       (warns if not, does not fail)
    3. Each @resource() reference in env vars points to a
       declared resource (fails if not)
```

**PluginHost** -- In-process plugin registry:

```
  Implements IPluginRegistrar.
  Four dictionaries keyed by name/type:
    _resourceProviders:  Dict<string, IResourceProvider>
    _backendAdapters:    Dict<string, IBackendAdapter>
    _runtimeAdapters:    Dict<string, IRuntimeAdapter>
    _messagingProviders: Dict<string, IMessagingProvider>

  RegisterPlugin(IPlugin) --> calls plugin.Register(this)
  GetResourceProvider("postgres") --> looks up in _resourceProviders
  GetBackendAdapter("pulumi") --> looks up in _backendAdapters
  GetRuntimeAdapter("kubernetes") --> looks up in _runtimeAdapters
```

**DeskribeEngine** -- Main orchestration:

```
  Methods:
    ValidateAsync(manifestPath, platformPath, env)
      --> Load -> PolicyValidate -> RefValidate -> ProviderValidate

    PlanAsync(manifestPath, platformPath, env, images)
      --> Load -> Merge -> ProviderPlan -> Build DeskribePlan

    ApplyAsync(plan)
      --> BackendApply -> ResolveRefs -> RuntimeRender -> RuntimeApply

    DestroyAsync(manifestPath, platformPath, env)
      --> Load -> RuntimeDestroy(namespace) -> BackendDestroy(app, env)
```

---

### Plugins

**Postgres Resource Provider**:

```
  ResourceType: "postgres"
  ValidSizes: { "xs", "s", "m", "l", "xl" }
  ValidVersions: { "14", "15", "16", "17" }

  Validate:
    - Size must be in ValidSizes (if provided)
    - Version must be in ValidVersions (if provided)

  Plan:
    - Defaults: size="s", version="16"
    - HA comes from resource.Ha ?? envConfig.Ha ?? false
    - Configuration: version, size, ha, appName, environment, region
    - Outputs (pending placeholders until backend provisions):
        connectionString: "<pending:{appName}-postgres>"
        host: "<pending:{appName}-postgres-host>"
        port: "5432"
```

**Redis Resource Provider**:

```
  ResourceType: "redis"
  ValidSizes: { "xs", "s", "m", "l", "xl" }

  Validate:
    - Size must be in ValidSizes (if provided)

  Plan:
    - HA comes from resource.Ha ?? envConfig.Ha ?? false
    - Configuration: size, ha, appName, environment, region
    - Outputs (pending placeholders until backend provisions):
        endpoint: "<pending:{appName}-redis>"
        host: "<pending:{appName}-redis-host>"
        port: "6379"
```

**Kafka Resource Provider + Messaging Provider**:

```
  ResourceType: "kafka.messaging"
  The KafkaPlugin registers TWO capabilities:

  KafkaResourceProvider (IResourceProvider):
    Validate:
      - Must have at least one topic
      - Each topic needs a name
      - Partitions >= 1 (if set)
      - RetentionHours >= 1 (if set)
      - Each topic needs at least one owner

    Plan:
      - Configuration: appName, environment, region, topics
      - Outputs (pending placeholders until backend provisions):
          endpoint: "<pending:{appName}-kafka>"
          bootstrapServers: "<pending:{appName}-kafka-bootstrap>"
      - Configuration includes topic details (name, partitions, retention)

  KafkaMessagingProvider (IMessagingProvider):
    Validate:
      - Platform minimum: 3 partitions per topic

    Plan:
      - Generates ACL entries:
        owners  --> WRITE permission
        consumers --> READ permission
```

**Pulumi Backend Adapter**:

```
  Name: "pulumi"

  ApplyAsync:
    Requires pulumiProjectDir in platform defaults (fails with error if not set).
    - Uses Pulumi.Automation.LocalWorkspace to run a real Pulumi project
    - Sets stack config from DeskribePlan (appName, environment, region, resources)
    - Runs 'pulumi up' and captures stack outputs
    - Returns real infrastructure outputs (connection strings, endpoints)

  DestroyAsync(appName, environment, platform):
    Requires pulumiProjectDir in platform defaults (throws if not set).
    - Creates or selects the Pulumi stack "{appName}-{environment}"
    - Runs stack.DestroyAsync to tear down all resources
    - Removes the stack from the workspace
```

**Kubernetes Runtime Adapter**:

```
  Name: "kubernetes"

  RenderAsync(workload):
    Generates four K8s resources as YAML:

    1. Namespace
       name: workload.Namespace (e.g., "payments-api-prod")
       label: app.kubernetes.io/managed-by=deskribe

    2. Secret (if env vars exist)
       name: "{appName}-env"
       type: Opaque
       stringData: resolved env vars

    3. Deployment
       name: workload.AppName
       replicas: workload.Replicas
       container image: workload.Image ?? "nginx:latest"
       port: 8080
       resources: cpu/memory from workload
       envFrom: secretRef to the Secret above

    4. Service
       name: workload.AppName
       port 80 -> targetPort 8080

  ApplyAsync(manifest):
    - Connects to K8s cluster via kubeconfig
    - For each resource: try read, if exists -> update, if 404 -> create
    - Requires a valid kubeconfig (fails if no cluster is available)

  DestroyAsync(namespace):
    - Deletes the entire namespace (cascading delete)
```

---

### Aspire Bridge (Deskribe.Aspire)

The Aspire bridge reads the same `deskribe.json` and creates local
development containers. The developer gets a complete local environment
with zero Docker Compose.

**How deskribe.json maps to Aspire resources:**

```
  deskribe.json resource         Aspire API call                You get
  ==========================     ==========================     =======================
  { "type": "postgres" }        builder.AddPostgres(name)      Postgres container
                                   .AddDatabase(dbName)         + PgAdmin UI
                                   .WithPgAdmin()               + Persistent volume
                                   .WithLifetime(Persistent)

  { "type": "redis" }           builder.AddRedis(name)         Redis container
                                   .WithRedisInsight()          + RedisInsight UI
                                   .WithLifetime(Persistent)    + Persistent volume

  { "type": "kafka.messaging" } builder.AddKafka(name)         Kafka container
                                   .WithKafkaUI()               + Kafka UI
                                   .WithLifetime(Persistent)    + Persistent volume
```

**Dynamic resource creation flow:**

```
  AppHost starts
       |
       v
  builder.AddDeskribeManifest("path/to/deskribe.json")
       |
       |  1. Reads and deserializes the manifest
       |  2. Iterates manifest.Resources
       |  3. For each resource, switches on type:
       |
       +---> type = "postgres"
       |       |
       |       v
       |     AddPostgres("{app}-postgres")
       |       .WithImageTag("16")             <-- from resource.Version
       |       .WithPgAdmin()                  <-- always on in local dev
       |       .WithLifetime(Persistent)       <-- always on in local dev
       |       .AddDatabase("{app}-db")
       |       |
       |       +---> Registers in DeskribeResourceMap
       |
       +---> type = "redis"
       |       |
       |       v
       |     AddRedis("{app}-redis")
       |       .WithRedisInsight()
       |       .WithLifetime(Persistent)
       |       |
       |       +---> Registers in DeskribeResourceMap
       |
       +---> type = "kafka.messaging"
               |
               v
             AddKafka("{app}-kafka")
               .WithKafkaUI()
               .WithLifetime(Persistent)
               |
               +---> Registers in DeskribeResourceMap
       |
       v
  Returns DeskribeResourceMap
       |
       v
  builder.AddProject<Projects.MyService>("my-service")
    .WithDeskribeResources(resources)
       |
       |  For each resource in map:
       |    .WithReference(resource)     <-- injects connection string
       |    .WaitFor(resource)           <-- waits for health check
       |
       v
  builder.Build().Run()
       |
       v
  Aspire Dashboard at http://localhost:15888
  All containers running, all connection strings injected.
```

**The AppHost code that ties it all together:**

```csharp
// AppHost.cs -- this is the entire file
var builder = DistributedApplication.CreateBuilder(args);

// Read manifest -- single source of truth
var manifestPath = builder.Configuration["Deskribe:ManifestPath"]
    ?? Path.Combine(builder.AppHostDirectory, "..", "..",
                    "examples", "payments-api", "deskribe.json");

var resources = builder.AddDeskribeManifest(manifestPath);

// Wire web dashboard with all resources
var web = builder.AddProject<Projects.Deskribe_Web>("deskribe-web")
    .WithDeskribeResources(resources)
    .WithExternalHttpEndpoints();

builder.Build().Run();
```

---

## 8. Data Flow Example

Complete end-to-end walkthrough: developer writes `deskribe.json`, runs
`deskribe apply --env prod`, and watches resources get provisioned.

### Starting State

The developer has this in their repo:

```
payments-api/
  deskribe.json        (shown above in section 2)
```

The platform team has this in their repo:

```
platform-config/
  base.json            (org defaults, backend mappings, policies)
  envs/
    dev.json           (dev overrides)
    prod.json          (prod overrides: replicas=3, cpu=500m, memory=1Gi, ha=true)
```

### The Command

```bash
deskribe apply --env prod \
  --platform ./platform-config \
  --image api=ghcr.io/acme/payments-api:sha-abc123
```

### Step-by-Step Execution

```
STEP 1: CLI PARSING
===================
System.CommandLine parses the arguments:
  file:     "deskribe.json" (default)
  env:      "prod"
  platform: "./platform-config"
  images:   ["api=ghcr.io/acme/payments-api:sha-abc123"]

Image parsing: "api=ghcr.io/acme/payments-api:sha-abc123"
  --> { "api": "ghcr.io/acme/payments-api:sha-abc123" }
```

```
STEP 2: LOAD CONFIGS
====================
ConfigLoader reads three files:

  (a) deskribe.json --> DeskribeManifest:
      {
        Name: "payments-api",
        Resources: [
          PostgresResource { Type: "postgres", Size: "m" },
          RedisResource { Type: "redis" },
          KafkaMessagingResource { Type: "kafka.messaging",
            Topics: [{ Name: "payments.transactions", Partitions: 6, ... }] }
        ],
        Services: [{
          Env: {
            "ConnectionStrings__Postgres": "@resource(postgres).connectionString",
            "Redis__Endpoint": "@resource(redis).endpoint",
            "Kafka__BootstrapServers": "@resource(kafka.messaging).endpoint"
          },
          Overrides: {
            "prod": { Replicas: 3, Cpu: "500m", Memory: "1Gi" }
          }
        }]
      }

  (b) platform-config/base.json --> PlatformConfig:
      {
        Organization: "acme",
        Defaults: { Runtime: "kubernetes", Region: "westeurope",
                    Replicas: 2, Cpu: "250m", Memory: "512Mi",
                    NamespacePattern: "{app}-{env}" },
        Backends: { "postgres": "pulumi", "redis": "pulumi",
                    "kafka.messaging": "pulumi" },
        Policies: { AllowedRegions: ["westeurope","northeurope"],
                    EnforceTls: true }
      }

  (c) platform-config/envs/prod.json --> EnvironmentConfig:
      {
        Name: "prod",
        Defaults: { Replicas: 3, Cpu: "500m", Memory: "1Gi", Ha: true },
        AlertRouting: { "default": ["slack://#prod-alerts"],
                        "critical": ["pagerduty://oncall", ...] }
      }
```

```
STEP 3: MERGE
=============
MergeEngine.MergeWorkloadPlan() runs the 3-layer merge:

  replicas:  2 (platform) --> 3 (env) --> 3 (dev says 3 too) = 3
  cpu:       "250m" (platform) --> "500m" (env) --> "500m" (dev) = "500m"
  memory:    "512Mi" (platform) --> "1Gi" (env) --> "1Gi" (dev) = "1Gi"
  namespace: "{app}-{env}" --> "payments-api-prod"
  image:     from --image flag, service name "api" matched = "ghcr.io/acme/payments-api:sha-abc123"

  Result: WorkloadPlan {
    AppName:     "payments-api"
    Environment: "prod"
    Namespace:   "payments-api-prod"
    Image:       "ghcr.io/acme/payments-api:sha-abc123"
    Replicas:    3
    Cpu:         "500m"
    Memory:      "1Gi"
    EnvironmentVariables: {
      "ConnectionStrings__Postgres": "@resource(postgres).connectionString",
      "Redis__Endpoint":             "@resource(redis).endpoint",
      "Kafka__BootstrapServers":     "@resource(kafka.messaging).endpoint"
    }
  }
```

```
STEP 4: PLAN RESOURCES
======================
For each resource, the engine calls the provider's PlanAsync:

  (a) PostgresResourceProvider.PlanAsync():
      ResourcePlanResult {
        ResourceType: "postgres"
        Action: "create"
        PlannedOutputs: {
          "connectionString": "Host=payments-api-postgres.payments-api-prod
                               .svc.cluster.local;Port=5432;
                               Database=payments-api;Username=app;
                               Password=<generated>"
          "host": "payments-api-postgres.payments-api-prod.svc.cluster.local"
          "port": "5432"
        }
        Configuration: {
          version: "16", size: "m", ha: true,
          appName: "payments-api", environment: "prod",
          region: "westeurope"
        }
      }

  (b) RedisResourceProvider.PlanAsync():
      ResourcePlanResult {
        ResourceType: "redis"
        Action: "create"
        PlannedOutputs: {
          "endpoint": "<pending:payments-api-redis>"
          "host": "<pending:payments-api-redis-host>"
          "port": "6379"
        }
        Configuration: {
          size: "s", ha: true,
          appName: "payments-api", environment: "prod",
          region: "westeurope"
        }
      }

  (c) KafkaResourceProvider.PlanAsync():
      ResourcePlanResult {
        ResourceType: "kafka.messaging"
        Action: "create"
        PlannedOutputs: {
          "endpoint": "<pending:payments-api-kafka>"
          "bootstrapServers": "<pending:payments-api-kafka-bootstrap>"
        }
        Configuration: {
          appName: "payments-api", environment: "prod",
          region: "westeurope",
          topics: [{ name: "payments.transactions",
                     partitions: 6, retentionHours: 168, ... }]
        }
      }
```

```
STEP 5: APPLY INFRASTRUCTURE
=============================
For each resource plan, the engine looks up the backend and calls ApplyAsync:

  platform.Backends["postgres"] = "pulumi"
  backend = PluginHost.GetBackendAdapter("pulumi") --> PulumiBackendAdapter

  PulumiBackendAdapter.ApplyAsync(plan):
    [Pulumi] Using Local Program mode with project: infra/
    [Pulumi] Running 'pulumi up' for stack payments-api-prod...
    ... (Pulumi provisions Azure resources: Resource Group, PostgreSQL, Redis, etc.)
    [Pulumi] Stack update complete: succeeded

  Returns BackendApplyResult with real resource outputs:
    resourceOutputs = {
      "postgres": {
        "connectionString": "Host=pg-payments-api-prod.postgres.database.azure.com;Port=5432;...",
        "endpoint": "pg-payments-api-prod.postgres.database.azure.com",
        "port": "5432"
      },
      "redis": {
        "endpoint": "redis-payments-api-prod.redis.cache.windows.net:6380",
        "host": "redis-payments-api-prod.redis.cache.windows.net",
        "port": "6380"
      },
      "kafka.messaging": {
        "endpoint": "kafka-payments-api-prod.servicebus.windows.net:9093",
        "bootstrapServers": "kafka-payments-api-prod.servicebus.windows.net:9093"
      }
    }
```

```
STEP 6: RESOLVE REFERENCES
===========================
ResourceReferenceResolver.ResolveReferences() replaces @resource() expressions:

  BEFORE:
    "ConnectionStrings__Postgres" = "@resource(postgres).connectionString"
    "Redis__Endpoint"             = "@resource(redis).endpoint"
    "Kafka__BootstrapServers"     = "@resource(kafka.messaging).endpoint"

  AFTER:
    "ConnectionStrings__Postgres" = "Host=payments-api-postgres.payments-api-prod
                                     .svc.cluster.local;Port=5432;
                                     Database=payments-api;Username=app;
                                     Password=<generated>"
    "Redis__Endpoint"             = "payments-api-redis-master.payments-api-prod
                                     .svc.cluster.local:6379"
    "Kafka__BootstrapServers"     = "payments-api-kafka.payments-api-prod
                                     .svc.cluster.local:9092"
```

```
STEP 7: DEPLOY TO KUBERNETES
=============================
KubernetesRuntimeAdapter.RenderAsync(resolvedWorkload) generates YAML:

  Resource 1: Namespace
    apiVersion: v1
    kind: Namespace
    metadata:
      name: payments-api-prod
      labels:
        app.kubernetes.io/managed-by: deskribe

  Resource 2: Secret
    apiVersion: v1
    kind: Secret
    metadata:
      name: payments-api-env
      namespace: payments-api-prod
    type: Opaque
    stringData:
      ConnectionStrings__Postgres: "Host=payments-api-postgres..."
      Redis__Endpoint: "payments-api-redis-master..."
      Kafka__BootstrapServers: "payments-api-kafka..."

  Resource 3: Deployment
    apiVersion: apps/v1
    kind: Deployment
    metadata:
      name: payments-api
      namespace: payments-api-prod
      labels:
        app: payments-api
        app.kubernetes.io/managed-by: deskribe
    spec:
      replicas: 3
      selector:
        matchLabels:
          app: payments-api
      template:
        spec:
          containers:
          - name: payments-api
            image: ghcr.io/acme/payments-api:sha-abc123
            ports:
            - containerPort: 8080
              name: http
            resources:
              requests:
                cpu: 500m
                memory: 1Gi
              limits:
                cpu: 500m
                memory: 1Gi
            envFrom:
            - secretRef:
                name: payments-api-env

  Resource 4: Service
    apiVersion: v1
    kind: Service
    metadata:
      name: payments-api
      namespace: payments-api-prod
    spec:
      selector:
        app: payments-api
      ports:
      - port: 80
        targetPort: 8080
        name: http

KubernetesRuntimeAdapter.ApplyAsync(manifest):
  For each resource:
    Try to read it from cluster.
    If it exists --> update (patch/replace).
    If 404      --> create.

  [Kubernetes] Created Namespace/payments-api-prod
  [Kubernetes] Created Secret/payments-api-prod/payments-api-env
  [Kubernetes] Created Deployment/payments-api-prod/payments-api
  [Kubernetes] Created Service/payments-api-prod/payments-api
```

```
STEP 8: DONE
=============

  Apply complete for 'payments-api' in 'prod'!

  What now exists:

  +-------------------------------------------------------------------+
  |  Kubernetes Cluster                                               |
  |                                                                   |
  |  Namespace: payments-api-prod                                     |
  |  +-------------------------------------------------------------+ |
  |  |                                                             | |
  |  |  Deployment: payments-api                                   | |
  |  |    Replicas: 3                                              | |
  |  |    Image: ghcr.io/acme/payments-api:sha-abc123              | |
  |  |    CPU: 500m, Memory: 1Gi                                   | |
  |  |    Env from Secret: payments-api-env                        | |
  |  |                                                             | |
  |  |  Secret: payments-api-env                                   | |
  |  |    ConnectionStrings__Postgres = "Host=...;Port=5432;..."   | |
  |  |    Redis__Endpoint = "...-redis-master...:6379"             | |
  |  |    Kafka__BootstrapServers = "...-kafka...:9092"            | |
  |  |                                                             | |
  |  |  Service: payments-api                                      | |
  |  |    :80 --> :8080                                            | |
  |  |                                                             | |
  |  +-------------------------------------------------------------+ |
  |                                                                   |
  |  Infrastructure (provisioned via Pulumi):                         |
  |  +-------------------------------------------------------------+ |
  |  |  Postgres (HA, size m, v16)                                 | |
  |  |  Redis (HA)                                                 | |
  |  |  Kafka (1 topic: payments.transactions, 6 partitions)       | |
  |  +-------------------------------------------------------------+ |
  +-------------------------------------------------------------------+
```

### The Same Manifest, Two Contexts

The power of Deskribe is that the same `deskribe.json` drives both environments:

```
  +---------------------------+     +---------------------------+
  |  LOCAL DEV (Aspire)       |     |  PRODUCTION (Pulumi+K8s) |
  +---------------------------+     +---------------------------+
  |                           |     |                           |
  |  deskribe.json            |     |  deskribe.json            |
  |       |                   |     |       |                   |
  |       v                   |     |       v                   |
  |  Aspire reads manifest    |     |  Engine reads manifest    |
  |       |                   |     |       |                   |
  |       v                   |     |       v                   |
  |  Docker containers:       |     |  Pulumi provisions:       |
  |  - Postgres + PgAdmin     |     |  - Managed Postgres       |
  |  - Redis + RedisInsight   |     |  - Managed Redis          |
  |  - Kafka + Kafka UI       |     |  - Managed Kafka          |
  |       |                   |     |       |                   |
  |       v                   |     |       v                   |
  |  Connection strings       |     |  K8s deployment:          |
  |  injected via Aspire      |     |  - Namespace              |
  |  reference system         |     |  - Secret (conn strings)  |
  |       |                   |     |  - Deployment (3 replicas)|
  |       v                   |     |  - Service                |
  |  App runs on localhost    |     |       |                   |
  |  Dashboard at :15888      |     |       v                   |
  |                           |     |  App runs in cluster      |
  +---------------------------+     +---------------------------+

  ONE FILE. TWO WORLDS. ZERO DRIFT.
```
