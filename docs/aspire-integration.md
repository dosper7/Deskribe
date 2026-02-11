# Aspire Integration

This guide explains how Deskribe integrates with .NET Aspire to turn your `deskribe.json` manifest into a fully running local development environment -- complete with real containers, admin tools, and automatic connection string injection.

---

## 1. How Aspire Integration Works

### The Big Picture

Deskribe reads the same manifest that drives production infrastructure and translates each declared resource into a real Aspire container resource. The developer writes one file. Aspire handles local dev. Pulumi/Terraform handles production.

```
                          deskribe.json
                               |
                               v
                  +---------------------------+
                  | DeskribeAspireExtensions  |
                  |                           |
                  | AddDeskribeManifest()      |
                  |   Reads the JSON manifest |
                  |   Iterates each resource  |
                  |   Calls the right Aspire  |
                  |   builder method          |
                  +---------------------------+
                               |
             +-----------------+-----------------+
             |                 |                 |
             v                 v                 v
  +------------------+ +---------------+ +----------------+
  | AddPostgres()    | | AddRedis()    | | AddKafka()     |
  | + AddDatabase()  | | + RedisInsight| | + KafkaUI      |
  | + PgAdmin        | |               | |                |
  +------------------+ +---------------+ +----------------+
             |                 |                 |
             v                 v                 v
  +------------------+ +---------------+ +----------------+
  | PostgresServer   | | Redis         | | Kafka          |
  | Resource +       | | Resource      | | ServerResource |
  | PostgresDatabase | |               | |                |
  | Resource         | |               | |                |
  +------------------+ +---------------+ +----------------+
             |                 |                 |
             +-----------------+-----------------+
                               |
                               v
                  +---------------------------+
                  | DeskribeResourceMap        |
                  |                           |
                  | Holds all created Aspire  |
                  | resources + references    |
                  +---------------------------+
                               |
                               v
                  +---------------------------+
                  | WithDeskribeResources()    |
                  |                           |
                  | Wires connection strings  |
                  | to your project via       |
                  | .WithReference()          |
                  | + .WaitFor()              |
                  +---------------------------+
                               |
                               v
                  +---------------------------+
                  | Aspire Dashboard          |
                  | http://localhost:15888     |
                  |                           |
                  | Shows all resources,      |
                  | health, logs, metrics     |
                  +---------------------------+
```

### Step-by-Step Flow

When you run `dotnet run --project src/Deskribe.AppHost`, the following happens:

1. **Aspire creates a DistributedApplicationBuilder** -- the standard Aspire entry point.

2. **`AddDeskribeManifest(path)` is called** -- this reads your `deskribe.json` from disk and deserializes it into a `DeskribeManifest` object. The JSON converter handles polymorphic deserialization, mapping `"type": "postgres"` to `PostgresResource`, `"type": "redis"` to `RedisResource`, etc.

3. **For each resource in the manifest**, the extension method calls the appropriate Aspire builder:

   | Manifest `type`     | Aspire Method Called                                        |
   |---------------------|-------------------------------------------------------------|
   | `postgres`          | `builder.AddPostgres(name).AddDatabase(dbName)`            |
   | `redis`             | `builder.AddRedis(name)`                                   |
   | `kafka.messaging`   | `builder.AddKafka(name)`                                   |

4. **Local-dev settings are derived from existing resource properties** -- Aspire does not read a separate config section. Instead, it derives everything from standard resource properties:
   - `Version` property on the resource calls `.WithImageTag(version)` (e.g., `"version": "16"` on postgres results in image tag `16`)
   - Admin tools (PgAdmin, RedisInsight, KafkaUI) are always enabled in local dev
   - Persistence is always enabled (`ContainerLifetime.Persistent`) -- containers survive AppHost restarts
   - No environment variable injection from the manifest

5. **All resources are collected in a `DeskribeResourceMap`** -- this object holds references to every Aspire resource created, along with which ones provide connection strings and which ones should be waited on.

