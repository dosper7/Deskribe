# Platform team guide

The platform config is the platform team's half of the contract. Developers say what they need in `deskribe.json`; the platform config says what is allowed, how each resource type is provisioned, and where and how workloads run. This guide is about organising one. The exact keys are in the [configuration reference](configuration-reference.md).

## What you decide

| Decision | Config |
|---|---|
| Which resource types exist and what sizes/versions are valid | `resources` (the capability catalog) |
| Which provisioner creates each type | `provisioners` and `provisionerConfigs` |
| Defaults every app gets | `defaults` (replicas, cpu, memory, region, namespace pattern, secrets strategy) |
| Hard limits | `policies.allowedRegions`, `policies.limits` |
| How workloads are deployed | `runtime` (`kubernetes` and its `config`) |
| Differences between environments and regions | one file per environment |

Deskribe checks the manifest against this at `validate`, `plan`, `apply` and `generate`, so developers learn about a violation before anything is provisioned. The rules are listed under [validation rules](configuration-reference.md#validation-rules).

## Layout

Either a single JSON file with an `environments` map, or a directory:

```
platform-config/
  base.json         organization, defaults, runtime, provisioners, catalog, policies
  envs/
    dev.json
    prod.json
    prod-eu.json
    prod-us.json
```

Each `envs/<name>.json` can override any `defaults` field, and the file's `name` is what developers pass as `--env`. An environment that has no file does not exist: `--env` with an unknown name is an error that lists the known ones. `examples/platform-config` is a complete directory.

Unknown keys are rejected at the top level and inside `policies` and `policies.limits`, so a misspelt policy fails loudly. Elsewhere (`defaults`, `runtime`, the environment files) an unknown key is ignored without a message, so run `validate` and `plan` against it in the platform repository's CI (as `ci` does in the [shipped workflow](cicd-integration.md)).

## Choosing a provisioner per resource type

| Provisioner | Use it when | Needs |
|---|---|---|
| `static` | The resource already exists (shared dev cluster, managed service created elsewhere) and you only want to hand its coordinates to apps. Also the way to run Deskribe locally with no cloud | Nothing; outputs are listed in the config, and may come from environment variables with `env:NAME` |
| `pulumi` | Deskribe should create the resource, with a Pulumi program you own | The Pulumi CLI and backend credentials; a `projectDir` containing your program |
| `terraform` | Same, with a Terraform root module you own | The Terraform CLI; a root module with the expected inputs and a `deskribe_outputs` output |
| `platform-output` | Deskribe should not provision anything: it writes the inputs (`tfvars`) for modules your own pipeline runs | Mappings per resource type. It works with `generate`. With `apply` it provisions nothing and returns no outputs, so references to those resources cannot resolve; use it with `generate` only |

All settings, the naming of the outputs each provisioner returns, and the state each one keeps (Pulumi stack `<app>-<env>`, Terraform workspace `{app}-{env}`) are in [provisioner settings](configuration-reference.md#provisioner-settings). Real Pulumi and Terraform runs have been tested only with a mocked runner and a fake binary, not against cloud infrastructure; run them against a sandbox account first.

A fully local platform config is in `examples/platform-config-local`: every type mapped to `static`, one replica, small resources, and connection strings taken from environment variables (`env:DESKRIBE_LOCAL_POSTGRES_CONNECTION`). With it, `deskribe apply` needs only a cluster.

## Secrets

`defaults.secretsStrategy` selects how sensitive outputs reach the pods. It can differ per environment.

- `opaque` (the default): Deskribe writes a Kubernetes Secret during `apply`, so it must be able to read the real value. `generate` refuses this strategy.
- `external-secrets`: Deskribe emits an `ExternalSecret` that names a remote key. You run the External Secrets Operator and a store, set `defaults.externalSecretsStore` (and `runtime.config.externalSecretsStoreKind`, `externalSecretsKeyTemplate` if needed), and put the values there. The emitted API version is `external-secrets.io/v1beta1`; Deskribe does not install or check the operator.

The `prod` environment in the example uses `external-secrets` with the store `platform-keyvault`.

## Environments and regions

There is no separate region feature. A region is an environment: give each its own file and set `region`.

```json
{ "name": "prod-eu", "defaults": { "region": "westeurope", "replicas": 3, "ha": true } }
```

```json
{ "name": "prod-us", "defaults": { "region": "eastus2", "replicas": 3, "ha": true } }
```

The region is checked against `policies.allowedRegions` and is passed to provisioners. The namespace pattern `{app}-{env}` gives `payments-api-prod-eu` and `payments-api-prod-us`, so both can live in one cluster.

Know what Deskribe does not do here:

- It has no notion of clusters. The Kubernetes plugin uses the default kubeconfig of the process, so each region is deployed by a separate run, with the kubeconfig for that region's cluster. In the shipped workflow that means one GitHub environment per region, each with its own `KUBECONFIG` secret.
- A manifest's `overrides` may only name environments the platform defines. A manifest that overrides `prod-eu` fails validation against a platform without that file, in every environment.
- Which regions an app is deployed to is decided by who runs which environment, not by the manifest.

## Policies

| Key | Effect |
|---|---|
| `allowedRegions` | The environment's effective region must be listed |
| `limits.maxReplicas`, `maxCpu`, `maxMemory` | Checked against the effective values (service override or platform default) of the target environment |
| `enforceTLS` | Accepted, but not enforced. A validation warning says so |

Limits are enforced on values after merging, so a platform default above the limit also fails. They are only checked for the environment being validated.

## Recommended workflow

1. Keep the platform config in its own repository with `validate`/`plan` of a representative manifest per environment in CI.
2. Change `base.json` or an env file by pull request. Review the effect with `deskribe plan` on a real manifest.
3. Give each GitHub environment (or pipeline stage) its own cluster credentials and approvals.
4. Publish the capability catalog (`resources`) so developers know the valid sizes and versions; the dashboard's `/api/plugins` and the `Resources` page show what each provider accepts.
