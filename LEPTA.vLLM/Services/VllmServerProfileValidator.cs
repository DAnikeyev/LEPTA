using System.Text.Json;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class VllmServerProfileValidator
{
    private const string ExampleEndpoint = "http://localhost:8512";

    public VllmServerValidationResult ValidateExternalEndpoint(string? rawEndpoint)
    {
        if (string.IsNullOrWhiteSpace(rawEndpoint))
        {
            return new VllmServerValidationResult(
                false,
                null,
                $"Enter an already deployed HTTP server address first. Example: {ExampleEndpoint}");
        }

        var normalizedInput = rawEndpoint.Trim();
        if (!normalizedInput.Contains("://", StringComparison.Ordinal))
        {
            normalizedInput = $"http://{normalizedInput}";
        }

        if (!Uri.TryCreate(normalizedInput, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return new VllmServerValidationResult(
                false,
                null,
                $"Enter a valid HTTP or HTTPS server address. Example: {ExampleEndpoint}");
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        // Respect the port the user typed (or the scheme's default port). We must NOT inject the
        // local Docker host port here: for cloud servers such as OpenRouter that live on the
        // https default port (443), forcing a local port (e.g. 8512) makes every probe time out.
        var normalizedPath = builder.Path?.TrimEnd('/');
        builder.Path = string.IsNullOrEmpty(normalizedPath) ? "/" : normalizedPath;
        var normalizedEndpoint = builder.Uri.ToString().TrimEnd('/');

        return new VllmServerValidationResult(true, normalizedEndpoint, $"Using {normalizedEndpoint}.");
    }

    /// <summary>
    /// Validates an <see cref="ExternalRequestOverrides"/>: auth-header name charset, custom
    /// header names/values, ExtraBody keys, and that the auth header is not duplicated in Headers.
    /// A null/empty overrides instance is always valid.
    /// </summary>
    public VllmServerValidationResult ValidateRequestOverrides(ExternalRequestOverrides? overrides)
    {
        if (overrides is null)
        {
            return new VllmServerValidationResult(true, null, "No request overrides configured.");
        }

        var authHeaderName = string.IsNullOrWhiteSpace(overrides.AuthHeaderName)
            ? "Authorization"
            : overrides.AuthHeaderName.Trim();

        if (!IsValidHeaderToken(authHeaderName))
        {
            return new VllmServerValidationResult(
                false,
                null,
                $"The authentication header name '{overrides.AuthHeaderName}' is not a valid HTTP header token. Use a name like 'Authorization', 'api-key', or 'X-API-Key'.");
        }

        if (!string.IsNullOrWhiteSpace(overrides.AuthHeaderScheme)
            && !IsValidHeaderToken(overrides.AuthHeaderScheme.Trim()))
        {
            return new VllmServerValidationResult(
                false,
                null,
                $"The authentication scheme '{overrides.AuthHeaderScheme}' contains characters that are not valid in an HTTP header value.");
        }

        foreach (var header in overrides.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Key))
            {
                return new VllmServerValidationResult(false, null, "Custom headers cannot have an empty name.");
            }

            if (!IsValidHeaderToken(header.Key.Trim()))
            {
                return new VllmServerValidationResult(
                    false,
                    null,
                    $"The custom header name '{header.Key}' is not a valid HTTP header token.");
            }

            if (string.Equals(header.Key, authHeaderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                return new VllmServerValidationResult(
                    false,
                    null,
                    $"The custom header '{header.Key}' duplicates the authentication header '{authHeaderName}'. Remove it from the headers list.");
            }
        }

        foreach (var extra in overrides.ExtraBody)
        {
            if (string.IsNullOrWhiteSpace(extra.Key))
            {
                return new VllmServerValidationResult(false, null, "Extra body fields cannot have an empty name.");
            }

            if (extra.Value.ValueKind == JsonValueKind.Undefined)
            {
                return new VllmServerValidationResult(false, null, $"The extra body field '{extra.Key}' has no value.");
            }
        }

        return new VllmServerValidationResult(true, null, "Request overrides are valid.");
    }

    private static bool IsValidHeaderToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // RFC 7230 token: tchar = "!" / "#" / "$" / "%" / "&" / "'" / "*" / "+" / "-" / "." /
        // "^" / "_" / "`" / "|" / "~" / DIGIT / ALPHA.
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                continue;
            }

            if ("!#$%&'*+-.^_`|~".IndexOf(character, StringComparison.Ordinal) >= 0)
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
