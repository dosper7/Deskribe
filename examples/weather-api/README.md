# Weather API — Deskribe E2E Example

A minimal ASP.NET Core Web API that reads weather data from PostgreSQL.
Deployable to **local Kubernetes (Docker Desktop)** and **Azure (AKS + Azure DB for PostgreSQL)**.

## Prerequisites

- .NET 10 SDK
- Docker Desktop with Kubernetes enabled
- Helm 3 (`winget install Helm.Helm`)
- (For Azure) Azure CLI + subscription + Pulumi CLI

## 1. Deploy to Local K8s (Docker Desktop)

```bash
# Build the Weather API image
docker build -t weather-api:local -f examples/weather-api/src/WeatherApi/Dockerfile examples/weather-api/src/WeatherApi

# Validate the manifest
dotnet run --project src/Deskribe.Cli -- validate \
  -f examples/weather-api/deskribe.json \
  --env local \
  --platform examples/platform-config

# Deploy everything (Postgres via Helm + app to K8s)
dotnet run --project src/Deskribe.Cli -- apply \
  -f examples/weather-api/deskribe.json \
  --env local \
  --platform examples/platform-config \
  --image api=weather-api:local

# Test it
kubectl port-forward svc/weather-api -n weather-api-local 8080:80
curl http://localhost:8080/weatherforecast
curl http://localhost:8080/health
```

### What happens under the hood

1. Deskribe reads `deskribe.json` — sees `postgres` resource + `api` service
2. Environment `local` maps `postgres` backend to `helm` (via `envs/local.json`)
3. **HelmBackendAdapter** runs `helm upgrade --install` with the Bitnami PostgreSQL chart
4. After Helm install, the adapter reads the generated K8s secret to get the password
5. Deskribe builds the connection string and injects it into the Weather API deployment
6. **KubernetesRuntimeAdapter** renders and applies Namespace + Secret + Deployment + Service

## 2. Deploy to Azure

```bash
# Login to Azure
az login
az account set --subscription <your-subscription-id>

# Get AKS credentials
az aks get-credentials --resource-group rg-prod-eu --name aks-prod-eu

# Push image to ACR
az acr login --name myacr
docker tag weather-api:local myacr.azurecr.io/weather-api:v1
docker push myacr.azurecr.io/weather-api:v1

# Deploy (Postgres via Pulumi + app to AKS)
dotnet run --project src/Deskribe.Cli -- apply \
  -f examples/weather-api/deskribe.json \
  --env prod-eu \
  --platform examples/platform-config \
  --image api=myacr.azurecr.io/weather-api:v1
```

### What happens under the hood

1. Environment `prod-eu` uses the default `pulumi` backend from `base.json`
2. **PulumiBackendAdapter** (Local Program mode) provisions Azure DB for PostgreSQL Flexible Server
3. Stack outputs provide the real connection string
4. Deskribe resolves `@resource(postgres).connectionString` and deploys to AKS

## 3. Secrets Strategy

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
You then encrypt it with `kubeseal` before committing to git.

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
