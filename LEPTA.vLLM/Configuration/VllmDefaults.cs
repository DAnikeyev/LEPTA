using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Configuration;

public static class VllmDefaults
{
    /// <summary>Canonical OpenRouter API endpoint used by the seed profile and the quick-fill button.</summary>
    public const string OpenRouterEndpoint = "https://openrouter.ai/api";

    /// <summary>
    /// Extra HTTP headers OpenRouter recommends on every request (<c>HTTP-Referer</c> + <c>X-Title</c>).
    /// Shared by the seed profile and the "Apply OpenRouter defaults" quick-fill so the two never drift.
    /// </summary>
    public static IReadOnlyDictionary<string, string> OpenRouterRecommendedHeaders { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTP-Referer"] = "https://github.com/lepta",
            ["X-Title"] = "LEPTA"
        };

    /// <summary>
    /// Builds the request-override template for an OpenRouter profile: default <c>Authorization: Bearer</c>
    /// auth, the recommended <c>HTTP-Referer</c>/<c>X-Title</c> headers, and an empty extra body. The API
    /// key and model are intentionally left blank — they are per-user.
    /// </summary>
    public static ExternalRequestOverrides BuildOpenRouterOverrides() => new()
    {
        AuthHeaderName = "Authorization",
        AuthHeaderScheme = "Bearer",
        Headers = new Dictionary<string, string>(OpenRouterRecommendedHeaders, StringComparer.OrdinalIgnoreCase),
        ExtraBody = new()
    };

    public static IReadOnlyList<VllmServerConfiguration> CreateServers() =>
    [
        new VllmServerConfiguration
        {
            Name = "OpenRouter",
            UseExistingHttpServer = true,
            HttpServerAddress = OpenRouterEndpoint,
            Model = "openai/o3",
            HostPort = 443,
            RequestOverrides = new ExternalRequestOverrides
            {
                ApiKey = "sk-or-v1-...",
                AuthHeaderName = "Authorization",
                AuthHeaderScheme = "Bearer",
                Headers = new Dictionary<string, string>(OpenRouterRecommendedHeaders, StringComparer.OrdinalIgnoreCase),
                ExtraBody = new()
            },
            EnableVerboseLogs = false,
            Runtime =
            {
                StatusText = "Configure API key",
                StatusDetails = "Replace the placeholder API key with your OpenRouter key."
            }
        }
    ];

    public const string VllmModelNote = "For Docker-managed local deployments, choose a vLLM-compatible Hugging Face Transformers-style folder with config.json, tokenizer files, and .safetensors/.bin weights. It does not need to come directly from huggingface.co, but GGUF or llama.cpp/Ollama-style folders are not suitable for this workflow. You can also leave Local folder empty and enter a Hugging Face model ID instead.";
}
