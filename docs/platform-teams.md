# Platform Team Integration Guide

How platform/DevOps teams integrate Deskribe with their existing infrastructure.

**The key insight**: Deskribe is NOT replacing your IaC. It is the glue layer --- the
translator between developers and the platform you already built. Think of how Uber
did not invent GPS, digital payments, or cars. It joined them together into a single
experience. Deskribe does the same for infrastructure: your Terraform modules, your
Pulumi programs, your Kubernetes clusters, your policies --- all stay exactly where
they are. Deskribe just gives developers a single JSON file that maps to all of it.

---

## 1. How Deskribe Fits Into Your Stack

```
  DEVELOPER REPO                   PLATFORM CONFIG REPO
  +------------------+             +---------------------+
  | deskribe.json    |             | base.json           |
  |                  |             | envs/               |
  | "I need postgres,|             |   dev.json          |
  |  redis, kafka"   |             |   staging.json      |
  |                  |             |   prod.json         |
  +--------+---------+             |   dr.json           |
           |                       +----------+----------+
           |                                  |
           +-------------+  +----------------+
                         |  |
                         v  v
              +---------------------+
              |   DESKRIBE ENGINE   |
              |                     |
              | 1. Load manifest    |
              | 2. Load platform    |
              | 3. Merge configs    |
              | 4. Validate policy  |
              | 5. Resolve refs     |
              | 6. Plan resources   |
              +----------+----------+
                         |
              +----------+----------+
              |                     |
              v                     v
     +-----------------+   +-----------------+
     | IBackendAdapter |   | IRuntimeAdapter |
     | (your IaC)      |   | (your runtime)  |
     +-----------------+   +-----------------+
              |                     |
              v                     v
     +-----------------+   +-----------------+
     | EXISTING IaC    |   | EXISTING        |
     | REPOS           |   | CLUSTERS        |
     |                 |   |                 |
     | terraform/      |   | K8s clusters    |
     |   modules/      |   | Azure ACA       |
     |   azure-pg/     |   | AWS ECS         |
     |   aws-redis/    |   | GCP Cloud Run   |
     |                 |   |                 |
     | pulumi/         |   |                 |
     |   programs/     |   |                 |
     |   postgres/     |   |                 |
     +-----------------+   +-----------------+
              |                     |
              v                     v
     +------------------------------------+
     |            CLOUD                    |
     |   Azure / AWS / GCP / On-Prem K8s  |
     +------------------------------------+
```

### What Deskribe does NOT do

- It does **not** replace Terraform. It generates inputs (tfvars) and calls your existing modules.
- It does **not** replace Pulumi. It sets stack config and calls your existing programs.
- It does **not** replace Kubernetes. It generates manifests and applies them to your clusters.
- It does **not** own state. Terraform state stays in your backend. Pulumi state stays in your backend.

### What Deskribe DOES do

- Translates developer intent (`"type": "postgres", "size": "m"`) into the right call to your IaC tool.
- Merges organization defaults, environment overrides, and developer preferences.
- Validates manifests against your platform policies before anything gets deployed.
- Resolves resource references (`@resource(postgres).connectionString`) into real values.
- Gives developers a single CLI: `deskribe plan`, `deskribe apply`, `deskribe destroy`.

### Before and after for a platform team

**Before Deskribe:**

```
Platform team's week:
  Mon   Review 3 Terraform PRs from developers (bad variable values, wrong modules)
  Tue   Fix broken deployment --- someone forgot resource limits
  Wed   Answer 12 Slack questions about connection strings and regions
  Thu   Re-implement the same Redis pattern for the 4th team this month
  Fri   Write wiki page about "how to deploy a service" (nobody reads it)
```

**After Deskribe:**

```
Platform team's week:
  Mon   Publish updated base.json with new region policy
  Tue   Add a custom backend adapter for the new managed Kafka service
  Wed   Review one deskribe.json PR: "Looks right, merge it"
  Thu   Platform engineering work --- improving the underlying modules
  Fri   Same
```

Developers stop writing Terraform. They write a 15-line JSON file. Your existing
Terraform modules, Pulumi programs, and Kubernetes clusters remain untouched. Deskribe
is the translation layer.

---

## 2. Setting Up the Platform Config Repository

The platform config repo is owned by the platform team. Developers never modify it.
It defines organizational defaults, per-environment overrides, backend mappings, and policies.

### Directory structure

```
platform-config/
  base.json                 # Organization-wide defaults and backend mappings
  envs/
    dev.json                # Development environment overrides
    staging.json            # Staging environment overrides
    prod.json               # Production environment overrides
    dr.json                 # Disaster recovery environment overrides
```

### Full annotated `base.json`

```json
{
  "organization": "acme",

  "defaults": {
    "runtime": "kubernetes",
    "region": "westeurope",
    "replicas": 2,
    "cpu": "250m",
    "memory": "512Mi",
    "namespacePattern": "{app}-{env}"
  },

  "backends": {
    "postgres": "pulumi",
    "redis": "pulumi",
    "kafka.messaging": "pulumi"
  },

  "policies": {
    "allowedRegions": ["westeurope", "northeurope"],
    "enforceTLS": true
  }
}
```

Field-by-field breakdown:

| Field | Type | Purpose |
|-------|------|---------|
| `organization` | `string` | Your org name. Used for naming, tagging, and namespace prefixes. |
| `defaults.runtime` | `string` | Which `IRuntimeAdapter` to use. Maps to adapter `Name` property (e.g., `"kubernetes"`). |
| `defaults.region` | `string` | Default cloud region for all resources. |
| `defaults.replicas` | `int` | Default replica count for workloads. Developers can override per-env. |
| `defaults.cpu` | `string` | Default CPU request/limit (K8s resource quantity format). |
| `defaults.memory` | `string` | Default memory request/limit (K8s resource quantity format). |
| `defaults.namespacePattern` | `string` | Template for K8s namespace. `{app}` and `{env}` are replaced at plan time. |
| `defaults.ha` | `bool?` | Whether high-availability is enabled. Typically `null` in base (off), `true` in prod. |
| `backends` | `Dictionary<string, string>` | Maps resource types to backend adapter names. See Section 3. |
| `policies.allowedRegions` | `string[]` | Regions developers are allowed to target. Enforced by `PolicyValidator`. |
| `policies.enforceTLS` | `bool` | Whether TLS is mandatory for all resources. |

### Full annotated `envs/dev.json`

```json
{
  "name": "dev",

  "defaults": {
    "replicas": 2,
    "cpu": "250m",
    "memory": "512Mi"
  }
}
```

| Field | Purpose |
|-------|---------|
| `name` | Must match the environment name passed to `--env`. |
| `defaults.*` | Any field here overrides the same field in `base.json` for this environment. |

The dev environment typically mirrors base defaults --- small resources, no HA.

### Full annotated `envs/prod.json`

```json
{
  "name": "prod",

  "defaults": {
    "replicas": 3,
    "cpu": "500m",
    "memory": "1Gi",
    "ha": true
  },

  "alertRouting": {
    "default": ["slack://#prod-alerts"],
    "critical": ["pagerduty://oncall", "slack://#prod-oncall"]
  }
}
```

| Field | Purpose |
|-------|---------|
| `defaults.replicas` | Prod gets more replicas (3 instead of base 2). |
| `defaults.cpu` | Prod gets more CPU. |
| `defaults.memory` | Prod gets more memory. |
| `defaults.ha` | High-availability is enabled in prod. Backends use this to enable replicas, failover, etc. |
| `alertRouting` | Routing rules for alerts. Keyed by severity level. |

### Organizing for multiple environments

A typical enterprise setup:

```
platform-config/
  base.json
  envs/
    dev.json               # Low resources, no HA, fast iteration
    staging.json           # Mirrors prod sizing, HA enabled
    prod.json              # Full production: HA, high resources, alert routing
    dr.json                # Disaster recovery: different region, same prod config
    perf.json              # Performance testing: high resources, no alert routing
```

