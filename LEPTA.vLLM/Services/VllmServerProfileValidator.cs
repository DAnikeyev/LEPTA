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
}
