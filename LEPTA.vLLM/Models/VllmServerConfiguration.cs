using System.IO;
using System.Text;
using System.Text.Json.Serialization;

namespace LEPTA.vLLM.Models;

public sealed record VllmServerConfiguration
{
    public const string QwenMtpSpeculativeArguments = "--speculative-config '{\"method\":\"qwen3_next_mtp\",\"num_speculative_tokens\":2}'";
    public const string DefaultDockerImage = "vllm/vllm-openai:latest";
    public const int DefaultReadyTimeoutMinutes = 10;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Qwen 3.5 9B AWQ";
    public string Model { get; set; } = "cyankiwi/Qwen3.5-9B-AWQ-4bit";
    public string DockerImage { get; set; } = DefaultDockerImage;
    public string? ServedModelName { get; set; }
    public bool UseExistingHttpServer { get; set; }
    public string HttpServerAddress { get; set; } = "http://localhost:8512";
    public string? LocalModelPath { get; set; }
    public int HostPort { get; set; } = 8512;
    public string DType { get; set; } = "half";
    public bool EnforceEager { get; set; }
    public double GpuMemoryUtilization { get; set; } = 0.92;
    public double GpuVramGb { get; set; }
    public int MaxModelLength { get; set; } = 8192;
    public int ReadyTimeoutMinutes { get; set; } = DefaultReadyTimeoutMinutes;
    public string KvCacheDType { get; set; } = "fp8";
    public bool EnableVerboseLogs { get; set; } = true;
    public double ParameterCountBillions { get; set; }
    public string WeightQuantization { get; set; } = "AWQ";
    public string KCacheQuantization { get; set; } = "fp8";
    public string VCacheQuantization { get; set; } = "fp8";
    public int TensorParallelSize { get; set; } = 1;
    public double CpuOffloadGb { get; set; }
    public int MaxNumSeqs { get; set; } = 5;
    public bool EnableTokenizersParallelism { get; set; } = true;
    public string AdditionalVllmArguments { get; set; } = string.Empty;
    public bool EnablePrefixCaching { get; set; }
    public bool LanguageModelOnly { get; set; }
    public string? ReasoningParser { get; set; }
    public string MetadataSummary { get; set; } = "Select a local folder to scan metadata.";
    public string? DetectedArchitecture { get; set; }
    public int? DetectedMaxTokenLength { get; set; }
    public int? DetectedHiddenSize { get; set; }
    public int? DetectedLayerCount { get; set; }
    public IReadOnlyList<string> AvailableWeightQuantizations { get; set; } = Array.Empty<string>();
    [JsonIgnore]
    public string UiStatusKind { get; set; } = "Unknown";
    [JsonIgnore]
    public string UiStatusText { get; set; } = "Not checked";
    [JsonIgnore]
    public string UiStatusDetails { get; set; } = "Select the profile or use Check server to verify it.";
    public bool SupportsLifecycleManagement => !UseExistingHttpServer;

    public int MaxOutputTokens => Math.Max(256, MaxModelLength / 4);
    public int MaxContextTokens => Math.Max(512, MaxModelLength / 2);
    public int MaxDocumentTokens => Math.Max(512, MaxModelLength * 3 / 4);
    [JsonIgnore]
    public bool IsLeptaManagedDeploymentActive => SupportsLifecycleManagement && HasEstablishedConnection;
    [JsonIgnore]
    public string UiTypeLabel => UseExistingHttpServer ? "External server" : "LEPTA-managed local";
    [JsonIgnore]
    public bool HasEstablishedConnection => string.Equals(UiStatusKind, "Ready", StringComparison.OrdinalIgnoreCase);
    public bool SupportsThinking
        => !string.IsNullOrWhiteSpace(ReasoningParser)
           || LooksLikeThinkingModel(ServedModelName)
           || LooksLikeThinkingModel(Model)
           || LooksLikeThinkingModel(LocalModelPath);
    [JsonIgnore]
    public string EffectiveDockerImage => string.IsNullOrWhiteSpace(DockerImage)
        ? DefaultDockerImage
        : DockerImage.Trim();
    public string ContainerName => $"lepta-vllm-{Sanitize(Name)}";
    public string Endpoint => UseExistingHttpServer
        ? NormalizeHttpServerAddress(HttpServerAddress, HostPort)
        : $"http://localhost:{HostPort}";
    public string EffectiveServedModelName => !string.IsNullOrWhiteSpace(ServedModelName)
        ? ServedModelName.Trim()
        : $"{ResolveModelLabel()}-local";
    [JsonIgnore]
    public string UiEndpointLabel => UseExistingHttpServer
        ? NormalizeHttpServerAddress(HttpServerAddress, HostPort)
        : $"Runs at http://localhost:{HostPort}";

    public static string ResolveSuggestedAdditionalVllmArguments(
        string? displayName,
        string? model,
        string? localModelPath,
        string? architecture,
        string? reasoningParser)
        => LooksLikeQwen(displayName)
           || LooksLikeQwen(model)
           || LooksLikeQwen(localModelPath)
           || LooksLikeQwen(architecture)
           || string.Equals(reasoningParser, "qwen3", StringComparison.OrdinalIgnoreCase)
            ? QwenMtpSpeculativeArguments
            : string.Empty;

    private static string Sanitize(string value)
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

    private static string NormalizeHttpServerAddress(string? value, int fallbackPort)
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

    private string ResolveModelLabel()
    {
        if (!string.IsNullOrWhiteSpace(LocalModelPath))
        {
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(LocalModelPath));
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                return folderName;
            }
        }

        if (!string.IsNullOrWhiteSpace(Model))
        {
            var trimmed = Model.Trim().TrimEnd('/');
            var slashIndex = trimmed.LastIndexOf('/');
            return slashIndex >= 0 ? trimmed[(slashIndex + 1)..] : trimmed;
        }

        return Sanitize(Name);
    }

    private static bool LooksLikeThinkingModel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return LooksLikeQwen(value)
               || value.Contains("reasoning", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeQwen(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains("qwen", StringComparison.OrdinalIgnoreCase);
}

