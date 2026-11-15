# System design

How Deskribe is put together and what each operation does. For the file formats see the [configuration reference](configuration-reference.md); for writing plugins see [plugin development](plugin-development.md).

## Components

```
Deskribe.Cli   Deskribe.Web   Deskribe.Aspire (+ AppHost)
      |              |                |
      v              v                |
 Deskribe.Core (engine) ------+       |
      |                       |       |
      v                       v       v
                 Deskribe.Sdk (contracts + models)
                          ^
                          |
        Plugins: resource providers, provisioners, Kubernetes runtime
```

| Component | Responsibility |
|---|---|
| `Deskribe.Sdk` | The plugin interfaces (`IResourceProvider`, `IProvisioner`, `IRuntimePlugin`, `IPlugin`), the models (`DeskribeManifest`, `PlatformConfig`, `DeskribePlan`, `WorkloadPlan`, `Binding`, ...) and `DeskribePluginAttribute`. No dependencies |
| `Deskribe.Core` | `DeskribeEngine` (the operations), `ConfigLoader`, `MergeEngine`, `ManifestValidator`, `ResourceReferences` (the reference syntax), `PluginRegistry` and `PluginLoader`. Depends only on the SDK |
| Plugins | Depend only on the SDK. They never reference Core or each other |
| `Deskribe.Cli`, `Deskribe.Web` | Hosts. Reference Core and the built-in plugins, and register them by assembly |
| `Deskribe.Aspire` | Independent of the engine: it reads only the manifest (via the SDK models) and creates local Aspire resources |

The engine has no knowledge of Kubernetes, Postgres or Pulumi. Everything it does with them goes through the three plugin interfaces.

## Engine operations

All operations that take `(manifest, platform, environment)` first load both files, merge the environment's defaults, and resolve the namespace from `namespacePattern`.

### validate

