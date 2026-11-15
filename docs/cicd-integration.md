# CI/CD integration

Two paths exist: run `deskribe apply` from a pipeline (what `.github/workflows/deskribe-deploy.yml` does), or run `deskribe generate` and let a GitOps tool apply the files.

## The shipped workflow

`.github/workflows/deskribe-deploy.yml` has two jobs.

**`ci`** (pull requests and pushes to `main`): restore, build, test `Deskribe.slnx`; `dotnet pack src/Deskribe.Cli`; install the tool from the local package (`dotnet tool install --global deskribe-cli --add-source ./nupkg --version 0.1.0`); then `validate` and `plan` the example manifest against `dev`, `prod`, `prod-eu` and `prod-us` of `examples/platform-config`. It never contacts a cluster.

**`deploy`** (manual `workflow_dispatch` only):

| Input | Meaning |
|---|---|
| `environment` | Deskribe environment name. Must also be the name of a GitHub environment, which supplies the `KUBECONFIG` secret and any required-reviewer approval |
| `image` | The image to deploy (see the note below) |
| `manifest`, `platform` | Paths; default to the repository variables `DESKRIBE_MANIFEST` / `DESKRIBE_PLATFORM`, else the examples |

Steps: build and install the CLI, write the `KUBECONFIG` secret to a file in `$RUNNER_TEMP` (mode 600), `deskribe apply`, `deskribe status` (non-zero when anything is not ready), delete the kubeconfig. Deployments to the same environment are serialised by a concurrency group (`deskribe-deploy-<environment>`) and are not cancelled.

To use it as a template for an app repository: copy the file, point the paths at your manifest and platform config, create one GitHub environment per Deskribe environment with a `KUBECONFIG` secret, and add required reviewers where wanted.

### Things to know before using it

- `deskribe apply --image` expects `name=image:tag`. The workflow's input description says a plain `image:tag` also works for a single-service manifest, and the step passes the input unchanged, but the CLI rejects a plain value with `Invalid --image ... expected name=image:tag`. Give the input as `payments-api=ghcr.io/acme/payments-api:1.0.0`. (The dashboard API's `?image=` does accept a plain image.)
- The deploy job installs only the .NET SDK. A platform config that maps resources to `pulumi` or `terraform` needs those tools on the runner, plus their credentials and backend; add the steps yourself.
- The default platform, `examples/platform-config`, maps every resource type to `pulumi` with `projectDir: ./infra`, a directory that does not exist in this repository, so a deploy with the default paths fails at provisioning. It exists to demonstrate `validate`, `plan` and `generate`. `examples/platform-config-local` maps everything to `static` outputs and is the one that can deploy without cloud infrastructure; see the [examples README](../examples/README.md).
- The workflow does not build or push your application image. It deploys one that already exists.
- Authentication to the cluster is only the kubeconfig. The CLI uses the default client configuration, so `KUBECONFIG` or in-cluster configuration applies.

## GitOps with `generate`

`generate` writes files and never contacts a cluster or a cloud account, so it can run in any pipeline and commit the result.

```
deskribe generate -f examples/payments-api/deskribe.json -p examples/platform-config --env prod \
  --image payments-api=ghcr.io/acme/payments-api:1.0.0 --outputs outputs.json -o generated
```

`outputs.json` holds the values the references need that Deskribe cannot obtain without provisioning, as `{ "<resourceKey>": { "<output>": "<value>" } }`:

```json
{
  "redis": { "endpoint": "redis.prod.example.com:6380" },
  "kafka.messaging": { "bootstrapServers": "kafka.prod.example.com:9093" }
}
```

This is an executed example; it produced:

```
generated/
  Pulumi.payments-api-prod.json
  bindings.json
  mobile-replenishment/payments-api/prod/
    namespace.yaml
    externalsecret-payments-api-env.yaml
    service-payments-api.yaml
    deployment-payments-api.yaml
    kustomization.yaml
```

Rules to plan around:

- `generate` refuses a workload with sensitive variables under the `opaque` strategy, because it would have to write the secret into a file. Use `secretsStrategy: external-secrets` (as `prod` in the example does). The ExternalSecret references remote keys derived from `externalSecretsKeyTemplate`, for example `payments-api-prod-connectionstrings-postgres`, and the operator and the store (`platform-keyvault` here) must exist in the cluster. The emitted API version is `external-secrets.io/v1beta1`.
- Provisioner `ApplyAsync` is never called, so outputs of the `static` provisioner are not used. Every non-sensitive reference must be in the outputs file. The `dev` environment of the example uses `opaque` and therefore cannot be generated at all.
- An image is required for every workload.
- `--output-format runtime-only` writes only the Kubernetes files; `infra-only` writes only provisioner files and needs no image. `all` is the default.

What the pipeline then does:

1. Infrastructure files: the Pulumi file `Pulumi.<app>-<env>.json` is informational (it is not Pulumi's own stack-config format); the Terraform provisioner writes `infra/terraform/terraform.tfvars.json`; the `platform-output` provisioner writes module inputs under its `basePath` (`<team>/<app>/<env>/<module>/terraform.tfvars.json` in the example). Provisioning itself is up to your tooling.
2. Kubernetes files: apply the directory with `kubectl apply -k generated/<team>/<app>/<env>`, or point Argo CD or Flux at it. The `kustomization.yaml` lists the files; there are no overlays, because the environment is already resolved.
3. `bindings.json` lists, per workload, which variable gets which output of which resource and whether it is sensitive. It contains no values, so it is safe to publish or to use for review and auditing.

A `kubectl apply -k` or GitOps sync does not prune by Deskribe's rules and does not wait for the rollout the way `deskribe apply` does; those belong to the Kubernetes plugin's `apply`.

## Choosing

| | `deskribe apply` | `generate` + GitOps |
|---|---|---|
| Provisions infrastructure | Yes, through the provisioners | No, files only |
| Secrets | `opaque` or `external-secrets` | `external-secrets` only |
| Waits for rollout, prunes removed services | Yes | No (your GitOps tool's behaviour) |
| Needs cluster credentials in the pipeline | Yes | No |
| Needs an outputs file | No | Yes |

See the [configuration reference](configuration-reference.md#secrets-strategies) for the strategies and [system design](system-design.md#generate) for what `generate` does step by step.
