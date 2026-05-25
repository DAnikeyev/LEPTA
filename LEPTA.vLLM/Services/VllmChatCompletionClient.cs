using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LEPTA.Shared.Diagnostics;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class VllmChatCompletionClient
{
    private static readonly TimeSpan DefaultStreamFirstTokenTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DefaultStreamIdleTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient httpClient;
    private readonly ILeptaLogger logger;
    private readonly TimeSpan streamFirstTokenTimeout;
    private readonly TimeSpan streamIdleTimeout;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public VllmChatCompletionClient(
        HttpClient? httpClient = null,
        ILeptaLogger? logger = null,
        TimeSpan? streamFirstTokenTimeout = null,
        TimeSpan? streamIdleTimeout = null)
    {
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        this.logger = logger ?? NullLeptaLogger.Instance;
        this.streamFirstTokenTimeout = streamFirstTokenTimeout ?? DefaultStreamFirstTokenTimeout;
        this.streamIdleTimeout = streamIdleTimeout ?? DefaultStreamIdleTimeout;
    }

    public Task<CompletionResult> CompleteAsync(
        string endpoint,
        string model,
        string prompt,
        int maxTokens = 200,
        CancellationToken cancellationToken = default)
        => CompleteAsync(endpoint, model, prompt, maxTokens, 0.0, cancellationToken);

    public async Task<CompletionResult> CompleteAsync(
        string endpoint,
        string model,
        string prompt,
        int maxTokens,
        double temperature,
        CancellationToken cancellationToken = default)
    {
        logger.Log(nameof(VllmChatCompletionClient), $"Preparing text completion request for model '{model}' at {endpoint.TrimEnd('/')}/v1/completions. promptLength={prompt.Length}, maxTokens={maxTokens}, temperature={temperature}.");
        var payload = new { model, prompt, max_tokens = maxTokens, temperature };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var (response, elapsed) = await PostAsync(endpoint, "/v1/completions", content, cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<CompletionResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Null response from vLLM.");

        var completionTokens = result.Usage?.CompletionTokens ?? 0;
        var tokensPerSecond = completionTokens > 0 && elapsed.TotalSeconds > 0
            ? completionTokens / elapsed.TotalSeconds
            : 0;
        var text = result.Choices?.FirstOrDefault()?.Text ?? string.Empty;

        logger.Log(nameof(VllmChatCompletionClient), $"Text completion finished for model '{model}'. completionTokens={completionTokens}, elapsedMs={elapsed.TotalMilliseconds:F0}, textLength={text.Length}.");
        return new CompletionResult(completionTokens, elapsed, tokensPerSecond, text);
    }

    public async Task<ChatCompletionResult> CompleteChatAsync(
        string endpoint,
        string model,
        IReadOnlyList<VllmChatMessage> messages,
        int maxTokens = 256,
        double temperature = 0.2,
        VllmRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new ArgumentException("At least one chat message is required.", nameof(messages));
        }

        logger.Log(
            nameof(VllmChatCompletionClient),
            $"Preparing chat completion request for model '{model}' at {endpoint.TrimEnd('/')}/v1/chat/completions. messageCount={messages.Count}, maxTokens={maxTokens}, temperature={temperature}, enableThinking={(requestOptions?.EnableThinking ?? false).ToString().ToLowerInvariant()}, cacheSaltPresent={(!string.IsNullOrWhiteSpace(requestOptions?.CacheSalt)).ToString().ToLowerInvariant()}.");
        var payload = BuildChatPayload(model, messages, maxTokens, temperature, stream: false, requestOptions);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var (response, elapsed) = await PostAsync(endpoint, "/v1/chat/completions", content, cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(responseBody);
        var completionTokens = TryGetCompletionTokens(document.RootElement);
        var tokensPerSecond = completionTokens > 0 && elapsed.TotalSeconds > 0
            ? completionTokens / elapsed.TotalSeconds
            : 0;

        var text = ExtractChatCompletionText(document.RootElement, requestOptions?.OmitReasoningFromOutput == true);
        logger.Log(nameof(VllmChatCompletionClient), $"Chat completion finished for model '{model}'. completionTokens={completionTokens}, elapsedMs={elapsed.TotalMilliseconds:F0}, textLength={text.Length}.");
        return new ChatCompletionResult(completionTokens, elapsed, tokensPerSecond, text);
    }

    public async IAsyncEnumerable<StreamChunk> StreamChatCompletionAsync(
        string endpoint,
        string model,
        IReadOnlyList<VllmChatMessage> messages,
        int maxTokens = 700,
        double temperature = 0.2,
        VllmRequestOptions? requestOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new ArgumentException("At least one chat message is required.", nameof(messages));
        }

        logger.Log(
            nameof(VllmChatCompletionClient),
            $"Preparing streaming chat request for model '{model}' at {endpoint.TrimEnd('/')}/v1/chat/completions. messageCount={messages.Count}, maxTokens={maxTokens}, temperature={temperature}, enableThinking={(requestOptions?.EnableThinking ?? false).ToString().ToLowerInvariant()}, cacheSaltPresent={(!string.IsNullOrWhiteSpace(requestOptions?.CacheSalt)).ToString().ToLowerInvariant()}.");
        var payload = BuildChatPayload(model, messages, maxTokens, temperature, stream: true, requestOptions);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{endpoint.TrimEnd('/')}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.Log(nameof(VllmChatCompletionClient), $"Streaming chat request failed with status {(int)response.StatusCode}. body={Truncate(errorBody, 300)}");
            throw new InvalidOperationException($"vLLM streaming request failed with status {(int)response.StatusCode}: {errorBody}");
        }

        logger.Log(nameof(VllmChatCompletionClient), $"Streaming chat request accepted for model '{model}'.");

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(contentStream);
        var omitReasoning = requestOptions?.OmitReasoningFromOutput == true;
        var receivedContentToken = false;
        while (true)
        {
            var timeout = receivedContentToken ? streamIdleTimeout : streamFirstTokenTimeout;
            var line = await ReadLineWithTimeoutAsync(reader, timeout, receivedContentToken, cancellationToken);
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payloadLine = line["data:".Length..].Trim();
            if (string.Equals(payloadLine, "[DONE]", StringComparison.Ordinal))
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(payloadLine))
            {
                continue;
            }

            using var document = JsonDocument.Parse(payloadLine);
            var token = ExtractStreamingChunkText(document.RootElement, omitReasoning);
            var completionTokens = TryGetStreamCompletionTokens(document.RootElement);
            if (!string.IsNullOrEmpty(token))
            {
                receivedContentToken = true;
                logger.Log(nameof(VllmChatCompletionClient), $"Streaming chat token received. tokenLength={token.Length}.");
                yield return new StreamChunk(token, completionTokens);
            }
            else if (completionTokens.HasValue)
            {
                yield return new StreamChunk(string.Empty, completionTokens);
            }
        }
    }

    public sealed record StreamChunk(string Text, int? CompletionTokens);

    public sealed record CompletionResult(int Tokens, TimeSpan Elapsed, double TokensPerSecond, string Text);
    public sealed record ChatCompletionResult(int Tokens, TimeSpan Elapsed, double TokensPerSecond, string Text);

    private sealed record CompletionResponse(
        [property: JsonPropertyName("usage")] UsageInfo? Usage,
        [property: JsonPropertyName("choices")] List<ChoiceInfo>? Choices);

    private sealed record UsageInfo(
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);

    private sealed record ChoiceInfo(
        [property: JsonPropertyName("text")] string Text);

    private async Task<(HttpResponseMessage Response, TimeSpan Elapsed)> PostAsync(
        string endpoint,
        string relativePath,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var normalizedEndpoint = endpoint.TrimEnd('/');
        var normalizedPath = relativePath.StartsWith('/') ? relativePath : $"/{relativePath}";
        logger.Log(nameof(VllmChatCompletionClient), $"POST {normalizedEndpoint}{normalizedPath}");
        var response = await httpClient.PostAsync($"{normalizedEndpoint}{normalizedPath}", content, cancellationToken);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.Log(nameof(VllmChatCompletionClient), $"POST {normalizedEndpoint}{normalizedPath} failed with status {(int)response.StatusCode}. body={Truncate(errorBody, 300)}");
            response.Dispose();
            throw new InvalidOperationException($"vLLM request to {normalizedPath} failed with status {(int)response.StatusCode}: {errorBody}");
        }

        logger.Log(nameof(VllmChatCompletionClient), $"POST {normalizedEndpoint}{normalizedPath} succeeded in {sw.Elapsed.TotalMilliseconds:F0}ms.");

        return (response, sw.Elapsed);
    }

    private static string Truncate(string value, int limit)
        => value.Length <= limit
            ? value
            : $"{value[..limit]}...";

    private static Dictionary<string, object?> BuildChatPayload(
        string model,
        IReadOnlyList<VllmChatMessage> messages,
        int maxTokens,
        double temperature,
        bool stream,
        VllmRequestOptions? requestOptions)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(message => new { role = message.Role, content = message.Content }).ToArray(),
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["chat_template_kwargs"] = new { enable_thinking = requestOptions?.EnableThinking ?? false }
        };

        if (stream)
        {
            payload["stream"] = true;
        }

        if (!string.IsNullOrWhiteSpace(requestOptions?.CacheSalt))
        {
            payload["cache_salt"] = requestOptions.CacheSalt;
        }

        return payload;
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(
        StreamReader reader,
        TimeSpan timeout,
        bool receivedContentToken,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await reader.ReadLineAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var phase = receivedContentToken ? "after receiving partial content" : "while waiting for the first content token";
            throw new InvalidOperationException($"vLLM streaming response timed out {phase}.");
        }
    }

    private static int? TryGetStreamCompletionTokens(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("completion_tokens", out var completionTokens)
            || completionTokens.ValueKind != JsonValueKind.Number
            || !completionTokens.TryGetInt32(out var value))
        {
            return null;
        }

        return value;
    }

    private static int TryGetCompletionTokens(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("completion_tokens", out var completionTokens)
            || completionTokens.ValueKind != JsonValueKind.Number
            || !completionTokens.TryGetInt32(out var value))
        {
            return 0;
        }

        return value;
    }

    private static string ExtractChatCompletionText(JsonElement root, bool omitReasoning)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var choice in choices.EnumerateArray())
        {
            AppendChoiceText(builder, choice, includeMessage: true, includeDelta: false, omitReasoning);
        }

        return builder.ToString();
    }

    private static string ExtractStreamingChunkText(JsonElement root, bool omitReasoning)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var choice in choices.EnumerateArray())
        {
            AppendChoiceText(builder, choice, includeMessage: false, includeDelta: true, omitReasoning);
        }

        return builder.ToString();
    }

    private static void AppendChoiceText(StringBuilder builder, JsonElement choice, bool includeMessage, bool includeDelta, bool omitReasoning)
    {
        if (choice.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (includeMessage && choice.TryGetProperty("message", out var message))
        {
            builder.Append(ExtractMessageText(message, omitReasoning));
        }

        if (includeDelta && choice.TryGetProperty("delta", out var delta))
        {
            builder.Append(ExtractMessageText(delta, omitReasoning));
        }

        if (choice.TryGetProperty("text", out var text))
        {
            builder.Append(ExtractTextValue(text));
        }
    }

    private static string ExtractMessageText(JsonElement message, bool omitReasoning)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (message.TryGetProperty("content", out var content))
        {
            var contentText = ExtractTextValue(content);
            if (!string.IsNullOrEmpty(contentText))
            {
                return contentText;
            }
        }

        if (!omitReasoning && message.TryGetProperty("reasoning_content", out var reasoningContent))
        {
            var reasoningContentText = ExtractTextValue(reasoningContent);
            if (!string.IsNullOrEmpty(reasoningContentText))
            {
                return reasoningContentText;
            }
        }

        if (!omitReasoning && message.TryGetProperty("reasoning", out var reasoning))
        {
            var reasoningText = ExtractTextValue(reasoning);
            if (!string.IsNullOrEmpty(reasoningText))
            {
                return reasoningText;
            }
        }

        return string.Empty;
    }

    private static string ExtractTextValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Array => ExtractTextFromArray(value),
            JsonValueKind.Object => ExtractTextFromObject(value),
            _ => string.Empty
        };
    }

    private static string ExtractTextFromArray(JsonElement value)
    {
        var builder = new StringBuilder();
        foreach (var item in value.EnumerateArray())
        {
            builder.Append(ExtractTextValue(item));
        }

        return builder.ToString();
    }

    private static string ExtractTextFromObject(JsonElement value)
    {
        if (value.TryGetProperty("text", out var text))
        {
            return ExtractTextValue(text);
        }

        if (value.TryGetProperty("content", out var content))
        {
            var contentText = ExtractTextValue(content);
            if (!string.IsNullOrEmpty(contentText))
            {
                return contentText;
            }
        }

        if (value.TryGetProperty("reasoning_content", out var reasoningContent))
        {
            var reasoningContentText = ExtractTextValue(reasoningContent);
            if (!string.IsNullOrEmpty(reasoningContentText))
            {
                return reasoningContentText;
            }
        }

        if (value.TryGetProperty("reasoning", out var reasoning))
        {
            var reasoningText = ExtractTextValue(reasoning);
            if (!string.IsNullOrEmpty(reasoningText))
            {
                return reasoningText;
            }
        }

        return string.Empty;
    }
}

