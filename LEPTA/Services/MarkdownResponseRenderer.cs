using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LEPTA.Shared.Models;
using LEPTA.Shared.Services;
using LEPTA.Theming;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownBlock = Markdig.Syntax.Block;
using WpfInline = System.Windows.Documents.Inline;
using Paragraph = System.Windows.Controls.TextBlock;

namespace LEPTA.Services;

internal sealed class MarkdownResponseRenderer
{
    private const double BaseTextSize = 14;
    private readonly MarkdownPipeline pipeline;
    private readonly CodeSyntaxHighlighter syntaxHighlighter;

    public MarkdownResponseRenderer()
        : this(new CodeSyntaxHighlighter())
    {
    }

    public MarkdownResponseRenderer(CodeSyntaxHighlighter syntaxHighlighter)
    {
        this.syntaxHighlighter = syntaxHighlighter;
        pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
    }

    public IReadOnlyList<UIElement> BuildElements(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var document = Markdown.Parse(markdown, pipeline);
        var elements = new List<UIElement>();
        foreach (var block in document)
        {
            AddBlockElements(elements.Add, block, 0);
        }

        return elements;
    }

    private void AddBlockElements(Action<UIElement> addElement, MarkdownBlock block, int nestingDepth)
    {
        switch (block)
        {
            case HeadingBlock headingBlock:
                addElement(BuildHeading(headingBlock));
                break;
            case ParagraphBlock paragraphBlock:
                addElement(BuildParagraph(paragraphBlock));
                break;
            case QuoteBlock quoteBlock:
                addElement(BuildQuote(quoteBlock, nestingDepth));
                break;
            case ListBlock listBlock:
                addElement(BuildList(listBlock, nestingDepth));
                break;
            case FencedCodeBlock fencedCodeBlock:
                addElement(BuildCodeBlock(fencedCodeBlock.Lines.ToString(), fencedCodeBlock.Info));
                break;
            case CodeBlock codeBlock:
                addElement(BuildCodeBlock(codeBlock.Lines.ToString(), null));
                break;
            case ThematicBreakBlock:
                addElement(BuildSeparator());
                break;
            case HtmlBlock htmlBlock:
                addElement(BuildPlainTextBlock(htmlBlock.Lines.ToString()));
                break;
            case ContainerBlock containerBlock:
                foreach (var nested in containerBlock)
                {
                    AddBlockElements(addElement, nested, nestingDepth + 1);
                }
                break;
        }
    }

    private UIElement BuildHeading(HeadingBlock block)
    {
        var textBlock = CreateTextBlock(new Thickness(0, block.Level == 1 ? 0 : 6, 0, 10));
        textBlock.FontWeight = FontWeights.SemiBold;
        textBlock.FontSize = block.Level switch
        {
            1 => 24,
            2 => 21,
            3 => 19,
            4 => 17,
            _ => 15
        };

        AddInlines(textBlock.Inlines, block.Inline);
        return textBlock;
    }

    private UIElement BuildParagraph(ParagraphBlock block)
    {
        if (block.Inline is null)
        {
            return BuildPlainTextBlock(block.Lines.ToString());
        }

        var textBlock = CreateTextBlock(new Thickness(0, 0, 0, 10));
        AddInlines(textBlock.Inlines, block.Inline);
        return textBlock;
    }

