using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using PaymentsApi.Data;
using PaymentsApi.Models;
using StackExchange.Redis;

namespace PaymentsApi.Endpoints;

public static class PaymentEndpoints
{
    public static RouteGroupBuilder MapPaymentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/payments").WithTags("Payments");

        group.MapPost("/", CreatePayment)
            .WithName("CreatePayment")
            .Produces<PaymentResponse>(StatusCodes.Status201Created);

        group.MapGet("/", ListPayments)
            .WithName("ListPayments")
            .Produces<List<PaymentResponse>>();

        group.MapGet("/{id:guid}", GetPayment)
            .WithName("GetPayment")
            .Produces<PaymentResponse>()
            .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> CreatePayment(
        CreatePaymentRequest request,
        PaymentsDbContext db,
        IProducer<string, string> kafkaProducer,
        ILogger<Payment> logger)
    {
        var payment = new Payment
        {
            Amount = request.Amount,
            Currency = request.Currency,
            Description = request.Description,
            Status = "pending"
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        // Publish to Kafka
        try
        {
            var message = new Message<string, string>
            {
                Key = payment.Id.ToString(),
                Value = JsonSerializer.Serialize(new
                {
                    payment.Id,
                    payment.Amount,
                    payment.Currency,
                    payment.Status,
                    payment.CreatedAt,
                    EventType = "payment.created"
                })
            };

            await kafkaProducer.ProduceAsync("payments.transactions", message);
            logger.LogInformation("Published payment {Id} to Kafka", payment.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish payment {Id} to Kafka — continuing without event", payment.Id);
        }

        var response = ToResponse(payment);
        return Results.Created($"/api/payments/{payment.Id}", response);
    }

    private static async Task<IResult> ListPayments(
        PaymentsDbContext db,
        int? limit)
    {
        var payments = await db.Payments
            .OrderByDescending(p => p.CreatedAt)
            .Take(limit ?? 50)
            .Select(p => ToResponse(p))
            .ToListAsync();

        return Results.Ok(payments);
    }

    private static async Task<IResult> GetPayment(
        Guid id,
        PaymentsDbContext db,
        IConnectionMultiplexer redis,
        ILogger<Payment> logger)
    {
        // Try Redis cache first
        var cacheDb = redis.GetDatabase();
        var cacheKey = $"payment:{id}";

        try
        {
            var cached = await cacheDb.StringGetAsync(cacheKey);
            if (cached.HasValue)
            {
                logger.LogDebug("Cache hit for payment {Id}", id);
                var cachedPayment = JsonSerializer.Deserialize<PaymentResponse>(cached!);
                return Results.Ok(cachedPayment);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis cache read failed for {Id} — falling back to DB", id);
        }

        // Fall back to Postgres
        var payment = await db.Payments.FindAsync(id);
        if (payment is null)
            return Results.NotFound();

        var response = ToResponse(payment);

        // Cache the result
        try
        {
            await cacheDb.StringSetAsync(cacheKey, JsonSerializer.Serialize(response), TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis cache write failed for {Id}", id);
        }

        return Results.Ok(response);
    }

    private static PaymentResponse ToResponse(Payment p) =>
        new(p.Id, p.Amount, p.Currency, p.Status, p.Description, p.CreatedAt);
}
