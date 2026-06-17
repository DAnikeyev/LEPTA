using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Data;
using LEPTA.Controllers.Views;
using LEPTA.Shared.Diagnostics;
using LEPTA.Shared.Models;
using LEPTA.Shared.Services;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Controllers;

internal sealed partial class ChatController
{
    private const string AssistantRole = "assistant";
    private const string UserRole = "user";
    private const string SystemRole = "system";
    private const int ChatResponseMaxTokens = 4096;
    private static readonly TimeSpan HistoryRetentionPeriod = TimeSpan.FromDays(7);

    private readonly ChatMessagesViews messages;
    private readonly ChatInputViews input;
    private readonly ChatSettingsViews settings;
    private readonly ChatChromeViews chrome;
    private readonly ChatHistoryViews history;
    private readonly VllmDeploymentService deploymentService;
    private readonly VllmConversationService conversationService;
    private readonly ILeptaLogger logger;
    private readonly IActionLogEventStream actionLog;
    private readonly JsonFileStore fileStore;
    private readonly string chatHistoryFilePath;
    private readonly List<VllmChatMessage> conversation = [];
    private readonly ObservableCollection<ChatHistoryEntry> chatHistory = [];

    private string? activeEndpoint;
    private CancellationTokenSource? currentSendCts;
    private TaskCompletionSource<bool>? activeSendCompletion;
    private bool isSending;
    private bool suppressStateChanged;
    private bool hasPendingServerRefresh;
    private string? pendingHistoryEntryId;
    private string? pendingHistoryTitle;

    public ChatController(
        ChatControllerViews views,
        VllmDeploymentService deploymentService,
        VllmConversationService conversationService,
        JsonFileStore fileStore,
        string chatHistoryFilePath,
        ChatControllerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(fileStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatHistoryFilePath);
        options ??= new ChatControllerOptions();

        messages = views.Messages;
        input = views.Input;
        settings = views.Settings;
        chrome = views.Chrome;
        history = views.History;
        this.deploymentService = deploymentService;
        this.conversationService = conversationService;
        this.fileStore = fileStore;
        this.chatHistoryFilePath = chatHistoryFilePath;
        logger = options.Logger ?? NullLeptaLogger.Instance;
        actionLog = options.ActionLog ?? NullActionLogEventStream.Instance;
        settings.ThinkingCheckBox.Checked += (_, _) => HandleThinkingChanged();
        settings.ThinkingCheckBox.Unchecked += (_, _) => HandleThinkingChanged();
        var historyView = new ListCollectionView(chatHistory);
        historyView.SortDescriptions.Add(new SortDescription(nameof(ChatHistoryEntry.CreatedAt), ListSortDirection.Descending));
        historyView.SortDescriptions.Add(new SortDescription(nameof(ChatHistoryEntry.Title), ListSortDirection.Ascending));
        history.HistoryList.ItemsSource = historyView;
        LoadPersistedChatHistory();
        UpdateEmptyStateVisibility();
    }

    public event Action? StateChanged;

    public bool IsBusy => isSending;

    public void ApplySettings(ChatSettings chatSettings)
    {
        ArgumentNullException.ThrowIfNull(chatSettings);

        suppressStateChanged = true;
        try
        {
            settings.ThinkingCheckBox.IsChecked = chatSettings.EnableThinking;
        }
        finally
        {
            suppressStateChanged = false;
        }
    }

    public ChatSettings CaptureSettings() => new()
    {
        EnableThinking = settings.ThinkingCheckBox.IsChecked == true
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
        input.StopButton.IsEnabled = false;
        currentSendCts.Cancel();
    }

