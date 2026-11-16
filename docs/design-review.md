# Design Review — Deskribe (2026-11)

> **Note (added after the later redesign).** This report is kept as written. Since it was written, the engine and the Kubernetes plugin were redesigned so that Deskribe really deploys. For current behaviour see the [system design](system-design.md) and the [configuration reference](configuration-reference.md).
>
> Addressed by the redesign: A (multi-service manifests: one workload per service); B (the file layout and manifest rendering belong to the runtime plugin); E (`DeskribeAspireOptions` makes the developer tools and persistent containers configurable); finding 8 (there is no default image; a workload without one is an error); finding 12 (the Terraform provisioner now runs the Terraform CLI; it no longer emits Helm values); C in part (`sealed-secrets` was removed; the strategies are a closed set, `opaque` and `external-secrets`); D in part (labels are constants in one place but still fixed, and the ExternalSecret API version is fixed at `v1beta1`).
>
> Still open: F, G, I, J, K, and H (the Blazor WebAssembly client `Deskribe.Web.Client` no longer exists, so its same-origin point is moot). L was not re-assessed. Statements in the report that are no longer true: class names `ResourceReferenceResolver` and `PolicyValidator` are now `ResourceReferences` and `ManifestValidator`; finding 6 (plugin DLL load failures are logged) does not hold for the hosts today, where a DLL that fails to load is skipped silently; the finding 12 fix text (an error directing users to `generate`) was superseded as above; and `Deskribe:ManifestPath` in `appsettings.json` (finding 19) does not resolve correctly (see the [Aspire guide](aspire-integration.md)).


A design review and fix pass over the full repository: engine, plugins, CLI, web UI, samples, and docs. This report summarizes the method, the findings, what was fixed, and what remains as recommendations.

---

## Method

Four parallel exploration agents analyzed the codebase — Core/SDK, Plugins/Aspire, CLI/Web/Tests, and Samples/Docs. Findings were triaged by severity, and a follow-up pass implemented the fixes. Items that require breaking changes or a design decision are listed below as recommendations rather than silently changed.

## Architecture Overview

Deskribe is a single pipeline: `ConfigLoader` reads the platform config (single-file or split-file with `base.json` + `envs/{env}.json`) → `MergeEngine` merges platform < env < developer overrides → `ResourceReferenceResolver` parses `@resource(type).output` references → `PolicyValidator` enforces governance → resource providers produce plans via `PlanAsync` → provisioners run `ApplyAsync`/`GenerateArtifactsAsync` → the runtime plugin's `Render`/`Apply` produces and deploys workloads. Plugins are discovered via the `[DeskribePlugin]` attribute and assembly scanning, registered through `PluginRegistry`/`IPluginRegistrar`.

## Findings and Resolutions

