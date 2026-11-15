# Configuration Reference

Deskribe uses two kinds of JSON documents:

1. **`deskribe.json`** — the application manifest, owned by developers (the *what*).
2. **Platform config** — owned by platform teams (the *how*), provided as either:
   - **Split-file format**: a directory with `base.json` + `envs/{env}.json` files (see `examples/platform-config/`), or
   - **Single-file format**: one `platform.json` containing an `environments` map.

This reference documents every field as defined in the C# models under `src/Deskribe.Sdk/Models/`. JSON property names are camelCase.

---

## Manifest (`deskribe.json`)

Model: `DeskribeManifest` (`src/Deskribe.Sdk/Models/DeskribeManifest.cs`)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Application name. Used in token substitution (`{app}`) and generated names. |
| `team` | string | no | `null` | Owning team. Used in token substitution (`{team}`); defaults to `"default"` when absent during artifact generation. |
| `resources` | `ResourceDescriptor[]` | no | `[]` | Resources the app needs (databases, caches, messaging, …). |
| `services` | `ServiceDefinition[]` | no | `[]` | Deployable services and their environment wiring. |

### `ResourceDescriptor`

Model: `ResourceDescriptor` (`src/Deskribe.Sdk/Models/ResourceDescriptor.cs`)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `type` | string | **yes** | — | Resource type, e.g. `postgres`, `redis`, `kafka.messaging`. Must match a registered resource plugin. |
| `size` | string | no | `null` | T-shirt size (`xs`, `s`, `m`, `l`, `xl`); valid values are enforced by each resource plugin. |
| *(extension data)* | any | no | — | All additional properties (e.g. `version`, `ha`, `topics`) are captured and passed to the resource plugin for validation/planning. |

### `ServiceDefinition`

Model: `ServiceDefinition` (`src/Deskribe.Sdk/Models/ServiceDefinition.cs`)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | no | `null` | Service name. |
| `env` | `Dictionary<string, string>` | no | `{}` | Environment variables. Values may use `@resource(<type>).<output>` references (e.g. `"Kafka__BootstrapServers": "@resource(kafka.messaging).endpoint"`). |
| `overrides` | `Dictionary<string, ServiceOverride>` | no | `{}` | Per-environment workload overrides, keyed by environment name (e.g. `"dev"`, `"prod"`). |

### `ServiceOverride`

| Field | Type | Default | Description |
|---|---|---|---|
| `replicas` | int? | `null` | Replica count override for this environment. |
| `cpu` | string | `null` | CPU request/limit, e.g. `"250m"`. |
| `memory` | string | `null` | Memory request/limit, e.g. `"512Mi"`. |

Service overrides take precedence over platform `defaults` for the matching environment.

---

## Platform config

### `base.json` / single-file root — `PlatformConfig`

Model: `PlatformConfig` (`src/Deskribe.Sdk/Models/PlatformConfig.cs`)

| Field | Type | Default | Overridable in env file? | Description |
|---|---|---|---|---|
| `organization` | string | `null` | no | Organization name. Used in token substitution (`{org}`); defaults to `"default"` during artifact generation when absent. |
| `defaults` | `PlatformDefaults` | `{}` | **yes** — merged per-field by `envs/{env}.json` | Shared defaults for workloads and resources. |
| `runtime` | `RuntimeConfig` | `{ "name": "kubernetes" }` | no | Target runtime. |
| `provisionerConfigs` | `Dictionary<string, Dictionary<string, JsonElement>>` | `{}` | no | Provisioner-specific configuration, keyed by provisioner name (e.g. `pulumi`, `platform-output`). Content is provisioner-defined. |
| `provisioners` | `Dictionary<string, string>` | `{}` | no | Maps resource type → provisioner name, e.g. `{ "postgres": "pulumi" }`. |
| `policies` | `PlatformPolicies` | `{}` | no | Guardrails validated at `validate`/`plan` time. |
| `environments` | `Dictionary<string, EnvironmentConfig>` | `null` | — | **Single-file format only**: inline environment configs keyed by name. Mutually exclusive with the split-file `envs/` directory. |

### `RuntimeConfig`

| Field | Type | Default | Description |
|---|---|---|---|
| `name` | string | `"kubernetes"` | Runtime plugin name (e.g. `kubernetes`). |
| `config` | `Dictionary<string, JsonElement>` | `{}` | Runtime-specific settings passed through to the runtime plugin (e.g. `namespacePattern`). |

### `PlatformDefaults`

Set in `base.json` under `defaults`; any subset can be overridden per environment via `envs/{env}.json` → `defaults`.

