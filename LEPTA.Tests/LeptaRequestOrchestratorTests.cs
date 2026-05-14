using System.Net;
using System.Text;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class LeptaRequestOrchestratorTests
{
    [Test]
    public void BuildPrompt_UsesClipboardTailAndInstructions()
    {
        var clipboard = new string('A', LeptaRequestOrchestrator.ClipboardTailLimit + 25);

        var prompt = LeptaRequestOrchestrator.BuildPrompt(
            clipboard,
            "General guidance",
            "Panel-specific instruction");

        Assert.That(prompt, Does.Contain("General instruction:"));
        Assert.That(prompt, Does.Contain("General guidance"));
        Assert.That(prompt, Does.Contain("Panel instruction:"));
        Assert.That(prompt, Does.Contain("Panel-specific instruction"));
        Assert.That(prompt, Does.Contain(new string('A', LeptaRequestOrchestrator.ClipboardTailLimit)));
        Assert.That(prompt, Does.Not.Contain(new string('A', LeptaRequestOrchestrator.ClipboardTailLimit + 1)));
    }

    [Test]
    public async Task GenerateForPanelsAsync_StreamsAndCollectsPanelResponses()
    {
        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
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
            "Clipboard",
            "General",
            [
                new LeptaPanelRequest("A", "A instruction"),
                new LeptaPanelRequest("B", "B instruction")
            ],
            (index, token) =>
            {
                lock (streamedLock)
                {
                    streamed.Add((index, token));
                }
            });

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].Text, Is.EqualTo("Alpha part 1 Alpha part 2"));
        Assert.That(results[1].Text, Is.EqualTo("Beta part 1 Beta part 2"));
        Assert.That(streamed.Count, Is.EqualTo(4));
        Assert.That(streamed.Any(item => item.Index == 0 && item.Token.Contains("Alpha", StringComparison.Ordinal)), Is.True);
        Assert.That(streamed.Any(item => item.Index == 1 && item.Token.Contains("Beta", StringComparison.Ordinal)), Is.True);
        Assert.That(logger.Entries.Any(entry => entry.Contains("Generating 2 panel response(s)", StringComparison.Ordinal)), Is.True);
        Assert.That(logger.Entries.Any(entry => entry.Contains("Completed panel 'A' generation", StringComparison.Ordinal)), Is.True);
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
            Assert.That(body, Does.Contain("Return only the useful answer for this panel."));

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
            "Clipboard",
            "General",
            [new LeptaPanelRequest("A", "A instruction")],
            (index, token) => streamed.Add((index, token)));

        Assert.That(requestedPaths, Is.EqualTo(["/v1/chat/completions", "/v1/completions"]));
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Text, Is.EqualTo("Fallback panel response"));
        Assert.That(streamed, Is.EqualTo([(0, "Fallback panel response")]));
        Assert.That(logger.Entries.Any(entry => entry.Contains("Streaming chat rejected", StringComparison.Ordinal)), Is.True);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }
}
