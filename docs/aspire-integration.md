# Aspire integration

`Deskribe.Aspire` turns a `deskribe.json` into local .NET Aspire resources, so `dotnet run` on the AppHost starts the same Postgres, Redis and Kafka the manifest asks for and gives the application its connection values. It is a development convenience: it does not use the engine, the platform config, provisioners or the Kubernetes plugin.

## What it does and does not read

| Read from the manifest | Ignored |
|---|---|
| Resource `type`, `name`, `version`, `topics` (not used locally, see below) | `size`, `ha`, `overrides` |
| Service `env` map, with `@resource(key).output` references | Platform config, policies, limits, secrets strategy, sensitivity |

Because the platform config is not read, a manifest can be valid for Aspire and still fail `deskribe validate` (and the reverse).

## Usage

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var resources = builder.AddDeskribeManifest("path/to/deskribe.json", options: new DeskribeAspireOptions
{
    DeveloperTools = true,        // PgAdmin, RedisInsight, Kafka UI (default true)
    PersistentContainers = true   // keep containers between runs (default true)
});

builder.AddProject<Projects.PaymentsApi>("payments-api")
    .WithDeskribeResources(resources);   // pass a serviceName when the manifest has several services

builder.Build().Run();
```

- `AddDeskribeManifest` accepts a path or a `DeskribeManifest` object, an optional list of extra projections, and `DeskribeAspireOptions`.
- `WithDeskribeResources(resources, serviceName?)` sets the manifest service's `env` entries on the project, translating each reference to the local resource's value, and makes the project wait for the referenced resources. With one service no name is needed.
- `RegisterProjection(type, projection)` adds support for another resource type (implement `IAspireProjection`).

`src/Deskribe.AppHost/AppHost.cs` is the working example, using `examples/payments-api/deskribe.json`.

## Resource mapping

| Manifest type | Aspire resources | Outputs available to references |
|---|---|---|
| `postgres` | Postgres container (image tag = `version`, default `16`), a database named after the key, PgAdmin | `connectionString`, `host`, `port`, `database` |
| `redis` | Redis container (the password argument is removed), RedisInsight | `connectionString` (`host:port`), `endpoint`, `host`, `port` |
| `kafka.messaging` (also `kafka`) | Kafka container, Kafka UI | `bootstrapServers`, `endpoint` |

Kafka topics in the manifest are not created locally. A reference that names an output the projection does not provide, or an unknown resource key, fails when the AppHost starts.

References use a looser pattern here than in the engine: the key may be anything except `)` and whitespace.

## Running the sample

```
dotnet run --project src/Deskribe.AppHost
```

The manifest path comes from `Deskribe:ManifestPath` in `src/Deskribe.AppHost/appsettings.json`, resolved against the AppHost directory. Override it with `-- --Deskribe:ManifestPath=<path>`. Running needs a container runtime such as Docker.

## What was verified

Verified by generating Aspire's deployment manifest (`dotnet run --project src/Deskribe.AppHost -- --publisher manifest --output-path <file> --Deskribe:ManifestPath=...`). For the example it contains containers `payments-api-postgres` (`postgres:16`), `payments-api-redis` (`redis:8.2`), `payments-api-kafka-messaging` (`confluentinc/confluent-local:8.1.0`), the `payments-api` project and `deskribe-web`, and the project's environment becomes:

```
ConnectionStrings__Postgres={payments-api-postgres.connectionString};Database=postgres
Redis__Endpoint={payments-api-redis.bindings.tcp.host}:{payments-api-redis.bindings.tcp.port}
Kafka__BootstrapServers={payments-api-kafka-messaging.bindings.tcp.host}:{payments-api-kafka-messaging.bindings.tcp.port}
```

Not verified: starting the containers and the application end to end, the developer tools' behaviour, and that removing the Redis password argument yields a working unauthenticated Redis. There are no automated tests for the Aspire bridge.

## Relation to a real deployment

The same service `env` map is what `deskribe apply` resolves against provisioned infrastructure, so an application reads identical variable names locally and in the cluster. What differs is the infrastructure: local containers without the platform's policies versus what the platform's provisioners create. For a platform config that targets local resources, see `examples/platform-config-local` in the [examples README](../examples/README.md).
