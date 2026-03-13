using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using PaymentsApi.Data;
using PaymentsApi.Endpoints;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// --- Postgres (via EF Core) ---
// Reads from env var: ConnectionStrings__Postgres (set by Deskribe @resource(postgres).connectionString)
var pgConn = builder.Configuration.GetConnectionString("Postgres")
    ?? builder.Configuration["ConnectionStrings__Postgres"]
    ?? "Host=localhost;Database=payments;Username=postgres;Password=postgres";

builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseNpgsql(pgConn));

// --- Redis ---
// Reads from env var: Redis__Endpoint (set by Deskribe @resource(redis).endpoint)
var redisEndpoint = builder.Configuration["Redis__Endpoint"]
    ?? builder.Configuration["Redis:Endpoint"]
    ?? "localhost:6379";

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(redisEndpoint));

// --- Kafka Producer ---
// Reads from env var: Kafka__BootstrapServers (set by Deskribe @resource(kafka.messaging).endpoint)
var kafkaBootstrap = builder.Configuration["Kafka__BootstrapServers"]
    ?? builder.Configuration["Kafka:BootstrapServers"]
    ?? "localhost:9092";

builder.Services.AddSingleton<IProducer<string, string>>(sp =>
{
    var config = new ProducerConfig { BootstrapServers = kafkaBootstrap };
    return new ProducerBuilder<string, string>(config).Build();
});

// --- OpenAPI ---
builder.Services.AddOpenApi();

var app = builder.Build();

// Auto-migrate on startup (dev convenience)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    try
    {
        await db.Database.EnsureCreatedAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not auto-migrate database — it may not be available yet");
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// --- Health check ---
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
    .WithTags("Health");

// --- Payment endpoints ---
app.MapPaymentEndpoints();

app.Run();
