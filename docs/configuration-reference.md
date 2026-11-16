# Configuration reference

This is the schema of the two files Deskribe reads, as the code reads them: the developer's `deskribe.json` manifest and the platform team's platform config. It also lists every validation rule and every plugin setting.

JSON property names are case-insensitive when read. Examples use camelCase, which is what the code writes.

- [Manifest](#manifest)
- [Resource types](#resource-types)
- [References and bindings](#references-and-bindings)
- [Platform config](#platform-config)
- [How values are merged](#how-values-are-merged)
- [Kubernetes runtime settings](#kubernetes-runtime-settings)
- [Secrets strategies](#secrets-strategies)
- [Provisioner settings](#provisioner-settings)
- [Validation rules](#validation-rules)
- [Limitations](#limitations)

## Manifest

```json
{
  "name": "payments-api",
  "team": "mobile-replenishment",
  "resources": [ { "type": "postgres", "size": "m", "version": "16" } ],
  "services": [
    {
      "name": "api",
      "env": { "ConnectionStrings__Postgres": "@resource(postgres).connectionString", "LOG_LEVEL": "info" },
      "overrides": { "prod": { "replicas": 3, "cpu": "500m", "memory": "1Gi" } }
    }
  ]
}
```

Unknown properties are rejected at the manifest, service and override levels. Unknown properties on a resource are rejected by that resource's provider (see [resource types](#resource-types)).

| Property | Type | Required | Meaning |
|---|---|---|---|
| `name` | string | yes | App name. Must be a DNS-1123 label (lowercase letters, digits, `-`; starts and ends alphanumeric; at most 63 characters). Used in labels, workload names and the default namespace |
| `team` | string | no | Same format as `name`. Used in the namespace pattern, labels, and generated paths. When absent, `{team}` in patterns becomes `default` |
| `resources` | array | no | Infrastructure the app needs |
| `services` | array | no | Workloads to deploy |

### Resource

| Property | Type | Meaning |
|---|---|---|
| `type` | string, required | `postgres`, `redis`, `kafka.messaging`, or a type added by a plugin |
| `name` | string | Distinguishes resources of the same type. Required when an app declares two of the same type |
| `size` | string | `xs`, `s`, `m`, `l`, `xl` by default; the platform can change the set. Postgres and Redis validate it. The Kafka provider accepts and ignores it |
| other | | Type-specific properties |

The resource key is `name` when set, otherwise `type`. Keys may contain letters, digits, `.`, `_` and `-` and must be unique within the manifest.

### Service

| Property | Type | Meaning |
|---|---|---|
| `name` | string | Required, and unique, when the manifest has more than one service. Optional for one service |
| `env` | map string to string | Environment variables. A value may contain `@resource(key).output` references mixed with literal text |
| `overrides` | map env name to object | Per-environment `replicas` (integer), `cpu`, `memory`. Every key must be an environment the platform defines |

The workload name is the app name when the manifest has exactly one service, otherwise `<app>-<service>`. The name must be lowercase letters, digits and `-`, start with a letter, end with a letter or digit, and be at most 63 characters.

Sizing for a workload is the override for the target environment, else the effective platform default (see [merging](#how-values-are-merged)). There is no image in the manifest: supply `--image name=image:tag`, where `name` is the service name or the workload name. With exactly one service and exactly one `--image`, the key does not matter. The CLI requires the `name=` part.

## Resource types

### postgres

| Property | Type | Default | Notes |
|---|---|---|---|
| `size` | string | `s` | Must be in `validSizes` |
| `version` | string | catalog `defaultVersion`, else `16` | Must be in `validVersions` (default `14`, `15`, `16`, `17`) |
| `ha` | boolean | effective `defaults.ha`, else `false` | |
| `sku` | string | none | Passed through to the provisioner as `sku` (intended as a cloud SKU name) |

Outputs: `connectionString` (sensitive), `host`, `port`, `database`.

### redis

| Property | Type | Default | Notes |
|---|---|---|---|
| `size` | string | `s` | Must be in `validSizes` |
| `version` | string | none | |
| `ha` | boolean | effective `defaults.ha`, else `false` | |
| `maxMemoryMb` | integer >= 1 | none | |

Outputs: `connectionString` (sensitive), `endpoint`, `host`, `port`.

### kafka.messaging

| Property | Type | Notes |
|---|---|---|
| `topics` | array, required, at least one | Topic objects, below |

| Topic property | Type | Default | Rules |
|---|---|---|---|
| `name` | string | | Required |
| `partitions` | integer | `3` | At least 1 |
| `retentionHours` | integer | `168` | At least 1 |
| `owners` | array of string | | At least one |
| `consumers` | array of string | `[]` | |

Outputs: `bootstrapServers`, `endpoint`. Neither is sensitive.

### What the provider plans

`plan` shows each resource's configuration as the provider produced it. This is what provisioners receive. For postgres it is `version`, `size`, `ha`, `port`, `appName`, `environment`, `region` and, when set, `sku`. Redis has `size`, `ha`, `port`, `appName`, `environment`, `region` and, when set, `version` and `maxMemoryMb`. Kafka has `appName`, `environment`, `region` and `topics`. `region` is the effective `defaults.region`. `port` is a string.

### Capability catalog

The platform's `resources` section sets constraints and defaults per resource type. For each key the provider looks in `resources.<type>` first and falls back to `provisionerConfigs.<type>`.

| Type | Key | Meaning |
|---|---|---|
| `postgres`, `redis` | `validSizes` | Array of strings replacing the default size list |
| `postgres` | `validVersions` | Array of strings replacing `14`..`17` |
| `postgres` | `defaultVersion` | Version used when the resource sets none |
| `postgres`, `redis` | `port` | String or number; reported in the plan. Default `5432` and `6379` |

`kafka.messaging` reads no catalog keys. Other keys in the catalog are ignored by the built-in providers.

## References and bindings

A service env value may contain `@resource(<key>).<output>`:

- `<key>` matches `[A-Za-z0-9._-]+`; `<output>` matches `[A-Za-z0-9_]+`.
- Several references and literal text may share one value: `Host=@resource(db).host;Port=@resource(db).port`.
- The key must exist in the manifest and the output must be one the resource provider declares. Anything else starting with `@resource(` that does not parse is reported as malformed.

Each reference becomes a binding: `{ workload, envVar, resourceKey, resourceType, output, sensitive }`. `sensitive` is true when the resource provider lists the output as a credential. The env var that contains it is then delivered through the [secrets strategy](#secrets-strategies), never inline. `plan` and the dashboard print `(secret)` for such variables.

`generate` writes the bindings to `bindings.json`. It carries no values:

```json
{
  "app": "payments-api",
  "team": "mobile-replenishment",
  "environment": "prod",
  "namespace": "payments-api-prod",
  "bindings": [
    { "workload": "payments-api", "envVar": "ConnectionStrings__Postgres", "resourceKey": "postgres",
      "resourceType": "postgres", "output": "connectionString", "sensitive": true }
  ]
}
```

## Platform config

Two layouts, chosen by the path passed to `--platform`:

- A directory containing `base.json` and `envs/<env>.json`. The environments are the file names in `envs/`.
- A single `.json` file whose `environments` map holds the per-environment objects.

An environment must be defined for `--env` to be accepted. Unknown properties are rejected at the top level of the platform config and inside `policies` and `policies.limits`, so a misspelt policy key is an error rather than a silently disabled rule. Inside `defaults`, `runtime` and the environment objects, unknown properties are still ignored.

```json
{
  "organization": "acme",
  "defaults": { "region": "westeurope", "replicas": 2, "cpu": "250m", "memory": "512Mi",
                "namespacePattern": "{app}-{env}", "secretsStrategy": "opaque" },
  "runtime": { "name": "kubernetes", "config": { "containerPort": 8080, "healthPath": "/health" } },
  "resources": { "postgres": { "validSizes": ["s", "m", "l"], "defaultVersion": "16" } },
  "provisioners": { "postgres": "pulumi", "redis": "pulumi", "kafka.messaging": "pulumi" },
  "provisionerConfigs": { "pulumi": { "projectDir": "./infra" } },
  "policies": { "allowedRegions": ["westeurope", "northeurope"], "limits": { "maxReplicas": 10, "maxCpu": "2", "maxMemory": "4Gi" } }
}
```

| Property | Meaning |
|---|---|
| `organization` | Free text. Only the `platform-output` provisioner reads it (the `{org}` token) |
| `defaults` | See below |
| `runtime.name` | Runtime plugin to use. Default `kubernetes` |
| `runtime.config` | Settings for that plugin; see [Kubernetes runtime settings](#kubernetes-runtime-settings) |
| `resources` | Capability catalog, per resource type; see [above](#capability-catalog) |
| `provisioners` | Resource type to provisioner name. Every resource type used by a manifest needs an entry |
| `provisionerConfigs` | Provisioner name to its settings; see [provisioner settings](#provisioner-settings) |
| `policies` | See below |
| `environments` | Single-file layout only: environment name to environment object |

### defaults

| Key | Engine default | Meaning |
|---|---|---|
| `region` | `westeurope` | Passed to resource plans and provisioners; checked against `policies.allowedRegions` |
| `replicas` | `2` | Workload replicas |
| `cpu` | `250m` | Workload CPU request and limit |
| `memory` | `512Mi` | Workload memory request and limit |
| `namespacePattern` | `{app}-{env}` | Namespace for the app. Tokens `{team}`, `{app}`, `{env}` |
| `ha` | `false` | Default for the resources' `ha` property |
| `secretsStrategy` | `opaque` | `opaque` or `external-secrets` |
| `externalSecretsStore` | none | Name of the secret store for `external-secrets`. Falls back to `runtime.config.externalSecretsStore` |
| `runtime` | `kubernetes` | Accepted and merged but not read by anything; `runtime.name` selects the runtime |

The engine defaults are constants in `EngineDefaults`. No host binds them from configuration.

### policies

| Key | Effect |
|---|---|
| `allowedRegions` | When non-empty, the effective region for the environment must be listed. Note the engine default region is `westeurope`: with a non-empty list and no `region` set anywhere, validation fails unless `westeurope` is allowed |
| `enforceTLS` | Accepted but not enforced. Validation reports a warning when it is `true` |
| `limits.maxReplicas` | Upper bound for a workload's effective replicas |
| `limits.maxCpu` | Upper bound for effective CPU, a Kubernetes quantity such as `2` or `1500m` |
| `limits.maxMemory` | Upper bound for effective memory, such as `4Gi` |

Limits apply to the resolved value for the target environment, whether it came from a developer override or from the platform defaults. Overrides for other environments are not checked against the limits.

### Environment file

```json
{ "name": "prod", "defaults": { "replicas": 3, "ha": true, "secretsStrategy": "external-secrets", "externalSecretsStore": "platform-keyvault" } }
```

`name` is optional and defaults to the file or map key. `defaults` takes the same keys as the platform `defaults`. `alertRouting` (map of string to list of string) is accepted and ignored: nothing reads it.

## How values are merged

For each field of `defaults`: the environment value, else the platform `base` value, else the engine default. A developer's `overrides.<env>` then replaces `replicas`, `cpu` and `memory` for that service in that environment. Nothing else can be overridden per environment from the manifest.

Resolution order for a workload's replicas, CPU or memory: manifest `overrides.<env>`, environment file `defaults`, platform `defaults`, engine default.

The same merged defaults (`Defaults`) reach resource providers, provisioners and the runtime, so a plugin never has to merge by itself.

## Kubernetes runtime settings

`runtime.config` is read on every call. All keys are optional. Unknown keys are ignored. A known key with an invalid value is an error with the message `Invalid Kubernetes runtime setting runtime.config.<key>: ...`. Integers and booleans may be written as strings. These errors are raised when manifests are rendered or applied, not by `validate` or `plan` (see [limitations](#limitations)).

| Key | Default | Rule | Meaning |
|---|---|---|---|
| `containerPort` | `8080` | 1-65535 | Port the container listens on |
| `servicePort` | `80` | 1-65535 | Port of the Service |
| `portName` | `http` | IANA service name, 1-15 chars, lowercase letters/digits/`-`, at least one letter | Name of the container and Service port |
| `healthPath` | none | Must start with `/` | When set, adds an HTTP readiness probe on `containerPort` (period 5s, failure threshold 3). Without it there is no probe |
| `createNamespace` | `true` | boolean | Create the namespace if missing. When `false` and the namespace is missing, apply fails |
| `rolloutTimeoutSeconds` | `300` | >= 1 | How long `apply` waits for each Deployment |
| `externalSecretsStore` | `default` | non-empty | Store name, used when `defaults.externalSecretsStore` is not set |
| `externalSecretsStoreKind` | `ClusterSecretStore` | `ClusterSecretStore` or `SecretStore` | |
| `externalSecretsRefreshInterval` | `1h` | non-empty | `refreshInterval` of each ExternalSecret |
| `externalSecretsKeyTemplate` | `{app}-{env}-{var}` | non-empty | Remote key per secret variable. Tokens `{app}`, `{env}`, `{workload}`, `{team}`, `{var}`. `{var}` is the variable name lower-cased with `__` replaced by `-` |

CPU and memory become both the request and the limit of the container.

## Secrets strategies

`defaults.secretsStrategy` (per environment) decides how a workload's sensitive variables, those holding a sensitive output, reach the pod. Variables without sensitive outputs are always written inline in the Deployment.

| Strategy | `apply` | `generate` |
|---|---|---|
| `opaque` | Deskribe writes the resolved values into a Kubernetes Secret named `<workload>-env` and the container reads it with `envFrom`. A reference to a sensitive output must resolve | Refused when a workload has sensitive variables, because the values would be written to disk. A workload with none is fine |
| `external-secrets` | Deskribe applies an `ExternalSecret` named `<workload>-env` that points at the store. It never writes or sees the values; you must put them in the store under the remote keys. Sensitive references may stay unresolved | Writes the ExternalSecret. No secret value is written |

There is no other strategy. `sealed-secrets` no longer exists; any other value fails validation with `Secrets strategy '...' is not supported (supported: external-secrets, opaque)`.

The `external-secrets` strategy emits `apiVersion: external-secrets.io/v1beta1`, `creationPolicy: Owner`, and assumes the External Secrets Operator and the store exist in the cluster. If the CRD is missing, `apply` fails with a message saying so.

## Provisioner settings

A resource type is fulfilled by the provisioner named in `provisioners`. The provisioner's own settings are in `provisionerConfigs.<provisioner name>`. The engine calls each provisioner once per operation with only its resources.

### pulumi

Runs the platform team's Pulumi program with the Pulumi Automation API (the Pulumi CLI must be installed).

| Key | Required | Meaning |
|---|---|---|
| `projectDir` | yes for `apply` and `destroy` | Directory of the Pulumi project. Relative paths are relative to the working directory |

- Stack name: `<app>-<env>`. The stack is created if missing.
- Config set on the stack, all as strings: `appName`, `environment`, `region` (if set), `team` (if set), and for every plan property `<resourceKey>:<property>` such as `postgres:size` and `postgres:ha` (`"true"`). Structured values such as Kafka `topics` are JSON text.
- Outputs: read from the stack outputs named `<resourceKey>.<output>` or `<resourceKey>_<output>`, where non-alphanumeric characters in the key become `_`. For the key `postgres`, export `postgres.connectionString` or `postgres_connectionString`. When two keys could match, the longest key wins. Outputs that match no resource are ignored.
- `destroy` runs `pulumi destroy` and then removes the stack, which discards its history.
- `generate` writes `Pulumi.<app>-<env>.json` in the output directory: the config above as a flat JSON object. It is an informational file, not Pulumi's stack-config file format.

### terraform

Runs the Terraform CLI in the platform team's root module.

| Key | Default | Meaning |
|---|---|---|
| `workingDir` | required | Directory of the root module. Must exist |
| `binary` | `terraform` | Executable to run |
| `workspace` | `{app}-{env}` | Workspace name. Tokens `{app}`, `{env}`, `{team}` |
| `outputName` | `deskribe_outputs` | Name of the Terraform output Deskribe reads |
| `lockTimeoutSeconds` | `1800` | How long to wait for another Deskribe Terraform operation on the same `workingDir` before failing. `0` fails immediately when the directory is busy |

`apply` runs `init -input=false`, `workspace select -or-create=true <workspace>`, `apply -auto-approve -input=false -var-file=<temp file>`, then `output -json`. `destroy` runs the same first two steps and `destroy -auto-approve`. The variable file is written to the system temp directory and deleted afterwards. A failing step stops the sequence and its stderr (the last 4000 characters) is the error.

Terraform keeps the selected workspace inside the working directory, so Deskribe runs one operation per `workingDir` at a time: each `apply` or `destroy` holds an exclusive lock for its whole sequence, within one process and across processes that share the directory. The lock is an operating-system file lock on an empty `.deskribe-terraform.lock` file that Deskribe creates in `workingDir` and never deletes; it is released when the operation ends or the process dies. It does not protect against Terraform being run in that directory by hand or by another tool, and file locks may be unreliable on some network file systems.

The root module must declare these variables: `app_name`, `environment`, `region`, `team`, `resources`. `resources` is a map from resource key to `{ type, config }`, where `config` is the plan above (see `terraform.tfvars.json` below).

The module must output `deskribe_outputs` (or `outputName`) as a map from resource key to a map from output name to a string, number or boolean:

```hcl
output "deskribe_outputs" {
  value = {
    postgres = { connectionString = "...", host = "...", port = "5432", database = "app" }
  }
  sensitive = true
}
```

A resource missing from the map gets no outputs, and its references then fail to resolve. A missing `deskribe_outputs` output is an error. `terraform output -json` returns sensitive values.

`generate` writes `infra/terraform/terraform.tfvars.json` containing the same variables:

```json
{ "app_name": "payments-api", "environment": "dev", "region": "northeurope", "team": "mobile-replenishment",
  "resources": { "postgres": { "type": "postgres", "config": { "version": "16", "size": "m", "ha": false, "port": "5432",
                 "appName": "payments-api", "environment": "dev", "region": "northeurope" } } } }
```

This file is produced by running `generate` with a platform that maps `postgres` to `terraform`. Terraform does not emit Helm values.

### static

For infrastructure the platform already runs. It creates nothing and declares outputs.

```json
"static": {
  "outputs": {
    "postgres": { "connectionString": "env:DESKRIBE_LOCAL_POSTGRES_CONNECTION", "host": "postgres.shared.svc.cluster.local", "database": "{app}" }
  },
  "environments": { "prod": { "outputs": { "postgres": { "host": "pg.prod.internal" } } } }
}
```

- `outputs` maps a resource key or a resource type to output names and templates. A key entry wins over a type entry.
- `environments.<env>.outputs` has the same shape and overrides the above for that environment.
- A template is a string. `{app}`, `{env}`, `{team}` (empty when there is no team), `{region}` and `{key}` are substituted. A template that is exactly `env:NAME` is replaced by the value of the process environment variable `NAME`, and fails if it is unset or empty.
- A resource with no matching entry gets no outputs.
- `apply` and `destroy` do nothing else; `generate` writes no files.

### platform-output

Writes input files for a platform team's own Terraform modules. Used with `generate` only. `apply` and `destroy` succeed without doing anything and return no outputs, so a resource mapped to it cannot satisfy references during `apply`.

| Key | Default | Meaning |
|---|---|---|
| `basePath` | `{team}/{app}/{env}` | Directory under the output directory. Tokens `{team}` (`default` when absent), `{app}`, `{env}`, `{org}` (`organization`, else `default`) |
| `modules` | required | Map from resource type (or the type with `.` replaced by `_`) to a module |
| `modules.<type>.moduleName` | | Directory under `basePath` |
| `modules.<type>.fileName` | `terraform.tfvars.json` | File name. With more than one resource of the type it becomes `<resourceKey>.<fileName>` |
| `modules.<type>.mappings` | | Map from output field name to a template |

Template tokens: `{app}`, `{env}`, `{team}`, `{org}`, `{key}`, and every property of the resource plan (`{size}`, `{version}`, `{ha}`, `{region}`, ...). `{size:map(s=B1,m=D2)}` translates the token's value through the table, and leaves it unchanged if it has no entry. A template that is exactly one plain token keeps the value's type (a boolean stays a boolean); any other template is a string. Resource types with no module are skipped. Having no `platform-output` entry in `provisionerConfigs` is an error.

Using the `platform-output` section of `examples/platform-config/base.json` and changing `"postgres": "pulumi"` to `"postgres": "platform-output"` in `provisioners`, `generate --env dev` produces `<out>/mobile-replenishment/payments-api/dev/postgresql-flexible-server/terraform.tfvars.json`:

```json
{ "db_name": "payments-api-db", "sku_name": "GP_Standard_D2s_v3", "pg_version": "16", "environment": "dev", "team": "mobile-replenishment", "ha_enabled": false }
```

The example platform config does not map any resource type to `platform-output`, so its `platform-output` section is not used unless you change `provisioners`.

## Validation rules

`validate` and everything that plans run all rules and report every failure, not only the first. When the manifest or platform config cannot be loaded, load errors from both files are reported together.

**Loading**

- The manifest and platform files exist and are valid JSON with the expected shapes. Unknown manifest, service and override properties are errors.
- `--env` is defined by the platform config. The error lists the known environments.

**Names**

- The app name, the team and the resolved namespace are DNS-1123 labels. Every workload name is additionally required to start with a letter.

**Resources**

- Keys use only letters, digits, `.`, `_`, `-`, and are unique.
- A resource provider is registered for the type.
- The platform's `provisioners` maps the type to a provisioner, and that provisioner is registered.
- The provider's own checks pass: unknown properties, wrong value types, size, version, topics. Provider errors are prefixed with `Resource '<key>':`.

**Services**

- With several services, each has a unique name.
- Every key of `overrides` is an environment the platform defines (this is checked for all of the manifest's overrides, not only the target environment).
- Env values: no malformed `@resource(` text, the key exists, the output is offered by the resource's provider.
- Effective replicas is at least 1, and not above `limits.maxReplicas`.
- Effective CPU and memory are valid quantities. CPU: a number with optional `m` suffix (`500m`, `1`, `0.5`). Memory: a number with optional `k`, `M`, `G`, `T`, `Ki`, `Mi`, `Gi`, `Ti`. Neither may exceed `limits.maxCpu` and `limits.maxMemory`.
- The limits themselves must be valid quantities.

**Platform**

- A runtime plugin is registered for `runtime.name`.
- `defaults.secretsStrategy` is `opaque` or `external-secrets`.
- When `allowedRegions` is non-empty, the effective region is in it.
- `enforceTLS: true` produces the warning `Platform policy 'enforceTLS' is declared but not enforced by Deskribe.`

**At apply or generate time**

- Every workload has an image.
- Every reference resolves, except a sensitive variable under `external-secrets`.
- `generate` refuses `opaque` workloads that have sensitive variables.

## Limitations

- `policies.enforceTLS` is not enforced.
- `validate` and `plan` check the manifest and the platform's policies. They do not check `runtime.config`, provisioner settings or whether a Pulumi `projectDir` or Terraform `workingDir` exists. An invalid `runtime.config` value fails `generate`, `apply` and `status` with `Runtime 'kubernetes' failed to ...: Invalid Kubernetes runtime setting ...`; `apply` asks the runtime to render the deployment first, so this happens before any provisioner runs. Provisioner settings are found when the provisioner runs.
- Typos inside the platform config's `defaults`, `runtime` and environment objects are not detected (see above).
- A manifest with `overrides` for an environment the platform does not define fails validation for every environment, not only the one named.
- `defaults.runtime` and `alertRouting` are accepted and unused.
- Pulumi and Terraform have been tested only with a mocked runner and a fake binary, not against real infrastructure.
