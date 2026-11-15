# Plugin development

Deskribe has three plugin kinds. A plugin assembly may register any number of them.

| Kind | Interface | Job | Built in |
|---|---|---|---|
| Resource provider | `IResourceProvider` | Knows one resource type: validates it and turns it into provisioner configuration | `postgres`, `redis`, `kafka.messaging` |
| Provisioner | `IProvisioner` | Creates, describes and destroys infrastructure; returns real outputs | `pulumi`, `terraform`, `static`, `platform-output` |
| Runtime plugin | `IRuntimePlugin` | Deploys workloads; owns rendering, file layout, apply, prune, rollout and teardown | `kubernetes` |

The contracts live in `src/Deskribe.Sdk` and are commented there. Plugins reference only that project.

## Discovery and loading

A plugin is a public class that

- implements `IPlugin` (`Name`, `Register(IPluginRegistrar)`),
- has the attribute `[DeskribePlugin("some-name")]`,
- has a public parameterless constructor.

Hosts find plugins in two ways:

1. Built-in assemblies are passed to the registry explicitly by the CLI and the dashboard.
2. External plugins: every `*.dll` in the top level of the plugins directory (not subdirectories) is loaded into the default assembly load context and scanned. The directory is `--plugins-dir` for the CLI (default `~/.deskribe/plugins`) and `Deskribe:PluginsDir` for the dashboard.

For each discovered class the registry creates an instance and calls `Register`, where you call `RegisterResourceProvider`, `RegisterProvisioner` and `RegisterRuntimePlugin`. Registrations are keyed by `ResourceType` or `Name`; a later registration with the same key replaces the earlier one, so an external plugin can replace a built-in one (for example your own `kubernetes` runtime).

Things to know:

- A DLL that cannot be loaded is skipped without any message, and a plugin that fails during discovery is not reported. If your plugin does not show up, check the target framework (`net10.0`), that the DLL is directly in the directory, and that any dependency DLLs are beside it.
- Do not ship `Deskribe.Sdk.dll` with the plugin. The host's copy must be the one the plugin binds to. Reference the SDK project or package with `<Private>false</Private>` (or equivalent).
- There is no isolation: plugins run in the host process with its permissions.

## Minimal project

This plugin adds a `mongodb` resource type. It was built against the SDK and loaded with `--plugins-dir` to produce the output shown below.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="path/to/src/Deskribe.Sdk/Deskribe.Sdk.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>
</Project>
```

```csharp
using Deskribe.Sdk;
using Deskribe.Sdk.Models;

[DeskribePlugin("acme-mongo")]
public sealed class MongoPlugin : IPlugin
{
    public string Name => "Acme MongoDB resource provider";

    public void Register(IPluginRegistrar registrar) =>
        registrar.RegisterResourceProvider(new MongoResourceProvider());
}

public sealed class MongoResourceProvider : IResourceProvider
{
    public string ResourceType => "mongodb";

    public ResourceSchema GetSchema() => new()
    {
        ResourceType = "mongodb",
        Description = "MongoDB database",
        Properties = [new() { Name = "version", ValueType = "string", Description = "MongoDB major version" }],
        ProvidedOutputs = ["connectionString", "host"],
        SensitiveOutputs = ["connectionString"]
    };

    public Task<ValidationResult> ValidateAsync(ResourceDescriptor resource, ValidationContext ctx, CancellationToken ct = default)
    {
        var errors = resource.Properties.Keys
            .Where(k => k != "version")
            .Select(k => $"Unknown property '{k}' on mongodb resource '{resource.Key}'")
            .ToArray();
        return Task.FromResult(errors.Length == 0 ? ValidationResult.Valid() : ValidationResult.Invalid(errors));
    }

    public Task<ResourcePlanResult> PlanAsync(ResourceDescriptor resource, PlanContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new ResourcePlanResult
        {
            ResourceKey = resource.Key,
            ResourceType = "mongodb",
            Configuration = new()
            {
                ["version"] = resource.Properties.TryGetValue("version", out var v) ? v.GetString() : "7",
                ["appName"] = ctx.AppName,
                ["environment"] = ctx.Environment,
                ["region"] = ctx.Defaults.Region
            }
        });
}
```

Build it, copy `MongoPlugin.dll` into a directory, and use a manifest with `{"type": "mongodb", "version": "7"}` and a platform config that maps `mongodb` to a provisioner (here `static`, with outputs for the key `mongodb`):

```
$ deskribe validate -f mongo.json -p mplat --env dev
  Error: Resource type 'mongodb' has no registered resource provider.
$ deskribe plan -f mongo.json -p mplat --env dev --plugins-dir plugins --image notes=r/n:1
...
Bindings:
  notes: Mongo__Url <- mongodb(mongodb).connectionString  [sensitive]
