using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using LEPTA.Controls;

namespace LEPTA.Services;

internal sealed class MermaidRenderService
{
    private const double DefaultRenderWidth = 1100;
    private static readonly Lazy<MermaidRenderService> SharedInstance = new(() => new MermaidRenderService());
    private static readonly JsonSerializerOptions WebMessageSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim renderLock = new(1, 1);
    private readonly Dictionary<string, MermaidRenderResult> cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<MermaidRenderResult?>> inFlight = new(StringComparer.Ordinal);
    private Window? hostWindow;
    private WebView2? webView;
    private TaskCompletionSource<double>? pendingRender;
    private Task? warmupTask;

    public static MermaidRenderService Shared => SharedInstance.Value;

    public void ClearCache()
    {
        cache.Clear();
        inFlight.Clear();
    }

    public void Prefetch(string source, double fontSize, double renderWidth = DefaultRenderWidth)
        => _ = RenderAsync(source, fontSize, renderWidth);

    public void PrefetchSources(string? markdown, string? panelFormat, double fontSize, double renderWidth = DefaultRenderWidth)
    {
        foreach (var source in MarkdownResponseRenderer.CollectMermaidSources(markdown, panelFormat))
        {
            Prefetch(source, fontSize, renderWidth);
        }
    }

    public Task WarmupAsync()
    {
        warmupTask ??= RenderAsync("graph TD; A-->B;", 14);
        return warmupTask;
    }

    public Task<MermaidRenderResult?> RenderAsync(
        string source,
        double fontSize,
        double renderWidth = DefaultRenderWidth,
        CancellationToken cancellationToken = default)
    {
        var normalizedSource = MermaidSourceNormalizer.Normalize(source);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return Task.FromResult<MermaidRenderResult?>(null);
        }

        var themedSource = MermaidDiagramPalettePostProcessor.Apply(normalizedSource);

