using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LEPTA.vLLM.Models;

/// <summary>
/// Per-profile customization applied to every outbound request for an external
/// (already-deployed) OpenAI-compatible server. Captures authentication, arbitrary
/// HTTP headers, and arbitrary fields merged into the chat/completions JSON body.
/// </summary>
public sealed class ExternalRequestOverrides
{
    /// <summary>
    /// Bare API key. Rendered onto the request according to <see cref="AuthHeaderName"/>
    /// and <see cref="AuthHeaderScheme"/>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Value prefix used for the <c>Authorization</c> header (default <c>Bearer</c>).
    /// Ignored for non-Authorization header names. Examples: <c>Bearer</c>, <c>ApiKey</c>.
    /// </summary>
    public string AuthHeaderScheme { get; set; } = "Bearer";

    /// <summary>
    /// Header name that carries the API key (default <c>Authorization</c>).
    /// Set to <c>api-key</c> for Azure OpenAI or <c>X-API-Key</c> for some gateways.
    /// </summary>
    public string AuthHeaderName { get; set; } = "Authorization";

    /// <summary>
    /// Arbitrary key/value HTTP headers (e.g. <c>HTTP-Referer</c>, <c>X-Title</c> for OpenRouter).
    /// A header here matching <see cref="AuthHeaderName"/> is ignored — the key is applied separately.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// Arbitrary fields merged into every /v1/chat/completions JSON body. Values are
    /// stored as raw <see cref="JsonElement"/> so nested objects/arrays survive verbatim.
    /// Keys follow the same snake_case serialization policy as the rest of the payload.
    /// </summary>
    public Dictionary<string, JsonElement> ExtraBody { get; set; } = new();

    /// <summary>True when no overrides of any kind are configured.</summary>
    public bool IsEmpty
        => string.IsNullOrWhiteSpace(ApiKey)
           && string.IsNullOrWhiteSpace(AuthHeaderScheme) is false && AuthHeaderScheme == "Bearer"
           && string.IsNullOrWhiteSpace(AuthHeaderName) is false && AuthHeaderName == "Authorization"
           && Headers.Count == 0
           && ExtraBody.Count == 0;

    /// <summary>
    /// Applies authentication + custom headers to <paramref name="request"/>. Does not log
    /// the secret value. Returns itself for fluent use.
    /// </summary>
    public ExternalRequestOverrides ApplyTo(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var apiKey = ApiKey?.Trim();
        var headerName = string.IsNullOrWhiteSpace(AuthHeaderName) ? "Authorization" : AuthHeaderName.Trim();

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            if (string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                var scheme = string.IsNullOrWhiteSpace(AuthHeaderScheme) ? "Bearer" : AuthHeaderScheme.Trim();
                request.Headers.Authorization = new AuthenticationHeaderValue(scheme, apiKey);
            }
            else
            {
                // Non-Authorization header names (api-key, X-API-Key, ...) carry the bare key.
                request.Headers.TryAddWithoutValidation(headerName, apiKey);
            }
        }

        foreach (var header in Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Key)
                || string.Equals(header.Key, headerName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key.Trim(), header.Value);
        }

        return this;
    }

    /// <summary>Deep clones this instance so callers can hand the wire layer an immutable snapshot.</summary>
    public ExternalRequestOverrides Snapshot()
    {
        var clone = new ExternalRequestOverrides
        {
            ApiKey = ApiKey,
            AuthHeaderScheme = AuthHeaderScheme,
            AuthHeaderName = AuthHeaderName,
            Headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
            ExtraBody = new Dictionary<string, JsonElement>(ExtraBody)
        };
        return clone;
    }
}