    public async Task CancelForShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!isSending)
        {
            return;
        }

        logger.Log(nameof(ChatController), "Cancelling chat request during shutdown.");
        PublishAction("Cancelling the active chat response before shutdown.", ActionLogLevel.Warning);
        CancelCurrentMessage();
        var completion = activeSendCompletion?.Task;
        if (completion is null)
        {
            return;
        }

        await AwaitCompletionAsync(completion, timeout, cancellationToken);
    }

    public void HandleServerSelectionChanged()
    {
        var selectedServer = chrome.ServerCombo.SelectedItem as VllmServerConfiguration;
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
            UpdateAvailabilityState(false, "Select a verified server.");
            UpdateThinkingAvailability(null);
            if (hasServerChanged)
            {
                ClearConversation("Select a verified server to start chat.");
            }

            return;
        }

        var supportsChat = selectedServer.HasEstablishedConnection;
        logger.Log(nameof(ChatController), $"Chat server selected: '{selectedServer.Name}'. endpoint={selectedServer.Endpoint}, supportsChat={supportsChat.ToString().ToLowerInvariant()}.");
        UpdateAvailabilityState(
            supportsChat,
            supportsChat
                ? $"Chat ready on {selectedServer.Name}."
                : "Verify this server to enable chat.");
        UpdateThinkingAvailability(selectedServer);

        if (hasServerChanged)
        {
            ClearConversation(supportsChat
                ? $"Chat ready on {selectedServer.Name}."
                : "Verify this server before sending prompts.");
        }
    }

    public void StartNewChat()
    {
        if (isSending)
        {
            return;
        }

        var selectedServer = chrome.ServerCombo.SelectedItem as VllmServerConfiguration;
        activeEndpoint = selectedServer?.Endpoint;
        logger.Log(nameof(ChatController), $"New chat requested for server '{selectedServer?.Name ?? "(none)"}'.");
        ClearConversation(selectedServer?.HasEstablishedConnection == true
            ? $"New chat started on {selectedServer.Name}."
            : "New chat cleared. Select a verified server.");
    }

    public void LoadLeptaConversation(string userPrompt, string assistantResponse, string sourceName)
    {
        if (isSending)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantResponse);

        SaveCurrentConversationToHistory();

        conversation.Clear();
        messages.MessagesPanel.Children.Clear();
        UpdateEmptyStateVisibility();

        var selectedServer = chrome.ServerCombo.SelectedItem as VllmServerConfiguration;
        activeEndpoint = selectedServer?.Endpoint;

        var normalizedPrompt = userPrompt.Trim();
        var normalizedResponse = assistantResponse.Trim();
        conversation.Add(new VllmChatMessage(UserRole, normalizedPrompt));
        conversation.Add(new VllmChatMessage(AssistantRole, normalizedResponse));

        AddMessage(UserRole, "LEPTA request", normalizedPrompt);
        AddMessage(AssistantRole, sourceName, normalizedResponse);
        UpdateStatus(selectedServer?.HasEstablishedConnection == true
            ? $"Chat seeded from LEPTA on {selectedServer.Name}."
            : "Chat seeded from LEPTA. Select a verified server.");
        logger.Log(nameof(ChatController), $"Seeded chat conversation from LEPTA. source={sourceName}, promptLength={normalizedPrompt.Length}, responseLength={normalizedResponse.Length}.");
        PublishAction($"Chat seeded from LEPTA panel '{sourceName}'.");
    }

    public async Task SendCurrentMessageAsync(CancellationToken cancellationToken = default)
    {
        if (isSending || string.IsNullOrWhiteSpace(input.InputBox.Text))
        {
            return;
        }

        var prompt = input.InputBox.Text.Trim();
        var selectedServer = chrome.ServerCombo.SelectedItem as VllmServerConfiguration;
        logger.Log(nameof(ChatController), $"Chat send requested. promptLength={prompt.Length}, server='{selectedServer?.Name ?? "(none)"}'.");

        if (selectedServer is null)
        {
            AddSystemMessage("Select a server first.");
            logger.Log(nameof(ChatController), "Chat send rejected because no server is selected.");
            return;
        }

        if (!selectedServer.HasEstablishedConnection)
        {
            AddSystemMessage("Only verified servers can chat.");
            logger.Log(nameof(ChatController), $"Chat send rejected because '{selectedServer.Name}' has not established a verified connection.");
            return;
        }

        SetBusyState(true, $"Checking {selectedServer.Endpoint}...");
        ChatMessageBubble? assistantBubble = null;
        string? servedModelName = null;
        var resolvedSystemPrompt = ResolveSystemInstruction();
        var requestOptions = CreateRequestOptions(selectedServer);
        var sendStopwatch = Stopwatch.StartNew();
        PublishAction($"Sending chat prompt to '{selectedServer.Name}'.");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sendCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        currentSendCts = linkedCancellation;
        activeSendCompletion = sendCompletion;

        try
        {
            var probe = await deploymentService.ProbeHttpServerAsync(selectedServer, linkedCancellation.Token);
            if (!probe.IsSuccess || string.IsNullOrWhiteSpace(probe.FirstModelName))
            {
                throw new InvalidOperationException(probe.Message);
            }

            servedModelName = probe.FirstModelName;
            AddMessage(UserRole, "You", prompt);
            input.InputBox.Clear();
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
                requestOptions: requestOptions,
                requestOverrides: selectedServer.RequestOverrides,
                cancellationToken: linkedCancellation.Token);

            sendStopwatch.Stop();
            conversation.Clear();
            conversation.AddRange(response.Conversation);
            SetMessageText(assistantBubble, response.AssistantText);
            SetMessageMetadata(
                assistantBubble,
                BuildResponseMetadataSummary(servedModelName, response),
                BuildResponseMetadataDetails(response, requestOptions.EnableThinking));
            var fallbackSuffix = response.UsedPromptFallback ? " Used prompt fallback." : string.Empty;
            var completionSummary = response.Tokens > 0
                ? $"{response.Tokens} tokens in {response.Elapsed.TotalSeconds:F1}s"
                : $"received in {response.Elapsed.TotalSeconds:F1}s";
            UpdateStatus($"Last response from {servedModelName}: {completionSummary}.{fallbackSuffix}");
            logger.Log(nameof(ChatController), $"Chat send completed for server '{selectedServer.Name}'. usedPromptFallback={response.UsedPromptFallback.ToString().ToLowerInvariant()}, responseLength={response.AssistantText.Length}.");
            PublishAction($"Chat response completed from '{selectedServer.Name}' in {response.Elapsed.TotalSeconds:F1}s.");

            if (pendingHistoryEntryId is null)
            {
                _ = GenerateTitleAsync(prompt, response.AssistantText);
            }
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

            if (ReferenceEquals(activeSendCompletion, sendCompletion))
            {
                activeSendCompletion = null;
            }

            sendCompletion.TrySetResult(true);

            SetBusyState(false, chrome.StatusText.Text);
            if (shouldRefreshSelection)
            {
                HandleServerSelectionChanged();
            }
        }
    }

    private void SaveCurrentConversationToHistory()
    {
        if (conversation.Count == 0)
        {
            return;
        }

        var hasUserOrAssistant = conversation.Any(m =>
            string.Equals(m.Role, UserRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Role, AssistantRole, StringComparison.OrdinalIgnoreCase));
        if (!hasUserOrAssistant)
        {
            return;
        }

        var entryId = pendingHistoryEntryId;
        var existing = entryId is not null ? chatHistory.FirstOrDefault(e => e.Id == entryId) : null;
        if (existing is not null)
        {
            chatHistory.Remove(existing);
        }

        var title = pendingHistoryTitle ?? existing?.Title ?? ExtractHistoryTitle();
        var createdAt = existing?.CreatedAt ?? DateTime.Now;
        entryId ??= Guid.NewGuid().ToString("N");
        chatHistory.Insert(0, new ChatHistoryEntry(
            entryId,
            title,
            [.. conversation],
            createdAt));
        pendingHistoryEntryId = entryId;
        pendingHistoryTitle = null;
        PersistChatHistory();
        logger.Log(nameof(ChatController), $"Saved chat to history. title='{title}', messages={conversation.Count}.");
    }

    private string ExtractHistoryTitle()
    {
        var firstUserMsg = conversation.FirstOrDefault(m => string.Equals(m.Role, UserRole, StringComparison.OrdinalIgnoreCase));
        if (firstUserMsg is not null && !string.IsNullOrWhiteSpace(firstUserMsg.Content))
        {
            var trimmed = firstUserMsg.Content.Trim();
            var firstLine = trimmed.Split('\n')[0];
            return firstLine.Length > 60 ? firstLine[..60] + "..." : firstLine;
        }

        return $"Chat at {DateTime.Now:HH:mm}";
    }

    public async Task GenerateTitleAsync(string userPrompt, string assistantResponse)
    {
        var selectedServer = chrome.ServerCombo.SelectedItem as VllmServerConfiguration;
        if (selectedServer is null || !selectedServer.HasEstablishedConnection)
        {
            return;
        }

        try
        {
            var titlePrompt = $"Generate a short concise title (max 6 words, no quotes) for this conversation:\nUser: {userPrompt.Trim()}\nAssistant: {assistantResponse.Trim()}";
            var titleRequestOptions = new VllmRequestOptions { EnableThinking = false };
            var result = await conversationService.SendAsync(
                selectedServer.Endpoint,
                selectedServer.Name,
                [],
                titlePrompt,
                systemPrompt: "You generate short chat titles.",
                maxTokens: 20,
                temperature: 0.3,
                requestOptions: titleRequestOptions,
                requestOverrides: selectedServer.RequestOverrides);

            var generatedTitle = result.AssistantText.Trim().Trim('"', '\'').Trim();
            if (!string.IsNullOrWhiteSpace(generatedTitle) && generatedTitle.Length <= 80)
            {
                pendingHistoryEntryId ??= Guid.NewGuid().ToString("N");
                pendingHistoryTitle = generatedTitle;
                logger.Log(nameof(ChatController), $"Generated title for chat: '{generatedTitle}'.");
            }
        }
        catch (Exception exception)
        {
            logger.Log(nameof(ChatController), $"Failed to generate chat title. reason={exception.Message}");
        }
    }

    public void DeleteHistoryEntry(ChatHistoryEntry entry)
    {
        chatHistory.Remove(entry);
        PersistChatHistory();
        logger.Log(nameof(ChatController), $"Deleted chat history entry. title='{entry.Title}'.");
    }

    private void LoadPersistedChatHistory()
    {
        try
        {
            var cutoff = DateTime.Now - HistoryRetentionPeriod;
            var result = fileStore.Load<List<ChatHistoryEntry>>(chatHistoryFilePath, () => []);
            chatHistory.Clear();
            foreach (var entry in result.Value.Where(e => e.CreatedAt >= cutoff))
            {
                chatHistory.Add(entry);
            }

            if (result.Value.Count != chatHistory.Count)
            {
                PersistChatHistory();
            }

            logger.Log(nameof(ChatController), $"Loaded {chatHistory.Count} chat history entries from persistence.");
        }
        catch (Exception exception)
        {
            logger.Log(nameof(ChatController), $"Failed to load chat history. reason={exception.Message}");
        }
    }

    private void PersistChatHistory()
    {
        try
        {
            fileStore.Save(chatHistoryFilePath, chatHistory.ToList());
        }
        catch (Exception exception)
        {
            logger.Log(nameof(ChatController), $"Failed to persist chat history. reason={exception.Message}");
        }
    }

    public void LoadHistoryEntry(ChatHistoryEntry entry)
    {
        if (isSending)
        {
            return;
        }

        if (string.Equals(pendingHistoryEntryId, entry.Id, StringComparison.Ordinal))
        {
            UnselectHistoryList();
            return;
        }

        SaveCurrentConversationToHistory();

        conversation.Clear();
        conversation.AddRange(entry.Messages);

        pendingHistoryEntryId = entry.Id;

        messages.MessagesPanel.Children.Clear();
        foreach (var msg in entry.Messages)
        {
            var sender = string.Equals(msg.Role, UserRole, StringComparison.OrdinalIgnoreCase) ? "You"
                : string.Equals(msg.Role, AssistantRole, StringComparison.OrdinalIgnoreCase) ? activeEndpoint ?? "Assistant"
                : "System";
            AddMessage(msg.Role, sender, msg.Content);
        }

        UnselectHistoryList();
        UpdateStatus($"Loaded chat from history: {entry.Title}");
        logger.Log(nameof(ChatController), $"Loaded chat history entry. title='{entry.Title}', messages={entry.Messages.Count}.");
    }

    private void UnselectHistoryList()
    {
        if (history.HistoryList.SelectedItem is not null)
        {
            history.HistoryList.SelectedItem = null;
        }
    }

    public IReadOnlyList<ChatHistoryEntry> ChatHistory => chatHistory;
}

internal sealed record ChatHistoryEntry(string Id, string Title, List<VllmChatMessage> Messages, DateTime CreatedAt);
