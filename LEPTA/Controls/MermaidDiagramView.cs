using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LEPTA.Services;
using LEPTA.Theming;

namespace LEPTA.Controls;

internal sealed class MermaidDiagramView : Border
{
    private readonly string mermaidSource;
    private readonly double diagramFontSize;
    private readonly MermaidDiagramViewCache? renderCache;
    private readonly TextBlock statusText;
    private readonly TextBox fallbackTextBox;
    private readonly Image diagramImage;
    private bool hasStarted;

    public event EventHandler<MermaidDiagramRenderStateChangedEventArgs>? RenderStateChanged;

    public MermaidDiagramView(string mermaidSource, double diagramFontSize, MermaidDiagramViewCache? renderCache = null)
    {
        this.mermaidSource = mermaidSource;
        this.diagramFontSize = diagramFontSize;
        this.renderCache = renderCache;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12);
        Margin = new Thickness(0, 2, 0, 12);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        SetResourceReference(BackgroundProperty, ThemeResourceKeys.CodeBackgroundBrush);
        SetResourceReference(BorderBrushProperty, ThemeResourceKeys.BorderBrushTheme);

        statusText = new TextBlock
        {
            Text = "Rendering Mermaid diagram...",
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        };
        statusText.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.SecondaryTextBrush);

        diagramImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            SnapsToDevicePixels = true
        };

        fallbackTextBox = new TextBox
        {
            Text = this.mermaidSource,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = GetEffectiveFontSize(diagramFontSize)
        };
        fallbackTextBox.SetResourceReference(Control.ForegroundProperty, ThemeResourceKeys.PrimaryTextBrush);

        var host = new Grid();
        host.Children.Add(statusText);
        host.Children.Add(diagramImage);
        host.Children.Add(fallbackTextBox);
        Child = host;

        Loaded += MermaidDiagramView_Loaded;
        SizeChanged += (_, _) => ApplyDiagramFit();
    }

    private async void MermaidDiagramView_Loaded(object sender, RoutedEventArgs e)
    {
        if (hasStarted)
        {
            return;
        }

        hasStarted = true;
        try
        {
            var renderWidth = GetTargetRenderWidth();
            renderCache?.Prefetch(mermaidSource, diagramFontSize, renderWidth);

            MermaidRenderResult? result;
            if (renderCache?.TryGet(mermaidSource, diagramFontSize, out result) == true && result is not null)
            {
                renderCache.Track(mermaidSource, diagramFontSize);
            }
            else
            {
                result = await MermaidRenderService.Shared.RenderAsync(mermaidSource, diagramFontSize, renderWidth);
                if (result is not null)
                {
                    renderCache?.Store(mermaidSource, diagramFontSize, result);
                }
            }

            if (result is null)
            {
                ShowFallback("Mermaid preview unavailable.");
                return;
            }

            diagramImage.Source = result.Image;
            diagramImage.Visibility = Visibility.Visible;
            statusText.Visibility = Visibility.Collapsed;
            fallbackTextBox.Visibility = Visibility.Collapsed;
            ApplyDiagramFit();
            NotifyRenderStateChanged(isSuccess: true, errorMessage: null);
        }
        catch (Exception exception)
        {
            ShowFallback($"Mermaid preview unavailable. {exception.Message}");
        }
    }

    private void ApplyDiagramFit()
    {
        if (diagramImage.Source is not BitmapSource bitmap || diagramImage.Visibility != Visibility.Visible)
        {
            return;
        }

        var availableWidth = GetAvailableWidth();
        if (availableWidth <= 0 || bitmap.PixelWidth <= 0)
        {
            return;
        }

        diagramImage.Stretch = Stretch.Uniform;
        diagramImage.MaxWidth = availableWidth;
        var scale = availableWidth / bitmap.PixelWidth;
        diagramImage.MaxHeight = Math.Max(120, bitmap.PixelHeight * scale);
    }

    private double GetTargetRenderWidth()
        => Math.Max(320, GetAvailableWidth());

    private double GetAvailableWidth()
    {
        var width = ActualWidth - Padding.Left - Padding.Right;
        if (width > 120)
        {
            return width;
        }

        for (var element = Parent as DependencyObject; element is not null; element = LogicalTreeHelper.GetParent(element) ?? VisualTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { ActualWidth: > 120 } frameworkElement)
            {
                return frameworkElement.ActualWidth - 32;
            }
        }

        return 900;
    }

    private void ShowFallback(string message)
    {
        statusText.Visibility = Visibility.Collapsed;
        diagramImage.Visibility = Visibility.Collapsed;
        fallbackTextBox.Visibility = Visibility.Visible;
        NotifyRenderStateChanged(isSuccess: false, errorMessage: message);
    }

    private void NotifyRenderStateChanged(bool isSuccess, string? errorMessage)
        => RenderStateChanged?.Invoke(this, new MermaidDiagramRenderStateChangedEventArgs(isSuccess, errorMessage));

    internal static double GetEffectiveFontSize(double fontSize)
        => Math.Max(12, Math.Round((Math.Max(4, fontSize) * 1.3) + 2));
}

internal sealed class MermaidDiagramRenderStateChangedEventArgs(bool isSuccess, string? errorMessage) : EventArgs
{
    public bool IsSuccess { get; } = isSuccess;

    public string? ErrorMessage { get; } = errorMessage;
}

