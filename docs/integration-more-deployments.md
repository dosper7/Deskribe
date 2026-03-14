# Integration Guide: more-deployments (Kustomize)

This guide explains how to integrate Deskribe-generated manifests into an existing `more-deployments`-style Kustomize repository.

## Overview

Deskribe generates Kustomize-compatible `base/` and `overlays/{env}/` structures. These map directly to the `more-deployments` repo layout:

| Deskribe output | more-deployments target |
|---|---|
| `{app}/base/deployment.yaml` | `kustomize/base/{app}/deployment.yaml` |
| `{app}/base/secrets.yaml` | `kustomize/base/{app}/secrets.yaml` |
| `{app}/base/kustomization.yaml` | `kustomize/base/{app}/kustomization.yaml` |
| `{app}/overlays/{env}/kustomization.yaml` | `kustomize/overlays/{env}/{app}/kustomization.yaml` |

With team grouping enabled, the output path includes the team prefix:

| Deskribe output | more-deployments target |
|---|---|
| `{team}/{app}/base/...` | `kustomize/base/{team}/{app}/...` |
| `{team}/{app}/overlays/{env}/...` | `kustomize/overlays/{env}/{team}/{app}/...` |

## Step 1: Create the Manifest

In your application repo, create `deskribe.json`:

```json
{
  "name": "payments-api",
  "team": "mobile-replenishment",
  "resources": [
    { "type": "postgres", "size": "m", "version": "16" }
  ],
  "services": [
    {
      "env": {
        "ConnectionStrings__Postgres": "@resource(postgres).connectionString"
      },
      "overrides": {
        "dev": { "replicas": 1 },
        "prod": { "replicas": 3, "cpu": "500m", "memory": "1Gi" }
      }
    }
  ]
}
```

## Step 2: Generate K8s Manifests

```bash
deskribe generate \
  -f deskribe.json \
  --env prod \
  --platform ./platform-config \
  -o ./output \
  --output-format k8s-only \
  --image api=ghcr.io/acme/payments-api:v1.2.3
```

This produces:
```
output/
  mobile-replenishment/
    payments-api/
      base/
        deployment.yaml
        secrets.yaml
        kustomization.yaml
      overlays/
        prod/
          kustomization.yaml
```

## Step 3: Copy to more-deployments

Copy the generated output into the deployments repo:

```bash
# Base manifests
cp -r output/mobile-replenishment/payments-api/base/* \
  more-deployments/kustomize/base/mobile-replenishment/payments-api/

# Overlay
cp -r output/mobile-replenishment/payments-api/overlays/prod/* \
  more-deployments/kustomize/overlays/prod/mobile-replenishment/payments-api/
```

## Step 4: GitHub Actions CI Integration

Automate the generate-and-push flow in your application repo's CI:

```yaml
name: Deploy via Deskribe
on:
  push:
    branches: [main]

jobs:
  generate-and-push:
    runs-on: ubuntu-latest
    strategy:
      matrix:
        env: [dev, prod]
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Install Deskribe CLI
        run: dotnet tool install --global deskribe-cli

      - name: Generate K8s manifests
        run: |
          deskribe generate \
            -f deskribe.json \
            --env ${{ matrix.env }} \
            -p platform-config \
            -o ./output \
            --output-format k8s-only \
            --image api=${{ github.repository }}:${{ github.sha }}

      - name: Push to deployments repo
        uses: cpina/github-action-push-to-another-repository@main
        env:
          API_TOKEN_GITHUB: ${{ secrets.DEPLOYMENTS_REPO_TOKEN }}
        with:
          source-directory: output/
          destination-github-username: ${{ github.repository_owner }}
          destination-repository-name: more-deployments
          target-directory: kustomize/
          target-branch: main
```

## Step 5: Image Tag Updates

The existing `remote-action.yaml` workflow in `more-deployments` handles image tag updates. When the CI pushes updated overlays with the new image tag in the Kustomize patch, the deployment pipeline picks it up automatically.

The overlay's `kustomization.yaml` contains the image tag in the JSON patch:

```yaml
patches:
- target:
    kind: Deployment
    name: payments-api
  patch: |
    - op: replace
      path: /spec/template/spec/containers/0/image
      value: ghcr.io/acme/payments-api:abc123
```

## Verify

```bash
# From the more-deployments repo
kubectl apply -k kustomize/overlays/prod/mobile-replenishment/payments-api/ --dry-run=client
```
