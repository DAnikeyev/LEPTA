using System.Windows;
using LEPTA.Shared.Diagnostics;

namespace LEPTA.Services;

internal static class UserNotificationService
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTimeOffset> RecentMessages = new(StringComparer.Ordinal);
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(2);

    public static void ShowWarning(
        string title,
        string message,
        Window? owner = null,
        ILeptaLogger? logger = null,
        IActionLogEventStream? actionLog = null,
        string source = "LEPTA")
        => Show(MessageBoxImage.Warning, title, message, owner, logger, actionLog, source, ActionLogLevel.Warning);

    public static void ShowError(
        string title,
        string message,
        Window? owner = null,
        ILeptaLogger? logger = null,
        IActionLogEventStream? actionLog = null,
        string source = "LEPTA")
        => Show(MessageBoxImage.Error, title, message, owner, logger, actionLog, source, ActionLogLevel.Error);

    public static MessageBoxResult Confirm(
        string title,
        string message,
        MessageBoxButton buttons = MessageBoxButton.YesNo,
        MessageBoxImage image = MessageBoxImage.Question,
        Window? owner = null)
    {
        var normalizedMessage = NormalizeMessage(message);
        var resolvedOwner = ResolveOwner(owner);
        return resolvedOwner is null
            ? MessageBox.Show(normalizedMessage, title, buttons, image)
            : MessageBox.Show(resolvedOwner, normalizedMessage, title, buttons, image);
    }

    private static void Show(
        MessageBoxImage image,
        string title,
        string message,
        Window? owner,
        ILeptaLogger? logger,
        IActionLogEventStream? actionLog,
        string source,
        ActionLogLevel level)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "LEPTA" : title.Trim();
        var normalizedMessage = NormalizeMessage(message);
        logger?.Log(source, $"{normalizedTitle}: {normalizedMessage}");
        actionLog?.Publish(source, normalizedMessage, level);

        if (!ShouldDisplay(normalizedTitle, normalizedMessage, image))
        {
            return;
        }

        var resolvedOwner = ResolveOwner(owner);
        if (resolvedOwner is null)
        {
            MessageBox.Show(normalizedMessage, normalizedTitle, MessageBoxButton.OK, image);
            return;
        }

        MessageBox.Show(resolvedOwner, normalizedMessage, normalizedTitle, MessageBoxButton.OK, image);
    }

    private static bool ShouldDisplay(string title, string message, MessageBoxImage image)
    {
        var now = DateTimeOffset.UtcNow;
        var key = $"{title}\n{message}\n{(int)image}";

        lock (Sync)
        {
            foreach (var staleKey in RecentMessages
                         .Where(entry => now - entry.Value > DuplicateWindow)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                RecentMessages.Remove(staleKey);
            }

            if (RecentMessages.TryGetValue(key, out var lastShownAt)
                && now - lastShownAt <= DuplicateWindow)
            {
                return false;
            }

            RecentMessages[key] = now;
            return true;
        }
    }

    private static Window? ResolveOwner(Window? owner)
    {
        if (owner is not null)
        {
            return owner;
        }

        return Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive)
            ?? Application.Current?.MainWindow;
    }

    private static string NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "An unexpected problem occurred.";
        }

        var lines = message
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries);

        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }
}

