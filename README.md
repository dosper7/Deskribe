# Deskribe

**Intent-as-Code for developers who ship products, not YAML.**

Deskribe is the translation layer between developers and platform teams. Developers describe *what* they need. Platform teams define *how* it gets provisioned. Nobody touches each other's code.

---

## The Problem

Every developer who wants to deploy a service today must learn:

```
Terraform          "What's a state file?"
Kubernetes YAML    "Why does indentation break everything?"
Helm values        "Which chart version is compatible again?"
Cloud IAM          "I just need a database..."
CI/CD pipelines    "Can someone review my 200-line workflow?"
```

Meanwhile, platform teams:

```
Review bad Terraform PRs     "No, you can't use db.r5.24xlarge in dev"
Fix broken deployments       "They forgot resource limits again"
Re-implement patterns        "Third team this month writing the same Redis setup"
Answer Slack questions       "How do I get the connection string?"
```

**The result**: Developers context-switch away from product work. Platform teams babysit instead of building platforms. Every team reinvents the same patterns.

---

## The Solution

Deskribe eliminates this friction with a single file.

### Before Deskribe

Developer has to write and maintain:

```
my-service/
  terraform/
    main.tf                 # 80 lines - Postgres + Redis
    variables.tf            # 40 lines - all the knobs
    outputs.tf              # 15 lines
    backend.tf              # 10 lines
  k8s/
    deployment.yaml         # 60 lines
    service.yaml            # 20 lines
    configmap.yaml          # 15 lines
    namespace.yaml          # 8 lines
  helm/
    values-dev.yaml         # 30 lines
    values-prod.yaml        # 35 lines
  .github/workflows/
    deploy.yml              # 120 lines

  Total: ~430 lines of infra code the developer shouldn't own
```

### After Deskribe

Developer writes **one file** in their repo:

```json
// deskribe.json - that's it. 15 lines.
{
  "name": "payments-api",
  "resources": [
    { "type": "postgres", "size": "m" },
    { "type": "redis" },
    { "type": "kafka.messaging", "topics": [
        { "name": "payments.transactions", "partitions": 6 }
    ]}
  ],
  "services": [
    {
      "env": {
        "ConnectionStrings__Postgres": "@resource(postgres).connectionString",
        "Redis__Endpoint": "@resource(redis).endpoint",
        "Kafka__Servers": "@resource(kafka.messaging).endpoint"
      }
    }
  ]
}
```

Then they run:

```
$ deskribe plan --env dev

  Deskribe Plan for payments-api (dev)
  ===================================

  Resources:
    + postgres (size: m)    -> Pulumi: Azure PostgreSQL Flexible Server
    + redis    (size: s)    -> Pulumi: Azure Cache for Redis
    + kafka.messaging       -> Pulumi: Azure Event Hubs (1 topic, 6 partitions)

  Workload:
    Namespace:  payments-api-dev
    Replicas:   2
    CPU:        250m
    Memory:     512Mi

  Environment Variables:
    ConnectionStrings__Postgres  -> @resource(postgres).connectionString
    Redis__Endpoint              -> @resource(redis).endpoint
    Kafka__Servers               -> @resource(kafka.messaging).endpoint

  3 resources will be created. Run 'deskribe apply' to proceed.
```

**Where did the defaults come from?** The platform team's config:

```
platform-config/
  base.json       # "All services default to 2 replicas, 250m CPU, K8s runtime, Pulumi backend"
  envs/
    prod.json     # "Prod gets 3 replicas, 1Gi memory, HA enabled"
```

The developer never sees this. They don't need to. They just describe what they need.

---

## How It Works

```
  Developer's repo              Platform team's repo
  +--------------+              +------------------+
  | deskribe.json|              | base.json        |
  | "I need      |              | "Org defaults:   |
  |  postgres,   |              |  K8s, westeurope,|
  |  redis,      |              |  Pulumi backend" |
  |  kafka"      |              |                  |
  +------+-------+              | envs/dev.json    |
         |                      | envs/prod.json   |
         |                      +--------+---------+
         |                               |
         +----------+   +---------------+
                    |   |
                    v   v
          +-------------------+
          |   DESKRIBE ENGINE |
          |                   |
          | 1. Load configs   |
          | 2. Merge layers   |
          | 3. Validate       |
          | 4. Resolve refs   |
          | 5. Plan           |
          | 6. Apply          |
          +--------+----------+
                   |
          +--------+----------+
          |                   |
          v                   v
   +-------------+    +--------------+
   | Infra       |    | K8s Runtime  |
   | (Pulumi/TF) |    | Deployment,  |
   | Postgres,   |    | Secret,      |
   | Redis,      |    | Service,     |
   | Kafka       |    | Namespace    |
   +-------------+    +--------------+
```

