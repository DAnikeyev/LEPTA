using System.Globalization;

namespace LEPTA.vLLM.Models;

public sealed record VllmModelMetadata(
    string ModelDirectory,
    string DisplayName,
    string? ModelId,
    string? Architecture,
    int? MaxTokenLength,
    int? RecommendedMaxModelLength,
    int? HiddenSize,
    int? IntermediateSize,
    int? LayerCount,
    int? AttentionHeadCount,
    int? KeyValueHeadCount,
    double? ParameterCountBillions,
    IReadOnlyList<string> AvailableQuantizations,
    string? PreferredWeightQuantization,
    string? PreferredKvCacheDType,
    double? RecommendedGpuMemoryUtilization,
    int? RecommendedMaxNumSeqs,
    bool EnablePrefixCaching,
    bool LanguageModelOnly,
    string? ReasoningParser,
    string SuggestedServedModelName)
{
    public string BuildSummary()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(Architecture))
        {
            parts.Add($"Architecture: {Architecture}");
        }

        if (MaxTokenLength is { } maxTokenLength)
        {
            parts.Add($"Max tokens: {maxTokenLength.ToString("N0", CultureInfo.InvariantCulture)}");
        }

        if (LayerCount is { } layerCount)
        {
            parts.Add($"Layers: {layerCount}");
        }

        if (HiddenSize is { } hiddenSize)
        {
            parts.Add($"Hidden size: {hiddenSize}");
        }

        if (ParameterCountBillions is { } parameterCountBillions)
        {
            parts.Add($"Parameters: ~{parameterCountBillions.ToString("0.###", CultureInfo.InvariantCulture)}B");
        }

        if (AvailableQuantizations.Count > 0)
        {
            parts.Add($"Quantization: {string.Join(", ", AvailableQuantizations)}");
        }

        var recommendationParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(PreferredWeightQuantization))
        {
            recommendationParts.Add($"weights {PreferredWeightQuantization}");
        }

        if (!string.IsNullOrWhiteSpace(PreferredKvCacheDType))
        {
            recommendationParts.Add($"KV {PreferredKvCacheDType}");
        }

        if (RecommendedMaxModelLength is { } recommendedMaxModelLength)
        {
            recommendationParts.Add($"deploy len {recommendedMaxModelLength.ToString("N0", CultureInfo.InvariantCulture)}");
        }

        if (RecommendedMaxNumSeqs is { } recommendedMaxNumSeqs)
        {
            recommendationParts.Add($"seqs {recommendedMaxNumSeqs}");
        }

        if (recommendationParts.Count > 0)
        {
            parts.Add($"Recommended: {string.Join(", ", recommendationParts)}");
        }

        return parts.Count == 0
            ? "No supported local model metadata was detected."
            : string.Join(" • ", parts);
    }
}
