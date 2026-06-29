using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using System.Diagnostics;
using LEPTA.Shared.Diagnostics;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class VllmConversationService(VllmChatCompletionClient chatCompletionClient, ILeptaLogger? logger = null)
{
    private const string OmittedContextMarker = "\n\n[Earlier content omitted to fit context]\n\n";
    public const string DefaultSystemPrompt = """
        Be precise, technical, and concise.
        Prioritize correctness over sounding confident.
        For technical questions, explain the reasoning step-by-step before the conclusion.
        State assumptions explicitly when information is missing.
        Distinguish facts, estimates, and opinions clearly.
        Avoid generic advice and repetition.
        Use domain-specific terminology when appropriate.
        If multiple valid answers exist, compare tradeoffs briefly.
        When uncertain, say what would need to be verified.
        """;
    private readonly ILeptaLogger logger = logger ?? NullLeptaLogger.Instance;

    public async Task<ConversationTurnResult> SendAsync(
        string endpoint,
        string model,
        IReadOnlyList<VllmChatMessage> conversation,
        string userPrompt,
        string systemPrompt = DefaultSystemPrompt,
        int maxTokens = 256,
        double temperature = 0.2,
        VllmRequestOptions? requestOptions = null,
        ExternalRequestOverrides? requestOverrides = null,
        int maxContextTokens = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);

        var updatedConversation = conversation
            .Concat([new VllmChatMessage("user", userPrompt.Trim())])
            .ToArray();

        this.logger.Log(nameof(VllmConversationService), $"Sending conversational turn to model '{model}' at {endpoint.TrimEnd('/')}. priorMessages={conversation.Count}, systemPrompt={(!string.IsNullOrWhiteSpace(systemPrompt)).ToString().ToLowerInvariant()}.");

        try
        {
            var completion = await chatCompletionClient.CompleteChatAsync(
                endpoint,
                model,
                BuildRequestMessages(updatedConversation, systemPrompt, maxContextTokens),
                maxTokens,
                temperature,
                requestOptions,
                requestOverrides,
                cancellationToken);

            return CreateResult(
                updatedConversation,
                completion.Text,
                completion.Tokens,
                completion.Elapsed,
                completion.TokensPerSecond,
                usedPromptFallback: false);
        }
        catch (InvalidOperationException chatException) when (ShouldFallbackToTextCompletion(chatException))
        {
            this.logger.Log(nameof(VllmConversationService), $"Chat completion rejected for model '{model}'. Falling back to prompt completion. reason={chatException.Message}");
            try
            {
                var completion = await chatCompletionClient.CompleteAsync(
                    endpoint,
                    model,
                    BuildPromptFallback(updatedConversation, systemPrompt, maxContextTokens),
                    maxTokens,
                    temperature,
                    requestOverrides,
                    cancellationToken);

                return CreateResult(
                    updatedConversation,
                    completion.Text,
                    completion.Tokens,
                    completion.Elapsed,
                    completion.TokensPerSecond,
                    usedPromptFallback: true);
            }
            catch (Exception fallbackException) when (fallbackException is not OperationCanceledException)
            {
                this.logger.Log(nameof(VllmConversationService), $"Prompt fallback failed for model '{model}'. reason={fallbackException.Message}");
                throw new InvalidOperationException(
                    $"Chat completion failed and prompt fallback also failed.{Environment.NewLine}Chat error: {chatException.Message}{Environment.NewLine}Fallback error: {fallbackException.Message}",
                    fallbackException);
            }
        }
    }

    public async Task<ConversationTurnResult> StreamConversationAsync(
        string endpoint,
        string model,
        IReadOnlyList<VllmChatMessage> conversation,
        string userPrompt,
        Action<string>? onToken = null,
        string systemPrompt = DefaultSystemPrompt,
        int maxTokens = 256,
        double temperature = 0.2,
        VllmRequestOptions? requestOptions = null,
        ExternalRequestOverrides? requestOverrides = null,
        int maxContextTokens = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);

        var updatedConversation = conversation
            .Concat([new VllmChatMessage("user", userPrompt.Trim())])
            .ToArray();

        logger.Log(nameof(VllmConversationService), $"Streaming conversational turn to model '{model}' at {endpoint.TrimEnd('/')}. priorMessages={conversation.Count}, systemPrompt={(!string.IsNullOrWhiteSpace(systemPrompt)).ToString().ToLowerInvariant()}, maxTokens={maxTokens}.");
        var stopwatch = Stopwatch.StartNew();
        var requestMessages = BuildRequestMessages(updatedConversation, systemPrompt, maxContextTokens);
        var builder = new StringBuilder();

        var completionTokens = default(int?);
        try
        {
            try
            {
                await foreach (var chunk in chatCompletionClient.StreamChatCompletionAsync(
                                   endpoint,
                                   model,
                                   requestMessages,
                                   maxTokens,
                                   temperature,
                                   requestOptions,
                                   requestOverrides,
                                   cancellationToken))
                {
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        builder.Append(chunk.Text);
                        onToken?.Invoke(chunk.Text);
                    }

                    if (chunk.CompletionTokens.HasValue)
                    {
                        completionTokens = chunk.CompletionTokens;
                    }
                }
            }
            catch (InvalidOperationException streamException) when (ShouldRetryWithNonStreamingChat(streamException))
            {
                if (builder.Length > 0)
                {
                    stopwatch.Stop();
                    logger.Log(nameof(VllmConversationService), $"Streaming conversational turn timed out for model '{model}' after partial content. Returning partial assistant text. responseLength={builder.Length}, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}.");
                    return CreateResult(
                        updatedConversation,
                        builder.ToString(),
                        tokens: 0,
                        elapsed: stopwatch.Elapsed,
                        tokensPerSecond: 0,
                        usedPromptFallback: false);
                }

                logger.Log(nameof(VllmConversationService), $"Streaming chat yielded no usable content for model '{model}'. Retrying with non-streaming chat completion. reason={streamException.Message}");
                var completion = await chatCompletionClient.CompleteChatAsync(
                    endpoint,
                    model,
                    requestMessages,
                    maxTokens,
                    temperature,
                    requestOptions,
                    requestOverrides,
                    cancellationToken);

                return CreateResult(
                    updatedConversation,
                    completion.Text,
                    completion.Tokens,
                    completion.Elapsed,
                    completion.TokensPerSecond,
                    usedPromptFallback: false);
            }

            if (builder.Length == 0)
            {
                logger.Log(nameof(VllmConversationService), $"Streaming chat completed without assistant content for model '{model}'. Retrying with non-streaming chat completion.");
                var completion = await chatCompletionClient.CompleteChatAsync(
                    endpoint,
                    model,
                    requestMessages,
                    maxTokens,
                    temperature,
                    requestOptions,
                    requestOverrides,
                    cancellationToken);

                return CreateResult(
                    updatedConversation,
                    completion.Text,
                    completion.Tokens,
                    completion.Elapsed,
                    completion.TokensPerSecond,
                    usedPromptFallback: false);
            }

            stopwatch.Stop();
            var tokens = completionTokens ?? (builder.Length / 4);
            var tokensPerSecond = tokens > 0 && stopwatch.Elapsed.TotalSeconds > 0
                ? tokens / stopwatch.Elapsed.TotalSeconds
                : 0;
            logger.Log(nameof(VllmConversationService), $"Streaming conversational turn completed for model '{model}'. responseLength={builder.Length}, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F0}, tokens={tokens}.");
            return CreateResult(
                updatedConversation,
                builder.ToString(),
                tokens: tokens,
                elapsed: stopwatch.Elapsed,
                tokensPerSecond: tokensPerSecond,
                usedPromptFallback: false);
        }
        catch (InvalidOperationException chatException) when (ShouldFallbackToTextCompletion(chatException))
        {
            logger.Log(nameof(VllmConversationService), $"Streaming chat rejected for model '{model}'. Falling back to prompt completion. reason={chatException.Message}");
            try
            {
                var completion = await chatCompletionClient.CompleteAsync(
                    endpoint,
                    model,
                    BuildPromptFallback(updatedConversation, systemPrompt, maxContextTokens),
                    maxTokens,
                    temperature,
                    requestOverrides,
                    cancellationToken);

                var fallbackText = NormalizeAssistantText(completion.Text);
                if (!string.IsNullOrEmpty(fallbackText))
                {
                    onToken?.Invoke(fallbackText);
                }

                return CreateResult(
                    updatedConversation,
                    completion.Text,
                    completion.Tokens,
                    completion.Elapsed,
                    completion.TokensPerSecond,
                    usedPromptFallback: true);
            }
            catch (Exception fallbackException) when (fallbackException is not OperationCanceledException)
            {
                logger.Log(nameof(VllmConversationService), $"Streaming prompt fallback failed for model '{model}'. reason={fallbackException.Message}");
                throw new InvalidOperationException(
                    $"Chat streaming failed and prompt fallback also failed.{Environment.NewLine}Chat error: {chatException.Message}{Environment.NewLine}Fallback error: {fallbackException.Message}",
                    fallbackException);
            }
        }
    }

    public static IReadOnlyList<VllmChatMessage> BuildRequestMessages(
        IReadOnlyList<VllmChatMessage> conversation,
        string systemPrompt = DefaultSystemPrompt,
        int maxContextTokens = 0)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var normalizedSystemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt.Trim();
        var requestConversation = TrimConversationToContextWindow(conversation, normalizedSystemPrompt, maxContextTokens);
        var requestMessages = new List<VllmChatMessage>(requestConversation.Count + 1);
        if (!string.IsNullOrWhiteSpace(normalizedSystemPrompt))
        {
            requestMessages.Add(new VllmChatMessage("system", normalizedSystemPrompt));
        }

        requestMessages.AddRange(requestConversation);
        return requestMessages;
    }

    public async IAsyncEnumerable<string> StreamPromptAsync(
        string endpoint,
        string model,
        string prompt,
        string systemPrompt = "",
        int maxTokens = 700,
        double temperature = 0.2,
        VllmRequestOptions? requestOptions = null,
        ExternalRequestOverrides? requestOverrides = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var conversation = new[]
        {
            new VllmChatMessage("user", prompt.Trim())
        };

        logger.Log(nameof(VllmConversationService), $"Streaming prompt request to model '{model}' at {endpoint.TrimEnd('/')}. promptLength={prompt.Length}, systemPrompt={(!string.IsNullOrWhiteSpace(systemPrompt)).ToString().ToLowerInvariant()}.");

        var channel = Channel.CreateUnbounded<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in chatCompletionClient.StreamChatCompletionAsync(
                                   endpoint,
                                   model,
                                   BuildRequestMessages(conversation, systemPrompt),
                                   maxTokens,
                                   temperature,
                                   requestOptions,
                                   requestOverrides,
                                   cancellationToken))
                {
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        await channel.Writer.WriteAsync(chunk.Text, cancellationToken);
                    }
                }

                channel.Writer.TryComplete();
                logger.Log(nameof(VllmConversationService), $"Streaming prompt request completed for model '{model}' without fallback.");
            }
            catch (InvalidOperationException chatException) when (ShouldFallbackToTextCompletion(chatException))
            {
                logger.Log(nameof(VllmConversationService), $"Streaming chat rejected for model '{model}'. Falling back to prompt completion. reason={chatException.Message}");
                try
                {
                    var completion = await chatCompletionClient.CompleteAsync(
                        endpoint,
                        model,
                        BuildPromptFallback(conversation, systemPrompt),
                        maxTokens,
                        temperature,
                        requestOverrides,
                        cancellationToken);

                    var fallbackText = NormalizeAssistantText(completion.Text);
                    if (!string.IsNullOrEmpty(fallbackText))
                    {
                        await channel.Writer.WriteAsync(fallbackText, cancellationToken);
                    }

                    channel.Writer.TryComplete();
                    logger.Log(nameof(VllmConversationService), $"Streaming prompt fallback completed for model '{model}'. textLength={fallbackText.Length}.");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.Log(nameof(VllmConversationService), $"Streaming prompt fallback failed for model '{model}'. reason={exception.Message}");
                    channel.Writer.TryComplete(new InvalidOperationException(
                        $"Chat streaming failed and prompt fallback also failed.{Environment.NewLine}Chat error: {chatException.Message}{Environment.NewLine}Fallback error: {exception.Message}",
                        exception));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.Log(nameof(VllmConversationService), $"Streaming prompt request failed for model '{model}'. reason={exception.Message}");
                channel.Writer.TryComplete(exception);
            }
        }, CancellationToken.None);

        await foreach (var token in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return token;
        }
    }

    private static ConversationTurnResult CreateResult(
        IReadOnlyList<VllmChatMessage> conversation,
        string text,
        int tokens,
        TimeSpan elapsed,
        double tokensPerSecond,
        bool usedPromptFallback)
    {
        var assistantText = NormalizeAssistantText(text);
        var updatedConversation = conversation
            .Concat([new VllmChatMessage("assistant", assistantText)])
            .ToArray();

        return new ConversationTurnResult(
            assistantText,
            updatedConversation,
            tokens,
            elapsed,
            tokensPerSecond,
            usedPromptFallback);
    }

    private static string BuildPromptFallback(IReadOnlyList<VllmChatMessage> conversation, string systemPrompt, int maxContextTokens = 0)
    {
        var requestConversation = TrimConversationToContextWindow(conversation, systemPrompt, maxContextTokens);
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            builder.AppendLine("System instruction:");
            builder.AppendLine(systemPrompt.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("Conversation:");
        builder.AppendLine();

        foreach (var message in requestConversation)
        {
            builder.AppendLine($"{GetRoleLabel(message.Role)}:");
            builder.AppendLine(message.Content.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("Reply as the assistant to the latest user message. Return only the assistant reply.");
        return builder.ToString().Trim();
    }

    private static string NormalizeAssistantText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? "(No content returned by the server.)"
            : text.Trim();

    private static IReadOnlyList<VllmChatMessage> TrimConversationToContextWindow(
        IReadOnlyList<VllmChatMessage> conversation,
        string? systemPrompt,
        int maxContextTokens)
    {
        var normalizedConversation = conversation
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .ToArray();

        if (normalizedConversation.Length == 0 || maxContextTokens <= 0)
        {
            return normalizedConversation;
        }

        var availableConversationTokens = Math.Max(
            1,
            maxContextTokens - EstimateTokenCount(systemPrompt));
        var selected = new List<VllmChatMessage>(normalizedConversation.Length);
        var remainingTokens = availableConversationTokens;

        for (var index = normalizedConversation.Length - 1; index >= 0; index--)
        {
            var message = normalizedConversation[index];
            var messageTokens = EstimateTokenCount(message.Content);
            if (messageTokens <= remainingTokens)
            {
                selected.Add(message);
                remainingTokens -= messageTokens;
                continue;
            }

            if (selected.Count == 0)
            {
                selected.Add(new VllmChatMessage(message.Role, TrimMessageContent(message.Content, remainingTokens)));
            }

            break;
        }

        selected.Reverse();
        return selected;
    }

    private static string TrimMessageContent(string content, int tokenBudget)
    {
        var normalizedContent = content.Trim();
        if (string.IsNullOrEmpty(normalizedContent))
        {
            return string.Empty;
        }

        var characterBudget = Math.Max(LeptaRequestOrchestrator.EstimatedCharactersPerToken, tokenBudget * LeptaRequestOrchestrator.EstimatedCharactersPerToken);
        if (normalizedContent.Length <= characterBudget)
        {
            return normalizedContent;
        }

        if (characterBudget <= OmittedContextMarker.Length)
        {
            return normalizedContent[^characterBudget..];
        }

        var remainingCharacters = characterBudget - OmittedContextMarker.Length;
        var headLength = remainingCharacters / 2;
        var tailLength = remainingCharacters - headLength;
        return string.Concat(
            normalizedContent[..headLength],
            OmittedContextMarker,
            normalizedContent[^tailLength..]);
    }

    private static int EstimateTokenCount(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : (int)Math.Ceiling(text.Trim().Length / (double)LeptaRequestOrchestrator.EstimatedCharactersPerToken);

    private static bool ShouldFallbackToTextCompletion(InvalidOperationException exception)
    {
        var message = exception.Message;
        var isChatRequestFailure = message.Contains("/v1/chat/completions", StringComparison.Ordinal)
            || message.Contains("vLLM streaming request failed", StringComparison.Ordinal);

        if (!isChatRequestFailure)
        {
            return false;
        }

        return message.Contains("status 400", StringComparison.Ordinal)
            || message.Contains("status 404", StringComparison.Ordinal)
            || message.Contains("status 405", StringComparison.Ordinal)
            || message.Contains("status 415", StringComparison.Ordinal)
            || message.Contains("status 422", StringComparison.Ordinal)
            || message.Contains("status 501", StringComparison.Ordinal);
    }

    private static bool ShouldRetryWithNonStreamingChat(InvalidOperationException exception)
        => exception.Message.Contains("vLLM streaming response timed out", StringComparison.Ordinal);

    private static string GetRoleLabel(string role)
        => role.Trim().ToLowerInvariant() switch
        {
            "system" => "System",
            "assistant" => "Assistant",
            _ => "User"
        };

    public sealed record ConversationTurnResult(
        string AssistantText,
        IReadOnlyList<VllmChatMessage> Conversation,
        int Tokens,
        TimeSpan Elapsed,
        double TokensPerSecond,
        bool UsedPromptFallback);
}





