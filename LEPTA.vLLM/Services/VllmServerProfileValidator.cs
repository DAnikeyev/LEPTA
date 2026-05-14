using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class VllmServerProfileValidator
{
    private const string ExampleEndpoint = "http://localhost:8512";

    public VllmServerValidationResult ValidateExternalEndpoint(string? rawEndpoint, int fallbackPort = 8512)
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

        if (builder.Uri.IsDefaultPort && fallbackPort > 0)
        {
            builder.Port = fallbackPort;
        }

        var normalizedPath = builder.Path?.TrimEnd('/');
        builder.Path = string.IsNullOrEmpty(normalizedPath) ? "/" : normalizedPath;
        var normalizedEndpoint = builder.Uri.ToString().TrimEnd('/');

        return new VllmServerValidationResult(true, normalizedEndpoint, $"Using {normalizedEndpoint}.");
    }
}
