using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LEPTA.Controls;
using LEPTA.Shared.Diagnostics;
using LEPTA.Shared.Models;
using LEPTA.Theming;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Controllers;

internal sealed class ChatController
{
    private const string AssistantRole = "assistant";
    private const string UserRole = "user";
    private const string SystemRole = "system";
    private const int ChatResponseMaxTokens = 256;

    private readonly Panel messagesPanel;
    private readonly TextBox chatInputBox;
    private readonly TextBox chatSystemInstructionBox;
    private readonly TextBlock chatSystemInstructionHintText;
    private readonly ComboBox chatServerCombo;
    private readonly Button newChatButton;
    private readonly Button sendButton;
    private readonly Button stopButton;
    private readonly TextBlock chatStatusText;
    private readonly ScrollViewer messagesScrollViewer;
    private readonly ProgressBar chatProgress;
    private readonly VllmDeploymentService deploymentService;
    private readonly VllmConversationService conversationService;
    private readonly ILeptaLogger logger;
    private readonly IActionLogEventStream actionLog;
    private readonly List<VllmChatMessage> conversation = [];

    private string? activeEndpoint;
    private CancellationTokenSource? currentSendCts;
    private bool isSending;
    private bool suppressStateChanged;
    private bool hasPendingServerRefresh;

    public ChatController(
        Panel messagesPanel,
        TextBox chatInputBox,
        TextBox chatSystemInstructionBox,
        TextBlock chatSystemInstructionHintText,
        ComboBox chatServerCombo,
        Button newChatButton,
        Button sendButton,
        Button stopButton,
        TextBlock chatStatusText,
        ScrollViewer messagesScrollViewer,
        ProgressBar chatProgress,
        VllmDeploymentService deploymentService,
        VllmConversationService conversationService,
        ILeptaLogger? logger = null,
        IActionLogEventStream? actionLog = null)
    {
        this.messagesPanel = messagesPanel;
        this.chatInputBox = chatInputBox;
        this.chatSystemInstructionBox = chatSystemInstructionBox;
        this.chatSystemInstructionHintText = chatSystemInstructionHintText;
        this.chatServerCombo = chatServerCombo;
        this.newChatButton = newChatButton;
        this.sendButton = sendButton;
        this.stopButton = stopButton;
        this.chatStatusText = chatStatusText;
        this.messagesScrollViewer = messagesScrollViewer;
        this.chatProgress = chatProgress;
        this.deploymentService = deploymentService;
        this.conversationService = conversationService;
        this.logger = logger ?? NullLeptaLogger.Instance;
        this.actionLog = actionLog ?? NullActionLogEventStream.Instance;
        this.chatSystemInstructionBox.TextChanged += (_, _) => HandleSystemInstructionChanged();
        UpdateSystemInstructionHint();
    }

    public event Action? StateChanged;

    public void ApplySettings(ChatSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        suppressStateChanged = true;
        try
        {
            chatSystemInstructionBox.Text = settings.SystemInstruction ?? string.Empty;
            UpdateSystemInstructionHint();
        }
        finally
        {
            suppressStateChanged = false;
        }
    }

    public ChatSettings CaptureSettings() => new()
    {
        SystemInstruction = chatSystemInstructionBox.Text.Trim()
    };

    public void AddSystemMessage(string text) => AddMessage(SystemRole, "System", text);

    public void CancelCurrentMessage()
    {
        if (!isSending || currentSendCts is null || currentSendCts.IsCancellationRequested)
        {
            return;
        }

        logger.Log(nameof(ChatController), "Chat cancellation requested.");
        UpdateStatus("Cancelling chat response...");
        stopButton.IsEnabled = false;
        currentSendCts.Cancel();
    }

