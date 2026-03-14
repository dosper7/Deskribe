# Integration Guide: iac-kube-platform (Terraform)

This guide explains how Deskribe integrates with an existing `iac-kube-platform` Terraform repository that manages infrastructure modules (PostgreSQL, Redis, Kafka, etc.).

## Deskribe's Role

Deskribe **generates intent**, it does not replace your Terraform modules. The flow is:

1. Developer writes `deskribe.json` (what they need)
2. Deskribe generates `terraform.tfvars.json` (structured config)
3. An adapter script maps the output to the YAML config format expected by `iac-kube-platform`
4. Terraform applies the infrastructure using existing modules

## Step 1: Generate Terraform Artifacts

```bash
deskribe generate \
  -f deskribe.json \
  --env prod \
  --platform ./platform-config \
  -o ./output \
  --output-format terraform-only
```

This produces:
- `terraform.tfvars.json` — resource configuration
- `helm-values.yaml` — workload Helm values
- `bindings.json` — resource output bindings

## Step 2: Terraform tfvars Output

For a manifest like:

```json
{
  "name": "payments-api",
  "team": "mobile-replenishment",
  "resources": [
    { "type": "postgres", "size": "m", "version": "16" },
    { "type": "redis" }
  ]
}
```

Deskribe generates `terraform.tfvars.json`:

```json
{
  "app_name": "payments-api",
  "environment": "prod",
  "region": "westeurope",
  "postgres_config": {
    "sku": "GP_Gen5_2",
    "storageMb": 65536,
    "version": "16",
    "ha": true,
    "namespace": "payments-api-prod",
    "helmChart": "oci://registry-1.docker.io/bitnamicharts/postgresql"
  },
  "redis_config": {
    "sku": "Standard",
    "capacity": 1,
    "namespace": "payments-api-prod",
    "helmChart": "oci://registry-1.docker.io/bitnamicharts/redis"
  }
}
```

## Step 3: Map to iac-kube-platform Config

The `iac-kube-platform` repo uses YAML config files like `optional-modules-config.yaml`. A thin adapter script converts the Deskribe output:

```bash
#!/bin/bash
# adapt-deskribe-to-iac.sh
# Converts Deskribe terraform.tfvars.json to iac-kube-platform YAML format

TFVARS="output/terraform.tfvars.json"
APP_NAME=$(jq -r '.app_name' "$TFVARS")
ENV=$(jq -r '.environment' "$TFVARS")
REGION=$(jq -r '.region' "$TFVARS")

# Example: Generate postgresql-flexible-server module config
if jq -e '.postgres_config' "$TFVARS" > /dev/null 2>&1; then
  cat <<EOF > "environments/${TEAM:-default}/${APP_NAME}-postgres.yaml"
module: postgresql-flexible-server
name: ${APP_NAME}-db
environment: ${ENV}
region: ${REGION}
sku: $(jq -r '.postgres_config.sku' "$TFVARS")
storage_mb: $(jq -r '.postgres_config.storageMb' "$TFVARS")
version: $(jq -r '.postgres_config.version' "$TFVARS")
ha_enabled: $(jq -r '.postgres_config.ha // false' "$TFVARS")
EOF
fi
```

## Step 4: Team Grouping

The `"team"` field in the manifest maps directly to the folder structure in `iac-kube-platform`:

```
deskribe.json: "team": "mobile-replenishment"
    ↓
iac-kube-platform/environments/mobile-replenishment/payments-api-postgres.yaml
iac-kube-platform/environments/mobile-replenishment/payments-api-redis.yaml
```

## Step 5: Unconfigured Resources

When a resource type has no provisioner configured in the platform config, Deskribe emits a warning instead of failing:

```
⚠ No provisioner configured for 'kafka.messaging'. This resource must be provisioned manually.
```

The developer must handle these resources through existing manual processes or by adding provisioner mappings to the platform config.

## Step 6: CI/CD Integration

```yaml
name: Generate Infra Config
on:
  push:
    branches: [main]
    paths: ['deskribe.json']

jobs:
  generate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Generate Terraform artifacts
        run: |
          deskribe generate \
            -f deskribe.json \
            --env prod \
            -p platform-config \
            -o ./output \
            --output-format terraform-only

      - name: Convert to iac-kube-platform format
        run: ./scripts/adapt-deskribe-to-iac.sh

      - name: Create PR in iac-kube-platform
        run: |
          # Push generated config to iac-kube-platform via PR
          gh pr create --repo org/iac-kube-platform \
            --title "Update $APP_NAME infra config" \
            --body "Auto-generated from deskribe.json"
```

## Future: Custom Provisioner Plugin

A custom provisioner plugin can directly generate `iac-kube-platform` YAML format, eliminating the adapter script:

```csharp
[DeskribePlugin("iac-kube-platform")]
public class IacKubePlatformPlugin : IPlugin
{
    public void Register(IPluginRegistrar registrar)
    {
        registrar.RegisterProvisioner(new IacKubePlatformProvisioner());
    }
}
```

This plugin would implement `IProvisioner.GenerateArtifactsAsync()` to produce the YAML configs directly in the format expected by your Terraform modules.
