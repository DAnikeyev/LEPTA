using System.Net;
using System.Text;
using System.Text.Json;
using LEPTA.Shared.Models;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class LeptaRequestOrchestratorTests
{
    [Test]
    public void BuildPrompt_UsesRequestedTemplateOrder()
    {
        var clipboard = new string('A', LeptaRequestOrchestrator.DocumentCharacterLimit + 25);

        var prompt = LeptaRequestOrchestrator.BuildPrompt(
            "System guidance",
            clipboard,
            "Global guidance",
            "Panel-specific instruction");

        Assert.That(prompt, Does.Contain("System Instructions:"));
        Assert.That(prompt, Does.Contain("System guidance"));
        Assert.That(prompt, Does.Contain("Global Instructions:"));
        Assert.That(prompt, Does.Contain("Global guidance"));
        Assert.That(prompt, Does.Contain("Text:"));
        Assert.That(prompt, Does.Contain("Request Instructions:"));
        Assert.That(prompt, Does.Contain("Panel Instructions:"));
        Assert.That(prompt, Does.Contain("Panel-specific instruction"));
        Assert.That(prompt, Does.Contain("Answer format: markdown."));
        Assert.That(prompt, Does.EndWith("Response:"));
        Assert.That(prompt, Does.Contain(new string('A', LeptaRequestOrchestrator.DocumentCharacterLimit)));
        Assert.That(prompt, Does.Not.Contain(new string('A', LeptaRequestOrchestrator.DocumentCharacterLimit + 1)));

        var expectedOrder = new[]
        {
            "System Instructions:",
            "Global Instructions:",
            "Text:",
            "Request Instructions:",
            "Panel Instructions:",
            "Response:"
        };

        var indices = expectedOrder
            .Select(section => prompt.IndexOf(section, StringComparison.Ordinal))
            .ToArray();

        Assert.That(indices, Has.All.GreaterThanOrEqualTo(0));
        Assert.That(indices, Is.Ordered);
    }

    [Test]
    public void BuildPrompt_TrimStart_KeepsNewestDocumentText()
    {
        var head = "HEAD-" + new string('H', 32);
        var middle = new string('M', LeptaRequestOrchestrator.DocumentCharacterLimit);
        var tail = "TAIL-" + new string('T', 32);

        var prompt = LeptaRequestOrchestrator.BuildPrompt(
            "System guidance",
            head + middle + tail,
            "Global guidance",
            "Panel-specific instruction",
            LeptaDocumentTrimMode.TrimStart);

        Assert.That(prompt, Does.Not.Contain(head));
        Assert.That(prompt, Does.Contain(tail));
    }

    [Test]
    public void BuildPrompt_TrimEnd_KeepsEarliestDocumentText()
    {
        var head = "HEAD-" + new string('H', 32);
        var middle = new string('M', LeptaRequestOrchestrator.DocumentCharacterLimit);
        var tail = "TAIL-" + new string('T', 32);

        var prompt = LeptaRequestOrchestrator.BuildPrompt(
            "System guidance",
            head + middle + tail,
            "Global guidance",
            "Panel-specific instruction",
            LeptaDocumentTrimMode.TrimEnd);

        Assert.That(prompt, Does.Contain(head));
        Assert.That(prompt, Does.Not.Contain(tail));
    }

    [Test]
    public void BuildSharedPromptPrefix_CapsTextSectionToEstimated6000Tokens()
    {
        var clipboard = new string('A', LeptaRequestOrchestrator.DocumentCharacterLimit + 123);

        var prompt = LeptaRequestOrchestrator.BuildSharedPromptPrefix(
            "System guidance",
            clipboard,
            "Global guidance");

        var textSection = ExtractSection(prompt, "Text", "Request Instructions");
        Assert.That(textSection.Length, Is.LessThanOrEqualTo(LeptaRequestOrchestrator.DocumentCharacterLimit));
    }

    [Test]
    public void BuildPrompt_UsesConfiguredDocumentTokenLimit()
    {
        const int documentTokenLimit = 12;
        var documentCharacterLimit = LeptaRequestOrchestrator.GetDocumentCharacterLimit(documentTokenLimit);
        var clipboard = "HEAD-" + new string('A', documentCharacterLimit) + "TAIL-" + new string('Z', 24);

        var prompt = LeptaRequestOrchestrator.BuildPrompt(
            "System guidance",
            clipboard,
            "Global guidance",
            "Panel-specific instruction",
            LeptaDocumentTrimMode.TrimEnd,
            documentTokenLimit);

        Assert.That(prompt, Does.Contain("HEAD-"));
        Assert.That(prompt, Does.Not.Contain("TAIL-"));
        var textSection = ExtractSection(prompt, "Text", "Request Instructions");
        Assert.That(textSection.Length, Is.LessThanOrEqualTo(documentCharacterLimit));
    }

    [Test]
    public void BuildPrompt_MermaidFormat_AddsHiddenPanelInstructions()
    {
        var prompt = LeptaRequestOrchestrator.BuildPrompt(
            "System guidance",
            "Clipboard",
            "Global guidance",
            "Show the architecture",
            panelFormat: LeptaPanelFormats.Mermaid);

        Assert.That(prompt, Does.Contain("Request Instructions:"));
        Assert.That(prompt, Does.Contain("Show the architecture"));
        Assert.That(prompt, Does.Contain("Panel Instructions:"));
        Assert.That(prompt, Does.Contain("Answer format: mermaid ONLY."));
    }

    [Test]
    public void BuildMermaidRepairPrompt_IncludesBrokenDiagramAndRenderError()
    {
        var prompt = LeptaRequestOrchestrator.BuildMermaidRepairPrompt(
            "```mermaid\ngraph TD\nA-->B\n```",
            "Parse error near node A");

        Assert.That(prompt, Does.Contain("Task:"));
        Assert.That(prompt, Does.Contain("Render Error:"));
        Assert.That(prompt, Does.Contain("Parse error near node A"));
        Assert.That(prompt, Does.Contain("Broken Mermaid:"));
        Assert.That(prompt, Does.Contain("graph TD"));
        Assert.That(prompt, Does.Not.Contain("```mermaid"));
        Assert.That(prompt, Does.Contain("Common classDiagram mistakes"));
        Assert.That(prompt, Does.Contain("Return only valid Mermaid source"));
    }

    [Test]
    public async Task RepairMermaidDiagramAsync_StripsMarkdownFenceFromAssistantReply()
    {
        string? requestBody = null;
        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "```mermaid\ngraph TD\nA[Quoted] --> B[Done]\n```"
                          }
                        }
                      ],
                      "usage": {
                        "completion_tokens": 8
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var logger = new TestLeptaLogger();
        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var orchestrator = new LeptaRequestOrchestrator(service, logger);

        var result = await orchestrator.RepairMermaidDiagramAsync(
            "http://localhost:8512",
            "test-model",
            "graph TD\nA --> B",
            "Parse error near A");

        Assert.That(result.Error, Is.Null);
        Assert.That(result.Text, Is.EqualTo("graph TD\nA[Quoted] --> B[Done]"));
        Assert.That(result.EstimatedVisibleTokenCount, Is.GreaterThan(0));
        Assert.That(requestBody, Is.Not.Null.And.Not.Empty);
        Assert.That(requestBody, Does.Contain("Repair the Mermaid diagram"));
        Assert.That(requestBody, Does.Contain("Parse error near A"));
        Assert.That(requestBody, Does.Contain("Return only valid Mermaid source"));
    }

    [Test]
    public async Task GenerateForPanelsAsync_StreamsAndCollectsPanelResponses_WhenSharedPrefillIsEnabled()
    {
        var requestBodies = new List<string>();
        var requestBodiesLock = new object();
        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            lock (requestBodiesLock)
            {
                requestBodies.Add(body);
            }
            if (body.Contains("Return only READY.", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "choices": [
                            {
                              "message": {
                                "content": "READY"
                              }
                            }
                          ],
                          "usage": {
                            "completion_tokens": 1
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            var responseText = body.Contains("A instruction", StringComparison.Ordinal)
                ? "Alpha"
                : "Beta";

            var streamBody =
                $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{responseText} part 1 \"}}}}]}}{Environment.NewLine}{Environment.NewLine}" +
                $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{responseText} part 2\"}}}}]}}{Environment.NewLine}{Environment.NewLine}" +
                $"data: [DONE]{Environment.NewLine}{Environment.NewLine}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streamBody, Encoding.UTF8, "text/event-stream")
            };
        }));

        var logger = new TestLeptaLogger();
        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var orchestrator = new LeptaRequestOrchestrator(service, logger);
        var streamed = new List<(int Index, string Token)>();
        var streamedLock = new object();

        var results = await orchestrator.GenerateForPanelsAsync(
            "http://localhost:8512",
            "test-model",
            "System",
            "Clipboard",
            "Global",
            [
                new LeptaPanelRequest("A", "A instruction"),
                new LeptaPanelRequest("B", "B instruction")
            ],
            warmSharedPrefix: true,
            onToken: (index, token) =>
            {
                lock (streamedLock)
                {
                    streamed.Add((index, token));
                }
            });

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].Text, Is.EqualTo("Alpha part 1 Alpha part 2"));
        Assert.That(results[1].Text, Is.EqualTo("Beta part 1 Beta part 2"));
        Assert.That(results[0].EstimatedVisibleTokenCount, Is.GreaterThan(0));
        Assert.That(results[1].EstimatedVisibleTokenCount, Is.GreaterThan(0));
        Assert.That(results[0].GenerationDuration, Is.Not.Null);
        Assert.That(results[1].GenerationDuration, Is.Not.Null);
        Assert.That(requestBodies, Has.Count.EqualTo(3));
        Assert.That(requestBodies[0], Does.Contain("Warm the shared prefix cache"));
        Assert.That(requestBodies[1], Does.Contain("System Instructions:"));
        Assert.That(requestBodies[1], Does.Contain("Global Instructions:"));
        Assert.That(requestBodies[1], Does.Contain("Text:"));
        Assert.That(requestBodies[1], Does.Contain("Request Instructions:"));
        Assert.That(requestBodies[1], Does.Contain("Panel Instructions:"));
        Assert.That(requestBodies[1], Does.Contain("Answer format: markdown."));
        Assert.That(requestBodies[1], Does.Contain("Response:"));
        Assert.That(requestBodies[2], Does.Contain("Response:"));

        using var warmupPayload = JsonDocument.Parse(requestBodies[0]);
        using var panelPayloadA = JsonDocument.Parse(requestBodies[1]);
        using var panelPayloadB = JsonDocument.Parse(requestBodies[2]);
        var warmupCacheSalt = warmupPayload.RootElement.GetProperty("cache_salt").GetString();
        Assert.That(warmupCacheSalt, Is.Not.Null.And.Not.Empty);
        Assert.That(panelPayloadA.RootElement.GetProperty("cache_salt").GetString(), Is.EqualTo(warmupCacheSalt));
        Assert.That(panelPayloadB.RootElement.GetProperty("cache_salt").GetString(), Is.EqualTo(warmupCacheSalt));
        Assert.That(streamed.Count, Is.EqualTo(4));
        Assert.That(streamed.Any(item => item.Index == 0 && item.Token.Contains("Alpha", StringComparison.Ordinal)), Is.True);
        Assert.That(streamed.Any(item => item.Index == 1 && item.Token.Contains("Beta", StringComparison.Ordinal)), Is.True);
        Assert.That(logger.Entries.Any(entry => entry.Contains("Generating 2 panel response(s)", StringComparison.Ordinal)), Is.True);
        Assert.That(logger.Entries.Any(entry => entry.Contains("Warming shared prompt prefix cache", StringComparison.Ordinal)), Is.True);
        Assert.That(logger.Entries.Any(entry => entry.Contains("Completed panel 'A' generation", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task GenerateForPanelsAsync_DoesNotWarmSharedPrefix_WhenSharedPrefillIsDisabled()
    {
        var requestBodies = new List<string>();
        var requestBodiesLock = new object();
        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            lock (requestBodiesLock)
            {
                requestBodies.Add(body);
            }

            var responseText = body.Contains("A instruction", StringComparison.Ordinal)
                ? "Alpha"
                : "Beta";

            var streamBody =
                $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{responseText}\"}}}}]}}{Environment.NewLine}{Environment.NewLine}" +
                $"data: [DONE]{Environment.NewLine}{Environment.NewLine}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streamBody, Encoding.UTF8, "text/event-stream")
            };
        }));

        var logger = new TestLeptaLogger();
        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var orchestrator = new LeptaRequestOrchestrator(service, logger);

        var results = await orchestrator.GenerateForPanelsAsync(
            "http://localhost:8512",
            "test-model",
            "System",
            "Clipboard",
            "Global",
            [
                new LeptaPanelRequest("A", "A instruction"),
                new LeptaPanelRequest("B", "B instruction")
            ]);

        Assert.That(results.Select(result => result.Text), Is.EqualTo(["Alpha", "Beta"]));
        Assert.That(requestBodies, Has.Count.EqualTo(2));
        Assert.That(requestBodies.All(body => !body.Contains("Warm the shared prefix cache", StringComparison.Ordinal)), Is.True);
        Assert.That(logger.Entries.Any(entry => entry.Contains("Warming shared prompt prefix cache", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task PrefillSharedPromptPrefixAsync_SendsSharedPrefixPromptWithRequestedCacheSaltAndMaxTokens()
    {
        string? requestBody = null;
        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "READY"
                          }
                        }
                      ],
                      "usage": {
                        "completion_tokens": 1
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var logger = new TestLeptaLogger();
        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var orchestrator = new LeptaRequestOrchestrator(service, logger);

        await orchestrator.PrefillSharedPromptPrefixAsync(
            "http://localhost:8512",
            "test-model",
            LeptaRequestOrchestrator.BuildSharedPromptPrefix("System", "Clipboard", "Global"),
            new VllmRequestOptions { CacheSalt = "prefill-salt" },
            maxTokens: 32);

        Assert.That(requestBody, Is.Not.Null.And.Not.Empty);
        Assert.That(requestBody, Does.Contain("System Instructions:"));
        Assert.That(requestBody, Does.Contain("Global Instructions:"));
        Assert.That(requestBody, Does.Contain("Text:"));
        Assert.That(requestBody, Does.Contain("Prefill the LEPTA clipboard cache"));
        using var payload = JsonDocument.Parse(requestBody!);
        Assert.That(payload.RootElement.GetProperty("max_tokens").GetInt32(), Is.EqualTo(32));
        Assert.That(payload.RootElement.GetProperty("cache_salt").GetString(), Is.EqualTo("prefill-salt"));
        Assert.That(logger.Entries.Any(entry => entry.Contains("Prefilling shared prompt prefix cache", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task GenerateForPanelsAsync_ReusesSuppliedSharedCacheSaltWithoutSendingAnotherWarmupRequest()
    {
        var requestBodies = new List<string>();
        var requestBodiesLock = new object();
        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            lock (requestBodiesLock)
            {
                requestBodies.Add(body);
            }

            var responseText = body.Contains("A instruction", StringComparison.Ordinal)
                ? "Alpha"
                : "Beta";
            var streamBody =
                $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{responseText}\"}}}}]}}{Environment.NewLine}{Environment.NewLine}" +
                $"data: [DONE]{Environment.NewLine}{Environment.NewLine}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streamBody, Encoding.UTF8, "text/event-stream")
            };
        }));

        var logger = new TestLeptaLogger();
        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var orchestrator = new LeptaRequestOrchestrator(service, logger);

        var results = await orchestrator.GenerateForPanelsAsync(
            "http://localhost:8512",
            "test-model",
            "System",
            "Clipboard",
            "Global",
            [
                new LeptaPanelRequest("A", "A instruction"),
                new LeptaPanelRequest("B", "B instruction")
            ],
            warmSharedPrefix: true,
            sharedCacheSalt: "prefill-salt",
            sharedPrefixAlreadyWarm: true);

        Assert.That(results.Select(result => result.Text), Is.EqualTo(["Alpha", "Beta"]));
        Assert.That(requestBodies, Has.Count.EqualTo(2));
        Assert.That(requestBodies.All(body => !body.Contains("Warm the shared prefix cache", StringComparison.Ordinal)), Is.True);
        Assert.That(requestBodies.All(body => body.Contains("prefill-salt", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task GenerateForPanelsAsync_FallsBackToCompletions_WhenChatStreamingIsRejected()
    {
        var requestedPaths = new List<string>();

        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync();

            if (request.RequestUri.AbsolutePath == "/v1/chat/completions")
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("chat template rejected the message payload", Encoding.UTF8, "text/plain")
                };
            }

            Assert.That(request.RequestUri.AbsolutePath, Is.EqualTo("/v1/completions"));
            Assert.That(body, Does.Contain("System Instructions:"));
            Assert.That(body, Does.Contain("Response:"));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [
                        {
                          "text": "Fallback panel response"
                        }
                      ],
                      "usage": {
                        "completion_tokens": 3
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var logger = new TestLeptaLogger();
        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var orchestrator = new LeptaRequestOrchestrator(service, logger);
        var streamed = new List<(int Index, string Token)>();

        var results = await orchestrator.GenerateForPanelsAsync(
            "http://localhost:8512",
            "test-model",
            "System",
            "Clipboard",
            "Global",
            [new LeptaPanelRequest("A", "A instruction")],
            (index, token) => streamed.Add((index, token)));

        Assert.That(requestedPaths, Is.EqualTo(["/v1/chat/completions", "/v1/completions"]));
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Text, Is.EqualTo("Fallback panel response"));
        Assert.That(streamed, Is.EqualTo([(0, "Fallback panel response")]));
        Assert.That(logger.Entries.Any(entry => entry.Contains("Streaming chat rejected", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task GenerateForPanelsAsync_SendsThinkingFlagAndTemperature_WhenRequested()
    {
        string? capturedBody = null;
        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    data: {"choices":[{"delta":{"content":"Done"}}]}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream")
            };
        }));

        var service = new VllmConversationService(new VllmChatCompletionClient(http));
        var orchestrator = new LeptaRequestOrchestrator(service);

        var results = await orchestrator.GenerateForPanelsAsync(
            "http://localhost:8512",
            "test-model",
            "System",
            "Clipboard",
            "Global",
            [new LeptaPanelRequest("A", "A instruction")],
            enableThinking: true,
            temperature: 0.65);

        Assert.That(results[0].Text, Is.EqualTo("Done"));
        Assert.That(capturedBody, Is.Not.Null);
        using var payload = JsonDocument.Parse(capturedBody!);
        Assert.That(
            payload.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean(),
            Is.True);
        Assert.That(payload.RootElement.GetProperty("temperature").GetDouble(), Is.EqualTo(0.65).Within(0.001));
    }

    [Test]
    public async Task GenerateForPanelsAsync_OmitsReasoningAndThinkTags_WhenThinkingEnabled()
    {
        var streamed = new List<(int Index, string Token)>();
        var openThink = string.Concat('<', "think", '>');
        var closeThink = string.Concat('<', '/', "think", '>');
        var content = openThink + "Still hidden" + closeThink + "Visible answer.";
        var reasoningLine = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { reasoning = "Hidden reasoning" } } } });
        var contentLine = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content } } } });
        var streamBody = $"data: {reasoningLine}\n\ndata: {contentLine}\n\ndata: [DONE]\n\n";
        using var http = new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(streamBody, Encoding.UTF8, "text/event-stream")
        })));

        var service = new VllmConversationService(new VllmChatCompletionClient(http));
        var orchestrator = new LeptaRequestOrchestrator(service);

        var results = await orchestrator.GenerateForPanelsAsync(
            "http://localhost:8512",
            "test-model",
            "System",
            "Clipboard",
            "Global",
            [new LeptaPanelRequest("A", "A instruction")],
            onToken: (index, token) => streamed.Add((index, token)),
            enableThinking: true);

        Assert.That(string.Concat(streamed.Select(item => item.Token)), Is.EqualTo("Visible answer."));
        Assert.That(results[0].Text, Is.EqualTo("Visible answer."));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }

    private static string ExtractSection(string prompt, string sectionName, string? nextSectionName)
    {
        var startMarker = $"{sectionName}:{Environment.NewLine}";
        var startIndex = prompt.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.That(startIndex, Is.GreaterThanOrEqualTo(0), $"Expected section '{sectionName}' in prompt.");
        startIndex += startMarker.Length;

        if (string.IsNullOrWhiteSpace(nextSectionName))
        {
            return prompt[startIndex..].TrimEnd();
        }

        var endMarker = $"{Environment.NewLine}{Environment.NewLine}{nextSectionName}:";
        var endIndex = prompt.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
        return endIndex >= 0
            ? prompt[startIndex..endIndex].TrimEnd()
            : prompt[startIndex..].TrimEnd();
    }
}
