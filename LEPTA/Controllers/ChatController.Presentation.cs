using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LEPTA.Controls;
using LEPTA.Shared.Models;
using LEPTA.Theming;
using LEPTA.vLLM.Services;

namespace LEPTA.Controllers;

internal sealed partial class ChatController
{
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
                PanelFormat = LeptaPanelFormats.Markdown,
                ShowPreviewButton = false,
                Margin = new Thickness(0, 4, 0, 0)
            };
            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                BindingOperations.SetBinding(
                    markdownView,
                    MarkdownResponseView.ResponseFontSizeProperty,
                    new Binding(nameof(MainWindow.ResponseFontSize))
                    {
                        Source = mainWindow,
                        Mode = BindingMode.OneWay
                    });
            }

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
            messages.MessagesPanel.Children.Add(bubble);
            UpdateEmptyStateVisibility();
            messages.ScrollViewer.ScrollToEnd();
            return new ChatMessageBubble(bubble, markdownView, metadataSummaryText, metadataDetailsText);
        }

        var plainSenderText = new TextBlock
        {
            Text = sender,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        plainSenderText.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);

        var textBox = new TextBox
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            IsTabStop = false
        };

        textBox.SetResourceReference(Control.ForegroundProperty, foregroundKey);
        stackPanel.Children.Add(plainSenderText);
        stackPanel.Children.Add(textBox);
        bubble.Child = stackPanel;
        messages.MessagesPanel.Children.Add(bubble);
        UpdateEmptyStateVisibility();
        messages.ScrollViewer.ScrollToEnd();
        return new ChatMessageBubble(bubble, textBox, null, null);
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

            messages.ScrollViewer.ScrollToEnd();
            return;
        }

        _ = target.Bubble.Dispatcher.InvokeAsync(() =>
        {
            if (target.MarkdownView is not null)
            {
                target.MarkdownView.AppendText(text);
            }
            else if (target.PlainTextBlock is not null)
            {
                target.PlainTextBlock.Text += text;
            }

            messages.ScrollViewer.ScrollToEnd();
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

            messages.ScrollViewer.ScrollToEnd();
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

            messages.ScrollViewer.ScrollToEnd();
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
            messages.MessagesPanel.Children.Remove(bubble);
            UpdateEmptyStateVisibility();
            return;
        }

        _ = bubble.Dispatcher.InvokeAsync(() =>
        {
            messages.MessagesPanel.Children.Remove(bubble);
            UpdateEmptyStateVisibility();
        });
    }

    private static string BuildResponseMetadataSummary(
        string servedModelName,
        VllmConversationService.ConversationTurnResult response)
    {
        var elapsed = response.Elapsed.TotalSeconds;
        var tokenSummary = response.Tokens > 0
            ? $"{response.Tokens} tokens{(elapsed > 0 ? $" • {response.Tokens / elapsed:F0} tok/s" : string.Empty)}"
            : "tokens unavailable";
        return $"{servedModelName} • {elapsed:F1}s • {tokenSummary}";
    }

    private static string BuildResponseMetadataDetails(VllmConversationService.ConversationTurnResult response, bool usedThinking)
        => response.UsedPromptFallback
            ? $"Mode: prompt fallback via /v1/completions{(usedThinking ? " • thinking requested" : string.Empty)}"
            : usedThinking
                ? "Mode: standard chat completion • thinking requested"
                : "Mode: standard chat completion";

    private sealed record ChatMessageBubble(
        Border Bubble,
        TextBox? PlainTextBlock,
        MarkdownResponseView? MarkdownView,
        TextBlock? MetadataSummaryText,
        TextBlock? MetadataDetailsText)
    {
        public ChatMessageBubble(
            Border bubble,
            TextBox? plainTextBlock,
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