**Key principle**: The manifest is merged in 3 layers with clear priority:

```
Platform base  <  Environment  <  Developer
(lowest)          (middle)        (highest - for allowed fields)
```

Developers can override replicas, CPU, memory per environment. They **cannot** override which backend is used (Pulumi vs Terraform) or which region to deploy to. That's platform-level governance.

---

## Architecture: Three Plugin Types

Deskribe's plugin system cleanly separates three concerns:

```
  +- Resource Provider (cloud-agnostic) ----+
  |  "I need postgres v16, size S"          |
  |  Validates config and produces a plan   |
  |  with pending placeholder outputs.      |
  +------------------+---------------------+
                     |
                     v
  +- Backend Adapter (cloud-aware) ---------+
  |  Takes config, runs IaC tool            |
  |  (Pulumi/Terraform), returns real       |
  |  outputs like connection strings.       |
  +------------------+---------------------+
                     |
                     v
  +- Runtime Adapter (cluster-aware) -------+
  |  Deploys app containers to K8s,         |
  |  injects resource outputs as env vars   |
  |  via Secrets.                           |
  +------------------+---------------------+
```

| Plugin Type | Concern | Example |
|---|---|---|
| **Resource Provider** | What resources are needed (cloud-agnostic) | PostgresResourceProvider validates `size: "s"`, `version: "16"` |
| **Backend Adapter** | How resources are provisioned (cloud-aware) | PulumiBackendAdapter runs `pulumi up` targeting Azure |
| **Runtime Adapter** | Where the app runs (cluster-aware) | KubernetesRuntimeAdapter renders Namespace + Secret + Deployment + Service |

---

## Local Dev with Aspire

Here's where it gets interesting. The **same `deskribe.json`** that drives production also powers your local development environment via .NET Aspire.

```
$ dotnet run --project src/Deskribe.AppHost
```

What happens:

```
  Deskribe reads deskribe.json
       |
       |   "postgres" declared?
       +----> Spins up Postgres container + PgAdmin UI
       |
       |   "redis" declared?
       +----> Spins up Redis container + RedisInsight UI
       |
       |   "kafka.messaging" declared?
       +----> Spins up Kafka container + Kafka UI
       |
       +----> Launches your service with all connection
              strings injected automatically
       |
       +----> Opens Aspire Dashboard
              http://localhost:15888
```

**What you see in the Aspire Dashboard:**

```
  +----------------------------------------------------------+
  |  Aspire Dashboard                                        |
  +----------------------------------------------------------+
  |                                                          |
  |  Resources                          Status               |
  |  ------------------------------------------------------ |
  |  payments-api-postgres              Running              |
  |  payments-api-db                    Running              |
  |  payments-api-redis                 Running              |
  |  payments-api-kafka                 Running              |
  |  deskribe-web                       Running              |
  |                                                          |
  |  Each resource shows:                                    |
  |    - Health checks (green/red)                           |
  |    - Structured logs (live)                              |
  |    - Connection strings (click to copy)                  |
  |    - Resource metrics                                    |
  +----------------------------------------------------------+
```

**One file. Zero Docker Compose. Zero manual setup.** Add `"type": "redis"` to your manifest, restart — Redis appears.

### How the Aspire bridge works

```csharp
// In your AppHost — this is all it takes:
var builder = DistributedApplication.CreateBuilder(args);

var resources = builder.AddDeskribeManifest("path/to/deskribe.json");

builder.AddProject<Projects.MyService>("my-service")
    .WithDeskribeResources(resources);

builder.Build().Run();
```

Deskribe reads the manifest and calls the right Aspire APIs:

| Manifest resource | Aspire call | You get |
|---|---|---|
| `"type": "postgres"` | `AddPostgres().WithPgAdmin()` | Postgres + PgAdmin UI |
| `"type": "redis"` | `AddRedis().WithRedisInsight()` | Redis + RedisInsight UI |
| `"type": "kafka.messaging"` | `AddKafka().WithKafkaUI()` | Kafka + Kafka UI |

---

## The `@resource()` Syntax

