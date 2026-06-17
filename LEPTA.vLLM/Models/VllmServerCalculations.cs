using System.IO;
using System.Text;

namespace LEPTA.vLLM.Models;

/// <summary>
/// Pure, side-effect-free calculations derived from a <see cref="VllmServerConfiguration"/>.
/// Extracted from the record so they are unit-testable independent of WPF and persistence.
/// The record keeps thin facade properties that delegate here.
/// </summary>
public static class VllmServerCalculations
{
    /// <summary>
    /// Lowercases the value and replaces every non-alphanumeric run with a single dash,
    /// yielding a Docker-safe identifier fragment. Empty input falls back to "server".
    /// </summary>
    public static string SanitizeContainerName(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "server" : sanitized;
    }

    /// <summary>
    /// Ensures an HTTP server address has a scheme (defaulting to http) and no trailing slash.
    /// A null/blank value falls back to <c>http://localhost:{fallbackPort}</c>.
    /// </summary>
    public static string NormalizeHttpServerAddress(string? value, int fallbackPort)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? $"http://localhost:{fallbackPort}"
            : value.Trim();

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"http://{normalized}";
        }

        return normalized.TrimEnd('/');
    }

    /// <summary>
    /// Resolves a human-readable label for the model: the local-folder name, else the last path
    /// segment of the model id, else a sanitized server name.
    /// </summary>
    public static string ResolveModelLabel(string? name, string? model, string? localModelPath)
    {
        if (!string.IsNullOrWhiteSpace(localModelPath))
        {
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(localModelPath));
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                return folderName;
            }
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            var trimmed = model.Trim().TrimEnd('/');
            var slashIndex = trimmed.LastIndexOf('/');
            return slashIndex >= 0 ? trimmed[(slashIndex + 1)..] : trimmed;
        }

        return SanitizeContainerName(name ?? string.Empty);
    }

    public static bool LooksLikeThinkingModel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return LooksLikeQwen(value)
               || value.Contains("reasoning", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeQwen(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains("qwen", StringComparison.OrdinalIgnoreCase);

    public static string ResolveSuggestedAdditionalVllmArguments(
        string? displayName,
        string? model,
        string? localModelPath,
        string? architecture,
        string? reasoningParser,
        string qwenMtpSpeculativeArguments)
        => LooksLikeQwen(displayName)
           || LooksLikeQwen(model)
           || LooksLikeQwen(localModelPath)
           || LooksLikeQwen(architecture)
           || string.Equals(reasoningParser, "qwen3", StringComparison.OrdinalIgnoreCase)
            ? qwenMtpSpeculativeArguments
            : string.Empty;

    public static int ResolveMaxOutputTokens(int maxModelLength)
        => Math.Max(256, maxModelLength / 4);

    public static int ResolveMaxContextTokens(int maxModelLength)
        => Math.Max(512, maxModelLength / 2);

    public static int ResolveMaxDocumentTokens(int maxModelLength)
        => Math.Max(512, maxModelLength * 3 / 4);
}