Load, then run the [validation rules](configuration-reference.md#validation-rules). Returns every error and warning. Load failures of the manifest and the platform config are collected together.

### plan

1. Validate. Any error throws a validation exception carrying all errors.
2. For each resource, call the provider's `PlanAsync`. The result's key must equal the resource key, otherwise the engine reports a plugin bug.
3. For each service, build a `WorkloadPlan`: name, replicas/cpu/memory (override, else default), image (from `--image`, possibly null), the env map unresolved, and the secrets strategy.
4. For each `@resource` reference in each env value, record a `Binding`. If the output is sensitive, add the variable to the workload's `SecretKeys`.

Plan never contacts a provisioner or the runtime.

### apply

1. Fail if any workload has no image. This happens before anything is provisioned.
2. Group the planned resources by the provisioner the platform maps their type to. A type with no mapping, or a provisioner that is not registered, is an error.
3. Call `ApplyAsync` on each provisioner once with its resources. A failed result stops the operation. Outputs are collected as `resourceKey -> output -> value`.
4. Resolve every workload's env values with those outputs. An unresolved reference is an error listing all of them, except a sensitive variable under `external-secrets`, whose value the cluster pulls from the store.
5. Call the runtime's `ApplyAsync` with the resolved workloads. Non-Deskribe exceptions from the runtime are wrapped as `Runtime 'kubernetes' failed to deploy: ...`.
6. Return the namespace and the lists of applied and pruned objects. The result never carries resolved values.

There is no rollback. If provisioning succeeded and the rollout fails, the infrastructure stays and the failed rollout is reported. Applying again is safe because every step is idempotent.

### generate

Writes files for a GitOps pipeline and never contacts a cluster or a cloud.

1. Plan (so validation applies).
2. Unless the format is `infra-only`: require images; refuse workloads that have sensitive variables under `opaque`; load the `--outputs` file (`{ "<resourceKey>": { "<output>": "<value>" } }`) and resolve references with it. Provisioners are not called, so their outputs, including `static` ones, are not used: every non-sensitive reference has to be in the outputs file.
3. Unless the format is `runtime-only`: call each provisioner's `GenerateArtifactsAsync`.
4. Unless the format is `infra-only`: call the runtime's `GenerateArtifactsAsync`.
5. Add `bindings.json`.
6. Resolve every path against the output directory, reject any file that would land outside it or that duplicates another, and write the files.

Formats: `all`, `runtime-only`, `infra-only` (`k8s-only` and `terraform-only` are accepted aliases).

### status

Loads the configuration (no validation, no images needed) and calls the runtime's `GetStatusAsync`, which lists the deployed workloads and their ready replicas.

### destroy

Loads the configuration, checks that all plugins can be resolved, then:

1. Runtime `DestroyAsync`.
2. Each provisioner's `DestroyAsync` with its resources.

Every step is attempted even if an earlier one failed. Failures are reported together at the end as `Destroy did not complete:`.

## Bindings and secrets

A binding is the contract between an application and a resource: "the env var `X` of workload `W` receives output `O` of resource `K`". The plan holds the bindings, `plan` prints them and `generate` writes them to `bindings.json`. They never contain values.

Whether an output is a credential is declared by the resource provider (`SensitiveOutputs` in its schema), not guessed from the name. For Postgres and Redis `connectionString` is sensitive. The variables holding a sensitive output are delivered by the secrets strategy (see the [reference](configuration-reference.md#secrets-strategies)):

- `opaque`: `apply` writes the values into a Kubernetes Secret. They are never written to disk by `generate`, which refuses instead.
- `external-secrets`: Deskribe only describes where the value lives. The ExternalSecret names remote keys derived from `externalSecretsKeyTemplate`; the operator copies the value into a Secret.

A variable is classed as sensitive if any of the references in its value is. Values are not logged: the Kubernetes plugin logs object names only, and API errors for Secrets are reduced to a status code and reason.

## The Kubernetes runtime plugin

### What is rendered

Per workload, in this order: the Secret or ExternalSecret `<workload>-env` (only if the workload has sensitive variables), the Service `<workload>`, the Deployment `<workload>`. Optionally a Namespace first.

- Deployment: `replicas` from the workload; one container named after the workload with the image, one named port, CPU and memory as both requests and limits, plain env vars inline sorted by name, `envFrom` the `<workload>-env` Secret when there are sensitive variables, and a readiness probe when `healthPath` is set.
- The pod template carries the annotation `deskribe.dev/env-hash`, a SHA-256 of the plain env values and of the secret values (or the remote keys, under `external-secrets`). A changed value changes the hash, which triggers a rollout even though the Secret is not part of the pod spec.
- There is no default image. A workload without one fails rendering.

### Labels

| Key | Value | Where |
|---|---|---|
| `app.kubernetes.io/managed-by` | `deskribe` | Everything, including namespaces it creates |
| `app.kubernetes.io/name` | workload name | Namespaced objects |
| `deskribe.dev/app` | app name | Namespaced objects |
| `deskribe.dev/environment` | environment | Namespaced objects (not in the selector) |
| `deskribe.dev/team` | team | Namespaced objects, when there is a team |

The Deployment and Service selectors use only `deskribe.dev/app` and `app.kubernetes.io/name`, because a Deployment's selector is immutable. Namespaces carry the annotation `deskribe.dev/owner: <app>/<env>` when Deskribe created them.

### apply

1. Read the settings from `runtime.config`; render all objects. Names, label values and Secret keys are validated first.
2. Make sure the namespace exists. It is created, with the owner annotation, only if `createNamespace` is `true`. A namespace that is being deleted is an error. If the namespace is missing and `createNamespace` is `false`, apply fails.
3. Apply each object with Kubernetes server-side apply (field manager `deskribe`, force). Objects are patched, not replaced, so applying twice is a no-op.
4. Wait for every Deployment to roll out: observed generation current, all replicas updated and available, no old replicas left. It polls every 2 seconds up to `rolloutTimeoutSeconds`. It fails early when the Deployment reports `ProgressDeadlineExceeded`, when a pod's image name is invalid or `ErrImageNeverPull`, or when image pulls have been failing for 20 seconds. The error names the workload and includes replica counts, conditions, container waiting/terminated reasons and recent warning events of the new pods.
5. Only after every rollout succeeded, prune: delete Deployments, Services, ExternalSecrets, and Secrets that carry the labels `managed-by=deskribe`, `deskribe.dev/app=<app>` and `deskribe.dev/environment=<env>` in the namespace and are not in the set just applied. Secrets owned by another object (those an ExternalSecret created) are left to garbage collection. A failed rollout therefore prunes nothing and the old Secret stays until the new pods are running.

Server-side apply with force means Deskribe wins field conflicts. It always sets `replicas`, so an autoscaler that changes replicas will be reset on the next apply.

### status

Lists Deployments with the app and environment labels. `Exists` is false when there are none. The CLI exits non-zero when nothing is deployed or any workload has fewer ready replicas than desired. A missing namespace gives the same result as an empty one.

### destroy

1. Prune everything that carries the app and environment labels (nothing is kept).
2. Read the namespace. If it does not exist, or the caller may not read it, stop.
3. Delete the namespace only if its `deskribe.dev/owner` annotation equals `<app>/<env>` (Deskribe created it for this deployment) and it holds no objects that belong to anything else (objects that are neither this app's nor owned by another object, ignoring the service-account token Secrets and `kube-root-ca.crt`). Otherwise leave it and log why. A namespace Deskribe did not create is never deleted.

### generate

Writes one self-contained directory per app and environment under the output directory:

```
<out>/<team>/<app>/<env>/     (<out>/<app>/<env>/ when there is no team)
  namespace.yaml                        only when createNamespace is true
  externalsecret-<workload>-env.yaml    only when there are sensitive variables
  service-<workload>.yaml
  deployment-<workload>.yaml
  kustomization.yaml                    lists every file above
```

Apply it with `kubectl apply -k <dir>` or point Argo CD or Flux at the directory. Provisioner files and `bindings.json` go elsewhere under `<out>`; see the [CI/CD guide](cicd-integration.md).

### Upgrading from the earlier plugin

Deployments created by the previous version of the plugin are not migrated. They selected pods by `app: <app>`, while the new Deployments use `deskribe.dev/app` and `app.kubernetes.io/name`. Selectors are immutable, so applying over such a Deployment fails. Delete the old Deployment (and Service) once, then apply again.

## State

Deskribe has no database, state file, or lock of its own.

| What | Where it lives |
|---|---|
| Infrastructure | The provisioner's own state: Pulumi stack `<app>-<env>` in the Pulumi backend, or the Terraform workspace `{app}-{env}` in the root module's backend |
| Deployed workloads | The labelled objects in the cluster. `status`, prune and destroy find them by label |
| Which version was last applied | Not tracked. The Deployment's image and the `deskribe.dev/env-hash` annotation are all that remain |
| Resolved secret values | Not kept. Under `opaque` they exist only inside the cluster Secret |

Consequences: two concurrent `apply` runs for the same app and environment are not coordinated by Deskribe (the CI workflow uses a concurrency group for this); `destroy` of an environment relies on the provisioner's state to know what to remove; nothing records drift or history.

## Dashboard

`Deskribe.Web` is a Blazor server app with a small JSON API. It scans `Deskribe:ManifestsRoot` for `deskribe.json` files, uses the platform config at `Deskribe:PlatformPath`, and runs the same engine in process.

| Setting | Default | Meaning |
|---|---|---|
| `Deskribe:ManifestsRoot` | `examples` under the solution root | Scanned recursively for `deskribe.json`, skipping `bin`, `obj`, `node_modules` |
| `Deskribe:PlatformPath` | `examples/platform-config` | Platform config |
| `Deskribe:PluginsDir` | `~/.deskribe/plugins` | External plugins |
| `Deskribe:EnableMutations` | `false` | Enables apply and destroy buttons in the plan page |

The HTTP API is `GET` only: `/api/apps`, `/api/apps/{name}/manifest`, `/api/apps/{name}/validate/{env}`, `/api/apps/{name}/plan/{env}?image=name=image:tag`, `/api/apps/{name}/status/{env}` and `/api/plugins` (resource provider schemas). Plan responses replace sensitive values with `(secret)` and omit the platform config. Apply and destroy exist only as buttons in the Blazor UI, disabled unless `Deskribe:EnableMutations` is `true`. Operation outcomes shown on the dashboard are held in memory and are lost on restart; status is read from the cluster.

There is no authentication or authorisation. That is why mutations are off by default. Enable them only on a host that only you can reach.

## Limitations

- Pulumi and Terraform provisioning is covered by unit tests with a mocked runner and a fake binary, not by runs against real infrastructure.
- `external-secrets` emits `external-secrets.io/v1beta1` and assumes the operator and the store exist.
- `policies.enforceTLS` is not enforced.
- Only Kubernetes Deployments and Services are produced: no Ingress, autoscaler, probes other than readiness, security context, or multiple ports.
- No rollback and no drift detection (see State).
- Deployments from the previous plugin version are not migrated.