Environment variables reference provisioned resources using `@resource(type).property`:

```json
{
  "env": {
    "ConnectionStrings__Postgres": "@resource(postgres).connectionString",
    "Redis__Endpoint": "@resource(redis).endpoint",
    "Kafka__BootstrapServers": "@resource(kafka.messaging).endpoint"
  }
}
```

**What happens at each stage:**

```
  Plan phase:
    @resource(postgres).connectionString
    -> Validated: "postgres" resource exists, "connectionString" is a known output

  Apply phase (Pulumi provisions Postgres):
    @resource(postgres).connectionString
    -> Resolved: "Host=10.0.1.5;Port=5432;Database=payments-api-db;Username=..."

  Local dev (Aspire):
    -> Aspire injects it automatically via its connection string system
```

---

## Platform Config

Platform teams maintain a separate config repo. Developers never touch it.

### `base.json` — Organization defaults

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

### `envs/prod.json` — Production overrides

```json
{
  "name": "prod",
  "defaults": {
    "replicas": 3,
    "cpu": "500m",
    "memory": "1Gi",
    "ha": true
  }
}
```

### What the developer can and cannot override

```
  Developer CAN override (per-env in deskribe.json):
    replicas        "I need 5 replicas in prod for Black Friday"
    cpu             "My service is CPU-heavy"
    memory          "I need more memory for caching"
    env vars        "I have custom config"

  Developer CANNOT override (platform-level only):
    backend         Pulumi vs Terraform — platform decides
    region          Governance policy
    TLS policy      Security requirement
    namespace       Generated from pattern
```

---

## CLI Commands

### Validate

Check your manifest against platform policies before deploying:

```
$ deskribe validate -f deskribe.json --env dev --platform ./platform-config

  Validating payments-api for dev...

  [PASS] Manifest name is set
  [PASS] All resource types have registered backends
  [PASS] Environment variable references resolve to declared resources
  [PASS] Postgres: size 'm' is valid (xs, s, m, l, xl)
  [PASS] Redis: configuration valid
  [PASS] Kafka: all topics have at least 3 partitions

  Validation passed with 0 errors, 0 warnings.
```

### Plan

See what will be created without touching anything:

```
$ deskribe plan --env prod --platform ./platform-config \
    --image api=ghcr.io/acme/payments-api:sha-abc123
```

### Apply

Provision infrastructure and deploy:

```
$ deskribe apply --env prod --platform ./platform-config \
    --image api=ghcr.io/acme/payments-api:sha-abc123

  Applying payments-api to prod...

  [1/3] Provisioning postgres via pulumi... done (45s)
  [2/3] Provisioning redis via pulumi... done (30s)
  [3/3] Provisioning kafka.messaging via pulumi... done (25s)

  Deploying to kubernetes...
    Namespace: payments-api-prod   created
    Secret: payments-api-secrets   created
    Deployment: payments-api       created (3 replicas)
    Service: payments-api          created

  payments-api is live in prod.
```

### Destroy

Tear down everything:

```
$ deskribe destroy --env dev --platform ./platform-config
```

---

## Kafka — First-Class Messaging

Kafka topics and ACLs are managed declaratively:

```json
{
  "type": "kafka.messaging",
  "topics": [
    {
      "name": "payments.transactions",
      "partitions": 6,
      "retentionHours": 168,
      "owners": ["team-payments"],
      "consumers": ["team-fraud", "team-notifications"]
    }
  ]
}
```

**What Deskribe does with this:**

```
  Topic: payments.transactions
    Partitions: 6
    Retention: 7 days (168 hours)

  ACLs (auto-generated):
    team-payments       -> WRITE (owners)
    team-fraud          -> READ  (consumer)
    team-notifications  -> READ  (consumer)

  Platform policy enforced:
    Minimum 3 partitions per topic (validated)
```

---

## Web Dashboard

Deskribe includes a web UI for visualizing your infrastructure:

```
$ dotnet run --project src/Deskribe.Web

  +----------------------------------------------------------+
  |  DESKRIBE                                                |
  |  Intent-as-Code                                          |
  +----------------------------------------------------------+
  |           |                                              |
  | Dashboard |  Applications          2                     |
  | Apps      |  Environments          3                     |
  | Resources |  Resources             5                     |
  | Plan View |  Status                Healthy               |
  |           |                                              |
  |           |  +------------------+  +------------------+  |
  |           |  | payments-api     |  | user-service     |  |
  |           |  | postgres redis   |  | postgres         |  |
  |           |  | kafka            |  |                  |  |
  |           |  | dev staging prod |  | dev prod         |  |
  |           |  +------------------+  +------------------+  |
  +----------------------------------------------------------+
```

