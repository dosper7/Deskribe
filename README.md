# Deskribe

Deskribe is an intent-as-code tool. A developer describes what an application needs in a `deskribe.json` manifest: resources such as a database, a cache and a Kafka topic, and the services that consume them. A platform team describes how those needs are fulfilled in a platform config. Deskribe validates the manifest against the platform's rules, provisions the infrastructure through provisioner plugins, resolves the references between services and resources into real values, and deploys the workloads to Kubernetes through a runtime plugin.

Locally, a .NET Aspire bridge runs the same manifest against containers.

## Status

| Area | State |
|---|---|
| Validate, plan, generate, status | Exercised against the examples in this repository |
| Kubernetes deploy (`apply`, `status`, `destroy`) | Covered by unit tests and by integration tests that run against a real cluster when `DESKRIBE_E2E_KUBECONFIG` is set |
| Pulumi and Terraform provisioners | Covered by unit tests only (mocked stack runner, fake `terraform` binary). They have not been run against real cloud infrastructure |
| `external-secrets` strategy | Emits `external-secrets.io/v1beta1` objects and assumes the External Secrets Operator and the secret store already exist |
| Aspire bridge | Verified by producing Aspire's manifest (`--publisher manifest`); not verified with a full container run |
| `policies.enforceTLS` | Accepted, not enforced. Validation prints a warning |
| Dashboard | Read-only by default; has no authentication |

Other limitations are listed where they matter: [configuration reference](docs/configuration-reference.md), [system design](docs/system-design.md).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- `kubectl` access to a cluster, only for `apply`, `status` and `destroy` (Deskribe reads the standard kubeconfig: `KUBECONFIG`, then `~/.kube/config`, or the in-cluster configuration)
- Docker, only for local development with Aspire
- The Terraform or Pulumi CLI, only if a platform maps resources to those provisioners

## Quick start

From a fresh clone:

```bash
git clone https://github.com/dosper7/Deskribe.git
cd Deskribe
dotnet build src/Deskribe.Cli
alias deskribe='dotnet src/Deskribe.Cli/bin/Debug/net10.0/Deskribe.Cli.dll'

# Check the manifest against the platform's rules for the dev environment
deskribe validate -f examples/payments-api/deskribe.json -p examples/platform-config --env dev

# Show what would be provisioned and deployed; nothing is changed
deskribe plan -f examples/payments-api/deskribe.json -p examples/platform-config --env prod \
  --image payments-api=ghcr.io/acme/payments-api:1.0.0
```

`generate` writes GitOps files instead of touching a cluster. The `prod` environment of the example uses the `external-secrets` strategy, so the generated files contain no secret values. Values for the non-secret references come from an outputs file:

```bash
cat > outputs.json <<'JSON'
{
  "redis": { "endpoint": "redis.prod.example.com:6380" },
  "kafka.messaging": { "bootstrapServers": "kafka.prod.example.com:9093" }
}
JSON

deskribe generate -f examples/payments-api/deskribe.json -p examples/platform-config --env prod \
  --image payments-api=ghcr.io/acme/payments-api:1.0.0 --outputs outputs.json -o ./generated
```

The CLI is not published to NuGet. To install it as a global tool from a local package:

```bash
dotnet pack src/Deskribe.Cli -c Release -o ./nupkg
dotnet tool install --global deskribe-cli --add-source ./nupkg --version 0.1.0
deskribe --help
```

The package id is `deskribe-cli` and the command is `deskribe`.

## The manifest

`deskribe.json` lives in the application repository. This is `examples/payments-api/deskribe.json`, shortened:

```json
{
  "name": "payments-api",
  "team": "mobile-replenishment",
  "resources": [
    { "type": "postgres", "size": "m", "version": "16" },
    { "type": "redis" },
    { "type": "kafka.messaging", "topics": [ { "name": "payments.transactions", "partitions": 6, "retentionHours": 168, "owners": ["team-payments"] } ] }
  ],
  "services": [
    {
      "env": {
        "ConnectionStrings__Postgres": "@resource(postgres).connectionString",
        "Redis__Endpoint": "@resource(redis).endpoint",
        "Kafka__BootstrapServers": "@resource(kafka.messaging).bootstrapServers"
      },
      "overrides": { "prod": { "replicas": 3, "cpu": "500m", "memory": "1Gi" } }
    }
  ]
}
```

- A resource is identified by its `name`, or by its `type` when it has no name. `@resource(<key>).<output>` refers to that key.
- The manifest has no image. The image is supplied at deploy time with `--image name=image:tag`.
- Unknown properties in the manifest, a service or an override are errors.

## The platform config

The platform config is a directory (`base.json` plus `envs/<env>.json`) or a single `.json` file with an `environments` map. It sets defaults, the Kubernetes runtime settings, which provisioner fulfils each resource type, the capability catalog, and policies and limits. See `examples/platform-config/` and the [configuration reference](docs/configuration-reference.md).

## Commands