Example `envs/staging.json`:

```json
{
  "name": "staging",
  "defaults": {
    "replicas": 3,
    "cpu": "500m",
    "memory": "1Gi",
    "ha": true
  }
}
```

Example `envs/dr.json`:

```json
{
  "name": "dr",
  "defaults": {
    "region": "northeurope",
    "replicas": 3,
    "cpu": "500m",
    "memory": "1Gi",
    "ha": true
  }
}
```

### Versioning and managing the config repo

Treat the platform config repo like any other infrastructure code:

1. **Git-managed**: Every change to `base.json` or `envs/*.json` goes through a PR.
2. **Branch protection**: Require at least one platform team reviewer.
3. **CI validation**: Run `deskribe validate` in CI against a reference manifest to catch breaking changes.
4. **Semantic versioning**: Tag releases (`v1.0.0`, `v1.1.0`). Pin the config version in CI pipelines.
5. **Changelog**: Document what changed and why (e.g., "Increased prod default memory to 1Gi due to OOM incidents").

```
# CI pipeline validates platform config changes
deskribe validate \
  -f tests/reference-manifest.json \
  --env dev \
  --platform .
```

---

## 3. Mapping Resources to Backends

The `backends` section in `base.json` is the routing table. It tells Deskribe:
"When a developer asks for resource type X, use backend adapter Y to provision it."

### How the mapping works

```json
{
  "backends": {
    "postgres": "pulumi",
    "redis": "pulumi",
    "kafka.messaging": "pulumi"
  }
}
```

Reading this out loud:

- `"postgres": "pulumi"` --- When a developer writes `{ "type": "postgres" }` in their
  `deskribe.json`, use the **Pulumi backend adapter** to provision it.
- `"redis": "pulumi"` --- Same for Redis.
- `"kafka.messaging": "pulumi"` --- Same for Kafka messaging.

### Mixing backends

You are not limited to one backend. Different resource types can use different IaC tools:

```json
{
  "backends": {
    "postgres": "terraform",
    "redis": "pulumi",
    "kafka.messaging": "terraform"
  }
}
```

This means:
- Postgres is provisioned by your Terraform modules.
- Redis is provisioned by your Pulumi programs.
- Kafka is provisioned by your Terraform modules.

This is common in organizations migrating from one tool to another, or where certain
resources are better supported by specific tools.

### How the engine resolves backends

When `DeskribeEngine.ApplyAsync` runs, it iterates over each resource plan and looks up
the backend:

```
For each ResourcePlan in the DeskribePlan:
    1. Look up: platform.Backends[resourcePlan.ResourceType]
       e.g., "postgres" -> "pulumi"

    2. Get the adapter: pluginHost.GetBackendAdapter("pulumi")
       Returns the registered IBackendAdapter with Name == "pulumi"

    3. Call: backend.ApplyAsync(plan, ct)
       The adapter provisions the resource using its IaC tool

    4. Capture outputs: result.ResourceOutputs
       e.g., { "postgres": { "connectionString": "Host=..." } }
```

```
  deskribe.json                  base.json                  Adapter Registry
  +----------------+             +----------------+         +------------------+
  | resources:     |             | backends:      |         | "pulumi" ->      |
  |  - postgres    +---lookup--->|  postgres:     +--get--->| PulumiBackend    |
  |  - redis       |             |    "pulumi"    |         |   Adapter        |
  |  - kafka       |             |  redis:        |         |                  |
  +----------------+             |    "pulumi"    |         | "terraform" ->   |
                                 |  kafka:        |         | TerraformBackend |
                                 |    "pulumi"    |         |   Adapter        |
                                 +----------------+         +------------------+
```

### Writing a custom backend adapter

Every backend adapter implements the `IBackendAdapter` interface:

```csharp
public interface IBackendAdapter
{
    string Name { get; }
    Task<BackendApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct = default);
    Task DestroyAsync(string appName, string environment, CancellationToken ct = default);
}

public record BackendApplyResult
{
    public bool Success { get; init; }
    public Dictionary<string, Dictionary<string, string>> ResourceOutputs { get; init; } = new();
    public List<string> Errors { get; init; } = [];
}
```

To create a custom adapter that wraps your existing Terraform modules:

```csharp
public class TerraformBackendAdapter : IBackendAdapter
{
    public string Name => "terraform";

    public async Task<BackendApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct)
    {
        var outputs = new Dictionary<string, Dictionary<string, string>>();

        foreach (var resourcePlan in plan.ResourcePlans)
        {
            // 1. Map resource plan to your Terraform module path
            var modulePath = MapToModule(resourcePlan.ResourceType);

            // 2. Generate tfvars from the plan configuration
            var tfvars = GenerateTfVars(plan, resourcePlan);

            // 3. Run terraform init + apply
            var tfOutputs = await RunTerraform(modulePath, tfvars, ct);

            // 4. Capture outputs
            outputs[resourcePlan.ResourceType] = tfOutputs;
        }

        return new BackendApplyResult { Success = true, ResourceOutputs = outputs };
    }

    public async Task DestroyAsync(string appName, string environment, CancellationToken ct)
    {
        // Run terraform destroy against the workspace
        var workspace = $"{appName}-{environment}";
        await RunTerraformDestroy(workspace, ct);
    }
}
```

Register it via a plugin:

```csharp
public class TerraformPlugin : IPlugin
{
    public string Name => "terraform-backend";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterBackendAdapter(new TerraformBackendAdapter());
    }
}
```

---

## 4. Integrating with Existing Terraform Repositories

### Architecture

```
  DeskribePlan
       |
       v
  TerraformBackendAdapter
       |
       |  1. Map resource type to module path
       |  2. Generate terraform.tfvars.json
       |  3. terraform init -backend-config=...
       |  4. terraform apply -auto-approve
       |  5. terraform output -json
       |
       v
  Your Existing Terraform Module
  (e.g., modules/azure-postgres/)
       |
       v
  Cloud Provider API
  (Azure / AWS / GCP)
```

### Concrete example: Azure Database for PostgreSQL

You have an existing Terraform module at `modules/azure-postgres/`:

```hcl
# modules/azure-postgres/variables.tf

variable "app_name" {
  type        = string
  description = "Application name"
}

variable "environment" {
  type        = string
  description = "Environment (dev, staging, prod)"
}

variable "region" {
  type        = string
  description = "Azure region"
  default     = "westeurope"
}

variable "sku_name" {
  type        = string
  description = "Azure Flexible Server SKU"
  default     = "B_Standard_B1ms"
}

variable "storage_mb" {
  type        = number
  description = "Storage in MB"
  default     = 32768
}

variable "ha_enabled" {
  type        = bool
  description = "Enable high availability"
  default     = false
}

variable "postgres_version" {
  type        = string
  description = "PostgreSQL version"
  default     = "16"
}
```

```hcl
# modules/azure-postgres/main.tf

resource "azurerm_postgresql_flexible_server" "this" {
  name                   = "${var.app_name}-${var.environment}-pg"
  resource_group_name    = data.azurerm_resource_group.rg.name
  location               = var.region
  version                = var.postgres_version
  sku_name               = var.sku_name
  storage_mb             = var.storage_mb
  administrator_login    = "pgadmin"
  administrator_password = random_password.pg_password.result

  dynamic "high_availability" {
    for_each = var.ha_enabled ? [1] : []
    content {
      mode = "ZoneRedundant"
    }
  }
}

resource "azurerm_postgresql_flexible_server_database" "db" {
  name      = "${var.app_name}-db"
  server_id = azurerm_postgresql_flexible_server.this.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}
```

