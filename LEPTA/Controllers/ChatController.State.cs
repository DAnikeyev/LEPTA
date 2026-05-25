using System.Windows;
using LEPTA.Shared.Diagnostics;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Controllers;

internal sealed partial class ChatController
{
    private void PublishAction(string message, ActionLogLevel level = ActionLogLevel.Info)
        => actionLog.Publish(nameof(ChatController), message, level);

    private void ClearConversation(string initialMessage)
    {
        SaveCurrentConversationToHistory();
        pendingHistoryEntryId = null;
        pendingHistoryTitle = null;
        conversation.Clear();
        messages.MessagesPanel.Children.Clear();
        UpdateEmptyStateVisibility();
        AddSystemMessage(initialMessage);
        logger.Log(nameof(ChatController), $"Conversation cleared. initialMessage={initialMessage}");
    }

    private void UpdateAvailabilityState(bool isChatAvailable, string message)
    {
        if (!isSending)
        {
            input.InputBox.IsEnabled = isChatAvailable;
            input.SendButton.IsEnabled = isChatAvailable;
        }

        input.InputBox.ToolTip = message;
        UpdateStatus(message);
    }

    private void SetBusyState(bool busy, string statusMessage)
    {
        isSending = busy;
        chrome.ProgressBar.IsIndeterminate = busy;
        chrome.ServerCombo.IsEnabled = !busy;
        input.NewChatButton.IsEnabled = !busy;
        input.InputBox.IsEnabled = !busy && chrome.ServerCombo.SelectedItem is VllmServerConfiguration server && server.HasEstablishedConnection;
        input.SendButton.IsEnabled = !busy && input.InputBox.IsEnabled;
        settings.ThinkingCheckBox.IsEnabled = !busy && (chrome.ServerCombo.SelectedItem as VllmServerConfiguration)?.SupportsThinking == true;
        input.StopButton.IsEnabled = busy;
        UpdateStatus(statusMessage);
    }

    private void UpdateStatus(string message)
    {
        chrome.StatusText.Text = message;
    }

    private string ResolveSystemInstruction()
        => VllmConversationService.DefaultSystemPrompt;

    private void HandleThinkingChanged()
    {
        if (!suppressStateChanged)
        {
            StateChanged?.Invoke();
        }
    }

    private void UpdateThinkingAvailability(VllmServerConfiguration? server)
    {
        var supportsThinking = server?.SupportsThinking == true;
        settings.ThinkingCheckBox.IsEnabled = !isSending && supportsThinking;
        settings.ThinkingCheckBox.ToolTip = supportsThinking
            ? "Use extra reasoning when the server supports it."
            : "Thinking works only on reasoning-capable servers.";
    }

    private void UpdateEmptyStateVisibility()
        => messages.EmptyState.Visibility = messages.MessagesPanel.Children.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private static async Task AwaitCompletionAsync(Task completionTask, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        await Task.WhenAny(completionTask, timeoutTask);
    }

    private VllmRequestOptions CreateRequestOptions(VllmServerConfiguration server) => new()
    {
        EnableThinking = server.SupportsThinking && settings.ThinkingCheckBox.IsChecked == true
    };
}