| Command | What it does |
|---|---|
| `validate` | Loads the manifest and platform config and reports every problem. Exit code 1 on errors; warnings do not fail |
| `plan` | Validates, then prints resources, workloads, and bindings. Changes nothing |
| `apply` | Validates, provisions, resolves references, deploys, and waits for the rollout |
| `generate` | Writes GitOps files: Kubernetes manifests, infrastructure inputs, `bindings.json` |
| `status` | Reads the deployed workloads from the cluster. Exit code 1 if nothing is deployed or a workload is not ready |
| `destroy` | Removes the workloads and the provisioned infrastructure. Asks for the app name unless `-y` is given |

Common options: `-f/--file` (default `deskribe.json`), `-p/--platform` (required), `--env` (required). `plan`, `apply` and `generate` also take `--image name=image:tag` (repeatable). Global options: `--plugins-dir`, `--verbose`. Exit codes: 0 success, 1 failure, 130 cancelled.

## How a deployment proceeds

`deskribe apply` does the following, in order. It stops at the first failure.

1. Validate the manifest against the platform config and the resource providers.
2. Plan each resource through its resource provider, and build one workload per service. Every `@resource` reference becomes a binding, marked sensitive when the resource provider declares that output as a credential.
3. Check that every workload has an image.
4. Provision: call each provisioner once with only the resources mapped to it. A provisioner returns real outputs or fails; there are no placeholders.
5. Resolve the references in each workload's environment using those outputs. An unresolved reference is an error.
6. Deploy through the runtime plugin: create the namespace if it is allowed to, apply the Secret (or ExternalSecret), Service and Deployment, wait for the rollout, then delete objects that Deskribe previously deployed for this app and environment and that are no longer declared.

`destroy` removes the runtime objects first and then asks each provisioner to destroy its infrastructure. It attempts every step and reports all failures at the end.

Deskribe keeps no state of its own. Infrastructure state lives wherever the provisioner keeps it (Pulumi, Terraform). Runtime state is the labelled objects in the cluster.

## Local development with Aspire

`Deskribe.Aspire` turns the manifest's resources into local containers and wires the service's `env` map to them. `src/Deskribe.AppHost` is a working host for the example. See [docs/aspire-integration.md](docs/aspire-integration.md), including a path problem you will hit when starting the AppHost unmodified.

## Plugins

Everything backend-specific is a plugin:

| Kind | Built in | Role |
|---|---|---|
| Resource provider | `postgres`, `redis`, `kafka.messaging` | Validates a resource and plans its configuration; declares its outputs |
| Provisioner | `pulumi`, `terraform`, `static`, `platform-output` | Creates infrastructure and returns outputs, or writes GitOps inputs |
| Runtime | `kubernetes` | Renders, applies, prunes, reports status, destroys |

External plugins are DLLs in `~/.deskribe/plugins` (or `--plugins-dir`). See [docs/plugin-development.md](docs/plugin-development.md).

## Dashboard

`src/Deskribe.Web` is a Blazor dashboard that lists the manifests it finds, shows validation, plans, bindings and deployment status. It is read-only by default: apply and destroy are disabled unless `Deskribe:EnableMutations` is `true`, and the HTTP API only has `GET` endpoints. It has no authentication, so do not expose it and do not enable mutations on a shared network. Secret values are never shown. See [system design](docs/system-design.md#dashboard).

```bash
dotnet run --project src/Deskribe.Web
```

It reads manifests from `Deskribe:ManifestsRoot` and the platform config from `Deskribe:PlatformPath`, defaulting to this repository's `examples` and `examples/platform-config`.

## Project structure

```
src/
  Deskribe.Sdk/            Plugin contracts and models; no dependencies
  Deskribe.Core/           Engine, config loading, merging, validation, plugin registry
  Deskribe.Cli/            The deskribe command
  Deskribe.Web/            Read-only dashboard and API
  Deskribe.Aspire/         Aspire bridge (manifest -> local containers)
  Deskribe.AppHost/        Aspire host for the example
  Deskribe.ServiceDefaults/  Shared telemetry and health defaults
  Plugins/
    Resources/{Postgres,Redis,Kafka}
    Provisioners/{Pulumi,Terraform,Static,PlatformOutput}
    Runtimes/Kubernetes
tests/                     Core, plugin, and Kubernetes runtime tests
examples/                  payments-api (manifest + app), platform-config, platform-config-local
.github/workflows/deskribe-deploy.yml
```

Run the tests with `dotnet test Deskribe.slnx`. Kubernetes integration tests are skipped unless `DESKRIBE_E2E_KUBECONFIG` points at a kubeconfig file.

## Documentation

- [Configuration reference](docs/configuration-reference.md): manifest, platform config, validation rules
- [Platform team guide](docs/platform-teams.md): organising a platform config, provisioners, regions
- [System design](docs/system-design.md): architecture, data flow, the Kubernetes runtime, state
- [Plugin development](docs/plugin-development.md)
- [Aspire integration](docs/aspire-integration.md)
- [CI/CD and GitOps](docs/cicd-integration.md)
- [Examples](examples/README.md)
- [Design review (2026-11)](docs/design-review.md): an earlier review, kept for history
- [Product intent](deskribe-idea-inception-draft.md)
