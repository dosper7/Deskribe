namespace Deskribe.Sdk;

public interface IAlertSource
{
    string Name { get; }
    Task<IReadOnlyList<AlertRule>> GenerateAlertsAsync(AlertContext context, CancellationToken ct = default);
}

public record AlertContext
{
    public required string AppName { get; init; }
    public required string Environment { get; init; }
    public Dictionary<string, List<string>> Routing { get; init; } = new();
}

public record AlertRule
{
    public required string Name { get; init; }
    public required string Expression { get; init; }
    public string Severity { get; init; } = "warning";
    public string Duration { get; init; } = "5m";
}