    public void HandleServerSelectionChanged()
    {
        var selectedServer = chatServerCombo.SelectedItem as VllmServerConfiguration;
        var nextEndpoint = selectedServer?.Endpoint;
        var hasServerChanged = !string.Equals(activeEndpoint, nextEndpoint, StringComparison.OrdinalIgnoreCase);

        if (isSending)
        {
            hasPendingServerRefresh |= hasServerChanged;
            if (hasServerChanged)
            {
                logger.Log(nameof(ChatController), $"Deferred chat server change to '{selectedServer?.Name ?? "(none)"}' until the active response finishes.");
            }

            return;
        }

        activeEndpoint = nextEndpoint;

        if (selectedServer is null)
        {
            logger.Log(nameof(ChatController), "Chat server selection cleared.");
            UpdateAvailabilityState(false, "Select an already deployed HTTP vLLM server to enable chat.");
            if (hasServerChanged)
            {
                ClearConversation("Select a configured HTTP server to begin a chat.");
            }

            return;
        }

        var supportsChat = selectedServer.UseExistingHttpServer;
        logger.Log(nameof(ChatController), $"Chat server selected: '{selectedServer.Name}'. endpoint={selectedServer.Endpoint}, supportsChat={supportsChat.ToString().ToLowerInvariant()}.");
        UpdateAvailabilityState(
            supportsChat,
            supportsChat
                ? $"Chat uses the selected already deployed HTTP server, resolves the served model from /v1/models, and then sends prompts to /v1/chat/completions."
                : "Chat is currently enabled only for profiles using 'Already deployed HTTP server'. Docker-managed local deploy profiles remain a later-stage workflow.");

        if (hasServerChanged)
        {
            ClearConversation(supportsChat
                ? $"New chat ready for {selectedServer.Name}. LEPTA will resolve the served model from /v1/models before each send."
                : "This profile is configured for Docker lifecycle management, so chat stays disabled until an 'Already deployed HTTP server' profile is selected.");
        }
    }

    public void StartNewChat()
    {
        if (isSending)
        {
            return;
        }

        var selectedServer = chatServerCombo.SelectedItem as VllmServerConfiguration;
        activeEndpoint = selectedServer?.Endpoint;
        logger.Log(nameof(ChatController), $"New chat requested for server '{selectedServer?.Name ?? "(none)"}'.");
        ClearConversation(selectedServer?.UseExistingHttpServer == true
            ? $"New chat started for {selectedServer.Name}."
            : "New chat cleared. Select an already deployed HTTP vLLM server to send prompts.");
    }

