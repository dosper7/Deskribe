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
var manifestPath = builder.Configuration["Deskribe:ManifestPath"]
    ?? Path.Combine(builder.AppHostDirectory, "..", "..", "examples", "payments-api", "deskribe.json");

var resources = builder.AddDeskribeManifest(manifestPath);

// Deskribe Web UI — dashboard showing what's running
var web = builder.AddProject<Projects.Deskribe_Web>("deskribe-web")
    .WithDeskribeResources(resources)
    .WithExternalHttpEndpoints();

builder.Build().Run();
