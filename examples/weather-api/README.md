# Weather API — Deskribe End-to-End Example

A minimal ASP.NET Core Web API that reads weather data from PostgreSQL, deployed to Azure using Deskribe.

This example demonstrates the full Deskribe flow: a developer declares what they need (`deskribe.json`), a platform team configures how infrastructure is provisioned (`platform-config/`), and Deskribe orchestrates everything — provisioning an Azure PostgreSQL Flexible Server via Pulumi and deploying the app container to AKS via the Kubernetes runtime adapter.

## Prerequisites

| Tool | Purpose |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | Build Deskribe and the Weather API |
| [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az`) | Create Azure resources |
| [Pulumi CLI](https://www.pulumi.com/docs/install/) (`pulumi`) | Infrastructure-as-Code engine |
| [Docker](https://docs.docker.com/get-docker/) | Build container images |
| [kubectl](https://kubernetes.io/docs/tasks/tools/) | Verify Kubernetes deployments |

You also need an active Azure subscription.

## One-Time Azure Setup

These steps create the Azure infrastructure that Deskribe deploys into. Run them once.

### 1. Login to Azure

```bash
az login
az account set --subscription <your-subscription-id>
```

### 2. Create a Resource Group, ACR, and AKS cluster

```bash
# Resource group
az group create --name rg-deskribe --location westeurope

# Container registry
az acr create --name <your-acr-name> --resource-group rg-deskribe --sku Basic

# AKS cluster with ACR integration
az aks create \
  --name aks-deskribe \
  --resource-group rg-deskribe \
  --node-count 2 \
  --node-vm-size Standard_B2s \
  --attach-acr <your-acr-name> \
  --generate-ssh-keys
```

### 3. Get AKS credentials and configure Pulumi

```bash
# Point kubectl at your cluster
az aks get-credentials --resource-group rg-deskribe --name aks-deskribe

# Login to Pulumi (local state file or Pulumi Cloud)
pulumi login --local   # or: pulumi login
```

## Build & Push the Container Image

```bash
# Build the Weather API image
docker build \
  -t weather-api:latest \
  -f examples/weather-api/src/WeatherApi/Dockerfile \
  examples/weather-api/src/WeatherApi

# Tag and push to your ACR
az acr login --name <your-acr-name>
docker tag weather-api:latest <your-acr-name>.azurecr.io/weather-api:v1
docker push <your-acr-name>.azurecr.io/weather-api:v1
```

## Deploy with Deskribe

### 1. Validate

```bash
dotnet run --project src/Deskribe.Cli -- validate \
  -f examples/weather-api/deskribe.json \
  --env prod \
  --platform examples/platform-config
```

### 2. Apply

```bash
dotnet run --project src/Deskribe.Cli -- apply \
  -f examples/weather-api/deskribe.json \
  --env prod \
  --platform examples/platform-config \
  --image api=<your-acr-name>.azurecr.io/weather-api:v1
```

## What Happens Under the Hood

1. Deskribe reads `deskribe.json` — sees a `postgres` resource and an `api` service.
2. The platform config maps `postgres` to the `pulumi` backend and points `pulumiProjectDir` at `examples/weather-api/infra`.
3. **PulumiBackendAdapter** calls `pulumi up` against `infra/Program.cs`, which provisions:
   - Azure Resource Group (`rg-weather-api-prod`)
   - PostgreSQL Flexible Server (`pg-weather-api-prod`, Standard_B1ms, v16, 32 GB)
   - Database (`weatherapi`)
   - Firewall rule allowing Azure services
4. Pulumi stack outputs provide the real connection string: `Host=pg-weather-api-prod.postgres.database.azure.com;Port=5432;Database=weatherapi;...`
5. Deskribe resolves `@resource(postgres).connectionString` in the service env vars with the real value.
6. **KubernetesRuntimeAdapter** renders and applies to AKS:
   - `Namespace/weather-api-prod`
   - `Secret/weather-api-prod/weather-api-env` (contains the connection string)
   - `Deployment/weather-api-prod/weather-api` (2 replicas, 500m CPU, 512Mi memory)
   - `Service/weather-api-prod/weather-api` (port 80 → 8080)

## Verify

```bash
# Check all resources in the namespace
kubectl get all -n weather-api-prod

# Port-forward to the service
kubectl port-forward svc/weather-api -n weather-api-prod 8080:80

# Test the endpoints
curl http://localhost:8080/weatherforecast
curl http://localhost:8080/health
```

## Tear Down

```bash
# Destroy via Deskribe (removes K8s namespace + Pulumi stack)
dotnet run --project src/Deskribe.Cli -- destroy \
  -f examples/weather-api/deskribe.json \
  --env prod \
  --platform examples/platform-config

# Optionally delete the Azure resource group with ACR and AKS
az group delete --name rg-deskribe --yes --no-wait
```

## Project Structure

```
examples/weather-api/
  deskribe.json              # Deskribe manifest (what the developer declares)
  README.md                  # This file
  src/WeatherApi/
    Program.cs               # Minimal API endpoints
    WeatherDb.cs             # EF Core DbContext
    WeatherApi.csproj        # Project file
    Dockerfile               # Container build
    Migrations/              # EF Core migrations
  infra/
    Pulumi.yaml              # Pulumi project config
    Program.cs               # Azure PostgreSQL provisioning (Pulumi C#)
    Deskribe.Infra.csproj    # Pulumi Azure SDK references
```
