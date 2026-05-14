using System.Text;
using LEPTA.Shared.Diagnostics;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class LeptaRequestOrchestrator(VllmConversationService conversationService, ILeptaLogger? logger = null)
{
    public const int ClipboardTailLimit = 20_000;
    private readonly ILeptaLogger logger = logger ?? NullLeptaLogger.Instance;

    public static string BuildPrompt(string? clipboardText, string generalInstruction, string panelInstruction)
    {
        var safeClipboard = TrimClipboardTail(clipboardText);
        var safeGeneralInstruction = generalInstruction.Trim();
        var safePanelInstruction = panelInstruction.Trim();

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(safeClipboard))
        {
            builder.AppendLine("Clipboard context:");
            builder.AppendLine(safeClipboard);
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(safeGeneralInstruction))
        {
            builder.AppendLine("General instruction:");
            builder.AppendLine(safeGeneralInstruction);
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(safePanelInstruction))
        {
            builder.AppendLine("Panel instruction:");
            builder.AppendLine(safePanelInstruction);
            builder.AppendLine();
        }

        builder.AppendLine("Return only the useful answer for this panel.");
        return builder.ToString().Trim();
    }

    public async Task<IReadOnlyList<LeptaPanelResponse>> GenerateForPanelsAsync(
        string endpoint,
        string model,
        string? clipboardText,
        string generalInstruction,
        IReadOnlyList<LeptaPanelRequest> panels,
        Action<int, string>? onToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(panels);

        if (panels.Count == 0)
        {
            return [];
        }

        logger.Log(nameof(LeptaRequestOrchestrator), $"Generating {panels.Count} panel response(s) with model '{model}' at {endpoint.TrimEnd('/')}. clipboardLength={clipboardText?.Length ?? 0}.");

        var tasks = panels
            .Select((panel, index) => GeneratePanelAsync(endpoint, model, clipboardText, generalInstruction, panel, index, onToken, cancellationToken))
            .ToArray();

        return await Task.WhenAll(tasks);
    }

    private async Task<LeptaPanelResponse> GeneratePanelAsync(
        string endpoint,
        string model,
        string? clipboardText,
        string generalInstruction,
        LeptaPanelRequest panel,
        int panelIndex,
        Action<int, string>? onToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var prompt = BuildPrompt(clipboardText, generalInstruction, panel.CustomInstruction);
            logger.Log(nameof(LeptaRequestOrchestrator), $"Starting panel '{panel.Name}' generation. panelIndex={panelIndex}, promptLength={prompt.Length}.");
            var builder = new StringBuilder();
            await foreach (var token in conversationService.StreamPromptAsync(
                               endpoint,
                               model,
                               prompt,
                               systemPrompt: string.Empty,
                               cancellationToken: cancellationToken))
            {
                builder.Append(token);
                onToken?.Invoke(panelIndex, token);
            }

            logger.Log(nameof(LeptaRequestOrchestrator), $"Completed panel '{panel.Name}' generation. responseLength={builder.Length}.");
            return new LeptaPanelResponse(panel.Name, builder.ToString());
        }
        catch (Exception exception)
        {
            logger.Log(nameof(LeptaRequestOrchestrator), $"Panel '{panel.Name}' generation failed. reason={exception.Message}");
            return new LeptaPanelResponse(panel.Name, string.Empty, exception.Message);
        }
    }

    private static string TrimClipboardTail(string? clipboardText)
    {
        if (string.IsNullOrEmpty(clipboardText))
        {
            return string.Empty;
        }

        return clipboardText.Length <= ClipboardTailLimit
            ? clipboardText
            : clipboardText[^ClipboardTailLimit..];
    }
}
