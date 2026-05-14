using System.Windows;
using System.Windows.Controls;
using LEPTA.Services;

namespace LEPTA.Controls;

public partial class MarkdownResponseView : UserControl
{
    private static readonly MarkdownResponseRenderer Renderer = new();

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

    public MarkdownResponseView()
    {
        InitializeComponent();
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
    }

    public void StartStreaming()
    {
        IsStreaming = true;
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
        CopyResponseButton.IsEnabled = hasText;
        CopyResponseButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        ToolbarPanel.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

        if (IsStreaming)
        {
            StreamingTextBox.Text = Text ?? string.Empty;
            StreamingTextBox.Visibility = Visibility.Visible;
            RenderedScrollViewer.Visibility = Visibility.Collapsed;
            return;
        }

        StreamingTextBox.Visibility = Visibility.Collapsed;
        RenderedContentPanel.Children.Clear();
        foreach (var element in Renderer.BuildElements(Text))
        {
            RenderedContentPanel.Children.Add(element);
        }

        RenderedScrollViewer.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyResponseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Text ?? string.Empty);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"LEPTA could not copy the response: {exception.Message}", "Copy failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