    public async Task SendCurrentMessageAsync(CancellationToken cancellationToken = default)
    {
        if (isSending || string.IsNullOrWhiteSpace(chatInputBox.Text))
        {
            return;
        }

        var prompt = chatInputBox.Text.Trim();
        var selectedServer = chatServerCombo.SelectedItem as VllmServerConfiguration;
        logger.Log(nameof(ChatController), $"Chat send requested. promptLength={prompt.Length}, server='{selectedServer?.Name ?? "(none)"}'.");

        if (selectedServer is null)
        {
            AddSystemMessage("Select a configured HTTP vLLM server first.");
            logger.Log(nameof(ChatController), "Chat send rejected because no server is selected.");
            return;
        }

        if (!selectedServer.UseExistingHttpServer)
        {
            AddSystemMessage("Chat is temporarily limited to profiles using 'Already deployed HTTP server'. Switch the selected profile to an HTTP endpoint and retry.");
            logger.Log(nameof(ChatController), $"Chat send rejected because '{selectedServer.Name}' is not configured as an external HTTP server.");
            return;
        }

        SetBusyState(true, $"Checking {selectedServer.Endpoint}...");
        ChatMessageBubble? assistantBubble = null;
        string? servedModelName = null;
        var resolvedSystemPrompt = ResolveSystemInstruction();
        var sendStopwatch = Stopwatch.StartNew();
        PublishAction($"Sending chat prompt to '{selectedServer.Name}'.");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        currentSendCts = linkedCancellation;

        try
        {
            var probe = await deploymentService.ProbeHttpServerAsync(selectedServer, linkedCancellation.Token);
            if (!probe.IsSuccess || string.IsNullOrWhiteSpace(probe.FirstModelName))
            {
                throw new InvalidOperationException(probe.Message);
            }

            servedModelName = probe.FirstModelName;
            AddMessage(UserRole, "You", prompt);
            chatInputBox.Clear();
            assistantBubble = AddMessageBubble(AssistantRole, selectedServer.Name, string.Empty);
            SetStreamingState(assistantBubble, true);

            SetBusyState(true, $"Streaming from {servedModelName} at {selectedServer.Endpoint}...");
            var response = await conversationService.StreamConversationAsync(
                selectedServer.Endpoint,
                servedModelName,
                conversation,
                prompt,
                token => AppendMessageText(assistantBubble, token),
                systemPrompt: resolvedSystemPrompt,
                maxTokens: ChatResponseMaxTokens,
                cancellationToken: linkedCancellation.Token);

            sendStopwatch.Stop();
            conversation.Clear();
            conversation.AddRange(response.Conversation);
            SetMessageText(assistantBubble, response.AssistantText);
            SetMessageMetadata(
                assistantBubble,
                BuildResponseMetadataSummary(servedModelName, response),
                BuildResponseMetadataDetails(response));
            var fallbackSuffix = response.UsedPromptFallback ? " Used prompt fallback." : string.Empty;
            var completionSummary = response.Tokens > 0
                ? $"{response.Tokens} tokens in {response.Elapsed.TotalSeconds:F1}s"
                : $"received in {response.Elapsed.TotalSeconds:F1}s";
            UpdateStatus($"Connected to {servedModelName} at {selectedServer.Endpoint}. Last response: {completionSummary}.{fallbackSuffix}");
            logger.Log(nameof(ChatController), $"Chat send completed for server '{selectedServer.Name}'. usedPromptFallback={response.UsedPromptFallback.ToString().ToLowerInvariant()}, responseLength={response.AssistantText.Length}.");
            PublishAction($"Chat response completed from '{selectedServer.Name}' in {response.Elapsed.TotalSeconds:F1}s.");
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            sendStopwatch.Stop();
            if (assistantBubble is not null)
            {
                var partialResponse = ReadMessageText(assistantBubble);
                if (string.IsNullOrWhiteSpace(partialResponse))
                {
                    RemoveMessageBubble(assistantBubble.Bubble);
                }
                else
                {
                    SetStreamingState(assistantBubble, false);
                    SetMessageMetadata(
                        assistantBubble,
                        $"{servedModelName ?? selectedServer.Name} • cancelled after {sendStopwatch.Elapsed.TotalSeconds:F1}s",
                        "Mode: cancelled before completion");
                }
            }

            UpdateStatus($"Chat response cancelled for {selectedServer.Name}.");
            logger.Log(nameof(ChatController), $"Chat send cancelled for server '{selectedServer.Name}'. elapsedMs={sendStopwatch.Elapsed.TotalMilliseconds:F0}.");
            PublishAction($"Chat response cancelled for '{selectedServer.Name}'.", ActionLogLevel.Warning);
        }
        catch (Exception exception)
        {
            if (assistantBubble is not null && string.IsNullOrWhiteSpace(ReadMessageText(assistantBubble)))
            {
                RemoveMessageBubble(assistantBubble.Bubble);
            }

            AddSystemMessage(exception.Message);
            UpdateStatus(exception.Message);
            logger.Log(nameof(ChatController), $"Chat send failed for server '{selectedServer.Name}'. reason={exception.Message}");
            PublishAction($"Chat send failed for '{selectedServer.Name}': {exception.Message}", ActionLogLevel.Error);
        }
        finally
        {
            var shouldRefreshSelection = hasPendingServerRefresh;
            hasPendingServerRefresh = false;
            if (ReferenceEquals(currentSendCts, linkedCancellation))
            {
                currentSendCts = null;
            }

            SetBusyState(false, chatStatusText.Text);
            if (shouldRefreshSelection)
            {
                HandleServerSelectionChanged();
            }
        }
    }

    private void PublishAction(string message, ActionLogLevel level = ActionLogLevel.Info)
        => actionLog.Publish(nameof(ChatController), message, level);

    private void ClearConversation(string initialMessage)
    {
        conversation.Clear();
        messagesPanel.Children.Clear();
        AddSystemMessage(initialMessage);
        logger.Log(nameof(ChatController), $"Conversation cleared. initialMessage={initialMessage}");
    }

    private void UpdateAvailabilityState(bool isChatAvailable, string message)
    {
        if (!isSending)
        {
            chatInputBox.IsEnabled = isChatAvailable;
            sendButton.IsEnabled = isChatAvailable;
        }

        chatInputBox.ToolTip = message;
        UpdateStatus(message);
    }

    private void SetBusyState(bool busy, string statusMessage)
    {
        isSending = busy;
        chatProgress.IsIndeterminate = busy;
        chatServerCombo.IsEnabled = !busy;
        newChatButton.IsEnabled = !busy;
        chatInputBox.IsEnabled = !busy && chatServerCombo.SelectedItem is VllmServerConfiguration server && server.UseExistingHttpServer;
        sendButton.IsEnabled = !busy && chatInputBox.IsEnabled;
        stopButton.IsEnabled = busy;
        UpdateStatus(statusMessage);
    }

