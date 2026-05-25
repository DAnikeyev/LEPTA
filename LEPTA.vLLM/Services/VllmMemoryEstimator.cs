using System.Globalization;
using System.Text.RegularExpressions;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public static class VllmMemoryEstimator
{
    public static ModelMemoryEstimate Estimate(VllmServerConfiguration configuration)
    {
        var parameterBillions = ResolveParameterCountBillions(configuration);

        var weightFootprintGb = parameterBillions * ResolveWeightBytes(configuration.WeightQuantization) * 1.08;
        var kvCacheGb = EstimateKvCacheGb(configuration, parameterBillions);
        var runtimeGpuReserveGb = 0.45 + Math.Max(0, configuration.TensorParallelSize - 1) * 0.20;
        var vram = Math.Max(1.5, weightFootprintGb + kvCacheGb + runtimeGpuReserveGb);
        double? estimatedGpuUsageGb = configuration.GpuVramGb > 0
            ? configuration.GpuVramGb * configuration.GpuMemoryUtilization
            : null;
        var summary = estimatedGpuUsageGb is { } usage
            ? $"Estimated GPU usage ~{usage:F1} GB ({configuration.GpuVramGb:F1} GB x {configuration.GpuMemoryUtilization:0.##})."
            : $"Enter GPU VRAM to estimate GPU usage. Current GPU memory utilization is {configuration.GpuMemoryUtilization:0.##}.";
        return new ModelMemoryEstimate(vram, estimatedGpuUsageGb, summary);
    }

    private static double ResolveWeightBytes(string quantization) => quantization.ToLowerInvariant() switch
    {
        "int4" or "q4" or "nf4" or "gptq" or "awq" or "compressed-tensors" => 0.56,
        "int5" or "q5" => 0.72,
        "int8" or "q8" or "fp8" => 1.0,
        "fp16" or "bf16" => 2.0,
        _ => 1.5
    };

    private static double EstimateKvCacheGb(VllmServerConfiguration configuration, double parameterBillions)
    {
        var tokens = Math.Max(1, configuration.MaxModelLength) * Math.Max(1, configuration.MaxNumSeqs);
        var detectedPerTokenBytes = ResolveDetectedKvBytesPerToken(configuration);
        if (detectedPerTokenBytes is > 0)
        {
            var detectedEstimateGb = tokens * detectedPerTokenBytes.Value / BytesPerGigabyte;
            return Math.Max(0.4, detectedEstimateGb * 1.20);
        }

        var kBytes = KvBytes(configuration.KCacheQuantization);
        var vBytes = KvBytes(configuration.VCacheQuantization);
        return Math.Max(0.6, configuration.MaxModelLength / 4096.0 * configuration.MaxNumSeqs * (parameterBillions / 7.0) * ((kBytes + vBytes) / 2.0) * 0.18);
    }

    private static double? ResolveDetectedKvBytesPerToken(VllmServerConfiguration configuration)
    {
        if (configuration.DetectedHiddenSize is not > 0 || configuration.DetectedLayerCount is not > 0)
        {
            return null;
        }

        var headCount = ResolveAttentionHeadCount(configuration);
        if (headCount <= 0)
        {
            return null;
        }

        var headDimension = Math.Max(1, configuration.DetectedHiddenSize.Value / headCount);
        var kvHeadCount = ResolveKeyValueHeadCount(configuration, headCount);
        var kBytes = KvBytes(configuration.KCacheQuantization);
        var vBytes = KvBytes(configuration.VCacheQuantization);
        return configuration.DetectedLayerCount.Value * kvHeadCount * headDimension * (kBytes + vBytes);
    }

    private static int ResolveAttentionHeadCount(VllmServerConfiguration configuration)
    {
        if (configuration.DetectedHiddenSize is not > 0)
        {
            return 0;
        }

        foreach (var candidate in new[] { 128, 160, 96, 80, 64 })
        {
            if (configuration.DetectedHiddenSize.Value % candidate == 0)
            {
                return configuration.DetectedHiddenSize.Value / candidate;
            }
        }

        return 0;
    }

    private static int ResolveKeyValueHeadCount(VllmServerConfiguration configuration, int attentionHeadCount)
    {
        if (!string.IsNullOrWhiteSpace(configuration.DetectedArchitecture)
            && configuration.DetectedArchitecture.Contains("qwen", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(1, attentionHeadCount / 7);
        }

        return attentionHeadCount;
    }

    private static double KvBytes(string quantization) => quantization.ToLowerInvariant() switch
    {
        "fp8" or "int8" => 1.0,
        "fp16" or "bf16" => 2.0,
        "int4" or "q4" => 0.5,
        _ => 1.5
    };

    private const double BytesPerGigabyte = 1024d * 1024d * 1024d;

    public static double ResolveParameterCountBillions(VllmServerConfiguration configuration)
        => configuration.ParameterCountBillions > 0
            ? configuration.ParameterCountBillions
            : GuessParameterCount(configuration);

    private static double GuessParameterCount(VllmServerConfiguration configuration)
    {
        var source = string.IsNullOrWhiteSpace(configuration.Model) ? configuration.Name : configuration.Model;
        var match = Regex.Match(source, @"(?<count>\d+(?:\.\d+)?)\s*[bB]");
        return match.Success && double.TryParse(match.Groups["count"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 7;
    }
}

