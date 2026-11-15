# Deskribe Examples

Two example folders show both sides of the Deskribe contract:

- **`payments-api/`** — the developer side: an app manifest plus service code wired to `@resource(...)` env vars.
- **`platform-config/`** — the platform side: split-file platform configuration with provisioner mappings.

## payments-api/

`payments-api/deskribe.json` declares the app's intent:

- **Resources**: `postgres` (size `m`, version `16`), `redis`, and `kafka.messaging` with a `payments.transactions` topic (6 partitions, 168h retention, owners/consumers).
- **Services**: environment variables bound to resource outputs via `@resource()` references, plus per-environment `overrides` (replicas/cpu/memory for `dev` and `prod`).

The manifest's env vars match what the sample app reads in `src/Program.cs`:

| Manifest env var | Reference | Consumed in |
|---|---|---|
| `ConnectionStrings__Postgres` | `@resource(postgres).connectionString` | EF Core `DbContext` |
| `Redis__Endpoint` | `@resource(redis).endpoint` | Redis cache |
| `Kafka__BootstrapServers` | `@resource(kafka.messaging).endpoint` | Kafka producer |

`src/` is a .NET Minimal API (POST/GET `/api/payments`, health check) with a multi-stage `Dockerfile`.

## platform-config/

Split-file format — a shared base plus per-environment overlays:

```
platform-config/
  base.json       # organization defaults, runtime, provisioner mappings, policies
  envs/
    dev.json      # dev overrides (defaults: replicas/cpu/memory)
    prod.json     # prod overrides (+ ha, alertRouting)
    prod-eu.json  # region-specific prod override (westeurope)
    prod-us.json  # region-specific prod override (eastus2)
```

- **`base.json`** sets `organization`, shared `defaults` (region, replicas, cpu, memory, `namespacePattern`), the `runtime` (`kubernetes`), the resource→provisioner `provisioners` map, and `policies` (`allowedRegions`, `enforceTLS`).
- **`provisionerConfigs`** configures provisioners. The `platform-output` provisioner demonstrates **token substitution**: a `basePath` template like `{team}/{app}/{env}`, and per-resource `modules` whose `mappings` translate resource properties into module inputs. Tokens like `{app}`, `{env}`, `{team}`, `{size}`, `{version}`, `{ha}` are replaced with planned resource values; the map expression `{size:map(s=B_Standard_B1ms,m=GP_Standard_D2s_v3,l=GP_Standard_D4s_v3)}` translates a resource `size` token into a concrete SKU.
- **`envs/{env}.json`** files override `defaults` (e.g. replicas, ha) per environment and can add `alertRouting`. The file is selected with `--env <name>`.

See [docs/configuration-reference.md](../docs/configuration-reference.md) for the full schema.

## Try it

Run from the repo root (no CLI install needed):

```bash
# Validate the manifest against the dev environment
dotnet run --project src/Deskribe.Cli -- validate \
  -f examples/payments-api/deskribe.json \
  --platform examples/platform-config --env dev

# Show the execution plan (resources + workload)
dotnet run --project src/Deskribe.Cli -- plan \
  -f examples/payments-api/deskribe.json \
  --platform examples/platform-config --env dev

# Generate deployment artifacts (K8s + Kustomize + terraform.tfvars.json + helm-values.yaml + bindings.json)
dotnet run --project src/Deskribe.Cli -- generate \
  -f examples/payments-api/deskribe.json \
  --platform examples/platform-config --env dev \
  -o ./generated

# Generate only K8s manifests + Kustomize overlays
dotnet run --project src/Deskribe.Cli -- generate \
  -f examples/payments-api/deskribe.json \
  --platform examples/platform-config --env dev \
  -o ./generated --output-format k8s-only
```

Try other environments by swapping `--env dev` for `prod`, `prod-eu`, or `prod-us`. Add `--image api=ghcr.io/acme/payments-api:v1.2.3` to `plan`, `generate`, or `apply` to pin a container image.
