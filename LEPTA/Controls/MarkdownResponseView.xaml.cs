using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using LEPTA.Models;
using LEPTA.Services;
using LEPTA.Shared.Models;

namespace LEPTA.Controls;

public partial class MarkdownResponseView : UserControl
{
    private static readonly PanelResponseRendererRegistry Renderers = new();
    private readonly MermaidDiagramViewCache mermaidCache = new();
    private string lastRenderedText = string.Empty;
    private double lastRenderedFontSize;
    private string lastRenderedFormat = LeptaPanelFormats.Markdown;
    private bool lastRenderedStreamingState;
    private readonly Dictionary<MermaidDiagramView, string?> mermaidRenderErrors = [];

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarkdownResponseView),
        new PropertyMetadata(string.Empty, HandleRenderPropertyChanged));

    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(
        nameof(IsStreaming),
        typeof(bool),
        typeof(MarkdownResponseView),
        new PropertyMetadata(false, HandleRenderPropertyChanged));

    public static readonly DependencyProperty ShowChatButtonProperty = DependencyProperty.Register(
        nameof(ShowChatButton),
        typeof(bool),
        typeof(MarkdownResponseView),
        new PropertyMetadata(false, HandleRenderPropertyChanged));

    public static readonly DependencyProperty ShowPreviewButtonProperty = DependencyProperty.Register(
        nameof(ShowPreviewButton),
        typeof(bool),
        typeof(MarkdownResponseView),
        new PropertyMetadata(true, HandleRenderPropertyChanged));

    public static readonly DependencyProperty PanelFormatProperty = DependencyProperty.Register(
        nameof(PanelFormat),
        typeof(string),
        typeof(MarkdownResponseView),
        new PropertyMetadata(LeptaPanelFormats.Markdown, HandleRenderPropertyChanged));

    public static readonly DependencyProperty ResponseFontSizeProperty = DependencyProperty.Register(
        nameof(ResponseFontSize),
        typeof(double),
        typeof(MarkdownResponseView),
        new PropertyMetadata(14d, HandleRenderPropertyChanged));

    public static readonly RoutedEvent ChatRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(ChatRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(MarkdownResponseView));

    public static readonly RoutedEvent PreviewRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(PreviewRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(MarkdownResponseView));

    public MarkdownResponseView()
    {
        InitializeComponent();
        PreviewResponseButton.Click += PreviewResponseButton_Click;
        Loaded += (_, _) => Refresh();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    public bool ShowChatButton
    {
        get => (bool)GetValue(ShowChatButtonProperty);
        set => SetValue(ShowChatButtonProperty, value);
    }

    public bool ShowPreviewButton
    {
        get => (bool)GetValue(ShowPreviewButtonProperty);
        set => SetValue(ShowPreviewButtonProperty, value);
    }

    public string PanelFormat
    {
        get => (string)GetValue(PanelFormatProperty);
        set => SetValue(PanelFormatProperty, value);
    }

    public double ResponseFontSize
    {
        get => (double)GetValue(ResponseFontSizeProperty);
        set => SetValue(ResponseFontSizeProperty, value);
    }

    public event RoutedEventHandler ChatRequested
    {
        add => AddHandler(ChatRequestedEvent, value);
        remove => RemoveHandler(ChatRequestedEvent, value);
    }

    public event RoutedEventHandler PreviewRequested
    {
        add => AddHandler(PreviewRequestedEvent, value);
        remove => RemoveHandler(PreviewRequestedEvent, value);
    }

    public void AppendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Text += text;
    }

    public void SetFinalText(string text)
    {
        Text = text;
        IsStreaming = false;
        lastRenderedText = string.Empty;
    }

    public void StartStreaming()
    {
        IsStreaming = true;
        lastRenderedText = string.Empty;
    }

    private static void HandleRenderPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        if (dependencyObject is MarkdownResponseView responseView)
        {
            responseView.Refresh();
        }
    }

    private void Refresh()
    {
        if (!IsLoaded)
        {
            return;
        }

        var hasText = !string.IsNullOrWhiteSpace(Text);
        var normalizedFormat = LeptaPanelFormats.Normalize(PanelFormat);
        var isMermaidPanel = string.Equals(normalizedFormat, LeptaPanelFormats.Mermaid, StringComparison.OrdinalIgnoreCase);
        PreviewResponseButton.IsEnabled = hasText;
        PreviewResponseButton.Visibility = ShowPreviewButton && hasText ? Visibility.Visible : Visibility.Collapsed;
        CopyResponseButton.IsEnabled = hasText;
        CopyResponseButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        OpenChatButton.IsEnabled = hasText;
        OpenChatButton.Visibility = ShowChatButton && hasText ? Visibility.Visible : Visibility.Collapsed;
        ToolbarPanel.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

        if (!hasText)
        {
            ClearMermaidRenderTracking();
            StreamingTextBox.Visibility = Visibility.Collapsed;
            RenderedDocumentViewer.Visibility = Visibility.Collapsed;
            RenderedDocumentViewer.Document = null;
            lastRenderedText = string.Empty;
            mermaidCache.BeginBuild();
            mermaidCache.EndBuild();
            return;
        }

        if (IsStreaming && isMermaidPanel)
        {
            ClearMermaidRenderTracking();
            StreamingTextBox.Text = Text;
            StreamingTextBox.FontSize = ResponseFontSize;
            StreamingTextBox.Visibility = Visibility.Visible;
            RenderedDocumentViewer.Visibility = Visibility.Collapsed;
            lastRenderedText = string.Empty;
            return;
        }

        if (string.Equals(lastRenderedText, Text, StringComparison.Ordinal)
            && Math.Abs(lastRenderedFontSize - ResponseFontSize) < 0.01
            && string.Equals(lastRenderedFormat, normalizedFormat, StringComparison.OrdinalIgnoreCase)
            && lastRenderedStreamingState == IsStreaming)
        {
            return;
        }

        if (isMermaidPanel)
        {
            MermaidRenderService.Shared.PrefetchSources(Text, PanelFormat, ResponseFontSize);
        }

        StreamingTextBox.Visibility = Visibility.Collapsed;
        ClearMermaidRenderTracking();
        mermaidCache.BeginBuild();
        RenderedDocumentViewer.Document = Renderers
            .Resolve(PanelFormat)
            .BuildDocument(Text, ResponseFontSize, mermaidCache);
        mermaidCache.EndBuild();
        AttachMermaidRenderTracking(RenderedDocumentViewer.Document);
        RenderedDocumentViewer.Visibility = Visibility.Visible;

        lastRenderedText = Text;
        lastRenderedFontSize = ResponseFontSize;
        lastRenderedFormat = normalizedFormat;
        lastRenderedStreamingState = IsStreaming;
    }

    private void AttachMermaidRenderTracking(FlowDocument? document)
    {
        if (document is null)
        {
            UpdateBoundPanelRenderError(null);
            return;
        }

        foreach (var diagramView in EnumerateMermaidViews(document.Blocks))
        {
            mermaidRenderErrors[diagramView] = null;
            diagramView.RenderStateChanged += MermaidDiagramView_RenderStateChanged;
        }

        UpdateBoundPanelRenderError(null);
    }

    private void ClearMermaidRenderTracking()
    {
        foreach (var trackedView in mermaidRenderErrors.Keys.ToArray())
        {
            trackedView.RenderStateChanged -= MermaidDiagramView_RenderStateChanged;
        }

        mermaidRenderErrors.Clear();
        UpdateBoundPanelRenderError(null);
    }

    private void MermaidDiagramView_RenderStateChanged(object? sender, MermaidDiagramRenderStateChangedEventArgs e)
    {
        if (sender is not MermaidDiagramView view)
        {
            return;
        }

        mermaidRenderErrors[view] = e.IsSuccess
            ? null
            : e.ErrorMessage;
        UpdateBoundPanelRenderError(mermaidRenderErrors.Values.FirstOrDefault(static message => !string.IsNullOrWhiteSpace(message)));
    }

    private void UpdateBoundPanelRenderError(string? errorMessage)
    {
        if (DataContext is LeptaPanelStateBase panelState)
        {
            panelState.SetRenderError(errorMessage);
        }
    }

    private static IEnumerable<MermaidDiagramView> EnumerateMermaidViews(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case BlockUIContainer { Child: MermaidDiagramView diagramView }:
                    yield return diagramView;
                    break;
                case Section section:
                    foreach (var nested in EnumerateMermaidViews(section.Blocks))
                    {
                        yield return nested;
                    }

                    break;
                case System.Windows.Documents.List list:
                    foreach (var item in list.ListItems)
                    {
                        foreach (var nested in EnumerateMermaidViews(item.Blocks))
                        {
                            yield return nested;
                        }
                    }

                    break;
                case Table table:
                    foreach (var rowGroup in table.RowGroups)
                    {
                        foreach (var row in rowGroup.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                foreach (var nested in EnumerateMermaidViews(cell.Blocks))
                                {
                                    yield return nested;
                                }
                            }
                        }
                    }

                    break;
            }
        }
    }

    private void CopyResponseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Text);
        }
        catch (Exception exception)
        {
            UserNotificationService.ShowWarning(
                "Copy failed",
                $"LEPTA could not copy the response: {exception.Message}",
                source: nameof(MarkdownResponseView));
        }
    }

    private void OpenChatButton_Click(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(ChatRequestedEvent, this));
    }

    private void PreviewResponseButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            return;
        }

        RaiseEvent(new RoutedEventArgs(PreviewRequestedEvent, this));
    }

    private void Root_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        ResponseFontSizeCoordinator.RequestStep(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }
}

