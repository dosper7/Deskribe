namespace PaymentsApi.Models;

public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = "pending";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}

public record CreatePaymentRequest(decimal Amount, string Currency = "EUR", string? Description = null);

public record PaymentResponse(Guid Id, decimal Amount, string Currency, string Status, string? Description, DateTime CreatedAt);