6. **`WithDeskribeResources(resources)` is called on your project** -- this iterates the resource map and calls `.WithReference(resource)` for each connection-string-providing resource and `.WaitFor(resource)` for each resource that needs to be ready before the project starts.

7. **Aspire starts all containers** -- Docker containers are pulled and started. Connection strings are automatically injected into your application's environment variables.

8. **The Aspire Dashboard opens** -- you can see all resources, their health status, structured logs, and connection strings at `http://localhost:15888`.

### What Happens for Each Resource Type

**Postgres:**
```
  deskribe.json: { "type": "postgres", "size": "m" }
       |
       v
  Aspire creates:
    1. PostgresServerResource  ("payments-api-postgres")
       - Docker image: postgres:16-alpine (or configured tag)
       - Port: 5432 (mapped to random host port)
       - PgAdmin sidecar container
    2. PostgresDatabaseResource ("payments-api-db")
       - Database created inside the server
       - Connection string: Host=localhost;Port=XXXXX;Database=payments-api-db;...
```

**Redis:**
```
  deskribe.json: { "type": "redis" }
       |
       v
  Aspire creates:
    1. RedisResource ("payments-api-redis")
       - Docker image: redis:latest (or configured tag)
       - Port: 6379 (mapped to random host port)
       - RedisInsight sidecar container
       - Connection string: localhost:XXXXX
```

**Kafka:**
```
  deskribe.json: { "type": "kafka.messaging", "topics": [...] }
       |
       v
  Aspire creates:
    1. KafkaServerResource ("payments-api-kafka")
       - Docker image: confluentinc/confluent-local (or configured tag)
       - Port: 9092 (mapped to random host port)
       - Kafka UI sidecar container
       - Connection string: localhost:XXXXX
```

---

## 2. Resource Mapping Table

The following table shows the complete mapping between Deskribe manifest resources, the Aspire resources they create, associated admin tools, and the connection string format.

| Manifest Resource    | Aspire Resource(s)                                     | Admin Tool  | Connection String Format                                       |
|----------------------|--------------------------------------------------------|-------------|----------------------------------------------------------------|
| `postgres`           | `PostgresServerResource` + `PostgresDatabaseResource`  | PgAdmin     | `Host=localhost;Port={port};Database={app}-db;Username=postgres;Password={auto}` |
| `redis`              | `RedisResource`                                        | RedisInsight| `localhost:{port}`                                             |
| `kafka.messaging`    | `KafkaServerResource`                                  | Kafka UI    | `localhost:{port}`                                             |

**Notes on connection strings:**
- Aspire assigns random host ports to avoid conflicts when running multiple projects.
- Connection strings are injected via Aspire's `IResourceWithConnectionString` mechanism.
- Your application code never needs to know the port -- it reads the connection string from configuration, which Aspire populates automatically.
- The `@resource(type).connectionString` syntax in `deskribe.json` resolves to Aspire connection strings in local dev and to real cloud connection strings in production.

### Admin Tools Detail

| Admin Tool   | Default Port | What It Does                                    |
|--------------|--------------|------------------------------------------------ |
| PgAdmin      | Auto-assigned| Web UI for browsing Postgres databases and tables|
| RedisInsight | Auto-assigned| Web UI for browsing Redis keys and running queries|
| Kafka UI     | Auto-assigned| Web UI for browsing topics, messages, and consumers|

All admin tools are always enabled in local development. There is no option to disable them -- local dev should have full observability.

---

## 3. How Aspire Derives Configuration

Aspire is a transparent engine. It reads standard resource properties from `deskribe.json` and derives all local-dev container settings from them. There is no separate `aspire` config section -- the same properties that drive production also drive local development.

### Derivation Rules

| Resource Property | Aspire Behavior                                                                 |
|-------------------|---------------------------------------------------------------------------------|
| `Version`         | Used as the container image tag via `.WithImageTag(version)`. If a resource has `"version": "16"`, the container runs with image tag `16`. If no `Version` is set, Aspire uses its built-in default image. |
| Admin tools       | Always enabled in local dev. PgAdmin for Postgres, RedisInsight for Redis, KafkaUI for Kafka. There is no option to disable them. |
| Persistence       | Always enabled. All containers use `ContainerLifetime.Persistent`, meaning they survive AppHost restarts. Data is preserved between runs. |