        var cacheKey = MermaidDiagramViewCache.CreateKey(themedSource, fontSize);
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return Task.FromResult<MermaidRenderResult?>(cached);
        }

        var normalizedWidth = NormalizeRenderWidth(renderWidth);
        return inFlight.GetOrAdd(cacheKey, _ => RenderCoreAsync(themedSource, fontSize, normalizedWidth, cacheKey, cancellationToken));
    }

    private async Task<MermaidRenderResult?> RenderCoreAsync(
        string source,
        double fontSize,
        double renderWidth,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await renderLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }

                var contentHeight = await RunOnUiAsync(
                    () => RenderOnUiAsync(source, fontSize, renderWidth, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                var bitmap = await RunOnUiAsync(
                    () => CapturePreviewOnUiAsync(renderWidth, contentHeight),
                    cancellationToken).ConfigureAwait(false);
                if (bitmap is not BitmapSource bitmapSource)
                {
                    return null;
                }

                var result = new MermaidRenderResult(bitmapSource, bitmapSource.PixelWidth, contentHeight);
                cache[cacheKey] = result;
                return result;
            }
            finally
            {
                renderLock.Release();
            }
        }
        finally
        {
            inFlight.TryRemove(cacheKey, out _);
        }
    }

    private async Task<double> RenderOnUiAsync(
        string source,
        double fontSize,
        double renderWidth,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync().ConfigureAwait(true);

        var completion = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingRender = completion;
        webView!.Width = renderWidth;
        webView.Height = Math.Max(180, fontSize * 10);
        webView.NavigateToString(BuildHtml(source, fontSize, renderWidth));

        await using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task.ConfigureAwait(true);
    }

    private async Task<ImageSource?> CapturePreviewOnUiAsync(double renderWidth, double contentHeight)
    {
        if (webView?.CoreWebView2 is null)
        {
            return null;
        }

        webView.Width = renderWidth;
        webView.Height = Math.Max(180, contentHeight + 12);
        webView.UpdateLayout();
        await Task.Delay(20).ConfigureAwait(true);

        await using var stream = new MemoryStream();
        await webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream).ConfigureAwait(true);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private async Task EnsureInitializedAsync()
    {
        if (webView?.CoreWebView2 is not null)
        {
            return;
        }

        hostWindow = new Window
        {
            Width = 1280,
            Height = 960,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Opacity = 0,
            ShowActivated = false,
            Left = -10000,
            Top = -10000
        };

        webView = new WebView2
        {
            Width = DefaultRenderWidth,
            Height = 900,
            DefaultBackgroundColor = System.Drawing.Color.Transparent
        };
        hostWindow.Content = webView;
        hostWindow.Show();

        await webView.EnsureCoreWebView2Async().ConfigureAwait(true);
        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
        webView.CoreWebView2.WebMessageReceived += HandleWebMessageReceived;
    }

    private void HandleWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var payload = TryParseWebMessage(args);
            if (payload?.Type == "error")
            {
                pendingRender?.TrySetException(new InvalidOperationException(payload.Message ?? "Mermaid render failed."));
                pendingRender = null;
                return;
            }

            if (payload?.Type != "height" || payload.Value <= 0)
            {
                return;
            }

            pendingRender?.TrySetResult(payload.Value);
            pendingRender = null;
        }
        catch (Exception exception)
        {
            pendingRender?.TrySetException(exception);
            pendingRender = null;
        }
    }

    private static MermaidWebMessage? TryParseWebMessage(CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            return JsonSerializer.Deserialize<MermaidWebMessage>(args.WebMessageAsJson, WebMessageSerializerOptions);
        }
        catch (JsonException)
        {
            var raw = args.TryGetWebMessageAsString();
            return string.IsNullOrWhiteSpace(raw)
                ? null
                : JsonSerializer.Deserialize<MermaidWebMessage>(raw, WebMessageSerializerOptions);
        }
    }

    private static string BuildHtml(string mermaidSource, double fontSize, double renderWidth)
    {
        var encodedSource = JsonSerializer.Serialize(mermaidSource);
        var normalizedFontSize = MermaidDiagramView.GetEffectiveFontSize(fontSize);
        var normalizedWidth = NormalizeRenderWidth(renderWidth);
        var themeVariablesJson = MermaidDiagramPalettePostProcessor.CreateThemeVariablesJson(normalizedFontSize);
        return $$"""
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8" />
    <style>
        html, body {
            margin: 0;
            padding: 0;
            background: transparent;
            overflow: hidden;
            font-family: Segoe UI, sans-serif;
            font-size: {{normalizedFontSize}}px;
            width: {{normalizedWidth}}px;
        }

        #diagram {
            display: flex;
            justify-content: center;
            align-items: flex-start;
            width: {{normalizedWidth}}px;
            min-height: 120px;
            font-size: {{normalizedFontSize}}px;
        }

        svg {
            max-width: 100%;
            height: auto;
            font-size: {{normalizedFontSize}}px;
        }

        .error {
            color: #cc3344;
            white-space: pre-wrap;
            font-size: {{normalizedFontSize}}px;
        }
    </style>
</head>
<body>
    <div id="diagram"></div>
    <script src="https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js"></script>
    <script>
        (async function () {
            const source = {{encodedSource}};
            const host = document.getElementById('diagram');

            try {
                mermaid.initialize({
                    startOnLoad: false,
                    securityLevel: 'loose',
                    theme: 'base',
                    themeVariables: {{themeVariablesJson}}
                });

                const renderId = 'mermaid-' + Date.now().toString(36);
                const result = await mermaid.render(renderId, source);
                host.innerHTML = result.svg;
                requestAnimationFrame(function () {
                    requestAnimationFrame(function () {
                        const height = Math.ceil(document.documentElement.scrollHeight || document.body.scrollHeight || 240);
                        window.chrome.webview.postMessage({ type: 'height', value: height });
                    });
                });
            } catch (error) {
                host.className = 'error';
                host.textContent = error && error.message ? error.message : String(error);
                const height = Math.ceil(document.documentElement.scrollHeight || document.body.scrollHeight || 180);
                window.chrome.webview.postMessage({ type: 'error', message: host.textContent, value: height });
            }
        })();
    </script>
</body>
</html>
""";
    }

    private static double NormalizeRenderWidth(double renderWidth)
        => Math.Clamp(renderWidth, 320, 2400);

    private static async Task<T> RunOnUiAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            return await action().ConfigureAwait(dispatcher == Dispatcher.CurrentDispatcher);
        }

        var operation = dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
        return await operation.Task.Unwrap().ConfigureAwait(false);
    }

    private sealed class MermaidWebMessage
    {
        public string? Type { get; set; }
        public double Value { get; set; }
        public string? Message { get; set; }
    }
}