| # | Area | Finding | Severity | Status |
|---|---|---|---|---|
| 1 | Samples | `examples/platform-config/envs/dev.json` had defaults at the root instead of under `defaults` — silently ignored by the deserializer | High | Fixed |
| 2 | Docs | README used wrong env var `Kafka__Servers` instead of `Kafka__BootstrapServers` | Medium | Fixed |
| 3 | Core | `MergeEngine` hardcoded defaults (westeurope, kubernetes, 250m/512Mi, 2 replicas, `{app}-{env}`, opaque) | Medium | Fixed — new `EngineDefaults` options class (hosts can register a configured instance; defaults unchanged) |
| 4 | Core | `PolicyValidator` duplicated the resource-reference regex with a wrong pattern (`@resource\(([^)]+)\)`) and silently ignored malformed refs | High | Fixed — shared `ResourceReferenceResolver.ReferencePattern`; malformed refs are now validation errors |
| 5 | Core | `PluginRegistry` singleton used a non-thread-safe `Dictionary` | High | Fixed — `ConcurrentDictionary`; plugin activation failures now throw descriptive errors |
| 6 | Core | `PluginLoader` silently swallowed DLL load failures | Medium | Fixed — logs warnings with path and reason |
| 7 | Core | Namespace interpolation (`{team}`/`{app}`/`{env}`) duplicated in `MergeEngine` and `DeskribeEngine.DestroyAsync` | Medium | Fixed — shared `NamespaceInterpolator` |
| 8 | Core | Engine hardcoded fallbacks `"api"` and `"nginx:latest"` | Low | Fixed — `EngineDefaults.DefaultServiceName`/`DefaultImage` |
| 9 | Core | `ValidateAsync` only checked the first service's env refs | High | Fixed — validates all services; `PlanAsync` warns when a manifest has multiple services (only the first is deployed) |
| 10 | Plugins | Postgres/Redis providers hardcoded valid sizes/versions/ports (5432/6379, versions 14–17) | Medium | Fixed — overridable via `provisionerConfigs.postgres`/`redis` (`validSizes`, `validVersions`, `defaultVersion`, `port`) |
| 11 | Plugins | Kubernetes runtime hardcoded ports (8080/80) and external-secrets store/kind/refresh (default/`ClusterSecretStore`/1h) | Medium | Fixed — overridable via `runtime.config` keys (`containerPort`, `servicePort`, `portName`, `externalSecretsStore`, `externalSecretsStoreKind`, `externalSecretsRefreshInterval`) |
| 12 | Plugins | Terraform provisioner `ApplyAsync` returned fake success | High | Fixed — returns an explicit error directing users to `deskribe generate --output-format terraform-only` + Terraform CLI |
| 13 | Aspire | Projections required editing the library to add resource types | Medium | Fixed — new `DeskribeAspireExtensions.RegisterProjection` |
| 14 | CLI | Exit codes were never propagated — CI would see success on failure | High | Fixed — verified exit=1 on failures |
| 15 | CLI | `validate` required `-f` while other commands defaulted to `deskribe.json` | Low | Fixed — consistent default |
| 16 | CLI | Malformed `--image` entries were silently skipped | Low | Fixed — now warn |
| 17 | CLI | Missing manifest/platform paths produced cryptic engine errors | Medium | Fixed — commands fail fast with clear errors |
| 18 | Web | GitHub link pointed to the github.com homepage; pages had `catch {}` swallowing all API errors | Medium | Fixed — link points to the repo; dismissible `MudAlert` errors are shown |
| 19 | AppHost | Sample manifest path was a hidden literal | Low | Fixed — moved to `appsettings.json` (`Deskribe:ManifestPath`); literal kept only as a last-resort fallback |
| 20 | Tests | `PolicyValidator`, `PluginRegistry`, provider config overrides, K8s config, and Terraform honesty were untested | Medium | Fixed — 20 new tests (52 total) |

### Documented limitations and recommendations

Not fixed in this pass — either breaking changes or design decisions the team should make explicitly:

| # | Area | Recommendation | Status |
|---|---|---|---|
| A | Core | **Single-service assumption.** `MergeEngine`/`PlanAsync` merge only `Services.First()`; multi-service manifests need per-service workload plans (breaking change to `WorkloadPlan`/`DeskribePlan`) | Documented limitation |
| B | Core | **Kustomize generation is hardcoded** in `DeskribeEngine` (file layout, patches targeting `containers/0`) — should move behind an `IManifestGenerator`/runtime-plugin responsibility so non-Kustomize targets are possible | Documented limitation |
| C | Plugins | **K8s secret strategies** (opaque/external-secrets/sealed-secrets) are a closed switch — candidate for a strategy-plugin interface | Documented limitation |
| D | Plugins | K8s API versions, labels (`app.kubernetes.io/managed-by: deskribe`), and the sealed-secrets annotation remain fixed literals | Documented limitation |
| E | Aspire | Projections are a static registry (now overridable) but there is still no assembly-scanning discovery; auxiliary tools (PgAdmin/RedisInsight/Kafka UI) are always on | Documented limitation |
| F | Plugins | Terraform/Pulumi config key naming is inconsistent (snake_case vs camelCase) — needs a naming-convention decision | Documented limitation |
| G | Core | `generate` only invokes provisioners listed in the `provisioners` map, so `platform-output` modules in `base.json` are never emitted unless a resource maps to them (observed: a terraform-only run emits only `Pulumi.*.json`). Consider supporting multiple provisioners per resource or a dedicated platform-output pass | Documented limitation |
| H | Web | Backend URL assumes same origin; the Resources page makes N+1 API calls; the API has no standardized error responses | Documented limitation |
| I | Core | `ConfigLoader` is JSON-only with fixed file names (`base.json`, `envs/{env}.json`) | Documented limitation |
| J | Core/SDK | Plugins require parameterless constructors (no DI into plugins); no plugin lifecycle hooks | Documented limitation |
| K | SDK | Models are sealed records — no domain extension via inheritance (deliberate, but limits some scenarios) | Documented limitation |
| L | Dependencies | NU1902/NU1903 warnings for MessagePack 2.5.192 (high: GHSA-vh6j-jc39-fggf) and OpenTelemetry 1.14.0/1.9.0 — recommend upgrades in a dedicated dependency pass | Documented limitation |

## Verification

- `dotnet build` — 0 errors (NU190x warnings pre-existing).
- `dotnet test` — 52/52 passing (20 new).
- CLI end-to-end against the samples: `validate` (dev, prod), `plan` (prod-us), `generate` (dev) — all exit 0.
