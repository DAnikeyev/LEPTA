namespace LEPTA.Shared.Diagnostics;

public sealed class NullActionLogEventStream : IActionLogEventStream
{
    public static NullActionLogEventStream Instance { get; } = new();

    public event EventHandler<ActionLogEntry>? EntryPublished
    {
        add { }
        remove { }
    }

    public IReadOnlyList<ActionLogEntry> GetEntries() => [];

    public ActionLogEntry Publish(string source, string message, ActionLogLevel level = ActionLogLevel.Info)
        => new()
        {
            Source = source,
            Message = message,
            Level = level
        };
}