### How It Works

Aspire does not have its own config block. It reads the same properties your production infrastructure uses:

- If your Postgres resource has `"version": "16"`, Aspire runs `postgres:16`. Production provisions PostgreSQL 16 on your cloud provider. Same property, different implementation.
- Admin tools are always on because local development should have full observability. Production does not deploy admin UIs.
- Persistence is always on because rebuilding local state on every restart wastes developer time.

### Examples for Each Resource Type

**Postgres -- with version:**

```json
{
  "type": "postgres",
  "size": "m",
  "version": "16"
}
```

What this creates:
- A Postgres container with image tag `16` (derived from `Version`)
- A PgAdmin sidecar for browsing the database
- Persistent container (data survives restarts)
- A database named `{app}-db` is automatically created

**Redis -- minimal (no version):**

```json
{
  "type": "redis"
}
```

What this creates:
- A Redis container using the Aspire default image (no version override)
- RedisInsight admin tool enabled
- Persistent container (data survives restarts)

**Redis -- with version:**

```json
{
  "type": "redis",
  "version": "7"
}
```

What this creates:
- A Redis container with image tag `7` (derived from `Version`)
- RedisInsight admin tool enabled
- Persistent container (data survives restarts)

**Kafka -- with topics:**

```json
{
  "type": "kafka.messaging",
  "topics": [
    {
      "name": "payments.transactions",
      "partitions": 6,
      "retentionHours": 168,
      "owners": ["team-payments"],
      "consumers": ["team-fraud"]
    }
  ]
}
```

What this creates:
- A Kafka container using Aspire defaults (no `Version` property on Kafka -- it uses the built-in Confluent local image)
- Kafka UI for browsing topics and messages
- Persistent container

---

## 4. The AppHost -- How It Works

The AppHost is the Aspire orchestrator project. It is the entry point for local development. Here is the complete AppHost code with detailed annotations:

```csharp
// File: src/Deskribe.AppHost/AppHost.cs

using Deskribe.Aspire;

var builder = DistributedApplication.CreateBuilder(args);

// ------------------------------------------------------------------
// Step 1: Determine the manifest path.
//
// First, check if a custom path is provided via configuration
// (e.g., appsettings.json or command-line args).
// If not, default to the example manifest in the repo.
//
// In a real project, this would point to the deskribe.json
// in the developer's service repo.
// ------------------------------------------------------------------
var manifestPath = builder.Configuration["Deskribe:ManifestPath"]
    ?? Path.Combine(
        builder.AppHostDirectory,
        "..", "..",
        "examples", "payments-api", "deskribe.json"
    );

// ------------------------------------------------------------------
// Step 2: Read the manifest and create Aspire resources.
//
// AddDeskribeManifest does the following:
//   1. Reads and deserializes deskribe.json
//   2. For each resource (postgres, redis, kafka...):
//      - Calls the appropriate Aspire builder method
//      - Derives container config from resource properties (Version → image tag, always persistent, always admin tools)
//      - Registers the resource in a DeskribeResourceMap
//   3. Returns the map for wiring to projects
//
// After this call, Docker containers are scheduled to start.
// ------------------------------------------------------------------
var resources = builder.AddDeskribeManifest(manifestPath);

// ------------------------------------------------------------------
// Step 3: Add your project and wire Deskribe resources to it.
//
// WithDeskribeResources does two things:
//   - .WithReference(resource) for each connection-string resource
//     This injects the connection string into the project's env vars
//   - .WaitFor(resource) for each resource
//     This ensures the resource is healthy before the project starts
//
// .WithExternalHttpEndpoints() exposes the web UI outside the
// Aspire network so you can open it in your browser.
// ------------------------------------------------------------------
var web = builder.AddProject<Projects.Deskribe_Web>("deskribe-web")
    .WithDeskribeResources(resources)
    .WithExternalHttpEndpoints();

// ------------------------------------------------------------------
// Step 4: Build and run.
//
// Aspire pulls Docker images, starts containers, waits for health
// checks, injects connection strings, and starts your project.
// The Aspire Dashboard opens at http://localhost:15888.
// ------------------------------------------------------------------
builder.Build().Run();
```

