using System.Globalization;
using System.Text.RegularExpressions;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public static class VllmMemoryEstimator
{
    public static ModelMemoryEstimate Estimate(VllmServerConfiguration configuration)
    {
        var parameterBillions = ResolveParameterCountBillions(configuration);

        var bytesPerWeight = configuration.WeightQuantization.ToLowerInvariant() switch
        {
            "int4" or "q4" or "nf4" or "gptq" or "awq" or "compressed-tensors" => 0.56,
            "int5" or "q5" => 0.72,
            "int8" or "q8" or "fp8" => 1.0,
            "fp16" or "bf16" => 2.0,
            _ => 1.5
        };

        var kBytes = KvBytes(configuration.KCacheQuantization);
        var vBytes = KvBytes(configuration.VCacheQuantization);
        var kvCacheGb = Math.Max(0.6, configuration.MaxModelLength / 4096.0 * configuration.MaxNumSeqs * (parameterBillions / 7.0) * ((kBytes + vBytes) / 2.0) * 0.18);
        var weightFootprintGb = parameterBillions * bytesPerWeight * 1.08;
        var gpuLayerRatio = configuration.GpuLayers >= 999 ? 1.0 : Math.Clamp(configuration.GpuLayers / 80.0, 0.05, 1.0);
        var vram = Math.Max(1.5, weightFootprintGb * gpuLayerRatio + kvCacheGb + configuration.TensorParallelSize * 0.35);
        var ram = Math.Max(2.0, weightFootprintGb * (1.0 - gpuLayerRatio) + configuration.SwapSpaceGb + configuration.CpuOffloadGb + parameterBillions * 0.12);
        var summary = $"Estimated usage: ~{vram:F1} GB VRAM and ~{ram:F1} GB RAM.";
        return new ModelMemoryEstimate(vram, ram, summary);
    }

    private static double KvBytes(string quantization) => quantization.ToLowerInvariant() switch
    {
        "fp8" or "int8" => 1.0,
        "fp16" or "bf16" => 2.0,
        "int4" or "q4" => 0.5,
        _ => 1.5
    };

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

