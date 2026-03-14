# Deskribe

**Intent-as-Code for developers who ship products, not YAML.**

Deskribe is the translation layer between developers and platform teams. Developers describe *what* they need in `deskribe.json`. Platform teams define *how* it gets provisioned. Nobody touches each other's code.

---

## Installation & Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://www.docker.com/) (for Aspire local dev)
- [Pulumi CLI](https://www.pulumi.com/docs/install/) (optional, for cloud provisioning)
- [kubectl](https://kubernetes.io/docs/tasks/tools/) (optional, for Kubernetes deployments)

### Clone and Build

```bash
git clone https://github.com/dosper7/Deskribe.git
cd Deskribe
dotnet build
```

### Install the CLI (global tool)

```bash
# From the repo root
dotnet pack src/Deskribe.Cli -o ./nupkg
dotnet tool install --global --add-source ./nupkg Deskribe.Cli

# Or run directly without installing
dotnet run --project src/Deskribe.Cli -- <command>
```

### Run Tests

```bash
dotnet test
```

---

## End-to-End Guide

### Step 1: Write Your Manifest

Create `deskribe.json` in your service repo:

```json
{
  "name": "payments-api",
  "resources": [
    { "type": "postgres", "size": "m", "version": "16" },
    { "type": "redis" },
    { "type": "kafka.messaging", "topics": [
        { "name": "payments.transactions", "partitions": 6, "retentionHours": 168, "owners": ["team-payments"], "consumers": ["team-fraud"] }
    ]}
  ],
  "services": [
    {
      "env": {
        "ConnectionStrings__Postgres": "@resource(postgres).connectionString",
        "Redis__Endpoint": "@resource(redis).endpoint",
        "Kafka__Servers": "@resource(kafka.messaging).endpoint"
      },
      "overrides": {
        "dev": { "replicas": 1, "cpu": "250m", "memory": "512Mi" },
        "prod": { "replicas": 3, "cpu": "500m", "memory": "1Gi" }
      }
    }
  ]
}
```

### Step 2: Set Up Platform Config

Platform teams provide configuration. Use **split-file** format (enterprise) or **single-file** format (small projects):

**Split-file** (`platform-config/` directory):

```
platform-config/
  base.json       # Organization defaults
  envs/
    dev.json      # Dev overrides
    prod.json     # Prod overrides
```

`base.json`:
```json
{
  "organization": "acme",
  "defaults": {
    "region": "westeurope",
    "replicas": 2,
    "cpu": "250m",
    "memory": "512Mi",
    "namespacePattern": "{app}-{env}"
  },
  "runtime": { "name": "kubernetes" },
  "provisioners": { "postgres": "pulumi", "redis": "pulumi", "kafka.messaging": "pulumi" },
  "policies": { "allowedRegions": ["westeurope", "northeurope"], "enforceTLS": true }
}
```

`envs/prod.json`:
```json
{
  "name": "prod",
  "defaults": { "replicas": 3, "cpu": "500m", "memory": "1Gi", "ha": true }
}
```

**Single-file** (`platform.json`):
```json
{
  "organization": "acme",
  "defaults": { "region": "westeurope", "replicas": 2 },
  "runtime": { "name": "kubernetes" },
  "provisioners": { "postgres": "terraform", "redis": "terraform" },
  "environments": {
    "dev": { "defaults": { "replicas": 1 } },
    "prod": { "defaults": { "replicas": 3, "ha": true } }
  }
}
```

### Step 3: Validate

```bash
deskribe validate -f deskribe.json --env dev --platform ./platform-config
```

### Step 4: Plan

```bash
deskribe plan -f deskribe.json --env prod --platform ./platform-config \
  --image api=ghcr.io/acme/payments-api:v1.2.3
```

### Step 5: Generate Artifacts (for GitOps / CI/CD)

```bash
# Generate everything (Terraform + K8s + Kustomize)
deskribe generate -f deskribe.json --env prod -p ./platform-config -o ./generated/

# Generate only K8s manifests + Kustomize overlays (fastest for K8s/Kustomize pipelines)
deskribe generate -f deskribe.json --env prod -p ./platform-config -o ./generated/ \
  --output-format k8s-only

# Generate only Terraform artifacts
deskribe generate -f deskribe.json --env prod -p ./platform-config -o ./generated/ \
  --output-format terraform-only
```

Produces (with `--output-format all`):
- `terraform.tfvars.json` — resource config for Terraform modules
- `helm-values.yaml` — workload config for Helm/ArgoCD deployments
- `bindings.json` — machine-readable resource binding manifest
- `{app-name}/base/deployment.yaml` — K8s Namespace + Deployment + Service
- `{app-name}/base/secrets.yaml` — K8s Secret or ExternalSecret
- `{app-name}/base/kustomization.yaml` — Kustomize base
- `{app-name}/overlays/{env}/kustomization.yaml` — per-env overlay (replicas, image, resources)

### Step 6: Apply (Direct Deployment)

```bash
deskribe apply -f deskribe.json --env prod --platform ./platform-config \
  --image api=ghcr.io/acme/payments-api:v1.2.3
```

### Step 7: Destroy

```bash
deskribe destroy -f deskribe.json --env dev --platform ./platform-config
```

---

## Local Development with Aspire

The **same `deskribe.json`** powers your local dev environment via .NET Aspire. No Docker Compose needed.

```bash
dotnet run --project src/Deskribe.AppHost
```

This reads the manifest and spins up real containers:

| Manifest resource | What you get |
|---|---|
| `"type": "postgres"` | Postgres container + PgAdmin UI |
| `"type": "redis"` | Redis container + RedisInsight UI |
| `"type": "kafka.messaging"` | Kafka container + Kafka UI |

The `payments-api` sample project also starts alongside these containers, with connection strings automatically injected.

Open the Aspire dashboard at `http://localhost:15888`.

### Wiring in your AppHost

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var resources = builder.AddDeskribeManifest("path/to/deskribe.json");

// Your application — gets all resource connections auto-wired
builder.AddProject<Projects.PaymentsApi>("payments-api")
    .WithDeskribeResources(resources);

// Deskribe Web UI — dashboard for visibility
builder.AddProject<Projects.Deskribe_Web>("deskribe-web")
    .WithDeskribeResources(resources);

builder.Build().Run();
```

---

## Architecture

### Three Plugin Types

```
  Resource Provider (what)     Provisioner (how)          Runtime Plugin (where)
  ──────────────────────       ─────────────────          ────────────────────────
  "I need postgres v16"   →   Pulumi/Terraform     →    Kubernetes Deployment
  Validates, plans outputs     provisions real infra      injects env vars, deploys
```

| Plugin Type | Interface | Example |
|---|---|---|
| **Resource Provider** | `IResourceProvider` | PostgresResourceProvider validates size/version, plans outputs |
| **Provisioner** | `IProvisioner` | PulumiProvisioner runs `pulumi up`, TerraformProvisioner generates tfvars |
| **Runtime Plugin** | `IRuntimePlugin` | KubernetesRuntimePlugin renders Namespace + Secret + Deployment + Service |

### Plugin Discovery

Plugins are discovered automatically via `[DeskribePlugin]` attribute and assembly scanning:

```csharp
[DeskribePlugin("postgres")]
public class PostgresPlugin : IPlugin { ... }
```

Register via DI:
```csharp
services.AddDeskribe(
    typeof(PostgresPlugin).Assembly,
    typeof(PulumiPlugin).Assembly,
    typeof(KubernetesPlugin).Assembly);
```

### Resource Schema

Each resource plugin reports its schema for validation, UI generation, and documentation:

```csharp
public ResourceSchema GetSchema() => new()
{
    ResourceType = "postgres",
    Description = "PostgreSQL database",
    Properties = [
        new() { Name = "version", ValueType = "string", Default = "16" },
        new() { Name = "ha", ValueType = "bool" }
    ],
    ProvidedOutputs = ["connectionString", "host", "port"]
};
```

---

## The `@resource()` Syntax

Environment variables reference provisioned resources:

```json
{
  "env": {
    "ConnectionStrings__Postgres": "@resource(postgres).connectionString",
    "Redis__Endpoint": "@resource(redis).endpoint"
  }
}
```

- **Plan phase**: Validated that referenced resources and outputs exist
- **Apply phase**: Resolved to real values from provisioner outputs
- **Local dev**: Aspire injects connection strings automatically

---

## Web Dashboard

Deskribe includes a web UI built with Blazor Server:

```bash
dotnet run --project src/Deskribe.Web
# Or via Aspire (recommended — starts all containers too):
dotnet run --project src/Deskribe.AppHost
```

Features:
- **Dynamic app discovery** — scans `examples/` for `deskribe.json` manifests automatically
- **Dashboard** — application count, environments, resources, real status tracking
- **Resource overview** — per-type counts (Postgres, Redis, Kafka) across all apps
- **Plan Viewer** — generate plans, view K8s YAML previews, Apply and Destroy with confirmation dialogs
- **Environment switcher** — navigate between dev/prod per application
- **Container image input** — set the image from the UI before planning
- **Real status tracking** — tracks `Unknown → Planned → Applied → Error → Destroyed` per app/env

### API Endpoints

The dashboard exposes a Minimal API:

```
GET  /api/apps                        — discover applications
GET  /api/apps/{name}/manifest        — parsed manifest
GET  /api/apps/{name}/validate/{env}  — validation result
GET  /api/apps/{name}/plan/{env}      — execution plan
GET  /api/plugins                     — registered plugins + schemas
```

---

## Secrets Management

Three strategies, configured in platform defaults:

| Strategy | Description | Config |
|----------|-------------|--------|
| `opaque` (default) | Standard K8s Secret with stringData | `"secretsStrategy": "opaque"` |
| `external-secrets` | ExternalSecret CRD synced from Key Vault / Secrets Manager | `"secretsStrategy": "external-secrets"` |
| `sealed-secrets` | Secret with Bitnami Sealed Secrets annotation | `"secretsStrategy": "sealed-secrets"` |

---

## CI/CD Integration

Deskribe ships a GitHub Actions workflow at [`.github/workflows/deskribe-deploy.yml`](.github/workflows/deskribe-deploy.yml) that:

1. **Builds** your app, runs tests, pushes a Docker image
2. **Validates** the manifest against platform policies (matrix: dev, prod)
3. **Generates** K8s manifests + Kustomize overlays via `deskribe generate --output-format k8s-only`
4. **Pushes** the generated manifests to a separate deployments repo (e.g. `more-deployments`)
5. **Posts** a plan summary as a PR comment on pull requests

### Quick Setup

1. Place `deskribe.json` in your app repo root
2. Set GitHub secrets: `DEPLOYMENTS_REPO_TOKEN` (PAT with write access to the deployments repo)
3. Set GitHub variables: `DEPLOYMENTS_REPO` (e.g. `RELEX-Solutions/more-deployments`)

### GitHub Actions Example

```yaml
name: Deploy
on:
  push:
    branches: [main]
jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Validate
        run: deskribe validate -f deskribe.json --env prod --platform platform-config

      - name: Generate K8s Manifests
        run: |
          deskribe generate -f deskribe.json --env prod \
            -p platform-config -o ./output \
            --image api=${{ github.repository }}:${{ github.sha }} \
            --output-format k8s-only

      - name: Push to deployments repo
        run: |
          # Copy output/{app-name}/ into your deployments repo
          # Your existing GitHub Actions apply them via kubectl apply -k
```

### GitOps Flow

```
Developer edits deskribe.json
    → CI runs `deskribe validate` + `deskribe generate --output-format k8s-only`
    → Generated Kustomize manifests pushed to deployments repo
    → Existing pipeline applies via kubectl apply -k / ArgoCD
```

See [docs/cicd-integration.md](docs/cicd-integration.md) for the full guide.

---

## Project Structure

```
Deskribe/
  .github/
    workflows/
      deskribe-deploy.yml      GitHub Actions CI/CD workflow
  src/
    Deskribe.Sdk/              Plugin contracts, models, ResourceDescriptor
    Deskribe.Core/             Engine, config loader, merge, validation, resolution
    Deskribe.Cli/              CLI (validate, plan, apply, destroy, generate)
    Deskribe.Web/              Blazor Server web dashboard + Minimal API
    Deskribe.Web.Client/       Blazor WASM client (alternative UI)
    Deskribe.Aspire/           Manifest-to-Aspire bridge + projections
    Deskribe.AppHost/          Aspire orchestrator
    Deskribe.ServiceDefaults/  OpenTelemetry, health checks
    Plugins/
      Provisioners/
        Pulumi/                Pulumi Automation API provisioner
        Terraform/             Terraform artifact generator provisioner
      Resources/
        Postgres/              PostgreSQL resource provider
        Redis/                 Redis resource provider
        Kafka/                 Kafka messaging resource provider
      Runtimes/
        Kubernetes/            K8s runtime plugin (Deployment, Secret, Service)
  tests/
    Deskribe.Core.Tests/       Engine, merge, reference resolver tests
    Deskribe.Plugins.Tests/    Postgres, Kafka, secrets strategy tests
  examples/
    payments-api/
      deskribe.json            Manifest: Postgres + Redis + Kafka
      src/                     .NET Minimal API sample project
        Program.cs             Wires Postgres (EF), Redis, Kafka producer
        Endpoints/             POST/GET /api/payments
        Models/                Payment entity
        Data/                  EF Core DbContext
        Dockerfile             Multi-stage build
    platform-config/           Sample platform config (split-file format)
      base.json
      envs/
        dev.json
        prod.json
```

---

## E2E Example: Payments API

A complete working example with a .NET Minimal API that uses Postgres (EF Core), Redis (caching), and Kafka (event publishing).

### Local Dev (Aspire)

```bash
# Starts payments-api + Postgres + Redis + Kafka + Deskribe Web UI
dotnet run --project src/Deskribe.AppHost

# Test the API
curl http://localhost:{port}/api/payments                           # GET  — list payments
curl -X POST http://localhost:{port}/api/payments \
  -H "Content-Type: application/json" \
  -d '{"amount": 99.99, "currency": "EUR"}'                        # POST — create payment
curl http://localhost:{port}/api/payments/{id}                      # GET  — get by ID (Redis cached)
curl http://localhost:{port}/health                                 # GET  — health check
```

### Generate K8s Manifests

```bash
deskribe generate \
  -f examples/payments-api/deskribe.json \
  --env prod --platform examples/platform-config \
  -o ./output --output-format k8s-only \
  --image api=ghcr.io/acme/payments-api:v1.2.3
```

### Deploy to Kubernetes

```bash
deskribe apply \
  -f examples/payments-api/deskribe.json \
  --env prod --platform examples/platform-config \
  --image api=ghcr.io/acme/payments-api:v1.2.3

# Or apply the generated Kustomize overlays directly:
kubectl apply -k output/payments-api/overlays/prod
```

---

## Philosophy

1. **One file to rule them all** — `deskribe.json` is the single source of truth
2. **Don't reinvent, integrate** — Aspire for local dev, Pulumi/Terraform for prod, K8s for runtime
3. **Separation of concerns** — Developers own intent, platform teams own implementation
4. **Progressive disclosure** — Start with `{ "type": "postgres" }`, add details only when needed
5. **Shift left** — Validate and plan before deploying. Catch policy violations in CI, not in prod

---

## License

MIT
