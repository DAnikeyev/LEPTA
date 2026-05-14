namespace LEPTA.Shared.Diagnostics;

public sealed class ActionLogEventStream : IActionLogEventStream
{
    private readonly object sync = new();
    private readonly List<ActionLogEntry> entries = [];
    private readonly int maxEntries;

    public ActionLogEventStream(int maxEntries = 50)
    {
        if (maxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Action log capacity must be at least one entry.");
        }

        this.maxEntries = maxEntries;
    }

    public event EventHandler<ActionLogEntry>? EntryPublished;

    public IReadOnlyList<ActionLogEntry> GetEntries()
    {
        lock (sync)
        {
            return entries.ToArray();
        }
    }

    public ActionLogEntry Publish(string source, string message, ActionLogLevel level = ActionLogLevel.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var entry = new ActionLogEntry
        {
            Source = source.Trim(),
            Message = message.Trim(),
            Level = level,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        lock (sync)
        {
            entries.Add(entry);
            if (entries.Count > maxEntries)
            {
                entries.RemoveRange(0, entries.Count - maxEntries);
            }
        }

        EntryPublished?.Invoke(this, entry);
        return entry;
    }
}



