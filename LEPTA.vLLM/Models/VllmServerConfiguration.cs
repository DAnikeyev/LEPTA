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

    /// <summary>
    /// Authentication, custom headers, and extra body fields applied to outbound requests
    /// for external OpenAI-compatible servers. Serialized; the legacy <see cref="ApiKey"/>
    /// property is a shim that proxies into this for backward compatibility.
    /// </summary>
    public ExternalRequestOverrides RequestOverrides { get; set; } = new();

    /// <summary>
    /// Legacy API-key accessor. Reads/writes <see cref="ExternalRequestOverrides.ApiKey"/>. Never
    /// serialized (the canonical form is <see cref="RequestOverrides"/>); legacy stores that stored
    /// a top-level <c>ApiKey</c> are migrated into <see cref="RequestOverrides"/> on load by
    /// <see cref="Services.VllmServerConfigurationStore"/>.
    /// </summary>
    [JsonIgnore]
    public string ApiKey
    {
        get => RequestOverrides.ApiKey ?? string.Empty;
        set => RequestOverrides.ApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public bool EnablePrefixCaching { get; set; }
    public bool LanguageModelOnly { get; set; }
    public string? ReasoningParser { get; set; }
    public string MetadataSummary { get; set; } = "Select a local folder to scan metadata.";
    public string? DetectedArchitecture { get; set; }
    public int? DetectedMaxTokenLength { get; set; }
    public int? DetectedHiddenSize { get; set; }
    public int? DetectedLayerCount { get; set; }
    public IReadOnlyList<string> AvailableWeightQuantizations { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Transient UI/runtime status (probe result, status dot/pill text, details). Never serialized;
    /// the only bridge from the persisted record to live UI state. See <see cref="VllmServerRuntimeState"/>.
    /// </summary>
    [JsonIgnore]
    public VllmServerRuntimeState Runtime { get; init; } = new();

    public bool SupportsLifecycleManagement => !UseExistingHttpServer;

    public int MaxOutputTokens => VllmServerCalculations.ResolveMaxOutputTokens(MaxModelLength);
    public int MaxContextTokens => VllmServerCalculations.ResolveMaxContextTokens(MaxModelLength);
    public int MaxDocumentTokens => VllmServerCalculations.ResolveMaxDocumentTokens(MaxModelLength);
    [JsonIgnore]
    public bool IsLeptaManagedDeploymentActive => SupportsLifecycleManagement && HasEstablishedConnection;
    [JsonIgnore]
    public string UiTypeLabel => VllmServerCalculations.ResolveUiTypeLabel(UseExistingHttpServer);
    [JsonIgnore]
    public bool HasEstablishedConnection => Runtime.StatusKind == ServerStatusKind.Ready;
    public bool SupportsThinking
        => !string.IsNullOrWhiteSpace(ReasoningParser)
           || VllmServerCalculations.LooksLikeThinkingModel(ServedModelName)
           || VllmServerCalculations.LooksLikeThinkingModel(Model)
           || VllmServerCalculations.LooksLikeThinkingModel(LocalModelPath);
    [JsonIgnore]
    public string EffectiveDockerImage => string.IsNullOrWhiteSpace(DockerImage)
        ? DefaultDockerImage
        : DockerImage.Trim();
    public string ContainerName => $"lepta-vllm-{VllmServerCalculations.SanitizeContainerName(Name)}";
    public string Endpoint => UseExistingHttpServer
        ? VllmServerCalculations.NormalizeHttpServerAddress(HttpServerAddress, HostPort)
        : $"http://localhost:{HostPort}";
    public string EffectiveServedModelName => !string.IsNullOrWhiteSpace(ServedModelName)
        ? ServedModelName.Trim()
        : $"{VllmServerCalculations.ResolveModelLabel(Name, Model, LocalModelPath)}-local";
    [JsonIgnore]
    public string UiEndpointLabel => VllmServerCalculations.ResolveUiEndpointLabel(UseExistingHttpServer, HttpServerAddress, HostPort);

    public static string ResolveSuggestedAdditionalVllmArguments(
        string? displayName,
        string? model,
        string? localModelPath,
        string? architecture,
        string? reasoningParser)
        => VllmServerCalculations.ResolveSuggestedAdditionalVllmArguments(
            displayName,
            model,
            localModelPath,
            architecture,
            reasoningParser,
            QwenMtpSpeculativeArguments);
}