    private void UpdateStatus(string message)
    {
        chatStatusText.Text = message;
    }

    private void AddMessage(string role, string sender, string text)
        => AddMessageBubble(role, sender, text);

    private ChatMessageBubble AddMessageBubble(string role, string sender, string text)
    {
        var (backgroundKey, foregroundKey) = role switch
        {
            UserRole => (ThemeResourceKeys.AccentBrush, ThemeResourceKeys.AccentForegroundBrush),
            AssistantRole => (ThemeResourceKeys.MessageSurfaceBrush, ThemeResourceKeys.PrimaryTextBrush),
            _ => (ThemeResourceKeys.PanelBackgroundAltBrush, ThemeResourceKeys.SecondaryTextBrush)
        };

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            BorderThickness = new Thickness(role == SystemRole ? 1 : 0)
        };

        bubble.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        if (role == SystemRole)
        {
            bubble.SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.BorderBrushTheme);
        }

        var stackPanel = new StackPanel();

        if (role == AssistantRole)
        {
            var header = new DockPanel { LastChildFill = true };

            var metadataSummaryText = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(12, 0, 0, 0),
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center
            };
            metadataSummaryText.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.SecondaryTextBrush);

            var senderText = new TextBlock
            {
                Text = sender,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            senderText.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);

            DockPanel.SetDock(metadataSummaryText, Dock.Right);
            header.Children.Add(metadataSummaryText);
            header.Children.Add(senderText);

            var markdownView = new MarkdownResponseView
            {
                Text = text,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var metadataDetailsText = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            metadataDetailsText.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.SecondaryTextBrush);

            stackPanel.Children.Add(header);
            stackPanel.Children.Add(markdownView);
            stackPanel.Children.Add(metadataDetailsText);
            bubble.Child = stackPanel;
            messagesPanel.Children.Add(bubble);
            messagesScrollViewer.ScrollToEnd();
            return new ChatMessageBubble(bubble, markdownView, metadataSummaryText, metadataDetailsText);
        }

        var plainSenderText = new TextBlock
        {
            Text = sender,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        plainSenderText.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);

        var textBlock = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };

        textBlock.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
        stackPanel.Children.Add(plainSenderText);
        stackPanel.Children.Add(textBlock);
        bubble.Child = stackPanel;
        messagesPanel.Children.Add(bubble);
        messagesScrollViewer.ScrollToEnd();
        return new ChatMessageBubble(bubble, textBlock, null, null);
    }

    private void AppendMessageText(ChatMessageBubble? target, string text)
    {
        if (target is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        if (target.Bubble.Dispatcher.CheckAccess())
        {
            if (target.MarkdownView is not null)
            {
                target.MarkdownView.AppendText(text);
            }
            else if (target.PlainTextBlock is not null)
            {
                target.PlainTextBlock.Text += text;
            }

            messagesScrollViewer.ScrollToEnd();
            return;
        }

        target.Bubble.Dispatcher.Invoke(() =>
        {
            if (target.MarkdownView is not null)
            {
                target.MarkdownView.AppendText(text);
            }
            else if (target.PlainTextBlock is not null)
            {
                target.PlainTextBlock.Text += text;
            }

            messagesScrollViewer.ScrollToEnd();
        });
    }

    private void SetMessageText(ChatMessageBubble? target, string text)
    {
        if (target is null)
        {
            return;
        }

        if (target.Bubble.Dispatcher.CheckAccess())
        {
            if (target.MarkdownView is not null)
            {
                target.MarkdownView.SetFinalText(text);
            }
            else if (target.PlainTextBlock is not null)
            {
                target.PlainTextBlock.Text = text;
            }

            messagesScrollViewer.ScrollToEnd();
            return;
        }

        target.Bubble.Dispatcher.Invoke(() =>
        {
            if (target.MarkdownView is not null)
            {
                target.MarkdownView.SetFinalText(text);
            }
            else if (target.PlainTextBlock is not null)
            {
                target.PlainTextBlock.Text = text;
            }

            messagesScrollViewer.ScrollToEnd();
        });
    }

    private void SetStreamingState(ChatMessageBubble? target, bool isStreaming)
    {
        if (target?.MarkdownView is null)
        {
            return;
        }

        if (target.MarkdownView.Dispatcher.CheckAccess())
        {
            if (isStreaming)
            {
                target.MarkdownView.StartStreaming();
            }
            else
            {
                target.MarkdownView.IsStreaming = false;
            }

            return;
        }

        target.MarkdownView.Dispatcher.Invoke(() =>
        {
            if (isStreaming)
            {
                target.MarkdownView.StartStreaming();
            }
            else
            {
                target.MarkdownView.IsStreaming = false;
            }
        });
    }

    private void SetMessageMetadata(ChatMessageBubble? target, string? summary, string? details)
    {
        if (target?.Bubble is null)
        {
            return;
        }

        if (target.Bubble.Dispatcher.CheckAccess())
        {
            ApplyMessageMetadata(target, summary, details);
            return;
        }

        target.Bubble.Dispatcher.Invoke(() => ApplyMessageMetadata(target, summary, details));
    }

    private static void ApplyMessageMetadata(ChatMessageBubble target, string? summary, string? details)
    {
        if (target.MetadataSummaryText is not null)
        {
            target.MetadataSummaryText.Text = summary ?? string.Empty;
            target.MetadataSummaryText.Visibility = string.IsNullOrWhiteSpace(summary) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (target.MetadataDetailsText is not null)
        {
            target.MetadataDetailsText.Text = details ?? string.Empty;
            target.MetadataDetailsText.Visibility = string.IsNullOrWhiteSpace(details) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static string ReadMessageText(ChatMessageBubble target)
        => target.Bubble.Dispatcher.CheckAccess()
            ? target.MarkdownView?.Text ?? target.PlainTextBlock?.Text ?? string.Empty
            : target.Bubble.Dispatcher.Invoke(() => target.MarkdownView?.Text ?? target.PlainTextBlock?.Text ?? string.Empty);

    private void RemoveMessageBubble(Border bubble)
    {
        if (bubble.Dispatcher.CheckAccess())
        {
            messagesPanel.Children.Remove(bubble);
            return;
        }

        bubble.Dispatcher.Invoke(() => messagesPanel.Children.Remove(bubble));
    }

    private string ResolveSystemInstruction()
        => string.IsNullOrWhiteSpace(chatSystemInstructionBox.Text)
            ? VllmConversationService.DefaultSystemPrompt
            : chatSystemInstructionBox.Text.Trim();

    private void HandleSystemInstructionChanged()
    {
        UpdateSystemInstructionHint();
        if (!suppressStateChanged)
        {
            StateChanged?.Invoke();
        }
    }

    private void UpdateSystemInstructionHint()
        => chatSystemInstructionHintText.Text = string.IsNullOrWhiteSpace(chatSystemInstructionBox.Text)
            ? "Blank uses LEPTA's built-in chat system instruction."
            : "Custom system instruction will be sent before each user prompt.";

    private static string BuildResponseMetadataSummary(
        string servedModelName,
        VllmConversationService.ConversationTurnResult response)
    {
        var tokenSummary = response.Tokens > 0
            ? $"{response.Tokens} tokens"
            : "tokens unavailable";
        return $"{servedModelName} • {response.Elapsed.TotalSeconds:F1}s • {tokenSummary}";
    }

    private static string BuildResponseMetadataDetails(VllmConversationService.ConversationTurnResult response)
        => response.UsedPromptFallback
            ? "Mode: prompt fallback via /v1/completions"
            : "Mode: standard chat completion";

    private sealed record ChatMessageBubble(
        Border Bubble,
        TextBlock? PlainTextBlock,
        MarkdownResponseView? MarkdownView,
        TextBlock? MetadataSummaryText,
        TextBlock? MetadataDetailsText)
    {
        public ChatMessageBubble(
            Border bubble,
            TextBlock? plainTextBlock,
            TextBlock? metadataSummaryText,
            TextBlock? metadataDetailsText)
            : this(bubble, plainTextBlock, null, metadataSummaryText, metadataDetailsText)
        {
        }

        public ChatMessageBubble(
            Border bubble,
            MarkdownResponseView markdownView,
            TextBlock? metadataSummaryText,
            TextBlock? metadataDetailsText)
            : this(bubble, null, markdownView, metadataSummaryText, metadataDetailsText)
        {
        }
    }
}