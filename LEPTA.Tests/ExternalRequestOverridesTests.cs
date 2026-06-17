using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LEPTA.Shared.Services;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class ExternalRequestOverridesTests
{
    [Test]
    public async Task CompleteChatAsync_AppliesCustomAuthHeaderAndExtraBodyMergeAsync()
    {
        var overrides = new ExternalRequestOverrides
        {
            ApiKey = "secret-key",
            AuthHeaderName = "api-key",
            AuthHeaderScheme = "Bearer",
            Headers =
            {
                ["HTTP-Referer"] = "https://example.com",
                ["X-Title"] = "LEPTA"
            },
            ExtraBody =
            {
                ["user"] = JsonDocument.Parse("\"u-123\"").RootElement.Clone(),
                ["frequency_penalty"] = JsonDocument.Parse("0.3").RootElement.Clone()
            }
        };

        var capture = new RequestCapture();
        var handler = new RecordingHandler(capture, BuildChatCompletionResponse("hello"));
        var client = new VllmChatCompletionClient(new HttpClient(handler));

        await client.CompleteChatAsync(
            "http://localhost:8512",
            "test-model",
            [new VllmChatMessage("user", "ping")],
            requestOverrides: overrides);

        // Custom auth header carries the bare key (no Bearer prefix), Authorization untouched.
        Assert.That(capture.Authorization, Is.Null);
        Assert.That(capture.Headers["api-key"], Is.EqualTo("secret-key"));
        Assert.That(capture.Headers["HTTP-Referer"], Is.EqualTo("https://example.com"));
        Assert.That(capture.Headers["X-Title"], Is.EqualTo("LEPTA"));

        var body = JsonDocument.Parse(capture.Body!);
        Assert.That(body.RootElement.GetProperty("user").GetString(), Is.EqualTo("u-123"));
        Assert.That(body.RootElement.GetProperty("frequency_penalty").GetDouble(), Is.EqualTo(0.3));
        Assert.That(body.RootElement.GetProperty("model").GetString(), Is.EqualTo("test-model"));
    }

    [Test]
    public async Task CompleteChatAsync_AppliesBearerAuthorizationByDefaultAsync()
    {
        var overrides = new ExternalRequestOverrides { ApiKey = "sk-test" };

        var capture = new RequestCapture();
        var handler = new RecordingHandler(capture, BuildChatCompletionResponse("ok"));
        var client = new VllmChatCompletionClient(new HttpClient(handler));

        await client.CompleteChatAsync(
            "http://localhost:8512",
            "m",
            [new VllmChatMessage("user", "hi")],
            requestOverrides: overrides);

        Assert.That(capture.Authorization!.Scheme, Is.EqualTo("Bearer"));
        Assert.That(capture.Authorization.Parameter, Is.EqualTo("sk-test"));
    }

    [Test]
    public void VllmServerConfigurationStore_RoundTripsRequestOverrides()
    {
        using var sandbox = new TempDir();
        var paths = new AppDataPaths(sandbox.Path);
        var store = new VllmServerConfigurationStore(paths);
        var document = new VllmServerConfigurationsDocument
        {
            Servers =
            [
                new VllmServerConfiguration
                {
                    Id = "ext-1",
                    Name = "OpenRouter",
                    UseExistingHttpServer = true,
                    HttpServerAddress = "https://openrouter.ai/api",
                    RequestOverrides = new ExternalRequestOverrides
                    {
                        ApiKey = "sk-or",
                        AuthHeaderName = "Authorization",
                        AuthHeaderScheme = "Bearer",
                        Headers = { ["HTTP-Referer"] = "https://example.com" },
                        ExtraBody = { ["user"] = JsonDocument.Parse("\"u\"").RootElement.Clone() }
                    }
                }
            ]
        };

        store.Save(document);
        var loaded = store.Load();

        var server = loaded.Value.Servers[0];
        Assert.That(server.ApiKey, Is.EqualTo("sk-or"));
        Assert.That(server.RequestOverrides.AuthHeaderScheme, Is.EqualTo("Bearer"));
        Assert.That(server.RequestOverrides.Headers["HTTP-Referer"], Is.EqualTo("https://example.com"));
        Assert.That(server.RequestOverrides.ExtraBody["user"].GetString(), Is.EqualTo("u"));
        // The legacy top-level ApiKey must NOT be re-emitted; RequestOverrides is the canonical form.
        var persisted = JsonDocument.Parse(File.ReadAllText(paths.ModelConfigurationsFilePath));
        var persistedServer = persisted.RootElement.GetProperty("Servers").EnumerateArray().First();
        Assert.That(persistedServer.TryGetProperty("ApiKey", out _), Is.False, "legacy top-level ApiKey must not be serialized");
        Assert.That(persistedServer.GetProperty("RequestOverrides").GetProperty("ApiKey").GetString(), Is.EqualTo("sk-or"));
    }

    [Test]
    public void VllmServerConfigurationStore_MigratesLegacyTopLevelApiKey()
    {
        using var sandbox = new TempDir();
        var paths = new AppDataPaths(sandbox.Path);
        Directory.CreateDirectory(paths.ModelsDirectory);
        File.WriteAllText(
            paths.ModelConfigurationsFilePath,
            "{\"SchemaVersion\":1,\"Servers\":[{\"Id\":\"legacy\",\"Name\":\"Legacy\",\"ApiKey\":\"sk-legacy\",\"UseExistingHttpServer\":true,\"HttpServerAddress\":\"https://openrouter.ai/api\"}]}");

        var store = new VllmServerConfigurationStore(paths);
        var loaded = store.Load();

        Assert.That(loaded.Value.Servers[0].ApiKey, Is.EqualTo("sk-legacy"));
        Assert.That(loaded.Value.Servers[0].RequestOverrides.ApiKey, Is.EqualTo("sk-legacy"));
    }

    private static HttpResponseMessage BuildChatCompletionResponse(string text)
    {
        var json = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = text } } },
            usage = new { completion_tokens = 1 }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RequestCapture
    {
        public AuthenticationHeaderValue? Authorization { get; set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Body { get; set; }
    }

    private sealed class RecordingHandler(RequestCapture capture, HttpResponseMessage response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read everything now: HttpClient disposes the request (and its content) after SendAsync returns.
            capture.Authorization = request.Headers.Authorization;
            foreach (var header in request.Headers)
            {
                capture.Headers[header.Key] = string.Join(",", header.Value);
            }
            if (request.Content is not null)
            {
                capture.Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return response;
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LEPTA.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
