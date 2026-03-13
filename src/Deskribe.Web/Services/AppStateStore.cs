using System.Collections.Concurrent;

namespace Deskribe.Web.Services;

/// <summary>
/// In-memory state tracking for app/environment operations.
/// Tracks the last operation result, status, and timestamp.
/// Resets on application restart.
/// </summary>
public class AppStateStore
{
    private readonly ConcurrentDictionary<string, AppEnvState> _states = new();

    public AppEnvState GetState(string appName, string environment)
    {
        return _states.GetOrAdd(Key(appName, environment), _ => new AppEnvState
        {
            AppName = appName,
            Environment = environment,
            Status = AppStatus.Unknown
        });
    }

    public void SetStatus(string appName, string environment, AppStatus status, string? message = null)
    {
        var state = GetState(appName, environment);
        state.Status = status;
        state.LastMessage = message;
        state.LastUpdated = DateTimeOffset.UtcNow;
    }

    public AppStatus GetAggregateStatus(string appName, string[] environments)
    {
        var statuses = environments.Select(e => GetState(appName, e).Status).ToList();
        if (statuses.Any(s => s == AppStatus.Error)) return AppStatus.Error;
        if (statuses.Any(s => s == AppStatus.Applied)) return AppStatus.Applied;
        if (statuses.Any(s => s == AppStatus.Planned)) return AppStatus.Planned;
        return AppStatus.Unknown;
    }

    private static string Key(string appName, string environment) => $"{appName}:{environment}";
}

public class AppEnvState
{
    public required string AppName { get; init; }
    public required string Environment { get; init; }
    public AppStatus Status { get; set; } = AppStatus.Unknown;
    public string? LastMessage { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
}

public enum AppStatus
{
    Unknown,
    Planned,
    Applied,
    Error,
    Destroyed
}
