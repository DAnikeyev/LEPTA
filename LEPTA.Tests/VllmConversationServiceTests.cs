using System.Net;
using System.Text;
using System.Text.Json;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class VllmConversationServiceTests
{
    [Test]
    public async Task SendAsync_IncludesSystemPromptAndConversationHistory()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "Ready for the next step."
                          }
                        }
                      ],
                      "usage": {
                        "completion_tokens": 5
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var logger = new TestLeptaLogger();
        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var result = await service.SendAsync(
            "http://localhost:8512",
            "Qwen3.5-9B-local",
            [new VllmChatMessage("assistant", "Earlier reply.")],
            "What should LEPTA do next?",
            "Keep it brief.");

        Assert.That(result.AssistantText, Is.EqualTo("Ready for the next step."));
        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.RequestUri!.ToString(), Is.EqualTo("http://localhost:8512/v1/chat/completions"));
        Assert.That(capturedBody, Is.Not.Null);

        using var payload = JsonDocument.Parse(capturedBody!);
        var messages = payload.RootElement.GetProperty("messages");
        var chatTemplateKwargs = payload.RootElement.GetProperty("chat_template_kwargs");
        Assert.That(messages.GetArrayLength(), Is.EqualTo(3));
        Assert.That(messages[0].GetProperty("role").GetString(), Is.EqualTo("system"));
        Assert.That(messages[0].GetProperty("content").GetString(), Is.EqualTo("Keep it brief."));
        Assert.That(messages[1].GetProperty("role").GetString(), Is.EqualTo("assistant"));
        Assert.That(messages[2].GetProperty("role").GetString(), Is.EqualTo("user"));
        Assert.That(chatTemplateKwargs.GetProperty("enable_thinking").GetBoolean(), Is.False);
        Assert.That(result.Conversation.Select(message => message.Role), Is.EqualTo(["assistant", "user", "assistant"]));
        Assert.That(logger.Entries.Any(entry => entry.Contains("POST http://localhost:8512/v1/chat/completions", StringComparison.Ordinal)), Is.True);
        Assert.That(logger.Entries.Any(entry => entry.Contains("Sending conversational turn", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task SendAsync_FallsBackToPromptCompletion_WhenChatCompletionIsRejected()
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
            Assert.That(body, Does.Contain("Reply as the assistant to the latest user message"));
            Assert.That(body, Does.Contain("System instruction:"));
            Assert.That(body, Does.Contain("User:"));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [
                        {
                          "text": "LEPTA keeps the workflow moving."
                        }
                      ],
                      "usage": {
                        "completion_tokens": 6
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var logger = new TestLeptaLogger();
        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var result = await service.SendAsync(
            "http://localhost:8512",
            "Qwen3.5-9B-local",
            [],
            "Say one sentence about LEPTA.");

        Assert.That(requestedPaths, Is.EqualTo(["/v1/chat/completions", "/v1/completions"]));
        Assert.That(result.AssistantText, Is.EqualTo("LEPTA keeps the workflow moving."));
        Assert.That(result.UsedPromptFallback, Is.True);
        Assert.That(result.Conversation.Select(message => message.Role), Is.EqualTo(["user", "assistant"]));
        Assert.That(logger.Entries.Any(entry => entry.Contains("Falling back to prompt completion", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task SendAsync_ReadsStructuredChatContentParts()
    {
        using var http = new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "choices": [
                    {
                      "message": {
                        "content": [
                          { "type": "text", "text": "Ready" },
                          { "type": "text", "text": " to help." }
                        ]
                      }
                    }
                  ],
                  "usage": {
                    "completion_tokens": 4
                  }
                }
                """,
                Encoding.UTF8,
                "application/json")
        })))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var service = new VllmConversationService(new VllmChatCompletionClient(http));
        var result = await service.SendAsync(
            "http://localhost:8512",
            "Qwen3.5-9B-local",
            [],
            "What should LEPTA do next?");

        Assert.That(result.AssistantText, Is.EqualTo("Ready to help."));
    }

    [Test]
    public async Task StreamConversationAsync_StreamsAssistantTextAndPreservesConversationHistory()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var streamed = new StringBuilder();

        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    data: {"choices":[{"delta":{"content":"Ready"}}]}

                    data: {"choices":[{"delta":{"content":" to help."}}]}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream")
            };
        }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var logger = new TestLeptaLogger();
        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var result = await service.StreamConversationAsync(
            "http://localhost:8512",
            "Qwen3.5-9B-local",
            [new VllmChatMessage("assistant", "Earlier reply.")],
            "What should LEPTA do next?",
            token => streamed.Append(token),
            "Keep it brief.");

        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.RequestUri!.ToString(), Is.EqualTo("http://localhost:8512/v1/chat/completions"));
        Assert.That(capturedBody, Is.Not.Null);
        Assert.That(streamed.ToString(), Is.EqualTo("Ready to help."));
        Assert.That(result.AssistantText, Is.EqualTo("Ready to help."));
        Assert.That(result.UsedPromptFallback, Is.False);

        using var payload = JsonDocument.Parse(capturedBody!);
        var messages = payload.RootElement.GetProperty("messages");
        var chatTemplateKwargs = payload.RootElement.GetProperty("chat_template_kwargs");
        Assert.That(messages.GetArrayLength(), Is.EqualTo(3));
        Assert.That(messages[0].GetProperty("role").GetString(), Is.EqualTo("system"));
        Assert.That(messages[1].GetProperty("role").GetString(), Is.EqualTo("assistant"));
        Assert.That(messages[2].GetProperty("role").GetString(), Is.EqualTo("user"));
        Assert.That(chatTemplateKwargs.GetProperty("enable_thinking").GetBoolean(), Is.False);
        Assert.That(result.Conversation.Select(message => message.Role), Is.EqualTo(["assistant", "user", "assistant"]));
        Assert.That(logger.Entries.Any(entry => entry.Contains("Streaming conversational turn", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void StreamConversationAsync_ThrowsOperationCanceledException_WhenStreamingIsCancelled()
    {
        using var http = new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new DelayedStreamContent(
                TimeSpan.FromMilliseconds(250),
                "data: {\"choices\":[{\"delta\":{\"content\":\"Late chunk\"}}]}\n\ndata: [DONE]\n\n",
                "text/event-stream")
        })))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var client = new VllmChatCompletionClient(
            http,
            streamFirstTokenTimeout: TimeSpan.FromSeconds(5),
            streamIdleTimeout: TimeSpan.FromSeconds(5));
        var service = new VllmConversationService(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));

        Assert.That(
            async () => await service.StreamConversationAsync(
                "http://localhost:8512",
                "Qwen3.5-9B-local",
                [],
                "Say one sentence about LEPTA.",
                cancellationToken: cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task StreamConversationAsync_FallsBackToPromptCompletion_WhenStreamingChatIsRejected()
    {
        var requestedPaths = new List<string>();
        var streamed = new StringBuilder();

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
            Assert.That(body, Does.Contain("Reply as the assistant to the latest user message"));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [
                        {
                          "text": "LEPTA keeps the workflow moving."
                        }
                      ],
                      "usage": {
                        "completion_tokens": 6
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var logger = new TestLeptaLogger();
        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var result = await service.StreamConversationAsync(
            "http://localhost:8512",
            "Qwen3.5-9B-local",
            [],
            "Say one sentence about LEPTA.",
            token => streamed.Append(token));

        Assert.That(requestedPaths, Is.EqualTo(["/v1/chat/completions", "/v1/completions"]));
        Assert.That(streamed.ToString(), Is.EqualTo("LEPTA keeps the workflow moving."));
        Assert.That(result.AssistantText, Is.EqualTo("LEPTA keeps the workflow moving."));
        Assert.That(result.UsedPromptFallback, Is.True);
        Assert.That(result.Conversation.Select(message => message.Role), Is.EqualTo(["user", "assistant"]));
        Assert.That(logger.Entries.Any(entry => entry.Contains("Falling back to prompt completion", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task StreamConversationAsync_ReadsStructuredStreamingContentParts()
    {
        var streamed = new StringBuilder();

        using var http = new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                data: {"choices":[{"delta":{"content":[{"type":"text","text":"Ready"},{"type":"text","text":" to help."}]}}]}

                data: [DONE]

                """,
                Encoding.UTF8,
                "text/event-stream")
        })))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var service = new VllmConversationService(new VllmChatCompletionClient(http));
        var result = await service.StreamConversationAsync(
            "http://localhost:8512",
            "Qwen3.5-9B-local",
            [],
            "What should LEPTA do next?",
            token => streamed.Append(token));

        Assert.That(streamed.ToString(), Is.EqualTo("Ready to help."));
        Assert.That(result.AssistantText, Is.EqualTo("Ready to help."));
    }

    [Test]
    public async Task StreamConversationAsync_ReadsStreamingReasoningChunks_WhenContentIsEmpty()
    {
        var streamed = new StringBuilder();

        using var http = new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                data: {"choices":[{"delta":{"role":"assistant","content":""}}]}

                data: {"choices":[{"delta":{"reasoning":"Ready"}}]}

                data: {"choices":[{"delta":{"reasoning":" to help."}}]}

                data: [DONE]

                """,
                Encoding.UTF8,
                "text/event-stream")
        })))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var service = new VllmConversationService(new VllmChatCompletionClient(http));
        var result = await service.StreamConversationAsync(
            "http://localhost:8512",
            "Qwen3.5-9B-local",
            [],
            "What should LEPTA do next?",
            token => streamed.Append(token));

        Assert.That(streamed.ToString(), Is.EqualTo("Ready to help."));
        Assert.That(result.AssistantText, Is.EqualTo("Ready to help."));
        Assert.That(result.UsedPromptFallback, Is.False);
    }

    [Test]
    public async Task StreamConversationAsync_FallsBackToNonStreamingChat_WhenStreamCompletesWithoutContent()
    {
        var requestedPaths = new List<string>();
        var requestBodies = new List<string>();

        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            requestBodies.Add(await request.Content!.ReadAsStringAsync());

            if (requestBodies.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        data: {"choices":[{"delta":{"role":"assistant","content":""}}]}

                        data: [DONE]

                        """,
                        Encoding.UTF8,
                        "text/event-stream")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "Ready from non-streaming chat."
                          }
                        }
                      ],
                      "usage": {
                        "completion_tokens": 5
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var logger = new TestLeptaLogger();
        var service = new VllmConversationService(new VllmChatCompletionClient(http, logger), logger);
        var result = await service.StreamConversationAsync(
            "http://localhost:8512",
            "Qwen3.5-9B-local",
            [],
            "Say one sentence about LEPTA.");

        Assert.That(requestedPaths, Is.EqualTo(["/v1/chat/completions", "/v1/chat/completions"]));
        Assert.That(requestBodies[0], Does.Contain("\"stream\":true"));
        Assert.That(requestBodies[1], Does.Not.Contain("\"stream\":true"));
        Assert.That(result.AssistantText, Is.EqualTo("Ready from non-streaming chat."));
        Assert.That(result.UsedPromptFallback, Is.False);
        Assert.That(logger.Entries.Any(entry => entry.Contains("Retrying with non-streaming chat completion", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task StreamConversationAsync_FallsBackToNonStreamingChat_WhenStreamingProducesNoUsableContentAfterDelay()
    {
        var requestedPaths = new List<string>();

        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            _ = await request.Content!.ReadAsStringAsync();

            if (requestedPaths.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new DelayedStreamContent(TimeSpan.FromMilliseconds(150), "data: [DONE]\n\n", "text/event-stream")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "Recovered through non-streaming chat."
                          }
                        }
                      ],
                      "usage": {
                        "completion_tokens": 6
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var logger = new TestLeptaLogger();
        var client = new VllmChatCompletionClient(
            http,
            logger,
            streamFirstTokenTimeout: TimeSpan.FromMilliseconds(50),
            streamIdleTimeout: TimeSpan.FromMilliseconds(50));
        var service = new VllmConversationService(client, logger);
        var result = await service.StreamConversationAsync(
            "http://localhost:8512",
            "Qwen3.5-9B-local",
            [],
            "Say one sentence about LEPTA.");

        Assert.That(requestedPaths, Is.EqualTo(["/v1/chat/completions", "/v1/chat/completions"]));
        Assert.That(result.AssistantText, Is.EqualTo("Recovered through non-streaming chat."));
        Assert.That(result.UsedPromptFallback, Is.False);
        Assert.That(logger.Entries.Any(entry => entry.Contains("Retrying with non-streaming chat completion", StringComparison.Ordinal)), Is.True);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }

    private sealed class DelayedStreamContent : HttpContent
    {
        private readonly TimeSpan delay;
        private readonly string payload;
        private readonly string leadingPayload;

        public DelayedStreamContent(TimeSpan delay, string payload, string mediaType, string leadingPayload = "")
        {
            this.delay = delay;
            this.payload = payload;
            this.leadingPayload = leadingPayload;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
            if (!string.IsNullOrEmpty(leadingPayload))
            {
                await writer.WriteAsync(leadingPayload);
                await writer.FlushAsync();
            }

            await Task.Delay(delay);
            await writer.WriteAsync(payload);
            await writer.FlushAsync();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => WriteToStreamAsync(stream, cancellationToken);

        private async Task WriteToStreamAsync(Stream stream, CancellationToken cancellationToken)
        {
            await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
            if (!string.IsNullOrEmpty(leadingPayload))
            {
                await writer.WriteAsync(leadingPayload.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }

            await Task.Delay(delay, cancellationToken);
            await writer.WriteAsync(payload.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
    }
}



