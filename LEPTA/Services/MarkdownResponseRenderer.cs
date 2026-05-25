using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LEPTA.Controls;
using LEPTA.Shared.Models;
using LEPTA.Shared.Services;
using LEPTA.Theming;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using FlowList = System.Windows.Documents.List;
using MarkdownBlock = Markdig.Syntax.Block;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;

namespace LEPTA.Services;

internal interface IPanelResponseRenderer
{
    string Format { get; }
    FlowDocument BuildDocument(string? markdown, double fontSize, MermaidDiagramViewCache? mermaidCache = null);
}

internal sealed class PanelResponseRendererRegistry
{
    private readonly Dictionary<string, IPanelResponseRenderer> renderers;

    public PanelResponseRendererRegistry()
    {
        renderers = new Dictionary<string, IPanelResponseRenderer>(StringComparer.OrdinalIgnoreCase)
        {
            [LeptaPanelFormats.Markdown] = new MarkdownResponseRenderer(),
            [LeptaPanelFormats.Mermaid] = new MermaidResponseRenderer(),
            [LeptaPanelFormats.PlainText] = new PlainTextResponseRenderer()
        };
    }

    public IPanelResponseRenderer Resolve(string? format)
        => renderers.TryGetValue(LeptaPanelFormats.Normalize(format), out var renderer)
            ? renderer
            : renderers[LeptaPanelFormats.Markdown];
}

internal class MarkdownResponseRenderer : IPanelResponseRenderer
{
    private readonly MarkdownPipeline pipeline;
    private readonly CodeSyntaxHighlighter syntaxHighlighter;

    public MarkdownResponseRenderer()
        : this(new CodeSyntaxHighlighter())
    {
    }

    protected MarkdownResponseRenderer(CodeSyntaxHighlighter syntaxHighlighter)
    {
        this.syntaxHighlighter = syntaxHighlighter;
        pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
    }

    public virtual string Format => LeptaPanelFormats.Markdown;

    public virtual FlowDocument BuildDocument(string? markdown, double fontSize, MermaidDiagramViewCache? mermaidCache = null)
    {
        var document = CreateDocumentShell(fontSize);

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return document;
        }

        var segments = SplitThinkSegments(markdown);
        var thinkIndex = 0;
        foreach (var segment in segments)
        {
            if (segment.IsThink)
            {
                var thinkContent = segment.Text;
                if (!string.IsNullOrWhiteSpace(thinkContent))
                {
                    document.Blocks.Add(BuildThinkExpander(thinkContent, fontSize, thinkIndex++));
                }
            }
            else
            {
                var parsedDocument = Markdown.Parse(segment.Text, pipeline);
                foreach (var block in parsedDocument)
                {
                    foreach (var converted in BuildBlocks(block, 0, fontSize, mermaidCache))
                    {
                        document.Blocks.Add(converted);
                    }
                }
            }
        }

