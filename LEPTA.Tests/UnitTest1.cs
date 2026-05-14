using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
[NonParallelizable]
public sealed class VllmCompletionIntegrationTests
{
    [Test]
    [Category("Unit")]
    public async Task ConversationService_SendAsync_WithOpenAiPayload_ReturnsAssistantText()
    {
        var logger = new TestLeptaLogger();
        using var http = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "choices": [
                    {
                      "message": {
                        "content": "LEPTA keeps chat readable."
                      }
                    }
                  ],
                  "usage": {
                    "completion_tokens": 7
                  }
                }
                """,
                Encoding.UTF8,
                "application/json")
        }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var result = await service.SendAsync(
            "http://localhost:8512",
            "Qwen3.5-9B-local",
            [],
            "Reply with five words.");

        Assert.That(result.AssistantText, Is.EqualTo("LEPTA keeps chat readable."));
        Assert.That(result.Tokens, Is.EqualTo(7));
        Assert.That(result.UsedPromptFallback, Is.False);
        Assert.That(logger.Entries.Any(entry => entry.Contains("Preparing chat completion request", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    [Category("Integration")]
    public async Task CompleteText_FromRunningVllmServer_ReturnsNonEmptyResponse()
    {
        var baseUrl = Environment.GetEnvironmentVariable("VLLM_BASE_URL") ?? "http://localhost:8512";

        using HttpClient http = new()
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        TestContext.Progress.WriteLine($"[Integration] Checking vLLM health at {baseUrl}/health");
        await EnsureServerReachableAsync(http, baseUrl);

        TestContext.Progress.WriteLine($"[Integration] Resolving first model via {baseUrl}/v1/models");
        var model = await ResolveFirstModelIdAsync(http, baseUrl);
        var request = new
        {
            model,
            prompt = "Complete this sentence in one short line: LEPTA helps teams",
            max_tokens = 8,
            temperature = 0.2
        };

        TestContext.Progress.WriteLine($"[Integration] POST {baseUrl.TrimEnd('/')}/v1/completions model={model}");
        using var completionResponse = await http.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/v1/completions", request);
        var rawBody = await completionResponse.Content.ReadAsStringAsync();
        TestContext.Progress.WriteLine($"[Integration] /v1/completions status={(int)completionResponse.StatusCode} bodyLength={rawBody.Length}");

        Assert.That(
            completionResponse.IsSuccessStatusCode,
            Is.True,
            $"Completion request failed with status {(int)completionResponse.StatusCode}. Body: {rawBody}");

        using var payload = JsonDocument.Parse(rawBody);
        var generatedText = payload.RootElement
            .GetProperty("choices")[0]
            .GetProperty("text")
            .GetString();

        Assert.That(generatedText, Is.Not.Null.And.Not.Empty, "vLLM returned an empty completion text.");
    }

    [Test]
    [Category("Integration")]
    public async Task CompleteChat_FromRunningVllmServer_ReturnsNonEmptyResponse()
    {
        var baseUrl = Environment.GetEnvironmentVariable("VLLM_BASE_URL") ?? "http://localhost:8512";

        using HttpClient http = new()
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        var logger = new TestLeptaLogger();
        TestContext.Progress.WriteLine($"[Integration] Checking vLLM health at {baseUrl}/health");
        await EnsureServerReachableAsync(http, baseUrl);

        TestContext.Progress.WriteLine($"[Integration] Resolving first model via {baseUrl}/v1/models");
        var model = await ResolveFirstModelIdAsync(http, baseUrl);
        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var result = await service.SendAsync(
            baseUrl,
            model,
            [],
            "Reply with one short sentence about LEPTA.",
            maxTokens: 40,
            temperature: 0.2);

        Assert.That(result.AssistantText, Is.Not.Null.And.Not.Empty, "vLLM returned an empty chat completion text.");
        Assert.That(logger.Entries.Any(entry => entry.Contains("Preparing chat completion request", StringComparison.Ordinal)), Is.True);
    }

    private static async Task EnsureServerReachableAsync(HttpClient http, string baseUrl)
    {
        HttpResponseMessage healthResponse;
        try
        {
            healthResponse = await http.GetAsync($"{baseUrl.TrimEnd('/')}/health");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"vLLM server is not reachable at {baseUrl}. Start docker image from LEPTA.vLLM/dev/dockerfile.vLLM-Dev first.",
                exception);
        }

        Assert.That(
            healthResponse.IsSuccessStatusCode,
            Is.True,
            $"vLLM server is not reachable at {baseUrl}. Start docker image from LEPTA.vLLM/dev/dockerfile.vLLM-Dev first.");
    }

    private static async Task<string> ResolveFirstModelIdAsync(HttpClient http, string baseUrl)
    {
        using var modelsResponse = await http.GetAsync($"{baseUrl.TrimEnd('/')}/v1/models");
        modelsResponse.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync());
        var data = payload.RootElement.GetProperty("data");
        Assert.That(data.GetArrayLength(), Is.GreaterThan(0), "vLLM returned no models from /v1/models.");

        var modelId = data[0].GetProperty("id").GetString();
        Assert.That(modelId, Is.Not.Null.And.Not.Empty, "First model id from /v1/models is empty.");
        return modelId!;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