```hcl
# modules/azure-postgres/outputs.tf

output "connection_string" {
  value     = "Host=${azurerm_postgresql_flexible_server.this.fqdn};Port=5432;Database=${azurerm_postgresql_flexible_server_database.db.name};Username=${azurerm_postgresql_flexible_server.this.administrator_login};Password=${random_password.pg_password.result}"
  sensitive = true
}

output "host" {
  value = azurerm_postgresql_flexible_server.this.fqdn
}

output "port" {
  value = "5432"
}
```

### The Terraform adapter translates DeskribePlan to tfvars

```csharp
public class TerraformBackendAdapter : IBackendAdapter
{
    private readonly string _modulesRoot;

    public TerraformBackendAdapter(string modulesRoot)
    {
        _modulesRoot = modulesRoot;  // e.g., "/infra/terraform/modules"
    }

    public string Name => "terraform";

    public async Task<BackendApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct)
    {
        var outputs = new Dictionary<string, Dictionary<string, string>>();

        foreach (var resourcePlan in plan.ResourcePlans)
        {
            // Step 1: Map resource type to Terraform module
            var modulePath = Path.Combine(_modulesRoot, MapResourceToModule(resourcePlan.ResourceType));
            //  "postgres" -> "/infra/terraform/modules/azure-postgres"

            // Step 2: Generate tfvars from DeskribePlan
            var tfvars = new Dictionary<string, object>
            {
                ["app_name"] = plan.AppName,
                ["environment"] = plan.Environment,
                ["region"] = plan.Platform.Defaults.Region
            };

            // Map Deskribe size to Terraform SKU
            if (resourcePlan.Configuration.TryGetValue("sku", out var sku))
                tfvars["sku_name"] = sku?.ToString() ?? "B_Standard_B1ms";

            // Map HA from environment config
            if (plan.EnvironmentConfig.Defaults.Ha == true)
                tfvars["ha_enabled"] = true;

            var tfvarsJson = JsonSerializer.Serialize(tfvars);
            var tfvarsPath = Path.Combine(modulePath, "terraform.tfvars.json");
            await File.WriteAllTextAsync(tfvarsPath, tfvarsJson, ct);

            // Step 3: terraform init
            await RunProcess("terraform", $"init -backend-config=key={plan.AppName}/{plan.Environment}/{resourcePlan.ResourceType}", modulePath, ct);

            // Step 4: terraform apply
            await RunProcess("terraform", "apply -auto-approve", modulePath, ct);

            // Step 5: Capture outputs
            var outputJson = await RunProcess("terraform", "output -json", modulePath, ct);
            var tfOutputs = ParseTerraformOutputs(outputJson);
            outputs[resourcePlan.ResourceType] = tfOutputs;
        }

        return new BackendApplyResult { Success = true, ResourceOutputs = outputs };
    }

    public async Task DestroyAsync(string appName, string environment, CancellationToken ct)
    {
        // Iterate known modules and destroy each workspace
        foreach (var moduleDir in Directory.GetDirectories(_modulesRoot))
        {
            await RunProcess("terraform", "destroy -auto-approve", moduleDir, ct);
        }
    }

    private static string MapResourceToModule(string resourceType) => resourceType switch
    {
        "postgres" => "azure-postgres",
        "redis" => "azure-redis",
        "kafka.messaging" => "azure-eventhubs",  // or Confluent, etc.
        _ => throw new NotSupportedException($"No Terraform module for resource type: {resourceType}")
    };
}
```

### The complete flow

```
  Developer writes:                Platform base.json says:
  { "type": "postgres",           { "backends": {
    "size": "m" }                      "postgres": "terraform" } }
       |                                       |
       v                                       v
  DeskribeEngine.PlanAsync()
       |
       |  Merges: size "m" + env defaults + platform defaults
       |  Produces ResourcePlanResult:
       |    ResourceType: "postgres"
       |    Action: "create"
       |    Configuration: { sku: "B_Standard_B2s", ha: false, version: "16" }
       |
       v
  DeskribeEngine.ApplyAsync()
       |
       |  Looks up: backends["postgres"] -> "terraform"
       |  Gets adapter: pluginHost.GetBackendAdapter("terraform")
       |
       v
  TerraformBackendAdapter.ApplyAsync()
       |
       |  1. Module path: modules/azure-postgres/
       |  2. Generates terraform.tfvars.json:
       |     { "app_name": "payments-api",
       |       "environment": "dev",
       |       "region": "westeurope",
       |       "sku_name": "B_Standard_B2s",
       |       "ha_enabled": false }
       |  3. terraform init
       |  4. terraform apply -auto-approve
       |  5. terraform output -json
       |
       v
  Returns BackendApplyResult:
    ResourceOutputs: {
      "postgres": {
        "connectionString": "Host=payments-api-dev-pg.postgres.database.azure.com;...",
        "host": "payments-api-dev-pg.postgres.database.azure.com",
        "port": "5432"
      }
    }
       |
       v
  ResourceReferenceResolver resolves:
    @resource(postgres).connectionString
    -> "Host=payments-api-dev-pg.postgres.database.azure.com;..."
       |
       v
  K8s Secret created with the real connection string
```

---

## 5. Integrating with Existing Pulumi Programs