    private UIElement BuildQuote(QuoteBlock block, int nestingDepth)
    {
        var innerPanel = new StackPanel();
        foreach (var nested in block)
        {
            AddBlockElements(element => innerPanel.Children.Add(element), nested, nestingDepth + 1);
        }

        var border = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 4, 0, 0),
            Margin = new Thickness(0, 0, 0, 10),
            Child = innerPanel
        };
        border.SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.AccentBrush);
        return border;
    }

    private UIElement BuildList(ListBlock listBlock, int nestingDepth)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(Math.Max(0, nestingDepth - 1) * 16, 0, 0, 10)
        };

        var itemIndex = int.TryParse(listBlock.OrderedStart, out var parsedStart) ? parsedStart : 1;
        foreach (var item in listBlock.OfType<ListItemBlock>())
        {
            panel.Children.Add(BuildListItem(item, listBlock.IsOrdered, itemIndex, nestingDepth));
            if (listBlock.IsOrdered)
            {
                itemIndex++;
            }
        }

        return panel;
    }

    private UIElement BuildListItem(ListItemBlock item, bool ordered, int itemIndex, int nestingDepth)
    {
        var itemGrid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 6)
        };
        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var marker = new TextBlock
        {
            Text = ordered ? $"{itemIndex}." : "•",
            FontSize = BaseTextSize,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        marker.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.SecondaryTextBrush);
        Grid.SetColumn(marker, 0);
        itemGrid.Children.Add(marker);

        var contentPanel = new StackPanel();
        foreach (var nested in item)
        {
            AddBlockElements(element => contentPanel.Children.Add(element), nested, nestingDepth + 1);
        }

        Grid.SetColumn(contentPanel, 1);
        itemGrid.Children.Add(contentPanel);
        return itemGrid;
    }

    private UIElement BuildCodeBlock(string? code, string? language)
    {
        var normalizedCode = (code ?? string.Empty).TrimEnd('\r', '\n');
        var normalizedLanguage = syntaxHighlighter.NormalizeLanguage(language);
        var lines = syntaxHighlighter.Highlight(normalizedCode, normalizedLanguage);

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
            HorizontalAlignment = HorizontalAlignment.Right,
            Tag = normalizedCode
        };
        copyButton.SetResourceReference(Button.StyleProperty, "SecondaryButtonStyle");
        copyButton.Click += (_, _) => CopyToClipboard(normalizedCode, "code block");
        DockPanel.SetDock(copyButton, Dock.Right);
        headerPanel.Children.Add(copyButton);

        var codeTextBlock = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap
        };
        codeTextBlock.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.PrimaryTextBrush);

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            foreach (var token in lines[lineIndex].Tokens)
            {
                var run = new Run(FormatCodeToken(token.Text));
                run.SetResourceReference(TextElement.ForegroundProperty, ResolveTokenBrushKey(token.Kind));
                codeTextBlock.Inlines.Add(run);
            }

            if (lineIndex < lines.Count - 1)
            {
                codeTextBlock.Inlines.Add(new LineBreak());
            }
        }

        var codeScrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = codeTextBlock
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
                    codeScrollViewer
                }
            }
        };
        container.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.CodeBackgroundBrush);
        container.SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.BorderBrushTheme);
        container.BorderThickness = new Thickness(1);
        container.CornerRadius = new CornerRadius(10);
        return container;
    }

    private UIElement BuildSeparator()
    {
        var separator = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 6, 0, 12)
        };
        separator.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.BorderBrushTheme);
        return separator;
    }

    private UIElement BuildPlainTextBlock(string? text)
    {
        var textBlock = CreateTextBlock(new Thickness(0, 0, 0, 10));
        textBlock.Text = text ?? string.Empty;
        return textBlock;
    }

    private Paragraph CreateTextBlock(Thickness margin)
    {
        var textBlock = new Paragraph
        {
            FontSize = BaseTextSize,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin
        };
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.PrimaryTextBrush);
        return textBlock;
    }

    private void AddInlines(InlineCollection target, ContainerInline? container)
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
                    target.Add(BuildInlineCode(codeInline.Content));
                    break;
                case EmphasisInline emphasisInline:
                    target.Add(BuildEmphasis(emphasisInline));
                    break;
                case LinkInline linkInline:
                    target.Add(BuildHyperlink(linkInline));
                    break;
                case ContainerInline nestedContainer:
                    AddInlines(target, nestedContainer);
                    break;
            }
        }
    }

    private WpfInline BuildInlineCode(string? code)
    {
        var text = string.IsNullOrEmpty(code) ? string.Empty : code;
        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Margin = new Thickness(0)
        };
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.PrimaryTextBrush);

        var border = new Border
        {
            Child = textBlock,
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(6)
        };
        border.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.CodeInlineBackgroundBrush);
        border.SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.BorderBrushTheme);
        border.BorderThickness = new Thickness(1);

        return new InlineUIContainer(border)
        {
            BaselineAlignment = BaselineAlignment.Center
        };
    }

    private Span BuildEmphasis(EmphasisInline emphasisInline)
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

        AddInlines(span.Inlines, emphasisInline);
        return span;
    }

    private WpfInline BuildHyperlink(LinkInline linkInline)
    {
        var hyperlink = new Hyperlink();
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, ThemeResourceKeys.LinkBrush);
        hyperlink.TextDecorations = TextDecorations.Underline;
        AddInlines(hyperlink.Inlines, linkInline);

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
            MessageBox.Show($"LEPTA could not open link: {uri.AbsoluteUri}", "Open link failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void CopyToClipboard(string text, string label)
    {
        try
        {
            Clipboard.SetText(text ?? string.Empty);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"LEPTA could not copy the {label}: {exception.Message}", "Copy failed", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var builder = new System.Text.StringBuilder(value.Length * 2);
        foreach (var character in value.Replace("\t", "    ", StringComparison.Ordinal))
        {
            builder.Append(character == ' ' ? '\u00A0' : character);
        }

        return builder.ToString();
    }
}

