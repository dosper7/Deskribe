# Examples

- `payments-api/`: the developer side. A manifest and a small .NET Minimal API that reads the environment variables the manifest defines.
- `platform-config/`: the platform side. A split-file platform config with environments `dev`, `prod`, `prod-eu` and `prod-us`. It maps resources to `pulumi`, so it demonstrates `validate`, `plan` and `generate`; applying it needs a Pulumi program at `./infra`, which this repository does not contain.
- `platform-config-local/`: a minimal platform config for environments `dev` and `prod` that maps every resource to the `static` provisioner, with no cloud infrastructure.

## payments-api

`payments-api/deskribe.json` declares `postgres` (size `m`, version `16`), `redis`, and `kafka.messaging` with the topic `payments.transactions`, and one service whose environment variables reference them. The `dev` and `prod` overrides set replicas, CPU and memory.

| Environment variable | Reference | Sensitive |
|---|---|---|
| `ConnectionStrings__Postgres` | `@resource(postgres).connectionString` | yes |
| `Redis__Endpoint` | `@resource(redis).endpoint` | no |
| `Kafka__BootstrapServers` | `@resource(kafka.messaging).bootstrapServers` | no |

`src/` has the API (`/api/payments`, `/health`) and a multi-stage `Dockerfile`. The manifest does not name an image; you supply one with `--image`.

## platform-config

| File | Contents |
|---|---|
| `base.json` | organization, defaults (`westeurope`, 2 replicas, `250m`, `512Mi`, `{app}-{env}`, `opaque`), runtime `kubernetes` (container port 8080, service port 80, health path `/health`, rollout timeout 300s), the capability catalog, `provisioners` (everything to `pulumi`), `provisionerConfigs` (`pulumi`, a `platform-output` mapping), policies (`allowedRegions`, limits) |
| `envs/dev.json` | default replicas, CPU and memory; `opaque` secrets |
| `envs/prod.json` | 3 replicas, `500m`, `1Gi`, `ha`, `external-secrets` with store `platform-keyvault`, and an `alertRouting` section, which is read but not used |
| `envs/prod-eu.json`, `envs/prod-us.json` | region-specific production environments (`westeurope`, `eastus2`) |

The `platform-output` mappings in `base.json` are not reached, because no resource type maps to `platform-output`. To see them, change `"postgres": "pulumi"` to `"postgres": "platform-output"` in a copy.

Every key is described in the [configuration reference](../docs/configuration-reference.md).

## platform-config-local

Static outputs for the three resource types: `postgres` and `redis` connection strings come from the environment variables `DESKRIBE_LOCAL_POSTGRES_CONNECTION` and `DESKRIBE_LOCAL_REDIS_CONNECTION`, the other values are fixed (`*.shared.svc.cluster.local`). One replica, `100m`, `128Mi`, limits `maxReplicas: 5`. Note that the manifest's own `dev` and `prod` overrides still win over these defaults.

## Try it

From the repository root, after `dotnet build src/Deskribe.Cli`. The examples use the built CLI directly; if you installed the tool, replace the first two words with `deskribe`.

```bash
CLI="dotnet src/Deskribe.Cli/bin/Debug/net10.0/Deskribe.Cli.dll"

$CLI validate -f examples/payments-api/deskribe.json -p examples/platform-config --env dev
$CLI plan     -f examples/payments-api/deskribe.json -p examples/platform-config --env dev \
              --image payments-api=ghcr.io/acme/payments-api:1.0.0
```

`generate` needs more:

```bash
# prod uses external-secrets, so it can be generated; the non-sensitive outputs come from a file
cat > outputs.json <<'JSON'
{
  "redis": { "endpoint": "redis.prod.example.com:6380" },
  "kafka.messaging": { "bootstrapServers": "kafka.prod.example.com:9093" }
}
JSON
$CLI generate -f examples/payments-api/deskribe.json -p examples/platform-config --env prod \
              --image payments-api=ghcr.io/acme/payments-api:1.0.0 --outputs outputs.json -o generated
```

`generate` for `dev` fails with `Cannot generate artifacts ... 'opaque' secrets strategy would write them to disk`, because `dev` uses `opaque`. Use `--output-format infra-only` there if you only want the provisioner files. `apply`, `status` and `destroy` need a cluster (and, with `platform-config`, Pulumi); see the [README](../README.md) and the [CI/CD guide](../docs/cicd-integration.md).
