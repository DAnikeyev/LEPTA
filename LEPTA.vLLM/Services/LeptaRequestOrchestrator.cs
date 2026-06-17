using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LEPTA.Shared.Models;
using LEPTA.Shared.Diagnostics;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class LeptaRequestOrchestrator(VllmConversationService conversationService, ILeptaLogger? logger = null)
{
    public const int DefaultDocumentTokenLimit = LeptaSettings.DefaultDocumentTokenLimit;
    public const int ClipboardCachePrefillMaxTokens = 32;
    public const int EstimatedCharactersPerToken = 4;
    public const int DocumentCharacterLimit = DefaultDocumentTokenLimit * EstimatedCharactersPerToken;
    private readonly ILeptaLogger logger = logger ?? NullLeptaLogger.Instance;

    public static string BuildSharedPromptPrefix(
        string systemInstructions,
        string? clipboardText,
        string globalInstructions,
        LeptaDocumentTrimMode documentTrimMode = LeptaDocumentTrimMode.TrimStart,
        int documentTokenLimit = DefaultDocumentTokenLimit)
    {
        var safeClipboard = TrimDocument(clipboardText, documentTrimMode, documentTokenLimit);
        var safeSystemInstructions = systemInstructions.Trim();
        var safeGlobalInstructions = globalInstructions.Trim();
        var builder = new StringBuilder();

        AppendSection(builder, "System Instructions", safeSystemInstructions);
        builder.AppendLine();
        AppendSection(builder, "Global Instructions", safeGlobalInstructions);
        builder.AppendLine();
        AppendSection(builder, "Text", safeClipboard);

        return builder.ToString().TrimEnd();
    }

    public static string BuildPanelPrompt(string sharedPromptPrefix, string requestInstruction, string? panelFormat = null)
    {
        var panelInstructions = LeptaPanelInstructions.Create(requestInstruction, panelFormat);
        var safeSharedPromptPrefix = sharedPromptPrefix.Trim();
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(safeSharedPromptPrefix))
        {
            builder.AppendLine(safeSharedPromptPrefix);
            builder.AppendLine();
        }

        AppendSection(builder, "Request Instructions", panelInstructions.RequestInstructions);
        builder.AppendLine();
        AppendSection(builder, "Panel Instructions", panelInstructions.PanelInstructions);
        builder.AppendLine();
        builder.Append("Response:");
        return builder.ToString().TrimEnd();
    }

    public static string BuildPrompt(
        string systemInstructions,
        string? clipboardText,
        string globalInstructions,
        string panelInstruction,
        LeptaDocumentTrimMode documentTrimMode = LeptaDocumentTrimMode.TrimStart,
        int documentTokenLimit = DefaultDocumentTokenLimit,
        string? panelFormat = null)
        => BuildPanelPrompt(BuildSharedPromptPrefix(systemInstructions, clipboardText, globalInstructions, documentTrimMode, documentTokenLimit), panelInstruction, panelFormat);

    public static string BuildMermaidRepairPrompt(string mermaidBlock, string renderError)
    {
        var builder = new StringBuilder();
        AppendSection(
            builder,
            "Task",
            "Repair the Mermaid diagram so it parses and renders successfully. Return Mermaid source only. Do not explain. Do not wrap the answer in markdown fences. Preserve the original intent whenever possible.");
        builder.AppendLine();
        AppendSection(builder, "Render Error", string.IsNullOrWhiteSpace(renderError) ? "Unknown Mermaid render error." : renderError.Trim());
        builder.AppendLine();
        AppendSection(builder, "Broken Mermaid", StripMermaidFence(mermaidBlock));
        builder.AppendLine();
        AppendSection(
            builder,
            "Common classDiagram mistakes to fix",
            "- Replace '-->|label|' with '--> Node : label' (classDiagram does NOT support |label| syntax).\n"
                + "- Replace 'note NodeId \"text\"' with 'note for NodeId \"text\"'.\n"
                + "- Remove any markdown code fences (triple-backtick mermaid).\n"
                + "- Ensure node IDs are alphanumeric with no spaces or dots inside the ID.\n"
                + "- Ensure brackets [] and parentheses () are balanced.");
        builder.AppendLine();
        AppendSection(builder, "Output Requirements", "Return only valid Mermaid source for a single diagram. No prose. No bullets. No markdown code fences.");
        return builder.ToString().TrimEnd();
    }

    public async Task<LeptaPanelResponse> RepairMermaidDiagramAsync(
        string endpoint,
        string model,
        string mermaidBlock,
        string renderError,
        bool enableThinking = false,
        double temperature = 0.1,
        int maxModelLength = 8192,
        ExternalRequestOverrides? requestOverrides = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(mermaidBlock);

        var prompt = BuildMermaidRepairPrompt(mermaidBlock, renderError);
        var requestOptions = new VllmRequestOptions
        {
            EnableThinking = enableThinking,
            OmitReasoningFromOutput = enableThinking
        };
        var normalizedTemperature = Math.Clamp(temperature, 0.0, 0.4);
        var maxOutputTokens = Math.Max(256, maxModelLength / 4);
        var maxTokens = Math.Clamp(Math.Max(256, EstimateTokenCount(mermaidBlock) * 2), 256, maxOutputTokens);
        logger.Log(nameof(LeptaRequestOrchestrator), $"Submitting Mermaid repair request for model '{model}' at {endpoint.TrimEnd('/')}. sourceLength={mermaidBlock.Length}, errorLength={renderError?.Length ?? 0}, maxTokens={maxTokens}.");

        try
        {
            var completion = await conversationService.SendAsync(
                endpoint,
                model,
                [],
                prompt,
                systemPrompt: "You repair invalid Mermaid diagrams. Return only valid Mermaid source for one diagram. Never explain your answer and never use markdown fences.",
                maxTokens: maxTokens,
                temperature: normalizedTemperature,
                requestOptions: requestOptions,
                requestOverrides: requestOverrides,
                cancellationToken: cancellationToken);

            var repaired = StripMermaidFence(completion.AssistantText);
            if (string.IsNullOrWhiteSpace(repaired))
            {
                return new LeptaPanelResponse(
                    "Mermaid repair",
                    string.Empty,
                    "The model returned an empty Mermaid repair response.",
                    GenerationDuration: completion.Elapsed);
            }

            return new LeptaPanelResponse(
                "Mermaid repair",
                repaired,
                EstimatedVisibleTokenCount: EstimateVisibleTokenCount(repaired),
                GenerationDuration: completion.Elapsed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Log(nameof(LeptaRequestOrchestrator), $"Mermaid repair request failed. reason={exception.Message}");
            return new LeptaPanelResponse("Mermaid repair", string.Empty, exception.Message);
        }
    }

    public async Task<IReadOnlyList<LeptaPanelResponse>> GenerateForPanelsAsync(
        string endpoint,
        string model,
        string systemInstructions,
        string? clipboardText,
        string globalInstructions,
        IReadOnlyList<LeptaPanelRequest> panels,
        Action<int, string>? onToken = null,
        Action<int>? onPanelCompleted = null,
        bool warmSharedPrefix = false,
        bool enableThinking = false,
        LeptaDocumentTrimMode documentTrimMode = LeptaDocumentTrimMode.TrimStart,
        int documentTokenLimit = DefaultDocumentTokenLimit,
        double temperature = LeptaSettings.DefaultTemperature,
        string? sharedCacheSalt = null,
        bool sharedPrefixAlreadyWarm = false,
        ExternalRequestOverrides? requestOverrides = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(panels);

        if (panels.Count == 0)
        {
            return [];
        }

        var normalizedDocumentTokenLimit = LeptaSettings.NormalizeDocumentTokenLimit(documentTokenLimit);
        var normalizedTemperature = LeptaSettings.NormalizeTemperature(temperature);
        var sharedPromptPrefix = BuildSharedPromptPrefix(systemInstructions, clipboardText, globalInstructions, documentTrimMode, normalizedDocumentTokenLimit);
        var shouldWarmSharedPrefix = ShouldWarmSharedPrefix(sharedPromptPrefix, panels.Count, warmSharedPrefix);
        var effectiveCacheSalt = string.IsNullOrWhiteSpace(sharedCacheSalt)
            ? shouldWarmSharedPrefix
                ? Guid.NewGuid().ToString("N")
                : null
            : sharedCacheSalt.Trim();
        var requestOptions = new VllmRequestOptions
        {
            EnableThinking = enableThinking,
            OmitReasoningFromOutput = enableThinking,
            CacheSalt = effectiveCacheSalt
        };
        logger.Log(nameof(LeptaRequestOrchestrator), $"Generating {panels.Count} panel response(s) with model '{model}' at {endpoint.TrimEnd('/')}. clipboardLength={clipboardText?.Length ?? 0}, sharedPrefixLength={sharedPromptPrefix.Length}, enableThinking={enableThinking.ToString().ToLowerInvariant()}, documentTokenLimit={normalizedDocumentTokenLimit}, temperature={normalizedTemperature:0.##}, cacheSaltPresent={(!string.IsNullOrWhiteSpace(requestOptions.CacheSalt)).ToString().ToLowerInvariant()}.");

        if (shouldWarmSharedPrefix && !sharedPrefixAlreadyWarm)
        {
            await WarmSharedPrefixAsync(endpoint, model, sharedPromptPrefix, requestOptions, requestOverrides, cancellationToken);
        }

        var tasks = panels
            .Select((panel, index) => GeneratePanelAsync(endpoint, model, sharedPromptPrefix, panel, index, requestOptions, normalizedTemperature, onToken, onPanelCompleted, requestOverrides, cancellationToken))
            .ToArray();

        return await Task.WhenAll(tasks);
    }

    public async Task PrefillSharedPromptPrefixAsync(
        string endpoint,
        string model,
        string sharedPromptPrefix,
        VllmRequestOptions? requestOptions = null,
        int maxTokens = ClipboardCachePrefillMaxTokens,
        ExternalRequestOverrides? requestOverrides = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (string.IsNullOrWhiteSpace(sharedPromptPrefix))
        {
            return;
        }

        var normalizedMaxTokens = Math.Max(1, maxTokens);
        logger.Log(nameof(LeptaRequestOrchestrator), $"Prefilling shared prompt prefix cache for model '{model}' at {endpoint.TrimEnd('/')}. prefixLength={sharedPromptPrefix.Length}, maxTokens={normalizedMaxTokens}, cacheSaltPresent={(!string.IsNullOrWhiteSpace(requestOptions?.CacheSalt)).ToString().ToLowerInvariant()}.");
        await PrimeSharedPrefixAsync(
            endpoint,
            model,
            BuildClipboardPrefillPrompt(sharedPromptPrefix),
            requestOptions ?? new VllmRequestOptions(),
            normalizedMaxTokens,
            temperature: 0.0,
            requestOverrides,
            cancellationToken);
    }

    private async Task<LeptaPanelResponse> GeneratePanelAsync(
        string endpoint,
        string model,
        string sharedPromptPrefix,
        LeptaPanelRequest panel,
        int panelIndex,
        VllmRequestOptions requestOptions,
        double temperature,
        Action<int, string>? onToken,
        Action<int>? onPanelCompleted,
        ExternalRequestOverrides? requestOverrides,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var estimatedVisibleTokenCount = 0;

        try
        {
            var prompt = BuildPanelPrompt(sharedPromptPrefix, panel.CustomInstruction, panel.Format);
            var systemPrompt = ResolvePanelSystemPrompt(panel.Format);
            logger.Log(nameof(LeptaRequestOrchestrator), $"Starting panel '{panel.Name}' generation. panelIndex={panelIndex}, promptLength={prompt.Length}, systemPrompt={(!string.IsNullOrWhiteSpace(systemPrompt)).ToString().ToLowerInvariant()}.");
            var builder = new StringBuilder();
            ThinkingContentStreamFilter? streamFilter = requestOptions.OmitReasoningFromOutput
                ? new ThinkingContentStreamFilter()
                : null;
            await foreach (var token in conversationService.StreamPromptAsync(
                               endpoint,
                               model,
                               prompt,
                               systemPrompt: systemPrompt,
                               temperature: temperature,
                               requestOptions: requestOptions,
                               requestOverrides: requestOverrides,
                               cancellationToken: cancellationToken))
            {
                builder.Append(token);
                var visibleToken = streamFilter?.Append(token) ?? token;
                if (!string.IsNullOrEmpty(visibleToken))
                {
                    estimatedVisibleTokenCount += EstimateVisibleTokenCount(visibleToken);
                    onToken?.Invoke(panelIndex, visibleToken);
                }
            }

            var responseText = streamFilter?.GetVisibleText() ?? builder.ToString();
            logger.Log(nameof(LeptaRequestOrchestrator), $"Completed panel '{panel.Name}' generation. responseLength={responseText.Length}.");
            return new LeptaPanelResponse(
                panel.Name,
                responseText,
                EstimatedVisibleTokenCount: estimatedVisibleTokenCount,
                GenerationDuration: stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            logger.Log(nameof(LeptaRequestOrchestrator), $"Panel '{panel.Name}' generation failed. reason={exception.Message}");
            return new LeptaPanelResponse(
                panel.Name,
                string.Empty,
                exception.Message,
                EstimatedVisibleTokenCount: estimatedVisibleTokenCount,
                GenerationDuration: stopwatch.Elapsed);
        }
        finally
        {
            onPanelCompleted?.Invoke(panelIndex);
        }
    }

    private static string ResolvePanelSystemPrompt(string? format)
    {
        if (string.Equals(LeptaPanelFormats.Normalize(format), LeptaPanelFormats.Mermaid, StringComparison.OrdinalIgnoreCase))
        {
            return "You are a Mermaid diagram expert. Generate valid, minimal Mermaid syntax. "
                + "You NEVER wrap the output in markdown fences. "
                + "You know the exact syntax rules for classDiagram, flowchart, and sequenceDiagram. "
                + "In classDiagram, label relationships with ': label' after the arrow. "
                + "In classDiagram, use 'note for NodeId \"text\"' for notes.";
        }

        return string.Empty;
    }

    private static int EstimateVisibleTokenCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(text.Trim().Length / (double)EstimatedCharactersPerToken));
    }

    private static string StripMermaidFence(string? mermaidBlock)
    {
        if (string.IsNullOrWhiteSpace(mermaidBlock))
        {
            return string.Empty;
        }

        var text = mermaidBlock.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var fencedMatch = Regex.Match(
            text,
            @"```(?:\s*mermaid)?\s*\n(?<code>[\s\S]*?)\n```",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return fencedMatch.Success
            ? fencedMatch.Groups["code"].Value.Trim()
            : text;
    }

    private async Task WarmSharedPrefixAsync(
        string endpoint,
        string model,
        string sharedPromptPrefix,
        VllmRequestOptions requestOptions,
        ExternalRequestOverrides? requestOverrides = null,
        CancellationToken cancellationToken = default)
    {
        logger.Log(nameof(LeptaRequestOrchestrator), $"Warming shared prompt prefix cache for model '{model}'. prefixLength={sharedPromptPrefix.Length}.");
        await PrimeSharedPrefixAsync(
            endpoint,
            model,
            BuildWarmupPrompt(sharedPromptPrefix),
            requestOptions,
            maxTokens: 8,
            temperature: 0.0,
            requestOverrides,
            cancellationToken);
    }

    private async Task PrimeSharedPrefixAsync(
        string endpoint,
        string model,
        string prompt,
        VllmRequestOptions requestOptions,
        int maxTokens,
        double temperature,
        ExternalRequestOverrides? requestOverrides = null,
        CancellationToken cancellationToken = default)
    {
        await conversationService.SendAsync(
            endpoint,
            model,
            [],
            prompt,
            systemPrompt: string.Empty,
            maxTokens: maxTokens,
            temperature: temperature,
            requestOptions: requestOptions,
            requestOverrides: requestOverrides,
            cancellationToken: cancellationToken);
    }

    private static bool ShouldWarmSharedPrefix(string sharedPromptPrefix, int panelCount, bool warmSharedPrefix)
        => warmSharedPrefix && panelCount > 1 && !string.IsNullOrWhiteSpace(sharedPromptPrefix);

    private static string BuildWarmupPrompt(string sharedPromptPrefix)
    {
        var builder = new StringBuilder();
        builder.AppendLine(sharedPromptPrefix.Trim());
        builder.AppendLine();
        builder.AppendLine("Panel Instructions:");
        builder.AppendLine("Warm the shared prefix cache for the upcoming panel requests. Return only READY.");
        builder.AppendLine();
        builder.Append("Response:");
        return builder.ToString().TrimEnd();
    }

    private static string BuildClipboardPrefillPrompt(string sharedPromptPrefix)
    {
        var builder = new StringBuilder();
        builder.AppendLine(sharedPromptPrefix.Trim());
        builder.AppendLine();
        builder.AppendLine("Request Instructions:");
        builder.AppendLine("Prefill the LEPTA clipboard cache for an upcoming run. Reply only READY.");
        builder.AppendLine();
        builder.AppendLine("Panel Instructions:");
        builder.AppendLine("Return only READY.");
        builder.AppendLine();
        builder.Append("Response:");
        return builder.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder builder, string label, string content)
    {
        builder.AppendLine($"{label}:");
        builder.AppendLine(content);
    }

    public static int GetDocumentCharacterLimit(int documentTokenLimit)
        => LeptaSettings.NormalizeDocumentTokenLimit(documentTokenLimit) * EstimatedCharactersPerToken;

    private static string TrimDocument(string? clipboardText, LeptaDocumentTrimMode documentTrimMode, int documentTokenLimit)
    {
        if (string.IsNullOrEmpty(clipboardText))
        {
            return string.Empty;
        }

        var normalizedDocumentTokenLimit = LeptaSettings.NormalizeDocumentTokenLimit(documentTokenLimit);
        if (EstimateTokenCount(clipboardText) <= normalizedDocumentTokenLimit)
        {
            return clipboardText;
        }

        var documentCharacterLimit = GetDocumentCharacterLimit(normalizedDocumentTokenLimit);

        return documentTrimMode == LeptaDocumentTrimMode.TrimEnd
            ? clipboardText[..documentCharacterLimit]
            : clipboardText[^documentCharacterLimit..];
    }

    private static int EstimateTokenCount(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : (int)Math.Ceiling(text.Length / (double)EstimatedCharactersPerToken);
}