The Pulumi backend adapter uses the [Pulumi Automation API](https://www.pulumi.com/docs/using-pulumi/automation-api/)
to call your existing Pulumi programs programmatically. The pattern mirrors Terraform
but uses `Pulumi.Automation.LocalWorkspace` instead of CLI calls.

### Architecture

```
  DeskribePlan
       |
       v
  PulumiBackendAdapter
       |
       |  1. Map resource type to Pulumi program path
       |  2. Set stack config from DeskribePlan
       |  3. pulumi up (via Automation API)
       |  4. Capture stack outputs
       |
       v
  Your Existing Pulumi Program
  (e.g., infra/pulumi/postgres/)
       |
       v
  Cloud Provider API
```

### Pulumi adapter using Automation API

```csharp
using Pulumi.Automation;

public class PulumiBackendAdapter : IBackendAdapter
{
    private readonly string _programsRoot;

    public PulumiBackendAdapter(string programsRoot)
    {
        _programsRoot = programsRoot;  // e.g., "/infra/pulumi/programs"
    }

    public string Name => "pulumi";

    public async Task<BackendApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct)
    {
        var outputs = new Dictionary<string, Dictionary<string, string>>();

        foreach (var resourcePlan in plan.ResourcePlans)
        {
            // Step 1: Locate the Pulumi program
            var programPath = Path.Combine(_programsRoot, MapResourceToProgram(resourcePlan.ResourceType));

            // Step 2: Create or select the stack
            var stackName = $"{plan.AppName}-{plan.Environment}";
            var stack = await LocalWorkspace.CreateOrSelectStackAsync(new LocalProgramArgs(stackName, programPath));

            // Step 3: Set config from the Deskribe plan
            await stack.SetConfigAsync("appName", new ConfigValue(plan.AppName));
            await stack.SetConfigAsync("environment", new ConfigValue(plan.Environment));
            await stack.SetConfigAsync("region", new ConfigValue(plan.Platform.Defaults.Region));

            if (resourcePlan.Configuration.TryGetValue("sku", out var sku))
                await stack.SetConfigAsync("sku", new ConfigValue(sku?.ToString() ?? ""));

            if (plan.EnvironmentConfig.Defaults.Ha == true)
                await stack.SetConfigAsync("ha", new ConfigValue("true"));

            // Step 4: pulumi up
            var upResult = await stack.UpAsync(new UpOptions { OnStandardOutput = Console.WriteLine });

            // Step 5: Capture outputs
            var stackOutputs = upResult.Outputs;
            var resourceOutputs = new Dictionary<string, string>();

            foreach (var (key, output) in stackOutputs)
            {
                resourceOutputs[key] = output.Value?.ToString() ?? "";
            }

            outputs[resourcePlan.ResourceType] = resourceOutputs;
        }

        return new BackendApplyResult { Success = true, ResourceOutputs = outputs };
    }

    public async Task DestroyAsync(string appName, string environment, CancellationToken ct)
    {
        var stackName = $"{appName}-{environment}";
        // Destroy each program's stack
        foreach (var programDir in Directory.GetDirectories(_programsRoot))
        {
            try
            {
                var stack = await LocalWorkspace.SelectStackAsync(new LocalProgramArgs(stackName, programDir));
                await stack.DestroyAsync(new DestroyOptions { OnStandardOutput = Console.WriteLine });
            }
            catch (Exception) { /* Stack may not exist for this program */ }
        }
    }

    private static string MapResourceToProgram(string resourceType) => resourceType switch
    {
        "postgres" => "postgres",
        "redis" => "redis",
        "kafka.messaging" => "kafka",
        _ => throw new NotSupportedException($"No Pulumi program for: {resourceType}")
    };
}
```

### The complete Pulumi flow

```
  DeskribePlan (for payments-api, prod)
       |
       v
  PulumiBackendAdapter.ApplyAsync()
       |
       |  Stack: "payments-api-prod"
       |  Program: infra/pulumi/programs/postgres/
       |
       |  Config set:
       |    appName     = "payments-api"
       |    environment = "prod"
       |    region      = "westeurope"
       |    sku         = "GP_Standard_D2s_v3"
       |    ha          = "true"
       |
       |  pulumi up
       |
       v
  Stack outputs captured:
    connectionString = "Host=payments-api-prod.postgres.database.azure.com;..."
    host             = "payments-api-prod.postgres.database.azure.com"
    port             = "5432"
       |
       v
  Returned as BackendApplyResult.ResourceOutputs["postgres"]
```

---

## 6. Runtime Integration --- Kubernetes

The built-in `KubernetesRuntimeAdapter` uses the official
[KubernetesClient C# SDK](https://github.com/kubernetes-client/csharp) (`k8s` NuGet
package) to generate and apply Kubernetes resources.

### What the K8s adapter generates

For every workload, the adapter generates four resources:

```
  WorkloadPlan
       |
       v
  KubernetesRuntimeAdapter.RenderAsync()
       |
       +---> Namespace       (e.g., payments-api-prod)
       |       Labels: app.kubernetes.io/managed-by = deskribe
       |
       +---> Secret           (e.g., payments-api-env)
       |       Contains resolved env vars as stringData
       |       Type: Opaque
       |
       +---> Deployment       (e.g., payments-api)
       |       Replicas from merged config (e.g., 3)
       |       CPU/memory requests and limits
       |       Container image from --image flag
       |       EnvFrom: SecretRef -> payments-api-env
       |
       +---> Service          (e.g., payments-api)
               Port 80 -> 8080 (containerPort)
               Selector: app = payments-api
```

### How the adapter applies resources

The adapter uses a create-or-update pattern. For each resource:

```
  Try to read the existing resource
       |
       +-- Found?  -> Replace/Patch it (update)
       |
       +-- NotFound (404)?  -> Create it
```

This is idempotent. Running `deskribe apply` multiple times is safe.

### Extending the K8s adapter

To add Ingress, NetworkPolicy, or other resources, extend the adapter:

```csharp
public class ExtendedKubernetesRuntimeAdapter : KubernetesRuntimeAdapter
{
    public override async Task<WorkloadManifest> RenderAsync(WorkloadPlan workload, CancellationToken ct)
    {
        // Get the base manifest (Namespace, Secret, Deployment, Service)
        var baseManifest = await base.RenderAsync(workload, ct);

        // Add an Ingress resource
        var ingress = new V1Ingress
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "Ingress",
            Metadata = new V1ObjectMeta
            {
                Name = workload.AppName,
                NamespaceProperty = workload.Namespace,
                Annotations = new Dictionary<string, string>
                {
                    ["nginx.ingress.kubernetes.io/ssl-redirect"] = "true"
                }
            },
            Spec = new V1IngressSpec
            {
                IngressClassName = "nginx",
                Rules = new List<V1IngressRule>
                {
                    new()
                    {
                        Host = $"{workload.AppName}.{workload.Environment}.yourdomain.com",
                        Http = new V1HTTPIngressRuleValue
                        {
                            Paths = new List<V1HTTPIngressPath>
                            {
                                new()
                                {
                                    Path = "/",
                                    PathType = "Prefix",
                                    Backend = new V1IngressBackend
                                    {
                                        Service = new V1IngressServiceBackend
                                        {
                                            Name = workload.AppName,
                                            Port = new V1ServiceBackendPort { Number = 80 }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        // Add NetworkPolicy
        var networkPolicy = new V1NetworkPolicy
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = new V1ObjectMeta
            {
                Name = $"{workload.AppName}-default",
                NamespaceProperty = workload.Namespace
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string> { ["app"] = workload.AppName }
                },
                PolicyTypes = new List<string> { "Ingress" },
                Ingress = new List<V1NetworkPolicyIngressRule>
                {
                    new()
                    {
                        Ports = new List<V1NetworkPolicyPort>
                        {
                            new() { Port = 8080, Protocol = "TCP" }
                        }
                    }
                }
            }
        };

        // Combine YAML
        var additionalYaml = string.Join("---\n",
            KubernetesYaml.Serialize(ingress),
            KubernetesYaml.Serialize(networkPolicy));

        return baseManifest with
        {
            Yaml = baseManifest.Yaml + "---\n" + additionalYaml,
            ResourceNames = [.. baseManifest.ResourceNames,
                $"Ingress/{workload.Namespace}/{workload.AppName}",
                $"NetworkPolicy/{workload.Namespace}/{workload.AppName}-default"]
        };
    }
}
```

---

## 7. Runtime Integration --- Azure Container Apps

For organizations using Azure Container Apps instead of Kubernetes, you implement a
custom `IRuntimeAdapter`.

### What a custom Azure Container Apps adapter looks like

```csharp
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Azure.Identity;

public class AzureContainerAppsRuntimeAdapter : IRuntimeAdapter
{
    private readonly string _subscriptionId;
    private readonly string _resourceGroupName;
    private readonly string _environmentName;

    public AzureContainerAppsRuntimeAdapter(
        string subscriptionId,
        string resourceGroupName,
        string environmentName)
    {
        _subscriptionId = subscriptionId;
        _resourceGroupName = resourceGroupName;
        _environmentName = environmentName;
    }

    public string Name => "azure-container-apps";

    public Task<WorkloadManifest> RenderAsync(WorkloadPlan workload, CancellationToken ct)
    {
        // For ACA, "render" means preparing the ARM resource definition
        // We return a placeholder YAML since the real deployment is via SDK
        var manifest = new WorkloadManifest
        {
            Namespace = workload.Namespace,
            Yaml = $"# Azure Container App: {workload.AppName}",
            ResourceNames = [$"ContainerApp/{workload.AppName}"]
        };
        return Task.FromResult(manifest);
    }

    public async Task ApplyAsync(WorkloadManifest manifest, CancellationToken ct)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        var subscription = client.GetSubscriptionResource(
            new Azure.Core.ResourceIdentifier($"/subscriptions/{_subscriptionId}"));
        var resourceGroup = (await subscription.GetResourceGroupAsync(_resourceGroupName, ct)).Value;

        // Map WorkloadPlan to Container App resource
        var containerApps = resourceGroup.GetContainerApps();
        var appData = new ContainerAppData(new Azure.Core.AzureLocation("westeurope"))
        {
            ManagedEnvironmentId = new Azure.Core.ResourceIdentifier(
                $"/subscriptions/{_subscriptionId}/resourceGroups/{_resourceGroupName}" +
                $"/providers/Microsoft.App/managedEnvironments/{_environmentName}"),
            Template = new ContainerAppTemplate
            {
                // Containers, scale rules, secrets configured here
                // from the WorkloadPlan data
            }
        };

        // Create or update the Container App
        await containerApps.CreateOrUpdateAsync(
            Azure.WaitUntil.Completed,
            manifest.ResourceNames.First().Split('/').Last(),
            appData,
            ct);
    }

    public async Task DestroyAsync(string namespaceName, CancellationToken ct)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        // Delete the container app resource
    }
}
```

### Mapping WorkloadPlan to Container App concepts

```
  WorkloadPlan                        Azure Container App
  +-------------------+               +----------------------------+
  | AppName           +-------------->| Container App name         |
  | Replicas: 3       +-------------->| Scale: min=3, max=3        |
  | Cpu: "500m"       +-------------->| Container CPU: 0.5         |
  | Memory: "1Gi"     +-------------->| Container Memory: 1.0Gi    |
  | Image             +-------------->| Container image            |
  | EnvironmentVars   +-------------->| Secrets + env references   |
  +-------------------+               +----------------------------+
```

Register it:

```csharp
public class AzureContainerAppsPlugin : IPlugin
{
    public string Name => "azure-container-apps-runtime";

    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterRuntimeAdapter(
            new AzureContainerAppsRuntimeAdapter(
                subscriptionId: Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID")!,
                resourceGroupName: "my-rg",
                environmentName: "my-aca-env"));
    }
}
```

Then set the runtime in your platform config:

```json
{
  "defaults": {
    "runtime": "azure-container-apps"
  }
}
```

---

## 8. Runtime Integration --- AWS ECS

For organizations running on AWS Elastic Container Service (ECS), you implement a
custom `IRuntimeAdapter` using the `AWSSDK.ECS` package.

### What a custom AWS ECS adapter looks like

```csharp
using Amazon.ECS;
using Amazon.ECS.Model;

public class AwsEcsRuntimeAdapter : IRuntimeAdapter
{
    private readonly string _clusterName;
    private readonly string _vpcSubnets;
    private readonly string _securityGroup;

    public AwsEcsRuntimeAdapter(string clusterName, string vpcSubnets, string securityGroup)
    {
        _clusterName = clusterName;
        _vpcSubnets = vpcSubnets;
        _securityGroup = securityGroup;
    }

    public string Name => "aws-ecs";

    public Task<WorkloadManifest> RenderAsync(WorkloadPlan workload, CancellationToken ct)
    {
        var manifest = new WorkloadManifest
        {
            Namespace = workload.Namespace,
            Yaml = $"# ECS Task Definition + Service: {workload.AppName}",
            ResourceNames = [
                $"TaskDefinition/{workload.AppName}",
                $"Service/{workload.AppName}"]
        };
        return Task.FromResult(manifest);
    }

    public async Task ApplyAsync(WorkloadManifest manifest, CancellationToken ct)
    {
        var client = new AmazonECSClient();

        // Parse workload from context (in practice, pass it through or store it)
        var appName = manifest.ResourceNames.First().Split('/').Last();

        // Step 1: Register task definition
        var taskDefResponse = await client.RegisterTaskDefinitionAsync(new RegisterTaskDefinitionRequest
        {
            Family = appName,
            NetworkMode = NetworkMode.Awsvpc,
            RequiresCompatibilities = ["FARGATE"],
            Cpu = "256",      // mapped from WorkloadPlan.Cpu
            Memory = "512",   // mapped from WorkloadPlan.Memory
            ContainerDefinitions =
            [
                new ContainerDefinition
                {
                    Name = appName,
                    Image = "nginx:latest",  // from WorkloadPlan.Image
                    Essential = true,
                    PortMappings =
                    [
                        new PortMapping
                        {
                            ContainerPort = 8080,
                            Protocol = TransportProtocol.Tcp
                        }
                    ]
                    // Environment variables from WorkloadPlan.EnvironmentVariables
                    // mapped to KeyValuePair list
                }
            ]
        }, ct);

        // Step 2: Create or update ECS service
        try
        {
            await client.UpdateServiceAsync(new UpdateServiceRequest
            {
                Cluster = _clusterName,
                Service = appName,
                TaskDefinition = taskDefResponse.TaskDefinition.TaskDefinitionArn,
                DesiredCount = 2  // from WorkloadPlan.Replicas
            }, ct);
        }
        catch (ServiceNotFoundException)
        {
            await client.CreateServiceAsync(new CreateServiceRequest
            {
                Cluster = _clusterName,
                ServiceName = appName,
                TaskDefinition = taskDefResponse.TaskDefinition.TaskDefinitionArn,
                DesiredCount = 2,
                LaunchType = LaunchType.FARGATE,
                NetworkConfiguration = new NetworkConfiguration
                {
                    AwsvpcConfiguration = new AwsVpcConfiguration
                    {
                        Subnets = [.. _vpcSubnets.Split(',')],
                        SecurityGroups = [_securityGroup],
                        AssignPublicIp = AssignPublicIp.DISABLED
                    }
                }
            }, ct);
        }
    }

    public async Task DestroyAsync(string namespaceName, CancellationToken ct)
    {
        var client = new AmazonECSClient();
        await client.UpdateServiceAsync(new UpdateServiceRequest
        {
            Cluster = _clusterName,
            Service = namespaceName,
            DesiredCount = 0
        }, ct);
        await client.DeleteServiceAsync(new DeleteServiceRequest
        {
            Cluster = _clusterName,
            Service = namespaceName
        }, ct);
    }
}
```

### Mapping WorkloadPlan to ECS concepts

```
  WorkloadPlan                      AWS ECS
  +-------------------+              +----------------------------+
  | AppName           +------------->| Task family + Service name |
  | Replicas: 3       +------------->| desiredCount: 3            |
  | Cpu: "500m"       +------------->| cpu: "512" (Fargate units) |
  | Memory: "1Gi"     +------------->| memory: "1024" (MB)        |
  | Image             +------------->| containerDefinition.image  |
  | EnvironmentVars   +------------->| containerDefinition.env    |
  +-------------------+              +----------------------------+
```

Note the unit conversion: Kubernetes uses `500m` (millicores) and `1Gi`, while
ECS Fargate uses `512` (CPU units) and `1024` (MB). Your adapter handles this mapping.

---

## 9. Policies and Governance

Policies are the platform team's guardrails. They prevent developers from deploying
resources that violate organizational standards.

### How policies work

The `PolicyValidator` in Deskribe validates developer manifests against the platform
config before any infrastructure is touched.

```
  Developer runs:  deskribe validate --env prod --platform ./platform-config
       |
       v
  PolicyValidator.Validate(manifest, platformConfig)
       |
       +---> Is manifest.Name set?
       |       Error if missing: "Manifest 'name' is required"
       |
       +---> For each resource:
       |       Does platform.Backends have a mapping for this resource type?
       |       Warning if not: "Resource type 'xyz' has no configured backend"
       |
       +---> For each service env var:
       |       If it uses @resource(type), does that resource type exist
       |       in the manifest's resources array?
       |       Error if not: "references resource type 'xyz' which is not declared"
       |
       +---> Resource-specific validation (via IResourceProvider.ValidateAsync):
               Postgres: Is size valid? Is version supported?
               Kafka: Do all topics have >= minimum partitions?
               Redis: Is maxMemoryMb within allowed range?
```

### The `policies` section in platform config

```json
{
  "policies": {
    "allowedRegions": ["westeurope", "northeurope"],
    "enforceTLS": true
  }
}
```

### Extending policies

You can extend the `PolicyValidator` or add validation logic in your custom
resource providers. Here are common policy patterns:

**Enforce allowed regions:**

```csharp
// In a custom PolicyValidator extension
if (platform.Policies.AllowedRegions.Count > 0)
{
    var requestedRegion = platform.Defaults.Region;
    if (!platform.Policies.AllowedRegions.Contains(requestedRegion))
    {
        errors.Add($"Region '{requestedRegion}' is not in the allowed list: " +
            string.Join(", ", platform.Policies.AllowedRegions));
    }
}
```

**Enforce maximum resource sizes:**

```csharp
// In your PostgresResourceProvider.ValidateAsync
var allowedSizes = new HashSet<string> { "xs", "s", "m", "l" };
if (postgres.Size is not null && !allowedSizes.Contains(postgres.Size))
{
    return ValidationResult.Invalid(
        $"Postgres size '{postgres.Size}' is not allowed. " +
        $"Allowed sizes: {string.Join(", ", allowedSizes)}. " +
        "Contact platform team for xl provisioning.");
}
```

**Enforce TLS everywhere:**

```csharp
if (platform.Policies.EnforceTls)
{
    // Ensure all connection strings will use SSL
    // Backend adapters should set sslmode=require in connection strings
}
```

**Enforce minimum Kafka partitions:**

```csharp
// In your KafkaResourceProvider.ValidateAsync
foreach (var topic in kafkaResource.Topics)
{
    if (topic.Partitions.HasValue && topic.Partitions.Value < 3)
    {
        errors.Add($"Topic '{topic.Name}' has {topic.Partitions} partitions. Minimum is 3.");
    }
}
```

### Examples of common policies

| Policy | Implementation | Error message |
|--------|---------------|---------------|
| Allowed regions | Check `platform.Policies.AllowedRegions` | "Region 'us-east-1' is not in the allowed list" |
| Enforce TLS | Check `platform.Policies.EnforceTls` | "TLS is required for all resources in this organization" |
| Max replicas in dev | Check env + replicas | "Dev environment is limited to 3 replicas. Requested: 10" |
| Required resource tags | Check resource metadata | "All resources must have 'cost-center' tag" |
| Blocked resource types | Check resource type against deny list | "Resource type 'gpu-cluster' is not available in this org" |
| Max Kafka partitions | Check topic config | "Topic partitions cannot exceed 24 in non-prod environments" |
| Required HA in prod | Check env + HA flag | "High availability must be enabled for production workloads" |

---

## 10. SKU/Size Mapping

Developers use abstract sizes (`xs`, `s`, `m`, `l`, `xl`) in their manifests. The
platform team controls what those sizes mean in each cloud provider.

### How the mapping works

When a developer writes:

```json
{ "type": "postgres", "size": "m" }
```

The resource provider (e.g., `PostgresResourceProvider`) maps `"m"` to a concrete SKU
based on the target backend. This mapping lives inside the backend adapter or resource
provider --- NOT in the developer's manifest.

### Size mapping examples

```
  Developer size    Azure                      AWS                    K8s (Helm)
  +----------+------+--------------------------+----------------------+------------------+
  | xs       |      | B_Standard_B1ms          | db.t3.micro          | 256Mi / 0.25 CPU |
  | s        |      | B_Standard_B1ms          | db.t3.small          | 512Mi / 0.5 CPU  |
  | m        |      | B_Standard_B2s           | db.t3.medium         | 2Gi / 1 CPU      |
  | l        |      | GP_Standard_D4s_v3       | db.r6g.large         | 4Gi / 2 CPU      |
  | xl       |      | GP_Standard_D8s_v3       | db.r6g.xlarge        | 8Gi / 4 CPU      |
  +----------+------+--------------------------+----------------------+------------------+
```

### Implementing size mapping in a resource provider

```csharp
public class PostgresResourceProvider : IResourceProvider
{
    public string ResourceType => "postgres";

    public Task<ResourcePlanResult> PlanAsync(DeskribeResource resource, PlanContext ctx, CancellationToken ct)
    {
        var postgres = (PostgresResource)resource;

        // Map abstract size to concrete SKU based on backend
        var backendName = ctx.Platform.Backends.GetValueOrDefault("postgres", "pulumi");
        var sku = MapSizeToSku(postgres.Size ?? "s", backendName, ctx.Environment);

        // Determine HA from environment config
        var ha = ctx.EnvironmentConfig.Defaults.Ha ?? false;

        return Task.FromResult(new ResourcePlanResult
        {
            ResourceType = "postgres",
            Action = "create",
            Configuration = new Dictionary<string, object?>
            {
                ["sku"] = sku,
                ["version"] = postgres.Version ?? "16",
                ["ha"] = ha
            },
            PlannedOutputs = new Dictionary<string, string>
            {
                ["connectionString"] = $"Host={ctx.AppName}-{ctx.Environment}-pg;Port=5432;Database={ctx.AppName}-db",
                ["host"] = $"{ctx.AppName}-{ctx.Environment}-pg",
                ["port"] = "5432"
            }
        });
    }

    private static string MapSizeToSku(string size, string backend, string environment) =>
        (size, backend) switch
        {
            ("xs", "terraform") => "B_Standard_B1ms",     // Azure via Terraform
            ("s",  "terraform") => "B_Standard_B1ms",
            ("m",  "terraform") => "B_Standard_B2s",
            ("l",  "terraform") => "GP_Standard_D4s_v3",
            ("xl", "terraform") => "GP_Standard_D8s_v3",

            ("xs", "pulumi")    => "B_Standard_B1ms",     // Azure via Pulumi
            ("s",  "pulumi")    => "B_Standard_B1ms",
            ("m",  "pulumi")    => "B_Standard_B2s",
            ("l",  "pulumi")    => "GP_Standard_D4s_v3",
            ("xl", "pulumi")    => "GP_Standard_D8s_v3",

            _ => "B_Standard_B1ms"  // Safe default
        };
}
```

### Redis size mapping

```
  Developer size    Azure Cache              AWS ElastiCache        K8s (Helm)
  +----------+------+------------------------+----------------------+------------------+
  | xs       |      | C0 (250MB)             | cache.t3.micro       | 128Mi            |
  | s        |      | C1 (1GB)               | cache.t3.small       | 256Mi            |
  | m        |      | C2 (2.5GB)             | cache.m6g.large      | 1Gi              |
  | l        |      | C3 (6GB)               | cache.m6g.xlarge     | 4Gi              |
  | xl       |      | P1 (6GB, Premium)      | cache.r6g.xlarge     | 8Gi              |
  +----------+------+------------------------+----------------------+------------------+
```

### Platform teams control the mapping

The size-to-SKU mapping is entirely in platform team code (resource providers and
backend adapters). Developers never see SKU names. They write `"size": "m"` and the
platform decides what that means for their cloud provider. This is a deliberate design
choice --- it allows platform teams to change cloud providers or resize tiers without
any developer changes.

---

## 11. Multi-Cloud Strategy

The same `deskribe.json` can deploy to different clouds depending on which environment
is targeted. The platform config per environment controls everything.

### Example: dev on local K8s, staging on Azure, prod on AWS

The developer's `deskribe.json` stays the same regardless of cloud:

```json
{
  "name": "payments-api",
  "resources": [
    { "type": "postgres", "size": "m" },
    { "type": "redis" }
  ],
  "services": [
    {
      "env": {
        "ConnectionStrings__Postgres": "@resource(postgres).connectionString",
        "Redis__Endpoint": "@resource(redis).endpoint"
      }
    }
  ]
}
```

The platform config controls the target per environment:

**`base.json` --- organization defaults (used when env doesn't override)**

```json
{
  "organization": "acme",
  "defaults": {
    "runtime": "kubernetes",
    "region": "westeurope",
    "replicas": 2,
    "cpu": "250m",
    "memory": "512Mi",
    "namespacePattern": "{app}-{env}"
  },
  "backends": {
    "postgres": "pulumi",
    "redis": "pulumi"
  },
  "policies": {
    "allowedRegions": ["westeurope", "northeurope", "us-east-1", "us-west-2"],
    "enforceTLS": true
  }
}
```

**`envs/dev.json` --- local Kubernetes**

```json
{
  "name": "dev",
  "defaults": {
    "runtime": "kubernetes",
    "region": "local",
    "replicas": 1,
    "cpu": "100m",
    "memory": "256Mi"
  }
}
```

Uses the `kubernetes` runtime adapter, deploying to a local kind/minikube/Docker Desktop cluster. Backend adapters for dev might use Helm charts to deploy Postgres/Redis as containers.

**`envs/staging.json` --- Azure**

```json
{
  "name": "staging",
  "defaults": {
    "runtime": "azure-container-apps",
    "region": "westeurope",
    "replicas": 2,
    "cpu": "500m",
    "memory": "1Gi",
    "ha": false
  }
}
```

Uses the `azure-container-apps` runtime adapter. Backend adapters provision Azure Database for PostgreSQL and Azure Cache for Redis via Terraform.

**`envs/prod.json` --- AWS**

```json
{
  "name": "prod",
  "defaults": {
    "runtime": "aws-ecs",
    "region": "us-east-1",
    "replicas": 3,
    "cpu": "500m",
    "memory": "1Gi",
    "ha": true
  },
  "alertRouting": {
    "default": ["slack://#prod-alerts"],
    "critical": ["pagerduty://oncall"]
  }
}
```

Uses the `aws-ecs` runtime adapter. Backend adapters provision RDS PostgreSQL and ElastiCache Redis via Terraform.

### How it works at deploy time

```
  deskribe apply --env dev --platform ./platform-config
       |
       |  Loads envs/dev.json -> runtime = "kubernetes"
       |  Backend: "pulumi" -> deploys Postgres/Redis via Helm
       |  Runtime: "kubernetes" -> generates K8s YAML, applies to local cluster
       v
  Result: Postgres + Redis containers on local K8s

  deskribe apply --env staging --platform ./platform-config
       |
       |  Loads envs/staging.json -> runtime = "azure-container-apps"
       |  Backend: "pulumi" -> provisions Azure DB for PostgreSQL, Azure Cache
       |  Runtime: "azure-container-apps" -> deploys as Azure Container App
       v
  Result: Managed Azure resources + Container App

  deskribe apply --env prod --platform ./platform-config
       |
       |  Loads envs/prod.json -> runtime = "aws-ecs"
       |  Backend: "pulumi" -> provisions RDS PostgreSQL, ElastiCache
       |  Runtime: "aws-ecs" -> deploys as ECS Fargate service
       v
  Result: Managed AWS resources + ECS service, HA enabled
```

The developer ran the same command three times with different `--env` flags.
They did not change their `deskribe.json`. The platform config determined everything.

---

## 12. Security Considerations

### How secrets flow

Secrets (connection strings, passwords, API keys) flow through Deskribe but are never
stored in developer manifests.

```
  Backend adapter provisions resource
       |
       v
  Resource outputs captured (e.g., connectionString)
       |   These are in-memory only during the apply phase
       v
  ResourceReferenceResolver resolves @resource() expressions
       |   Replaces placeholders with real values
       v
  Resolved env vars passed to IRuntimeAdapter
       |
       v
  Runtime adapter creates K8s Secret / ECS Secret / ACA Secret
       |   Secrets are created in the target platform's native secret store
       v
  Workload references the secret via envFrom / secretRef
       |   The application reads env vars at runtime
       v
  Application gets connection string as an environment variable
```

**What is NOT in the developer manifest:**

```json
{
  "env": {
    "ConnectionStrings__Postgres": "@resource(postgres).connectionString"
  }
}
```

This is a **reference**, not a value. The real connection string with the password
only exists:
1. In the backend adapter's output (in-memory during apply).
2. In the target platform's secret store (K8s Secret, AWS Secrets Manager, Azure Key Vault).
3. In the running application's environment variables.

It is never written to disk, never committed to git, never logged.

### Integration with HashiCorp Vault

For organizations using Vault, the backend adapter can write secrets to Vault
instead of (or in addition to) passing them directly:

```csharp
public class VaultAwareBackendAdapter : IBackendAdapter
{
    private readonly IBackendAdapter _inner;
    private readonly IVaultClient _vault;

    public string Name => _inner.Name;

    public async Task<BackendApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct)
    {
        var result = await _inner.ApplyAsync(plan, ct);

        if (result.Success)
        {
            // Write each resource's outputs to Vault
            foreach (var (resourceType, outputs) in result.ResourceOutputs)
            {
                var vaultPath = $"secret/data/{plan.AppName}/{plan.Environment}/{resourceType}";
                await _vault.V1.Secrets.KeyValue.V2.WriteSecretAsync(
                    vaultPath,
                    new Dictionary<string, object>(outputs));
            }
        }

        return result;
    }
}
```

### Integration with Azure Key Vault

```csharp
// In your custom runtime adapter, read secrets from Key Vault
// instead of passing them as plain env vars

using Azure.Security.KeyVault.Secrets;

public async Task ApplyAsync(WorkloadManifest manifest, CancellationToken ct)
{
    var kvClient = new SecretClient(
        new Uri("https://my-keyvault.vault.azure.net/"),
        new DefaultAzureCredential());

    // Store connection string in Key Vault
    await kvClient.SetSecretAsync("payments-api-postgres-connectionstring",
        resolvedConnectionString, ct);

    // Reference Key Vault secret in the Container App config
    // instead of embedding the raw value
}
```

### RBAC considerations

```
  Role                      Can do                              Cannot do
  +------------------------+-----------------------------------+---------------------------+
  | Developer              | deskribe validate                 | Modify platform config    |
  |                        | deskribe plan                     | Change backends mapping   |
  |                        | deskribe apply (via CI only)      | Override policies          |
  |                        | Write deskribe.json               | Access other apps' secrets|
  +------------------------+-----------------------------------+---------------------------+
  | Platform Engineer      | Modify base.json, envs/*.json     | Modify deskribe.json      |
  |                        | Write backend/runtime adapters    | (that's the dev's file)   |
  |                        | Define policies                   |                           |
  |                        | Manage the IaC modules/programs   |                           |
  +------------------------+-----------------------------------+---------------------------+
  | CI/CD Pipeline         | deskribe apply with --image       | Modify configs            |
  |                        | Access to platform config (read)  | Interactive operations    |
  |                        | Access to cloud credentials       |                           |
  +------------------------+-----------------------------------+---------------------------+
```

### Namespace isolation

The `namespacePattern` in platform config ensures each app-environment combination
gets its own namespace:

```json
{
  "defaults": {
    "namespacePattern": "{app}-{env}"
  }
}
```

This means `payments-api` in `prod` gets namespace `payments-api-prod`. Combined with
Kubernetes RBAC and NetworkPolicies (see Section 6), this provides strong isolation
between applications and environments.

### CI/CD pipeline security

A typical secure CI/CD pipeline using Deskribe:

```yaml
# .github/workflows/deploy.yml
name: Deploy

on:
  push:
    branches: [main]

jobs:
  deploy:
    runs-on: ubuntu-latest
    permissions:
      id-token: write   # For OIDC auth to cloud providers
      contents: read

    steps:
      - uses: actions/checkout@v4

      # Checkout platform config (separate repo, read-only)
      - uses: actions/checkout@v4
        with:
          repository: acme/platform-config
          path: platform-config
          token: ${{ secrets.PLATFORM_CONFIG_TOKEN }}

      - name: Validate
        run: |
          deskribe validate \
            -f deskribe.json \
            --env prod \
            --platform platform-config

      - name: Plan
        run: |
          deskribe plan \
            --env prod \
            --platform platform-config \
            --image api=ghcr.io/acme/payments-api:${{ github.sha }}

      - name: Apply
        run: |
          deskribe apply \
            --env prod \
            --platform platform-config \
            --image api=ghcr.io/acme/payments-api:${{ github.sha }}
        env:
          # Cloud credentials injected via OIDC or secrets
          # Never stored in deskribe.json or platform config
          ARM_CLIENT_ID: ${{ secrets.AZURE_CLIENT_ID }}
          ARM_TENANT_ID: ${{ secrets.AZURE_TENANT_ID }}
          ARM_SUBSCRIPTION_ID: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
```

Key points:
- Platform config is checked out as a separate repo (read-only for CI).
- Cloud credentials are injected as environment variables, never in config files.
- The validate step catches policy violations before any infrastructure is touched.
- The image tag is tied to the git SHA for full traceability.

---

## 13. Azure AKS Multi-Region Deployment

Deskribe does not need a special "multi-region" feature. Separate environment files per region
are all you need. Each `envs/` file targets a different AKS cluster.

### Environment files per region

Create one environment file per region in your platform config:

**`envs/prod-us.json`**

```json
{
  "name": "prod-us",
  "defaults": {
    "region": "eastus2",
    "replicas": 3,
    "cpu": "500m",
    "memory": "1Gi",
    "ha": true
  },
  "alertRouting": {
    "default": ["slack://#prod-us-alerts"],
    "critical": ["pagerduty://oncall-us"]
  }
}
```

**`envs/prod-eu.json`**

```json
{
  "name": "prod-eu",
  "defaults": {
    "region": "westeurope",
    "replicas": 3,
    "cpu": "500m",
    "memory": "1Gi",
    "ha": true
  },
  "alertRouting": {
    "default": ["slack://#prod-eu-alerts"],
    "critical": ["pagerduty://oncall-eu"]
  }
}
```

The `region` field flows through to backend adapters. For Terraform, it becomes the `location`
variable. For Pulumi, it sets `azure:location` in stack config.

### How `region` flows to infrastructure

```
  envs/prod-us.json                    Backend Adapter
  +-------------------+                +---------------------------+
  | "region": "eastus2"|  ---merge-->  | plan.Platform             |
  +-------------------+                |   .Defaults.Region        |
                                       |   = "eastus2"             |
                                       +------------+--------------+
                                                    |
                            +-----------------------+-----------------------+
                            |                                               |
                            v                                               v
                  Terraform adapter                              Pulumi adapter
                  tfvars["region"] = "eastus2"                   stack.SetConfig("azure:location", "eastus2")
                            |                                               |
                            v                                               v
                  azurerm_postgresql_flexible_server              new Azure.PostgreSql.FlexibleServer
                    location = "eastus2"                           { Location = "eastus2" }
```

### CI/CD matrix strategy

Deploy to both regions in a single CI pipeline using a matrix strategy:

```yaml
# .github/workflows/deploy-multi-region.yml
name: Deploy Multi-Region

on:
  push:
    tags: ["v*"]

jobs:
  deploy:
    runs-on: ubuntu-latest
    strategy:
      matrix:
        env: [prod-us, prod-eu]
      fail-fast: false
    environment: ${{ matrix.env }}

    steps:
      - uses: actions/checkout@v4

      - name: Checkout platform config
        uses: actions/checkout@v4
        with:
          repository: acme/platform-config
          path: ./platform-config
          token: ${{ secrets.PLATFORM_CONFIG_TOKEN }}

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - run: dotnet tool install --global deskribe-cli

      - name: Configure AKS context (${{ matrix.env }})
        uses: azure/aks-set-context@v4
        with:
          resource-group: ${{ secrets.AKS_RESOURCE_GROUP }}
          cluster-name: ${{ secrets.AKS_CLUSTER_NAME }}

      - name: Apply (${{ matrix.env }})
        run: |
          deskribe apply \
            -f deskribe.json \
            --env ${{ matrix.env }} \
            --platform ./platform-config \
            --image api=ghcr.io/acme/payments-api:${{ github.sha }}
```

Each matrix entry targets a different environment file (`envs/prod-us.json` vs `envs/prod-eu.json`)
and a different AKS cluster (via environment-scoped secrets for `AKS_RESOURCE_GROUP` and
`AKS_CLUSTER_NAME`).

### Kubeconfig context switching

Each AKS cluster has its own kubeconfig context. The `azure/aks-set-context` action handles
this automatically. If you manage kubeconfig manually:

```bash
# US region
az aks get-credentials --resource-group rg-prod-us --name aks-prod-us
deskribe apply --env prod-us --platform ./platform-config

# EU region
az aks get-credentials --resource-group rg-prod-eu --name aks-prod-eu
deskribe apply --env prod-eu --platform ./platform-config
```

### Complete data flow

```
  deskribe apply --env prod-us                    deskribe apply --env prod-eu
         |                                                |
         v                                                v
  Load envs/prod-us.json                          Load envs/prod-eu.json
    region: eastus2                                 region: westeurope
         |                                                |
         v                                                v
  Merge with base.json                            Merge with base.json
  (base region overridden)                        (base region kept)
         |                                                |
         v                                                v
  Backend provisions in eastus2                   Backend provisions in westeurope
  - Azure DB for PostgreSQL (eastus2)             - Azure DB for PostgreSQL (westeurope)
  - Azure Cache for Redis (eastus2)               - Azure Cache for Redis (westeurope)
         |                                                |
         v                                                v
  K8s deploy to aks-prod-us                       K8s deploy to aks-prod-eu
  namespace: payments-api-prod-us                 namespace: payments-api-prod-eu
```

The developer's `deskribe.json` is identical for both regions. Only the `--env` flag changes.

---

## Quick Reference: Interface Contracts

For platform engineers writing custom adapters, here are the core interfaces:

### IBackendAdapter --- Bridge to your IaC

```csharp
public interface IBackendAdapter
{
    string Name { get; }
    Task<BackendApplyResult> ApplyAsync(DeskribePlan plan, CancellationToken ct = default);
    Task DestroyAsync(string appName, string environment, CancellationToken ct = default);
}
```

### IRuntimeAdapter --- Bridge to your deployment target

```csharp
public interface IRuntimeAdapter
{
    string Name { get; }
    Task<WorkloadManifest> RenderAsync(WorkloadPlan workload, CancellationToken ct = default);
    Task ApplyAsync(WorkloadManifest manifest, CancellationToken ct = default);
    Task DestroyAsync(string namespaceName, CancellationToken ct = default);
}
```

### IPlugin --- Registration entry point

```csharp
public interface IPlugin
{
    string Name { get; }
    void Register(IPluginRegistrar registrar);
}

public interface IPluginRegistrar
{
    void RegisterResourceProvider(IResourceProvider provider);
    void RegisterBackendAdapter(IBackendAdapter adapter);
    void RegisterRuntimeAdapter(IRuntimeAdapter adapter);
    void RegisterMessagingProvider(IMessagingProvider provider);
}
```

### IResourceProvider --- Resource-specific validation and planning

```csharp
public interface IResourceProvider
{
    string ResourceType { get; }
    Task<ValidationResult> ValidateAsync(DeskribeResource resource, ValidationContext ctx, CancellationToken ct = default);
    Task<ResourcePlanResult> PlanAsync(DeskribeResource resource, PlanContext ctx, CancellationToken ct = default);
}
```

---

## Config Merge Order

Understanding the merge order is critical for platform teams. Values are merged in
layers with clear priority:

```
  Layer 1 (lowest priority):   base.json          Platform defaults
                                                     |
  Layer 2 (middle priority):   envs/prod.json      Environment overrides
                                                     |
  Layer 3 (highest priority):  deskribe.json        Developer overrides (limited fields)
                                                     |
                                                     v
                                              Final merged WorkloadPlan
```

What developers CAN override (in their `deskribe.json` services.overrides):

```
  replicas    - "I need 5 replicas in prod for Black Friday"
  cpu         - "My service is CPU-heavy, needs 1000m"
  memory      - "My service caches data in-memory, needs 2Gi"
```

What developers CANNOT override (platform-level only):

```
  runtime           - kubernetes vs ACA vs ECS (platform decides)
  region            - westeurope vs us-east-1 (governance policy)
  backends          - Pulumi vs Terraform (platform decides)
  namespacePattern  - how namespaces are structured (platform decides)
  policies          - TLS, allowed regions (security/compliance)
```

This separation is the core of Deskribe's design. Developers describe intent.
Platform teams control implementation. Neither touches the other's code.