### How `AddDeskribeManifest` Works Internally

```
  AddDeskribeManifest(manifestPath)
       |
       v
  1. Read file from disk
       Path.GetFullPath(manifestPath)
       File.ReadAllText(fullPath)
       |
       v
  2. Deserialize JSON with polymorphic converter
       JsonSerializer.Deserialize<DeskribeManifest>(json, options)
       |
       |  The ManifestResourceJsonConverter reads the "type" field
       |  and deserializes to the correct C# type:
       |    "postgres"        -> PostgresResource
       |    "redis"           -> RedisResource
       |    "kafka.messaging" -> KafkaMessagingResource
       |
       v
  3. Create a DeskribeResourceMap(manifest.Name)
       This is the container for all Aspire resources
       |
       v
  4. For each resource in manifest.Resources:
       |
       +-- "postgres" -----> AddPostgresResource()
       |     builder.AddPostgres("{app}-postgres")
       |       .WithImageTag(version)                   // if resource has Version
       |       .WithPgAdmin()                           // always on
       |       .WithLifetime(Persistent)                // always on
       |     server.AddDatabase("{app}-db")
       |     map.AddConnectionStringResource(db)
       |     map.AddWaitForResource(db)
       |
       +-- "redis" --------> AddRedisResource()
       |     builder.AddRedis("{app}-redis")
       |       .WithImageTag(version)                   // if resource has Version
       |       .WithRedisInsight()                      // always on
       |       .WithLifetime(Persistent)                // always on
       |     map.AddConnectionStringResource(redis)
       |     map.AddWaitForResource(redis)
       |
       +-- "kafka.messaging" -> AddKafkaResource()
             builder.AddKafka("{app}-kafka")
               .WithKafkaUI()                           // always on
               .WithLifetime(Persistent)                // always on
             map.AddConnectionStringResource(kafka)
             map.AddWaitForResource(kafka)
       |
       v
  5. Return the DeskribeResourceMap
```

### How `WithDeskribeResources` Wires Connection Strings

```
  WithDeskribeResources(resources)
       |
       v
  For each resource in resources.ConnectionStringResources:
       projectBuilder.WithReference(resource)
       |
       |  This tells Aspire: "inject this resource's connection
       |  string into the project's environment variables."
       |
       |  Aspire generates environment variables like:
       |    ConnectionStrings__payments-api-db = Host=...;Port=...;...
       |    ConnectionStrings__payments-api-redis = localhost:...
       |    ConnectionStrings__payments-api-kafka = localhost:...
       |
       v
  For each resource in resources.WaitForResources:
       projectBuilder.WaitFor(resource)
       |
       |  This tells Aspire: "do not start the project until
       |  this resource passes its health check."
       |
       v
  Return the project builder (fluent API)
```

---

## 5. Adding a Custom Resource to Aspire

If you are building a new Deskribe plugin (for example, MongoDB), you need to add Aspire support so that the resource works in local development.

### Step 1: Define the SDK Resource

Create a new resource type in `Deskribe.Sdk`:

```csharp
// File: src/Deskribe.Sdk/Resources/MongoDbResource.cs

namespace Deskribe.Sdk.Resources;

public sealed record MongoDbResource : DeskribeResource
{
    public string? Version { get; init; }
    public bool? ReplicaSet { get; init; }
    public string? DatabaseName { get; init; }
}
```

### Step 2: Register the Type in the JSON Converter

Add the new type to the `ManifestResourceJsonConverter` switch expression in `DeskribeAspireExtensions.cs`:

