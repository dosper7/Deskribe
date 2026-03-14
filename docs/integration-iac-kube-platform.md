# Integration Guide: iac-kube-platform (Platform Output Provisioner)

This guide explains how Deskribe integrates with an existing `iac-kube-platform` Terraform repository using the built-in `platform-output` provisioner. No adapter scripts required.

## How It Works

1. Platform team configures output mappings in `platform-config/base.json` (or uses the Web UI Output Mapper)
2. Developer writes `deskribe.json` (what they need)
3. `deskribe generate` produces files in the exact folder structure expected by `iac-kube-platform`
4. Terraform applies the infrastructure using existing modules

## Step 1: Configure Platform Output Mappings

In your platform config (`base.json`), add a `platform-output` provisioner config and assign it to resource types:

```json
{
  "organization": "acme",
  "provisionerConfigs": {
    "platform-output": {
      "basePath": "{team}/{app}/{env}",
      "modules": {
        "postgres": {
          "moduleName": "postgresql-flexible-server",
          "fileName": "terraform.tfvars.json",
          "mappings": {
            "db_name": "{app}-db",
            "sku_name": "{size:map(s=B_Standard_B1ms,m=GP_Standard_D2s_v3,l=GP_Standard_D4s_v3)}",
            "pg_version": "{version}",
            "environment": "{env}",
            "team": "{team}",
            "ha_enabled": "{ha}"
          }
        },
        "redis": {
          "moduleName": "redis-cache",
          "fileName": "terraform.tfvars.json",
          "mappings": {
            "name": "{app}-redis",
            "sku_name": "Standard",
            "environment": "{env}"
          }
        }
      }
    }
  },
  "provisioners": {
    "postgres": "platform-output",
    "redis": "platform-output"
  }
}
```

### Template Tokens

- `{app}`, `{env}`, `{team}`, `{org}` — from the manifest and platform context
- `{fieldName}` — from the resource configuration (size, version, ha, etc.)
- `{field:map(a=x,b=y)}` — value mapping (e.g., map size `s`/`m`/`l` to Azure SKU names)

## Step 2: Write Your Manifest

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

## Step 3: Generate Output

```bash
deskribe generate \
  -f deskribe.json \
  --env prod \
  --platform ./platform-config \
  -o ./output
```

This produces:

```
output/
  mobile-replenishment/payments-api/prod/
    postgresql-flexible-server/
      terraform.tfvars.json
    redis-cache/
      terraform.tfvars.json
```

The `postgresql-flexible-server/terraform.tfvars.json` contains:

```json
{
  "db_name": "payments-api-db",
  "sku_name": "GP_Standard_D2s_v3",
  "pg_version": "16",
  "environment": "prod",
  "team": "mobile-replenishment",
  "ha_enabled": ""
}
```

## Step 4: Team Grouping → Folder Structure

The `"team"` field in the manifest maps directly to the folder structure expected by `iac-kube-platform`:

```
deskribe.json: "team": "mobile-replenishment"
    ↓
output/mobile-replenishment/payments-api/prod/postgresql-flexible-server/terraform.tfvars.json
output/mobile-replenishment/payments-api/prod/redis-cache/terraform.tfvars.json
```

## Step 5: Use the Web UI Output Mapper

Instead of writing JSON by hand, use the Output Mapper page in the Deskribe Web UI:

1. Open the Web UI → **Output Mapper** in the sidebar
2. Set the base path template (e.g., `{team}/{app}/{env}`)
3. Enable resource types and configure module name, file name, and field mappings
4. Use the live JSON preview on the right to verify the config
5. Click **Copy JSON** and paste into your `base.json`

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

      - name: Install Deskribe CLI
        run: dotnet tool install --global deskribe-cli || true

      - name: Generate platform output
        run: |
          deskribe generate \
            -f deskribe.json \
            --env prod \
            -p platform-config \
            -o ./output

      - name: Create PR in iac-kube-platform
        run: |
          # Push generated config to iac-kube-platform via PR
          gh pr create --repo org/iac-kube-platform \
            --title "Update $APP_NAME infra config" \
            --body "Auto-generated from deskribe.json by platform-output provisioner"
```