        return document;
    }

    private static readonly Regex ThinkBlockRegex = new(
        @"(.*?)<think\s*>(.*?)</think\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex IncompleteThinkRegex = new(
        @"<think\s*>([\s\S]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record TextSegment(string Text, bool IsThink);

    private static List<TextSegment> SplitThinkSegments(string text)
    {
        var segments = new List<TextSegment>();
        var remaining = text;
        while (remaining.Length > 0)
        {
            var match = ThinkBlockRegex.Match(remaining);
            if (match.Success)
            {
                var before = match.Groups[1].Value;
                if (!string.IsNullOrEmpty(before))
                {
                    segments.Add(new TextSegment(before, IsThink: false));
                }

                segments.Add(new TextSegment(match.Groups[2].Value, IsThink: true));
                remaining = remaining[match.Length..];
            }
            else if (IncompleteThinkRegex.IsMatch(remaining))
            {
                segments.Add(new TextSegment(remaining, IsThink: false));
                remaining = string.Empty;
            }
            else
            {
                segments.Add(new TextSegment(remaining, IsThink: false));
                remaining = string.Empty;
            }
        }

        return segments;
    }

    private static BlockUIContainer BuildThinkExpander(string thinkContent, double fontSize, int index)
    {
        var innerFontSize = Math.Max(10, fontSize - 2);
        var innerDocument = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = innerFontSize,
            Background = Brushes.Transparent,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        innerDocument.SetResourceReference(FlowDocument.ForegroundProperty, ThemeResourceKeys.SecondaryTextBrush);
        innerDocument.Blocks.Add(new Paragraph(new Run(thinkContent.Trim()))
        {
            Margin = new Thickness(0),
            FontSize = innerFontSize
        });

        var expander = new Expander
        {
            Header = new TextBlock
            {
                Text = "Thinking",
                FontSize = Math.Max(10, fontSize - 1),
                FontStyle = FontStyles.Italic
            },
            Content = new RichTextBox
            {
                Document = innerDocument,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                FontSize = innerFontSize
            },
            IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 10)
        };
        expander.SetResourceReference(TextElement.ForegroundProperty, ThemeResourceKeys.SecondaryTextBrush);
        return new BlockUIContainer(expander);
    }

    protected FlowDocument CreateDocumentShell(double fontSize)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = fontSize,
            Background = Brushes.Transparent,
            TextAlignment = TextAlignment.Left,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, ThemeResourceKeys.PrimaryTextBrush);
        return document;
    }

    protected virtual IEnumerable<WpfBlock> BuildBlocks(MarkdownBlock block, int nestingDepth, double fontSize, MermaidDiagramViewCache? mermaidCache = null)
    {
        switch (block)
        {
            case HeadingBlock headingBlock:
                yield return BuildHeading(headingBlock, fontSize);
                yield break;
            case ParagraphBlock paragraphBlock:
                yield return BuildParagraph(paragraphBlock, fontSize);
                yield break;
            case QuoteBlock quoteBlock:
                yield return BuildQuote(quoteBlock, nestingDepth, fontSize, mermaidCache);
                yield break;
            case ListBlock listBlock:
                yield return BuildList(listBlock, nestingDepth, fontSize, mermaidCache);
                yield break;
            case FencedCodeBlock fencedCodeBlock:
                yield return BuildCodeBlock(fencedCodeBlock.Lines.ToString(), fencedCodeBlock.Info, fontSize, mermaidCache);
                yield break;
            case CodeBlock codeBlock:
                yield return BuildCodeBlock(codeBlock.Lines.ToString(), null, fontSize, mermaidCache);
                yield break;
            case ThematicBreakBlock:
                yield return BuildSeparator();
                yield break;
            case HtmlBlock htmlBlock:
                yield return BuildPlainTextParagraph(htmlBlock.Lines.ToString(), fontSize, new Thickness(0, 0, 0, 10));
                yield break;
            case ContainerBlock containerBlock:
                foreach (var nested in containerBlock)
                {
                    foreach (var converted in BuildBlocks(nested, nestingDepth + 1, fontSize, mermaidCache))
                    {
                        yield return converted;
                    }
                }

                yield break;
        }
    }

    protected virtual WpfBlock BuildCodeBlock(string? code, string? language, double fontSize, MermaidDiagramViewCache? mermaidCache = null)
    {
        var normalizedCode = (code ?? string.Empty).TrimEnd('\r', '\n');
        var normalizedLanguage = syntaxHighlighter.NormalizeLanguage(language);
        if (string.Equals(NormalizeFenceInfo(normalizedLanguage), "mermaid", StringComparison.OrdinalIgnoreCase))
        {
            var trimmedCode = MermaidSourceNormalizer.Normalize(normalizedCode);
            mermaidCache?.TryGet(trimmedCode, fontSize, out _);
            return new BlockUIContainer(new MermaidDiagramView(trimmedCode, fontSize, mermaidCache));
        }

        var highlightedLines = syntaxHighlighter.Highlight(normalizedCode, normalizedLanguage);

        var headerPanel = new DockPanel
        {
            LastChildFill = false,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "Code" : language.Trim(),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.SecondaryTextBrush);
        DockPanel.SetDock(label, Dock.Left);
        headerPanel.Children.Add(label);

        var copyButton = new Button
        {
            Content = "Copy code",
            Padding = new Thickness(10, 4, 10, 4),
            MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        copyButton.SetResourceReference(Button.StyleProperty, "SecondaryButtonStyle");
        copyButton.Click += (_, _) => CopyToClipboard(normalizedCode, "code block");
        DockPanel.SetDock(copyButton, Dock.Right);
        headerPanel.Children.Add(copyButton);

        var codeDocument = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = Math.Max(10, fontSize - 1),
            Background = Brushes.Transparent,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };

        var codeParagraph = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = Math.Max(12, fontSize + 5)
        };
        for (var lineIndex = 0; lineIndex < highlightedLines.Count; lineIndex++)
        {
            foreach (var token in highlightedLines[lineIndex].Tokens)
            {
                var run = new Run(FormatCodeToken(token.Text));
                run.SetResourceReference(TextElement.ForegroundProperty, ResolveTokenBrushKey(token.Kind));
                codeParagraph.Inlines.Add(run);
            }

            if (lineIndex < highlightedLines.Count - 1)
            {
                codeParagraph.Inlines.Add(new LineBreak());
            }
        }

        codeDocument.Blocks.Add(codeParagraph);

        var codeBox = new RichTextBox
        {
            Document = codeDocument,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AcceptsTab = true
        };

        var container = new Border
        {
            Margin = new Thickness(0, 2, 0, 12),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Children =
                {
                    headerPanel,
                    codeBox
                }
            }
        };
        container.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.CodeBackgroundBrush);
        container.SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.BorderBrushTheme);
        container.BorderThickness = new Thickness(1);
        container.CornerRadius = new CornerRadius(10);
        return new BlockUIContainer(container);
    }

    private WpfBlock BuildHeading(HeadingBlock block, double fontSize)
    {
        var paragraph = CreateParagraph(fontSize, new Thickness(0, block.Level == 1 ? 0 : 6, 0, 10));
        paragraph.FontWeight = FontWeights.SemiBold;
        paragraph.FontSize = block.Level switch
        {
            1 => fontSize + 10,
            2 => fontSize + 7,
            3 => fontSize + 5,
            4 => fontSize + 3,
            _ => fontSize + 1
        };
        AddInlines(paragraph.Inlines, block.Inline, fontSize);
        return paragraph;
    }

    private WpfBlock BuildParagraph(ParagraphBlock block, double fontSize)
    {
        if (block.Inline is null)
        {
            return BuildPlainTextParagraph(block.Lines.ToString(), fontSize, new Thickness(0, 0, 0, 10));
        }

        var paragraph = CreateParagraph(fontSize, new Thickness(0, 0, 0, 10));
        AddInlines(paragraph.Inlines, block.Inline, fontSize);
        return paragraph;
    }

    private WpfBlock BuildQuote(QuoteBlock block, int nestingDepth, double fontSize, MermaidDiagramViewCache? mermaidCache = null)
    {
        var section = new Section
        {
            Margin = new Thickness(14 + Math.Max(0, nestingDepth - 1) * 12, 0, 0, 10),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 0)
        };
        section.SetResourceReference(Section.BorderBrushProperty, ThemeResourceKeys.AccentBrush);

        foreach (var nested in block)
        {
            foreach (var converted in BuildBlocks(nested, nestingDepth + 1, fontSize, mermaidCache))
            {
                section.Blocks.Add(converted);
            }
        }

        return section;
    }

    private WpfBlock BuildList(ListBlock listBlock, int nestingDepth, double fontSize, MermaidDiagramViewCache? mermaidCache = null)
    {
        var list = new FlowList
        {
            Margin = new Thickness(Math.Max(0, nestingDepth - 1) * 18, 0, 0, 10),
            MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            StartIndex = int.TryParse(listBlock.OrderedStart, out var parsedStart) ? parsedStart : 1
        };

        foreach (var item in listBlock.OfType<ListItemBlock>())
        {
            var listItem = new ListItem();
            foreach (var nested in item)
            {
                foreach (var converted in BuildBlocks(nested, nestingDepth + 1, fontSize, mermaidCache))
                {
                    listItem.Blocks.Add(converted);
                }
            }

            if (listItem.Blocks.Count == 0)
            {
                listItem.Blocks.Add(BuildPlainTextParagraph(string.Empty, fontSize, new Thickness(0)));
            }

            list.ListItems.Add(listItem);
        }

        return list;
    }

    private WpfBlock BuildSeparator()
    {
        var separator = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 6, 0, 12)
        };
        separator.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.BorderBrushTheme);
        return new BlockUIContainer(separator);
    }

    private Paragraph BuildPlainTextParagraph(string? text, double fontSize, Thickness margin)
    {
        var paragraph = CreateParagraph(fontSize, margin);
        paragraph.Inlines.Add(new Run(text ?? string.Empty));
        return paragraph;
    }

    private Paragraph CreateParagraph(double fontSize, Thickness margin)
    {
        var paragraph = new Paragraph
        {
            Margin = margin,
            FontSize = fontSize,
            LineHeight = Math.Max(12, fontSize + 5)
        };
        paragraph.SetResourceReference(TextElement.ForegroundProperty, ThemeResourceKeys.PrimaryTextBrush);
        return paragraph;
    }

    private void AddInlines(InlineCollection target, ContainerInline? container, double fontSize)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literalInline:
                    target.Add(new Run(literalInline.Content.ToString()));
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case CodeInline codeInline:
                    target.Add(BuildInlineCode(codeInline.Content, fontSize));
                    break;
                case EmphasisInline emphasisInline:
                    target.Add(BuildEmphasis(emphasisInline, fontSize));
                    break;
                case LinkInline linkInline:
                    target.Add(BuildHyperlink(linkInline, fontSize));
                    break;
                case ContainerInline nestedContainer:
                    AddInlines(target, nestedContainer, fontSize);
                    break;
            }
        }
    }

    private WpfInline BuildInlineCode(string? code, double fontSize)
    {
        var span = new Span(new Run(code ?? string.Empty))
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = Math.Max(10, fontSize - 1)
        };
        span.SetResourceReference(TextElement.BackgroundProperty, ThemeResourceKeys.CodeInlineBackgroundBrush);
        span.SetResourceReference(TextElement.ForegroundProperty, ThemeResourceKeys.PrimaryTextBrush);
        return span;
    }

    private Span BuildEmphasis(EmphasisInline emphasisInline, double fontSize)
    {
        var span = new Span();
        if (emphasisInline.DelimiterCount >= 2)
        {
            span.FontWeight = FontWeights.SemiBold;
        }

        if (emphasisInline.DelimiterChar is '*' or '_')
        {
            span.FontStyle = FontStyles.Italic;
        }

        AddInlines(span.Inlines, emphasisInline, fontSize);
        return span;
    }

    private WpfInline BuildHyperlink(LinkInline linkInline, double fontSize)
    {
        var hyperlink = new Hyperlink();
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, ThemeResourceKeys.LinkBrush);
        hyperlink.TextDecorations = TextDecorations.Underline;
        AddInlines(hyperlink.Inlines, linkInline, fontSize);

        var target = linkInline.GetDynamicUrl != null
            ? linkInline.GetDynamicUrl() ?? string.Empty
            : linkInline.Url ?? string.Empty;

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            hyperlink.NavigateUri = uri;
            hyperlink.ToolTip = uri.AbsoluteUri;
            hyperlink.Click += (_, _) => OpenUri(uri);
        }

        return hyperlink;
    }

    protected static string NormalizeFenceInfo(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return string.Empty;
        }

        var value = info.Trim();
        var separatorIndex = value.IndexOfAny([' ', '\t', ',']);
        if (separatorIndex >= 0)
        {
            value = value[..separatorIndex];
        }

        return value.Trim().ToLowerInvariant();
    }

    private static void OpenUri(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            UserNotificationService.ShowWarning(
                "Open link failed",
                $"LEPTA could not open link: {uri.AbsoluteUri}",
                source: nameof(MarkdownResponseRenderer));
        }
    }

    protected static void CopyToClipboard(string text, string label)
    {
        try
        {
            Clipboard.SetText(text ?? string.Empty);
        }
        catch (Exception exception)
        {
            UserNotificationService.ShowWarning(
                "Copy failed",
                $"LEPTA could not copy the {label}: {exception.Message}",
                source: nameof(MarkdownResponseRenderer));
        }
    }

    private static string ResolveTokenBrushKey(CodeTokenKind kind)
        => kind switch
        {
            CodeTokenKind.Keyword => ThemeResourceKeys.CodeKeywordBrush,
            CodeTokenKind.String or CodeTokenKind.AttributeValue => ThemeResourceKeys.CodeStringBrush,
            CodeTokenKind.Comment => ThemeResourceKeys.CodeCommentBrush,
            CodeTokenKind.Number => ThemeResourceKeys.CodeNumberBrush,
            CodeTokenKind.TypeName or CodeTokenKind.PropertyName or CodeTokenKind.TagName or CodeTokenKind.AttributeName or CodeTokenKind.Variable or CodeTokenKind.Parameter or CodeTokenKind.MarkdownDelimiter => ThemeResourceKeys.CodeIdentifierBrush,
            _ => ThemeResourceKeys.PrimaryTextBrush
        };

    private static string FormatCodeToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length * 2);
        foreach (var character in value.Replace("\t", "    ", StringComparison.Ordinal))
        {
            builder.Append(character == ' ' ? '\u00A0' : character);
        }

        return builder.ToString();
    }

    protected static bool ContainsMermaidFence(string? markdown)
        => !string.IsNullOrWhiteSpace(markdown)
           && Regex.IsMatch(markdown, @"(^|\r?\n)```\s*mermaid\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> CollectMermaidSources(string? markdown, string? panelFormat)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var sources = new List<string>();
        if (string.Equals(LeptaPanelFormats.Normalize(panelFormat), LeptaPanelFormats.Mermaid, StringComparison.OrdinalIgnoreCase))
        {
            var standalone = TryExtractStandaloneMermaidDiagram(markdown);
            if (!string.IsNullOrWhiteSpace(standalone))
            {
                sources.Add(MermaidSourceNormalizer.Normalize(standalone));
                return sources;
            }

            if (!ContainsMermaidFence(markdown))
            {
                sources.Add(MermaidSourceNormalizer.Normalize(markdown));
                return sources;
            }
        }

        foreach (Match match in Regex.Matches(
                     markdown,
                     @"(?:^|\r?\n)```\s*mermaid\s*\r?\n(?<code>[\s\S]*?)\r?\n```",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var code = MermaidSourceNormalizer.Normalize(match.Groups["code"].Value);
            if (!string.IsNullOrWhiteSpace(code))
            {
                sources.Add(code);
            }
        }

        return sources;
    }

    protected static string? TryExtractStandaloneMermaidDiagram(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var match = Regex.Match(
            markdown,
            @"^\s*```(?:\s*mermaid)?\s*\r?\n(?<code>[\s\S]*?)\r?\n```\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var code = match.Groups["code"].Value.Trim();
        return string.IsNullOrWhiteSpace(code)
            ? null
            : code;
    }
}

internal sealed class MermaidResponseRenderer : MarkdownResponseRenderer
{
    public override string Format => LeptaPanelFormats.Mermaid;

    public override FlowDocument BuildDocument(string? markdown, double fontSize, MermaidDiagramViewCache? mermaidCache = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return base.BuildDocument(markdown, fontSize, mermaidCache);
        }

        var standaloneDiagram = MermaidSourceNormalizer.Normalize(TryExtractStandaloneMermaidDiagram(markdown));
        if (!string.IsNullOrWhiteSpace(standaloneDiagram))
        {
            var standaloneDocument = CreateDocumentShell(fontSize);
            mermaidCache?.TryGet(standaloneDiagram, fontSize, out _);
            standaloneDocument.Blocks.Add(new BlockUIContainer(new MermaidDiagramView(standaloneDiagram, fontSize, mermaidCache)));
            return standaloneDocument;
        }

        if (ContainsMermaidFence(markdown))
        {
            return base.BuildDocument(markdown, fontSize, mermaidCache);
        }

        var document = CreateDocumentShell(fontSize);
        var trimmedDiagram = MermaidSourceNormalizer.Normalize(markdown);
        mermaidCache?.TryGet(trimmedDiagram, fontSize, out _);
        document.Blocks.Add(new BlockUIContainer(new MermaidDiagramView(trimmedDiagram, fontSize, mermaidCache)));
        return document;
    }

    protected override WpfBlock BuildCodeBlock(string? code, string? language, double fontSize, MermaidDiagramViewCache? mermaidCache = null)
    {
        var normalizedCode = (code ?? string.Empty).Trim();
        var normalizedLanguage = NormalizeFenceInfo(language);
        if (!string.IsNullOrWhiteSpace(normalizedCode)
            && (string.IsNullOrWhiteSpace(normalizedLanguage)
                || string.Equals(normalizedLanguage, "mermaid", StringComparison.OrdinalIgnoreCase)))
        {
            var diagramCode = MermaidSourceNormalizer.Normalize(normalizedCode);
            mermaidCache?.TryGet(diagramCode, fontSize, out _);
            return new BlockUIContainer(new MermaidDiagramView(diagramCode, fontSize, mermaidCache));
        }

        return base.BuildCodeBlock(code, language, fontSize, mermaidCache);
    }
}

internal sealed class PlainTextResponseRenderer : IPanelResponseRenderer
{
    public string Format => LeptaPanelFormats.PlainText;

    public FlowDocument BuildDocument(string? text, double fontSize, MermaidDiagramViewCache? mermaidCache = null)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = fontSize,
            Background = Brushes.Transparent,
            TextAlignment = TextAlignment.Left,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, ThemeResourceKeys.PrimaryTextBrush);

        if (string.IsNullOrWhiteSpace(text))
        {
            return document;
        }

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 10),
            FontSize = fontSize,
            LineHeight = Math.Max(12, fontSize + 5)
        };
        paragraph.SetResourceReference(TextElement.ForegroundProperty, ThemeResourceKeys.PrimaryTextBrush);
        paragraph.Inlines.Add(new Run(text));
        document.Blocks.Add(paragraph);

        return document;
    }
}

