# Weather API — Deskribe E2E Example

A minimal ASP.NET Core Web API that reads weather data from PostgreSQL.
Deploys to **Azure** (AKS + Azure DB for PostgreSQL Flexible Server via Pulumi).

## Architecture

```
Developer writes deskribe.json:
  "I need postgres v16 and my weather-api service"

Platform team configures base.json:
  backends:  { "postgres": "pulumi" }            <- IaC tool to provision resources
  runtime:   "kubernetes"                         <- Where the app container runs
  pulumiProjectDir: "examples/weather-api/infra"  <- Pulumi program targeting Azure

Deskribe Engine orchestrates two separate concerns:

  +- RESOURCE PROVISIONING (Backend Adapter) ----------------------+
  |  postgres -> PulumiBackendAdapter -> Pulumi program -> Azure   |
  |  Result: Azure PostgreSQL Flexible Server (pg-weather-api-prod)|
  |  Output: real connection string                                |
  +----------------------------------------------------------------+

  +- APP DEPLOYMENT (Runtime Adapter) -----------------------------+
  |  weather-api -> KubernetesRuntimeAdapter -> AKS cluster        |
  |  Injects: @resource(postgres).connectionString into K8s Secret |
  |  Renders: Namespace + Secret + Deployment + Service YAML       |
  +----------------------------------------------------------------+
```

## Prerequisites

- .NET 10 SDK
- Azure CLI (`az`) + active subscription
- Pulumi CLI (`pulumi`)
- kubectl configured for your AKS cluster

## Deploy to Azure

### 1. Login and configure

```bash
# Azure login
az login
az account set --subscription <your-subscription-id>

# Pulumi login (local state or Pulumi Cloud)
pulumi login --local   # or: pulumi login

# Get AKS credentials
az aks get-credentials --resource-group rg-prod --name aks-prod
```

### 2. Build and push the image

```bash
# Build the Weather API image
docker build -t weather-api:latest -f examples/weather-api/src/WeatherApi/Dockerfile examples/weather-api/src/WeatherApi

# Tag and push to your ACR
az acr login --name myacr
docker tag weather-api:latest myacr.azurecr.io/weather-api:v1
docker push myacr.azurecr.io/weather-api:v1
```

### 3. Deploy with Deskribe

```bash
# Validate the manifest
dotnet run --project src/Deskribe.Cli -- validate \
  -f examples/weather-api/deskribe.json \
  --env prod \
  --platform examples/platform-config

# Deploy everything (Postgres via Pulumi + app to AKS)
dotnet run --project src/Deskribe.Cli -- apply \
  -f examples/weather-api/deskribe.json \
  --env prod \
  --platform examples/platform-config \
  --image api=myacr.azurecr.io/weather-api:v1
```

### 4. Verify

```bash
kubectl get all -n weather-api-prod
kubectl port-forward svc/weather-api -n weather-api-prod 8080:80
curl http://localhost:8080/weatherforecast
curl http://localhost:8080/health
```

### What happens under the hood

1. Deskribe reads `deskribe.json` — sees `postgres` resource + `api` service
2. **PulumiBackendAdapter** (Local Program mode) runs `pulumi up` against `infra/Program.cs`
3. Pulumi provisions: Azure Resource Group + PostgreSQL Flexible Server (`Standard_B1ms`) + Database
4. Stack outputs provide the real connection string: `Host=pg-weather-api-prod.postgres.database.azure.com;...`
5. Deskribe resolves `@resource(postgres).connectionString` with the real value
6. **KubernetesRuntimeAdapter** renders and applies Namespace + Secret + Deployment + Service to AKS

### Tear down

```bash
dotnet run --project src/Deskribe.Cli -- destroy \
  -f examples/weather-api/deskribe.json \
  --env prod \
  --platform examples/platform-config
```

## Secrets Strategy

By default, Deskribe creates standard Kubernetes `Opaque` secrets. You can change this in the platform config:

### External Secrets Operator (Azure Key Vault)

In `base.json`:
```json
{
  "defaults": {
    "secretsStrategy": "external-secrets",
    "externalSecretsStore": "azure-keyvault"
  }
}
```

This generates an `ExternalSecret` CRD instead of a `V1Secret`, referencing your `ClusterSecretStore`.

### Sealed Secrets

In `base.json`:
```json
{
  "defaults": {
    "secretsStrategy": "sealed-secrets"
  }
}
```

This generates a standard `V1Secret` with `sealedsecrets.bitnami.com/managed: "true"` annotation.

## Project Structure

```
examples/weather-api/
  deskribe.json              # Deskribe manifest
  README.md                  # This file
  src/WeatherApi/
    Program.cs               # Minimal API endpoints
    WeatherDb.cs             # EF Core DbContext
    WeatherApi.csproj        # Project file
    Dockerfile               # Container build
    Migrations/              # EF Core migrations
  infra/
    Pulumi.yaml              # Pulumi project config
    Program.cs               # Azure PostgreSQL provisioning
    Deskribe.Infra.csproj    # Pulumi Azure project
```