```csharp
return type switch
{
    "postgres"        => JsonSerializer.Deserialize<SdkPostgresResource>(rawJson, innerOptions),
    "redis"           => JsonSerializer.Deserialize<SdkRedisResource>(rawJson, innerOptions),
    "kafka.messaging" => JsonSerializer.Deserialize<SdkKafkaResource>(rawJson, innerOptions),
    "mongodb"         => JsonSerializer.Deserialize<SdkMongoDbResource>(rawJson, innerOptions),  // NEW
    _ => throw new JsonException($"Unknown resource type: {type}")
};
```

### Step 3: Add the Aspire Builder Method

Add a new private method to `DeskribeAspireExtensions` following the existing pattern:

```csharp
private static void AddMongoDbResource(
    IDistributedApplicationBuilder builder,
    string appName,
    SdkMongoDbResource? mongoResource,
    DeskribeResourceMap map)
{
    var serverName = $"{appName}-mongodb";
    var dbName = mongoResource?.DatabaseName ?? $"{appName}-db";

    // Use the Aspire MongoDB hosting package
    var server = builder.AddMongoDB(serverName);

    // Derive image tag from the resource's Version property
    if (mongoResource?.Version is { } version)
        server = server.WithImageTag(version);

    // Admin tools are always on in local dev
    server = server.WithMongoExpress();

    // Persistence is always on
    server = server.WithLifetime(ContainerLifetime.Persistent);

    var db = server.AddDatabase(dbName);

    map.AddConnectionStringResource(db);
    map.AddWaitForResource(db);
    map.RegisterResource("mongodb", serverName, dbName);
}
```

### Step 4: Add the Case to the Switch in AddDeskribeManifest

```csharp
foreach (var resource in manifest.Resources)
{
    switch (resource.Type)
    {
        case "postgres":
            AddPostgresResource(builder, manifest.Name, resource as SdkPostgresResource, map);
            break;
        case "redis":
            AddRedisResource(builder, manifest.Name, resource as SdkRedisResource, map);
            break;
        case "kafka.messaging":
            AddKafkaResource(builder, manifest.Name, resource as SdkKafkaResource, map);
            break;
        case "mongodb":  // NEW
            AddMongoDbResource(builder, manifest.Name, resource as SdkMongoDbResource, map);
            break;
    }
}
```

### Step 5: Add the NuGet Package

Add the Aspire MongoDB hosting package to `Deskribe.Aspire.csproj`:

```xml
<PackageReference Include="Aspire.Hosting.MongoDB" Version="10.0.0" />
```

### The Extension Method Pattern

Every resource follows the same pattern:

```
  1. Create a server resource      builder.Add{Type}(name)
  2. Apply image tag               .WithImageTag(version)     // if resource has Version
  3. Apply admin tool              .With{AdminTool}()         // always on
  4. Apply persistence             .WithLifetime(Persistent)  // always on
  5. (Optional) Create child       server.AddDatabase(dbName)
  6. Register in the map           map.AddConnectionStringResource(...)
                                   map.AddWaitForResource(...)
                                   map.RegisterResource(type, name, child)
```

This pattern makes it straightforward to add support for any new resource that has an Aspire hosting package: RabbitMQ, MySQL, Elasticsearch, Milvus, Qdrant, and so on.

---

## 6. Development Workflow

This section walks through the full developer experience from cloning the repo to seeing resources in the Aspire dashboard.

### Step 1: Clone the Repository

```bash
git clone https://github.com/acme/payments-api.git
cd payments-api
```

The repo contains:

```
payments-api/
  src/
    Payments.Api/            Your application code
    Payments.AppHost/        Aspire orchestrator
    Payments.ServiceDefaults/
  deskribe.json              The Deskribe manifest
```

### Step 2: Add a Resource to deskribe.json

Suppose the team needs Redis for caching. Open `deskribe.json` and add it:

```json
{
  "name": "payments-api",
  "resources": [
    { "type": "postgres", "size": "m" },
    { "type": "redis" }
  ],
  "services": [
    {
      "env": {
        "ConnectionStrings__Postgres": "@resource(postgres).connectionString",
        "Redis__Endpoint": "@resource(redis).endpoint"
      }
    }
  ]
}
```

