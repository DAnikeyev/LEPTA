using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class VllmModelMetadataScanner
{
    private static readonly Regex ParameterRegex = new(@"(?<count>\d+(?:\.\d+)?)\s*[bB]", RegexOptions.Compiled);
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public async Task<VllmModelMetadata> ScanAsync(string modelDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        if (!Directory.Exists(modelDirectory))
        {
            throw new DirectoryNotFoundException($"Model folder '{modelDirectory}' does not exist.");
        }

        var normalizedDirectory = Path.GetFullPath(modelDirectory);
        var displayName = Path.GetFileName(Path.TrimEndingDirectorySeparator(normalizedDirectory));
        var config = await ReadJsonIfExistsAsync(Path.Combine(normalizedDirectory, "config.json"), cancellationToken);
        var generationConfig = await ReadJsonIfExistsAsync(Path.Combine(normalizedDirectory, "generation_config.json"), cancellationToken);
        var tokenizerConfig = await ReadJsonIfExistsAsync(Path.Combine(normalizedDirectory, "tokenizer_config.json"), cancellationToken);
        var quantizeConfig = await ReadJsonIfExistsAsync(Path.Combine(normalizedDirectory, "quantize_config.json"), cancellationToken);

        var modelId = FirstNonEmpty(
            GetString(config, "_name_or_path"),
            displayName,
            GetString(config, "model_type"));

        var architecture = GetFirstStringArrayValue(config, "architectures");
        var hiddenSize = FirstInt(config, "hidden_size", "d_model", "n_embd");
        var intermediateSize = FirstInt(config, "intermediate_size", "ffn_hidden_size", "n_inner");
        var layerCount = FirstInt(config, "num_hidden_layers", "n_layer", "num_layers");
        var attentionHeads = FirstInt(config, "num_attention_heads", "n_head");
        var keyValueHeads = FirstInt(config, "num_key_value_heads");
        var maxTokenLength = ResolveMaxTokenLength(config, generationConfig, tokenizerConfig);
        var parameterCountBillions = ResolveParameterCountBillions(displayName, modelId, hiddenSize, intermediateSize, layerCount, attentionHeads, keyValueHeads, config);

        var availableQuantizations = ResolveAvailableQuantizations(displayName, modelId, config, quantizeConfig);
        var preferredWeightQuantization = ResolvePreferredWeightQuantization(availableQuantizations);
        var preferredKvCacheDType = ResolvePreferredKvCacheDType(displayName, modelId, architecture, preferredWeightQuantization);
        var reasoningParser = ResolveReasoningParser(displayName, modelId, architecture);
        var recommendedAdditionalVllmArguments = VllmServerConfiguration.ResolveSuggestedAdditionalVllmArguments(
            displayName,
            modelId,
            normalizedDirectory,
            architecture,
            reasoningParser);
        var recommendedMaxModelLength = ResolveRecommendedMaxModelLength(displayName, modelId, preferredWeightQuantization, maxTokenLength);
        var recommendedGpuMemoryUtilization = ResolveRecommendedGpuMemoryUtilization(displayName, modelId, preferredWeightQuantization);
        var recommendedMaxNumSeqs = ResolveRecommendedMaxNumSeqs(displayName, modelId, preferredWeightQuantization);
        var suggestedServedModelName = $"{displayName}-local";

        return new VllmModelMetadata(
            normalizedDirectory,
            displayName,
            modelId,
            architecture,
            maxTokenLength,
            recommendedMaxModelLength,
            hiddenSize,
            intermediateSize,
            layerCount,
            attentionHeads,
            keyValueHeads,
            parameterCountBillions,
            availableQuantizations,
            preferredWeightQuantization,
            preferredKvCacheDType,
            recommendedGpuMemoryUtilization,
            recommendedMaxNumSeqs,
            recommendedAdditionalVllmArguments,
            EnablePrefixCaching: IsQwenLike(displayName, modelId, architecture),
            LanguageModelOnly: IsQwenLike(displayName, modelId, architecture),
            ReasoningParser: reasoningParser,
            SuggestedServedModelName: suggestedServedModelName);
    }

    private static async Task<JsonElement?> ReadJsonIfExistsAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static int? ResolveMaxTokenLength(params JsonElement?[] documents)
    {
        var values = new List<int>();
        foreach (var document in documents)
        {
            TryAddInt(values, document, "max_position_embeddings");
            TryAddInt(values, document, "model_max_length");
            TryAddInt(values, document, "max_sequence_length");
            TryAddInt(values, document, "max_seq_len");
            TryAddInt(values, document, "seq_length");
            TryAddInt(values, document, "n_positions");
            TryAddInt(values, document, "max_length");
        }

        return values.Count == 0 ? null : values.Max();
    }

    private static double? ResolveParameterCountBillions(
        string displayName,
        string? modelId,
        int? hiddenSize,
        int? intermediateSize,
        int? layerCount,
        int? attentionHeads,
        int? keyValueHeads,
        JsonElement? config)
    {
        var fromName = ParseParameterCount(displayName) ?? ParseParameterCount(modelId);
        if (fromName is not null)
        {
            return fromName;
        }

        if (hiddenSize is null || layerCount is null)
        {
            return null;
        }

        var actualIntermediate = intermediateSize ?? hiddenSize.Value * 4;
        var vocabSize = FirstInt(config, "vocab_size") ?? 151_936;
        var kvRatio = attentionHeads > 0 && keyValueHeads > 0
            ? Math.Max(0.125, keyValueHeads.Value / (double)attentionHeads.Value)
            : 1.0;
        var hidden = hiddenSize.Value;
        var perLayerAttention = hidden * hidden * (3 + kvRatio + 1);
        var perLayerMlp = 3d * hidden * actualIntermediate;
        var perLayer = perLayerAttention + perLayerMlp + hidden * 4d;
        var embeddings = vocabSize * (double)hidden;
        var totalParameters = embeddings + layerCount.Value * perLayer;
        return Math.Round(totalParameters / 1_000_000_000d, 3);
    }

    private static IReadOnlyList<string> ResolveAvailableQuantizations(string displayName, string? modelId, JsonElement? config, JsonElement? quantizeConfig)
    {
        var results = new SortedSet<string>(Comparer);
        CollectQuantizations(results, displayName);
        CollectQuantizations(results, modelId);
        CollectQuantizations(results, GetString(config, "quantization_method"));
        CollectQuantizations(results, GetString(config, "quant_method"));
        CollectQuantizations(results, GetString(config, "compression_method"));
        CollectQuantizations(results, GetString(quantizeConfig, "quant_method"));
        CollectQuantizations(results, GetString(quantizeConfig, "format"));

        if (TryGetProperty(config, "quantization_config", out var quantizationConfig))
        {
            CollectQuantizations(results, GetString(quantizationConfig, "quant_method"));
            CollectQuantizations(results, GetString(quantizationConfig, "format"));
            CollectBitDepthQuantizations(results, quantizationConfig);
        }

        if (TryGetProperty(config, "compression_config", out var compressionConfig))
        {
            CollectQuantizations(results, GetString(compressionConfig, "format"));
            CollectQuantizations(results, GetString(compressionConfig, "quant_method"));
            CollectBitDepthQuantizations(results, compressionConfig);
        }

        if (TryGetProperty(quantizeConfig, "bits", out var bitsElement) && TryReadInt(bitsElement, out var bits))
        {
            results.Add(bits switch
            {
                <= 4 => "Int4",
                8 => "Int8",
                _ => $"Int{bits}"
            });
        }

        return results.ToArray();
    }

    private static string? ResolvePreferredWeightQuantization(IReadOnlyList<string> availableQuantizations)
    {
        foreach (var candidate in new[] { "compressed-tensors", "AWQ", "GPTQ", "NF4", "Int4", "FP8", "Int8", "BF16", "FP16" })
        {
            if (availableQuantizations.Any(value => Comparer.Equals(value, candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolvePreferredKvCacheDType(string displayName, string? modelId, string? architecture, string? preferredWeightQuantization)
        => IsQwenLike(displayName, modelId, architecture)
           || Comparer.Equals(preferredWeightQuantization, "compressed-tensors")
            ? "fp8"
            : null;

    private static string? ResolveReasoningParser(string displayName, string? modelId, string? architecture)
        => IsQwenLike(displayName, modelId, architecture) ? "qwen3" : null;

    private static int? ResolveRecommendedMaxModelLength(string displayName, string? modelId, string? preferredWeightQuantization, int? detectedMaxTokenLength)
    {
        if (IsQwenAwq4BitLike(displayName, modelId, preferredWeightQuantization))
        {
            return 5120;
        }

        return detectedMaxTokenLength is > 0 and <= 8192 ? detectedMaxTokenLength : null;
    }

    private static double? ResolveRecommendedGpuMemoryUtilization(string displayName, string? modelId, string? preferredWeightQuantization)
        => IsQwenAwq4BitLike(displayName, modelId, preferredWeightQuantization) ? 0.90 : null;

    private static int? ResolveRecommendedMaxNumSeqs(string displayName, string? modelId, string? preferredWeightQuantization)
        => IsQwenAwq4BitLike(displayName, modelId, preferredWeightQuantization) ? 1 : null;

    private static void CollectBitDepthQuantizations(ISet<string> results, JsonElement element)
    {
        if (TryGetProperty(element, "bits", out var bitsElement) && TryReadInt(bitsElement, out var bits))
        {
            results.Add(bits switch
            {
                <= 4 => "Int4",
                8 => "Int8",
                _ => $"Int{bits}"
            });
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                CollectBitDepthQuantizations(results, property.Value);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        CollectBitDepthQuantizations(results, item);
                    }
                }
            }
        }
    }

    private static void CollectQuantizations(ISet<string> results, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var text = source.Trim();
        if (text.Contains("compressed", StringComparison.OrdinalIgnoreCase)) results.Add("compressed-tensors");
        if (text.Contains("awq", StringComparison.OrdinalIgnoreCase)) results.Add("AWQ");
        if (text.Contains("gptq", StringComparison.OrdinalIgnoreCase)) results.Add("GPTQ");
        if (text.Contains("nf4", StringComparison.OrdinalIgnoreCase)) results.Add("NF4");
        if (text.Contains("fp8", StringComparison.OrdinalIgnoreCase)) results.Add("FP8");
        if (text.Contains("bf16", StringComparison.OrdinalIgnoreCase)) results.Add("BF16");
        if (text.Contains("fp16", StringComparison.OrdinalIgnoreCase) || text.Contains("float16", StringComparison.OrdinalIgnoreCase) || text.Contains("half", StringComparison.OrdinalIgnoreCase)) results.Add("FP16");
        if (text.Contains("4bit", StringComparison.OrdinalIgnoreCase) || text.Contains("int4", StringComparison.OrdinalIgnoreCase) || text.Contains("q4", StringComparison.OrdinalIgnoreCase)) results.Add("Int4");
        if (text.Contains("8bit", StringComparison.OrdinalIgnoreCase) || text.Contains("int8", StringComparison.OrdinalIgnoreCase) || text.Contains("q8", StringComparison.OrdinalIgnoreCase)) results.Add("Int8");
    }

    private static bool IsQwenLike(string displayName, string? modelId, string? architecture)
    {
        var combined = $"{displayName} {modelId} {architecture}";
        return combined.Contains("qwen", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQwenAwq4BitLike(string displayName, string? modelId, string? preferredWeightQuantization)
    {
        var combined = $"{displayName} {modelId}";
        return combined.Contains("qwen", StringComparison.OrdinalIgnoreCase)
               && (combined.Contains("4bit", StringComparison.OrdinalIgnoreCase)
                   || Comparer.Equals(preferredWeightQuantization, "compressed-tensors")
                   || Comparer.Equals(preferredWeightQuantization, "AWQ"));
    }

    private static double? ParseParameterCount(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var match = ParameterRegex.Match(source);
        return match.Success && double.TryParse(match.Groups["count"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? FirstInt(JsonElement? document, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetProperty(document, propertyName, out var element) && TryReadInt(element, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement? document, string propertyName)
        => TryGetProperty(document, propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? GetString(JsonElement document, string propertyName)
        => TryGetProperty(document, propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? GetFirstStringArrayValue(JsonElement? document, string propertyName)
    {
        if (!TryGetProperty(document, propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                return item.GetString();
            }
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static void TryAddInt(ICollection<int> results, JsonElement? document, string propertyName)
    {
        if (TryGetProperty(document, propertyName, out var element)
            && TryReadInt(element, out var value)
            && value > 0
            && value < 10_000_000)
        {
            results.Add(value);
        }
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetInt32(out value);
            case JsonValueKind.String:
                return int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                value = default;
                return false;
        }
    }

    private static bool TryGetProperty(JsonElement? document, string propertyName, out JsonElement value)
    {
        if (document is { } element && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (Comparer.Equals(property.Name, propertyName))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement document, string propertyName, out JsonElement value)
    {
        foreach (var property in document.EnumerateObject())
        {
            if (Comparer.Equals(property.Name, propertyName))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

