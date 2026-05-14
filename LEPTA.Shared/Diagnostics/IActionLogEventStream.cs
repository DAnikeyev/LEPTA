namespace LEPTA.Shared.Diagnostics;

public interface IActionLogEventStream
{
    event EventHandler<ActionLogEntry>? EntryPublished;

    IReadOnlyList<ActionLogEntry> GetEntries();

    ActionLogEntry Publish(string source, string message, ActionLogLevel level = ActionLogLevel.Info);
}