That is it. One line added to resources, one line added to env.

### Step 3: Press F5 (or `dotnet run`)

```bash
dotnet run --project src/Payments.AppHost
```

Output:

```
  info: Aspire.Hosting.DistributedApplication[0]
        Aspire version: 10.0.0
  info: Aspire.Hosting.DistributedApplication[0]
        Distributed application starting.
  info: Aspire.Hosting.DistributedApplication[0]
        Starting resource payments-api-postgres...
  info: Aspire.Hosting.DistributedApplication[0]
        Starting resource payments-api-db...
  info: Aspire.Hosting.DistributedApplication[0]
        Starting resource payments-api-redis...
  info: Aspire.Hosting.DistributedApplication[0]
        Starting resource deskribe-web...
  info: Aspire.Hosting.DistributedApplication[0]
        Dashboard is running at http://localhost:15888
```

### Step 4: See It in the Aspire Dashboard

Open `http://localhost:15888` in your browser:

```
  +------------------------------------------------------------------+
  |  .NET Aspire Dashboard                                           |
  +------------------------------------------------------------------+
  |                                                                  |
  |  Resources                                                       |
  |  +------------------------------------------------------------+  |
  |  | Name                    | Type       | State   | Endpoints |  |
  |  |------------------------------------------------------------|  |
  |  | payments-api-postgres   | Container  | Running | 5432      |  |
  |  | payments-api-db         | Postgres DB| Running |           |  |
  |  | payments-api-redis      | Container  | Running | 6379      |  |
  |  | pgadmin                 | Container  | Running | 8080      |  |
  |  | redis-insight           | Container  | Running | 5540      |  |
  |  | deskribe-web            | Project    | Running | 5000      |  |
  |  +------------------------------------------------------------+  |
  |                                                                  |
  |  Click any resource to see:                                      |
  |    - Structured logs (live streaming)                            |
  |    - Environment variables (including connection strings)        |
  |    - Health check status                                         |
  |    - Resource details and endpoints                              |
  |                                                                  |
  +------------------------------------------------------------------+
```

### Step 5: Verify Connection Strings

Click on `deskribe-web` in the dashboard. Under "Environment", you see the injected connection strings:

```
  ConnectionStrings__payments-api-db = Host=localhost;Port=52341;
      Database=payments-api-db;Username=postgres;Password=abc123xyz
  ConnectionStrings__payments-api-redis = localhost:52342
```

Your application reads these from `IConfiguration` as normal. No manual wiring needed.

### Step 6: Use the Admin Tools

Click the PgAdmin endpoint to open the database admin UI:
- The Postgres server is pre-configured
- Browse tables, run queries, inspect schemas

Click the RedisInsight endpoint to open the Redis admin UI:
- See all keys in real time
- Run Redis commands
- Monitor memory usage

### Step 7: Iterate

Need Kafka? Add it to `deskribe.json`:

```json
{
  "type": "kafka.messaging",
  "topics": [
    { "name": "payments.transactions", "partitions": 6 }
  ]
}
```

Restart the AppHost (Ctrl+C, F5). Kafka appears in the dashboard with Kafka UI. No Docker Compose file to write. No ports to manage. No environment variables to manually wire.

### The Full Loop

```
  Edit deskribe.json
       |
       v
  dotnet run --project AppHost    (or press F5 in your IDE)
       |
       v
  Deskribe reads manifest
       |
       v
  Aspire starts containers
       |
       v
  Connection strings injected
       |
       v
  App starts with all dependencies ready
       |
       v
  Open Dashboard to inspect
       |
       v
  Open Admin Tools to manage data
       |
       v
  Write code, test, repeat
```

---

## 7. Aspire vs Production

The same `deskribe.json` drives both local development and production, but the underlying implementation is completely different. Here is what happens in each environment for every resource type:

```
+-------------------+-------------------------------+------------------------------------+
|                   | Local (Aspire)                | Production                         |
+-------------------+-------------------------------+------------------------------------+
| postgres          | Docker container              | Azure Database for PostgreSQL       |
|                   | (postgres:16-alpine)          | AWS RDS PostgreSQL                  |
|                   | + PgAdmin container           | Helm chart (bitnami/postgresql)     |
|                   |                               | Managed by Pulumi/Terraform         |
+-------------------+-------------------------------+------------------------------------+
| redis             | Docker container              | Azure Cache for Redis               |
|                   | (redis:latest)                | AWS ElastiCache                     |
|                   | + RedisInsight container      | Helm chart (bitnami/redis)          |
|                   |                               | Managed by Pulumi/Terraform         |
+-------------------+-------------------------------+------------------------------------+
| kafka.messaging   | Docker container              | Confluent Cloud                     |
|                   | (confluentinc/confluent-local)| AWS MSK                             |
|                   | + Kafka UI container          | Helm chart (bitnami/kafka)          |
|                   |                               | Managed by Pulumi/Terraform         |
+-------------------+-------------------------------+------------------------------------+
| Connection        | Injected by Aspire via        | Injected by Kubernetes Secrets      |
| strings           | IConfiguration automatically  | Generated by Pulumi/Terraform       |
|                   | Using WithReference()         | Mounted as env vars in the pod      |
+-------------------+-------------------------------+------------------------------------+
| Admin tools       | PgAdmin, RedisInsight,        | Not deployed (production does not   |
|                   | Kafka UI -- all running       | need admin UIs in the cluster)      |
|                   | as sidecar containers         |                                     |
+-------------------+-------------------------------+------------------------------------+
| Resource sizing   | Ignored (local containers     | Applied by platform config          |
|                   | use whatever Docker gives)    | size: "m" maps to specific SKU      |
+-------------------+-------------------------------+------------------------------------+
| HA / Replication  | Single instance (no HA        | Multi-replica, cross-AZ, based on   |
|                   | needed for local dev)         | platform config and env overrides   |
+-------------------+-------------------------------+------------------------------------+
| Networking        | Docker bridge network         | Kubernetes cluster networking       |
|                   | Ports mapped to localhost     | Service discovery via DNS           |
+-------------------+-------------------------------+------------------------------------+
| State persistence | Always persistent (containers | Always persistent (managed disks,   |
|                   | survive restarts)             | cloud-native storage)               |
+-------------------+-------------------------------+------------------------------------+
```

### What Stays the Same

Despite the different implementations, these things remain identical:

1. **The manifest** -- `deskribe.json` is the same file in both environments.
2. **The resource references** -- `@resource(postgres).connectionString` resolves in both contexts.
3. **The environment variables** -- your application reads `ConnectionStrings__Postgres` from `IConfiguration` whether it is running locally or in Kubernetes.
4. **The validation rules** -- `deskribe validate` checks the same policies regardless of whether you are deploying locally or to prod.

### What Changes

The platform config layers control what is different:

```
  Local dev:
    - No platform config needed
    - Aspire handles everything
    - Resources are Docker containers
    - No cloud credentials required

  Production (dev env):
    - Platform base.json + envs/dev.json merged
    - Smaller instance sizes
    - Resources provisioned via Pulumi/Terraform
    - Cloud credentials required

  Production (prod env):
    - Platform base.json + envs/prod.json merged
    - Larger instance sizes, HA enabled
    - Stricter policies (TLS enforced, restricted regions)
    - Resources provisioned via Pulumi/Terraform
    - Cloud credentials required
```

### Why This Matters

The Aspire integration means:

- **No Docker Compose** -- you never write or maintain a `docker-compose.yml`. The manifest is the source of truth.
- **No port conflicts** -- Aspire assigns random ports. Run multiple projects simultaneously without conflicts.
- **No manual wiring** -- connection strings are injected automatically. Add a resource to the manifest, restart, and it works.
- **Parity with production** -- the same resource types are used locally and in production. If your manifest validates against prod policies, it will work in prod.
- **Fast onboarding** -- new team members clone the repo, run `dotnet run --project AppHost`, and have a fully working local environment in under a minute.
