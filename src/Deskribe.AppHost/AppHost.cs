using Deskribe.Aspire;

var builder = DistributedApplication.CreateBuilder(args);

// ------------------------------------------------------------------
// Read the developer's deskribe.json — single source of truth.
// All resources declared in the manifest get real Aspire containers:
//   postgres → Postgres container + PgAdmin
//   redis    → Redis container + RedisInsight
//   kafka    → Kafka container + Kafka UI
//
// The same manifest later drives production infra via Pulumi/Terraform.
// Developer writes ONE file. Deskribe translates it everywhere.
// ------------------------------------------------------------------
var configuredPath = builder.Configuration["Deskribe:ManifestPath"];
var manifestPath = configuredPath is not null
    ? Path.GetFullPath(Path.Combine(builder.AppHostDirectory, configuredPath))
    : Path.Combine(builder.AppHostDirectory, "..", "..", "examples", "payments-api", "deskribe.json");

// Developer tools (PgAdmin, RedisInsight, Kafka UI) and persistent containers default to on.
var resources = builder.AddDeskribeManifest(manifestPath, options: new DeskribeAspireOptions
{
    DeveloperTools = true,
    PersistentContainers = true
});

// Payments API — the sample application using Postgres, Redis, and Kafka.
// The manifest's service `env` map is applied as-is (@resource(...) refs resolve to the local containers).
// Pass a serviceName to WithDeskribeResources if the manifest declares several services.
var paymentsApi = builder.AddProject<Projects.PaymentsApi>("payments-api")
    .WithDeskribeResources(resources)
    .WithExternalHttpEndpoints();

// Deskribe Web UI — dashboard showing what's running (consumes no manifest resources)
var web = builder.AddProject<Projects.Deskribe_Web>("deskribe-web")
    .WithExternalHttpEndpoints();

builder.Build().Run();