The **Plan View** page lets you run plan/apply/destroy and see:
- Resource plan cards with configurations
- Workload details (replicas, CPU, memory)
- Generated Kubernetes YAML preview
- Tab-based navigation between views

---

## Project Structure

```
Deskribe/
  src/
    Deskribe.Sdk/              Plugin contracts + resource schemas
    Deskribe.Core/             Engine, merge, validation, resolution
    Deskribe.Cli/              CLI (validate, plan, apply, destroy)
    Deskribe.Web/              Blazor Server dashboard
    Deskribe.Aspire/           Manifest-to-Aspire bridge
    Deskribe.AppHost/          Aspire orchestrator
    Deskribe.ServiceDefaults/  OpenTelemetry, health checks
    Plugins/
      Backends/
        Pulumi/
      Resources/
        Postgres/
        Redis/
        Kafka/
      Runtimes/
        Kubernetes/
  tests/
    Deskribe.Core.Tests/       Merge engine, reference resolver, engine
    Deskribe.Plugins.Tests/    Postgres, Kafka provider tests
  examples/
    payments-api/              Sample deskribe.json
    platform-config/           Sample platform config (base + envs)
```

---

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://www.docker.com/) (for Aspire local dev)

### Run the example locally with Aspire

```bash
git clone https://github.com/dosper7/Deskribe.git
cd Deskribe

# Start everything — Postgres, Redis, Kafka + Web Dashboard
dotnet run --project src/Deskribe.AppHost
```

This reads `examples/payments-api/deskribe.json` and spins up all declared resources. Open the Aspire dashboard at `http://localhost:15888`.

### Use the CLI

```bash
# Validate the example manifest
dotnet run --project src/Deskribe.Cli -- validate \
  -f examples/payments-api/deskribe.json \
  --env dev \
  --platform examples/platform-config

# Generate a plan
dotnet run --project src/Deskribe.Cli -- plan \
  --env dev \
  --platform examples/platform-config \
  --image api=nginx:latest
```

### Run tests

```bash
dotnet test
```

---

## E2E Example: Weather API

A full working example deploying a Weather API + Postgres to Azure (AKS + Azure PostgreSQL Flexible Server via Pulumi).
See [`examples/weather-api/README.md`](examples/weather-api/README.md) for the complete guide.

### Deploy to Azure

```bash
# Build and push the image
docker build -t weather-api:latest -f examples/weather-api/src/WeatherApi/Dockerfile examples/weather-api/src/WeatherApi
az acr login --name myacr
docker tag weather-api:latest myacr.azurecr.io/weather-api:v1
docker push myacr.azurecr.io/weather-api:v1

# Deploy (Postgres via Pulumi + app to AKS)
dotnet run --project src/Deskribe.Cli -- apply \
  -f examples/weather-api/deskribe.json \
  --env prod \
  --platform examples/platform-config \
  --image api=myacr.azurecr.io/weather-api:v1

# Verify
kubectl port-forward svc/weather-api -n weather-api-prod 8080:80
curl http://localhost:8080/weatherforecast
```

### Secrets Management

Deskribe supports three secrets strategies, configured in platform defaults:

| Strategy | Description | Config |
|----------|-------------|--------|
| `opaque` (default) | Standard K8s `V1Secret` with `stringData` | `"secretsStrategy": "opaque"` |
| `external-secrets` | `ExternalSecret` CRD synced from Azure Key Vault / AWS Secrets Manager | `"secretsStrategy": "external-secrets"` |
| `sealed-secrets` | `V1Secret` with Bitnami Sealed Secrets annotation | `"secretsStrategy": "sealed-secrets"` |

---

## Philosophy

1. **One file to rule them all** — `deskribe.json` is the single source of truth for what a service needs
2. **Don't reinvent, integrate** — Aspire for local dev, Pulumi/Terraform for production, Kubernetes for runtime
3. **Separation of concerns** — Developers own intent, platform teams own implementation
4. **Progressive disclosure** — Start with `{ "type": "postgres" }`, add details only when needed
5. **Shift left** — Validate and plan before deploying. Catch policy violations in CI, not in prod

---

## License

MIT
