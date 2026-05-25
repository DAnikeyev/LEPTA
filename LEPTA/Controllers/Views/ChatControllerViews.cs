using System.Windows;
using System.Windows.Controls;

namespace LEPTA.Controllers.Views;

internal sealed class ChatControllerViews
{
    public required ChatMessagesViews Messages { get; init; }

    public required ChatInputViews Input { get; init; }

    public required ChatSettingsViews Settings { get; init; }

    public required ChatChromeViews Chrome { get; init; }

    public required ChatHistoryViews History { get; init; }
}

internal sealed class ChatMessagesViews
{
    public required Panel MessagesPanel { get; init; }

    public required FrameworkElement EmptyState { get; init; }

    public required ScrollViewer ScrollViewer { get; init; }
}

internal sealed class ChatInputViews
{
    public required TextBox InputBox { get; init; }

    public required Button NewChatButton { get; init; }

    public required Button SendButton { get; init; }

    public required Button StopButton { get; init; }
}

internal sealed class ChatSettingsViews
{
    public required CheckBox ThinkingCheckBox { get; init; }
}

internal sealed class ChatHistoryViews
{
    public required ListBox HistoryList { get; init; }
}

internal sealed class ChatChromeViews
{
    public required ComboBox ServerCombo { get; init; }

    public required TextBlock StatusText { get; init; }

    public required ProgressBar ProgressBar { get; init; }
}
