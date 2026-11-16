using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using PaymentsApi.Data;
using PaymentsApi.Endpoints;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Each setting is bound by deskribe.json (`env`) and arrives as an environment variable,
// e.g. ConnectionStrings__Postgres -> configuration key ConnectionStrings:Postgres.
// Only in Development a missing value falls back to a localhost default (running the API
// on its own, without the AppHost). Elsewhere a missing value is a broken binding and
// fails startup instead of silently pointing at localhost.
string Setting(string key, string devFallback)
{
    var value = builder.Configuration[key];
    if (!string.IsNullOrWhiteSpace(value))
        return value;

    if (builder.Environment.IsDevelopment())
    {
        Console.WriteLine($"warn: configuration '{key}' is not set; using development default '{devFallback}'.");
        return devFallback;
    }

    throw new InvalidOperationException(
        $"Configuration '{key}' is not set. Check that the env binding for it in deskribe.json is resolved.");
}

// --- Postgres (via EF Core) --- @resource(postgres).connectionString
var pgConn = Setting("ConnectionStrings:Postgres", "Host=localhost;Database=payments;Username=postgres");

builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseNpgsql(pgConn));

// --- Redis --- @resource(redis).endpoint
var redisEndpoint = Setting("Redis:Endpoint", "localhost:6379");

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(redisEndpoint));

// --- Kafka Producer --- @resource(kafka.messaging).endpoint
var kafkaBootstrap = Setting("Kafka:BootstrapServers", "localhost:9092");

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
