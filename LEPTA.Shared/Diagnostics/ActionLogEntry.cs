namespace LEPTA.Shared.Diagnostics;

public sealed class ActionLogEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Source { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public ActionLogLevel Level { get; init; } = ActionLogLevel.Info;

    public string DisplayTimestamp => TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
}