| Field | Type | Default | Description |
|---|---|---|---|
| `runtime` | string | `null` | Runtime name override. |
| `region` | string | `null` | Deployment region, e.g. `"westeurope"`. |
| `replicas` | int? | `null` | Default workload replica count. |
| `cpu` | string | `null` | Default CPU request/limit, e.g. `"250m"`. |
| `memory` | string | `null` | Default memory request/limit, e.g. `"512Mi"`. |
| `namespacePattern` | string | `null` | K8s namespace template, e.g. `"{app}-{env}"`. Supports token substitution. |
| `ha` | bool? | `null` | Enable high availability by default. Used by resource plugins (e.g. postgres `{ha}` token). |
| `secretsStrategy` | string | `null` (engine default: `opaque`) | Secret rendering: `opaque`, `external-secrets`, or `sealed-secrets`. |
| `externalSecretsStore` | string | `null` | SecretStore/ClusterSecretStore name used with `external-secrets` strategy. |

### `PlatformPolicies`

| Field | Type | Default | Description |
|---|---|---|---|
| `allowedRegions` | string[] | `[]` | Regions deployments may target; validated against `defaults.region`. |
| `enforceTLS` | bool | `false` | Require TLS on provisioned resources. |

### Environment files (`envs/{env}.json`) — `EnvironmentConfig`

Model: `EnvironmentConfig` (`src/Deskribe.Sdk/Models/EnvironmentConfig.cs`)

Selected with `--env <name>`, where `<name>` is the file name without `.json` (e.g. `--env dev` loads `envs/dev.json`).

| Field | Type | Default | Description |
|---|---|---|---|
| `name` | string | `null` | Environment name; should match the file name. |
| `defaults` | `PlatformDefaults` | `null` | Per-environment overrides for `base.json` → `defaults`. Same fields as `PlatformDefaults` above (region, replicas, cpu, memory, namespacePattern, ha, secretsStrategy, …). |
| `alertRouting` | `Dictionary<string, List<string>>` | `{}` | Alert routing channels per severity, e.g. `{ "critical": ["pagerduty://oncall"] }`. |

> ⚠️ `replicas`, `cpu`, `memory`, etc. **must** live under `defaults` in env files — the `EnvironmentConfig` model has no root-level `replicas`/`cpu`/`memory` properties, and root-level values are silently ignored during JSON deserialization.

**Override precedence** (highest wins):

1. Manifest `services[].overrides.{env}` (replicas/cpu/memory)
2. `envs/{env}.json` → `defaults`
3. `base.json` → `defaults`

---

## Token substitution (`platform-output` provisioner)

Implemented by `PlatformOutputProvisioner.ResolveTemplate` (`src/Plugins/Provisioners/PlatformOutput/PlatformOutputProvisioner.cs`), used in `provisionerConfigs.platform-output.basePath` and in each module's `mappings` values.

### Simple tokens — `{name}`

Any token is replaced by its value; unknown tokens are left as-is.

Built-in context tokens:

| Token | Source |
|---|---|
| `{app}` | Manifest `name` |
| `{env}` | Target environment (`--env`) |
| `{team}` | Manifest `team`, or `"default"` |
| `{org}` | Platform config `organization`, or `"default"` |

Additionally, every key in the resource's planned `Configuration` becomes a token. Which keys exist depends on the resource plugin; for `postgres` (`PostgresResourceProvider`) these are:

| Token | Source |
|---|---|
| `{size}` | Resource `size` (default `s` if unset) |
| `{version}` | Resource `version` property (default `"16"` for postgres) |
| `{ha}` | Resource `ha` property, falling back to environment `defaults.ha`, then `false` |
| `{appName}` | Manifest `name` |
| `{environment}` | Target environment |
| `{region}` | Platform `defaults.region` |

### Map expression — `{field:map(key=value,...)}`

Translates a token value through a lookup table:

```
{size:map(s=B_Standard_B1ms,m=GP_Standard_D2s_v3,l=GP_Standard_D4s_v3)}
```

- `field` is resolved like a simple token, then the value is looked up in the comma-separated `key=value` pairs.
- If the value has no matching key, the raw value is used unchanged.

### Example (from `examples/platform-config/base.json`)

```json
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
      }
    }
  }
}
```

For each planned resource whose type matches a module key, the provisioner writes `<outputDir>/<basePath>/<moduleName>/<fileName>` containing the resolved mappings as JSON. The `platform-output` provisioner is *output-only*: it generates files for external platform tooling (e.g. Terraform) and does not provision infrastructure itself.

---

## See also

- [examples/README.md](../examples/README.md) — runnable walkthrough of the sample manifest and platform config
- [docs/platform-teams.md](platform-teams.md) — platform team workflows
- [docs/plugin-development.md](plugin-development.md) — writing resource/provisioner/runtime plugins