```

Without the plugin the type is rejected; with it, planning works and the `connectionString` variable is classed as sensitive because the schema says so.

## Resource provider

| Member | Contract |
|---|---|
| `ResourceType` | The `type` string in manifests and the key in the platform's `provisioners` map |
| `GetSchema()` | Declares `Properties`, `ProvidedOutputs` and `SensitiveOutputs`. The engine uses `SensitiveOutputs` to decide which environment variables must go through the secrets strategy, and the dashboard lists the schemas |
| `ValidateAsync` | Returns errors for bad input. The context has the platform and environment name, so it can check the catalog entry in `platform.resources.<type>` (the built-in providers also fall back to `provisionerConfigs.<type>`) |
| `PlanAsync` | Returns a `ResourcePlanResult` whose `ResourceKey` is `resource.Key` (the engine throws if not) and whose `Configuration` is the dictionary the mapped provisioner will read. Use `ctx.Defaults`, which already has the environment's overrides applied, not `ctx.Platform.Defaults` |

The engine validates the manifest and calls the provider's `ValidateAsync` before planning, and does not call `PlanAsync` for an invalid manifest. The configuration keys the built-in providers emit are listed in the [configuration reference](configuration-reference.md#provisioner-settings).

Resources of the same type are told apart by `name`; `ResourceDescriptor.Key` is `name` when set, otherwise `type`. Properties other than `type`, `name` and `size` arrive in `resource.Properties`. Reject properties you do not understand; the engine does not.

## Provisioner

```csharp
Task<ProvisionResult>  ApplyAsync(ProvisionContext ctx, CancellationToken ct);
Task<ArtifactResult>   GenerateArtifactsAsync(ProvisionContext ctx, string outputDir, CancellationToken ct);
Task<ProvisionResult>  DestroyAsync(ProvisionContext ctx, CancellationToken ct);
```

Which provisioner handles a resource comes from the platform's `provisioners` map. `Name` must equal the value used there.

The engine guarantees:

- One call per operation, with only the resources mapped to this provisioner in `ctx.Resources`.
- `ctx.Config` is this provisioner's entry in `provisionerConfigs` (empty if absent). Parse and validate it yourself and return `ProvisionResult.Failed(...)` for bad config.
- `ctx.Defaults` has the environment's overrides applied.

The engine expects:

- `ApplyAsync` is idempotent. It returns `Success = true` and `ResourceOutputs[resourceKey][outputName]` for every output that is actually known. Do not invent placeholders for unknown values: the engine treats a missing output as unresolved and fails with a message naming it, which is what you want. Output names must match the ones the provider declares (`connectionString`, `host`, ...).
- A failed result (`Success = false`, with `Errors`) aborts the apply with `Provisioner '<name>' failed: ...`.
- `GenerateArtifactsAsync` writes nothing itself and does not contact the infrastructure. It returns `GeneratedFile`s. Paths are relative to `outputDir` or absolute inside it. The engine rejects a path outside `outputDir` and two files with the same path. Do not put secret values in them.
- `DestroyAsync` removes only what this app and environment own. All provisioners are attempted even if one fails.

Naming convention used by the built-in provisioners for state: Pulumi stack `<app>-<env>`, Terraform workspace `{app}-{env}`. A provisioner that keeps state is responsible for locating it again from the context alone, because Deskribe stores nothing.

## Runtime plugin

```csharp
Task<RuntimeArtifact>    RenderAsync(RuntimeContext ctx, CancellationToken ct);
Task<IReadOnlyList<GeneratedFile>> GenerateArtifactsAsync(RuntimeContext ctx, string outputDir, CancellationToken ct);
Task<RuntimeApplyResult> ApplyAsync(RuntimeContext ctx, CancellationToken ct);
Task<RuntimeStatus>      GetStatusAsync(RuntimeContext ctx, CancellationToken ct);
Task                     DestroyAsync(RuntimeContext ctx, CancellationToken ct);
```

The platform selects it with `runtime.name`. Its settings are `runtime.config`, which the engine does not interpret: the platform's `runtime` section is on `ctx.Platform.Runtime`, and `ctx.Defaults` carries the merged defaults.

The engine guarantees, for `ApplyAsync` and `GenerateArtifactsAsync`:

- Every workload has an image.
- `EnvironmentVariables` values are fully resolved, with one exception: a variable listed in `SecretKeys` under `external-secrets` may still contain its `@resource(...)` reference, because Deskribe does not have the value. Your plugin must not inline it; deliver it through the strategy.
- Under `opaque`, `SecretKeys` variables hold real values and must be delivered as a secret, never inline in a pod spec. `generate` never reaches the plugin with opaque secrets.
- `ctx.Namespace` is already computed from `namespacePattern`.

The engine expects:

- `ApplyAsync` is idempotent, removes objects that earlier applies of this app and environment created and that are no longer declared, waits for the rollout, and throws when the rollout fails. Prune only after success, so a failed rollout leaves the working version in place. The result lists `Applied` and `Pruned`. Non-Deskribe exceptions are wrapped as `Runtime '<name>' failed to deploy: ...`; throw `DeskribeException` for messages that need no wrapping.
- `GetStatusAsync` and `DestroyAsync` receive an empty `Workloads` list. They must find what to act on from the context identity (app, environment, namespace), as the Kubernetes plugin does with labels. `DestroyAsync` must remove only what this app and environment own.
- `GenerateArtifactsAsync` chooses the layout. Same path rules as provisioners.
- `RenderAsync` is part of the interface but the engine does not currently call it.

The Kubernetes plugin is the reference implementation: see [system design](system-design.md#the-kubernetes-runtime-plugin) for its behaviour and `src/Plugins/Runtimes/Kubernetes` for the code.

## Testing a plugin

The built-in plugins' tests under `tests/` show the pattern: construct the context records directly and call the interface methods, no host needed. For the whole path, put the DLL in a directory and run `deskribe plan` / `generate` with `--plugins-dir`.
