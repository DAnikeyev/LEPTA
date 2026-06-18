using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LEPTA.Controllers;
using LEPTA.Controllers.Views;

namespace LEPTA.Controls;

/// <summary>
/// Hosts the Chat screen markup and the chat-only input/history event handlers.
/// Extracted from <c>MainWindow</c> as the first <c>B1</c> per-screen UserControl; the
/// controllers are still constructed by <see cref="MainWindow"/> and attached here.
/// </summary>
public partial class ChatView : UserControl
{
    private ChatController? controller;

    public ChatView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The shared server-selection combo. Exposed so <see cref="ModelsController"/> can keep
    /// treating it as the canonical active-server selector and so <see cref="MainWindow"/> can
    /// wire the cross-screen <c>SelectionChanged</c> handler that drives both controllers.
    /// </summary>
    public ComboBox ServerCombo => ChatServerCombo;

    /// <summary>
    /// The shared chat/deployment progress bar. <see cref="ModelsController"/> drives this during
    /// server start/stop while <see cref="ChatController"/> drives it during streaming.
    /// </summary>
    public ProgressBar Progress => ChatProgress;

    /// <summary>
    /// The chat controller, attached by <see cref="MainWindow"/> once it has been constructed.
    /// The handlers routed through this control delegate to it.
    /// </summary>
    internal ChatController? Controller
    {
        get => controller;
        set => controller = value;
    }

    /// <summary>
    /// Builds the <see cref="ChatControllerViews"/> DTO from this view's named controls.
    /// Lets <see cref="MainWindow"/> construct the chat controller without owning the chat markup.
    /// </summary>
    internal ChatControllerViews BuildViews() => new()
    {
        Messages = new ChatMessagesViews
        {
            MessagesPanel = MessagesPanel,
            EmptyState = ChatEmptyStateBorder,
            ScrollViewer = MessagesScrollViewer
        },
        Input = new ChatInputViews
        {
            InputBox = ChatInputBox,
            NewChatButton = NewChatButton,
            SendButton = SendButton,
            StopButton = StopChatButton
        },
        Settings = new ChatSettingsViews
        {
            ThinkingCheckBox = ChatThinkingCheckBox
        },
        Chrome = new ChatChromeViews
        {
            ServerCombo = ChatServerCombo,
            StatusText = ChatStatusText,
            ProgressBar = ChatProgress
        },
        History = new ChatHistoryViews
        {
            HistoryList = ChatHistoryList
        }
    };

    private void NewChatButton_Click(object sender, RoutedEventArgs e)
        => controller?.StartNewChat();

    private void StopChatButton_Click(object sender, RoutedEventArgs e)
        => controller?.CancelCurrentMessage();

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (controller is null)
        {
            return;
        }

        await controller.SendCurrentMessageAsync();
    }

    private async void ChatInputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        e.Handled = true;
        if (controller is null)
        {
            return;
        }

        await controller.SendCurrentMessageAsync();
    }

    private void ChatHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ChatHistoryEntry entry)
        {
            controller?.LoadHistoryEntry(entry);
        }
    }

    private void DeleteHistoryEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChatHistoryEntry entry })
        {
            controller?.DeleteHistoryEntry(entry);
            e.Handled = true;
        }
    }
}
