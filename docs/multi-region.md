# Multi-Region Deployments

Deskribe supports multi-region deployments through environment files. No code changes are needed — create separate env files per region and use the `--env` flag to select the target.

## Single-Region (Default)

Use a single environment file per stage:

```bash
deskribe plan -f deskribe.json --env dev --platform ./platform-config
deskribe plan -f deskribe.json --env prod --platform ./platform-config
```

The region comes from `base.json` defaults (e.g., `"region": "westeurope"`).

## Multi-Region

Create one environment file per region:

**`envs/prod-us.json`**
```json
{
  "name": "prod-us",
  "defaults": {
    "region": "eastus2",
    "replicas": 3,
    "ha": true
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
    "ha": true
  }
}
```

Then target each region explicitly:

```bash
deskribe plan -f deskribe.json --env prod-us --platform ./platform-config
deskribe plan -f deskribe.json --env prod-eu --platform ./platform-config
```

Each command uses the region specified in the corresponding env file.

## Per-Resource Region Override

Individual resources can override the environment region via their `properties`:

```json
{
  "type": "postgres",
  "size": "l",
  "properties": {
    "region": "eastus2"
  }
}
```

This is useful when a resource must be in a specific region regardless of the environment target (e.g., a shared database).

## Generating Artifacts for Multiple Regions

```bash
# Generate for US region
deskribe generate -f deskribe.json --env prod-us -p ./platform-config -o ./output --output-format k8s-only

# Generate for EU region
deskribe generate -f deskribe.json --env prod-eu -p ./platform-config -o ./output --output-format k8s-only
```

Each generates a separate set of Kustomize overlays under `overlays/prod-us/` and `overlays/prod-eu/`.

## Namespace Pattern

The namespace pattern `{app}-{env}` automatically differentiates regions:

- `payments-api-prod-us`
- `payments-api-prod-eu`

## CI/CD Matrix

Use a GitHub Actions matrix to deploy to multiple regions:

```yaml
strategy:
  matrix:
    env: [prod-us, prod-eu]
steps:
  - run: deskribe generate -f deskribe.json --env ${{ matrix.env }} -p ./platform-config -o ./output --output-format k8s-only
```
