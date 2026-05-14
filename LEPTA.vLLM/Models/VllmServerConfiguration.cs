using System.IO;
using System.Text;

namespace LEPTA.vLLM.Models;

public sealed record VllmServerConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Qwen 3.5 9B AWQ";
    public string Model { get; set; } = "cyankiwi/Qwen3.5-9B-AWQ-4bit";
    public string DockerImage { get; set; } = "vllm/vllm-openai:latest";
    public string? ServedModelName { get; set; }
    public bool UseExistingHttpServer { get; set; }
    public string HttpServerAddress { get; set; } = "http://localhost:8512";
    public string? LocalModelPath { get; set; }
    public int HostPort { get; set; } = 8000;
    public string DType { get; set; } = "half";
    public bool EnforceEager { get; set; }
    public double GpuMemoryUtilization { get; set; } = 0.88;
    public int MaxModelLength { get; set; } = 16384;
    public string KvCacheDType { get; set; } = "fp8";
    public int SwapSpaceGb { get; set; } = 8;
    public bool EnableVerboseLogs { get; set; } = true;
    public double ParameterCountBillions { get; set; }
    public string WeightQuantization { get; set; } = "AWQ";
    public string KCacheQuantization { get; set; } = "fp8";
    public string VCacheQuantization { get; set; } = "fp8";
    public int GpuLayers { get; set; } = 999;
    public int TensorParallelSize { get; set; } = 1;
    public double CpuOffloadGb { get; set; }
    public int MaxNumSeqs { get; set; } = 8;
    public bool EnablePrefixCaching { get; set; }
    public bool LanguageModelOnly { get; set; }
    public string? ReasoningParser { get; set; }
    public string MetadataSummary { get; set; } = "Select a local folder to scan metadata.";
    public string? DetectedArchitecture { get; set; }
    public int? DetectedMaxTokenLength { get; set; }
    public int? DetectedHiddenSize { get; set; }
    public int? DetectedLayerCount { get; set; }
    public IReadOnlyList<string> AvailableWeightQuantizations { get; set; } = Array.Empty<string>();
    public bool SupportsLifecycleManagement => !UseExistingHttpServer;
    public string ContainerName => $"lepta-vllm-{Sanitize(Name)}";
    public string Endpoint => UseExistingHttpServer
        ? NormalizeHttpServerAddress(HttpServerAddress, HostPort)
        : $"http://localhost:{HostPort}";
    public string EffectiveServedModelName => !string.IsNullOrWhiteSpace(ServedModelName)
        ? ServedModelName.Trim()
        : $"{ResolveModelLabel()}-local";

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
}

