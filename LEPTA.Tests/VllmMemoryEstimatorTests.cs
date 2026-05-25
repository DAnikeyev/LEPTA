using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class VllmMemoryEstimatorTests
{
    [Test]
    public void Estimate_ForLocalQwenAwqProfile_ReportsCombinedWorkingSetNearFourteenGigabytes()
    {
        var configuration = new VllmServerConfiguration
        {
            Name = "Qwen 3.5 9B AWQ",
            Model = "Qwen/Qwen3.5-9B-AWQ-4bit",
            LocalModelPath = @"D:\Models\Qwen3.5-9B-AWQ-4bit",
            WeightQuantization = "compressed-tensors",
            KCacheQuantization = "fp8",
            VCacheQuantization = "fp8",
            GpuMemoryUtilization = 0.90,
            GpuVramGb = 24,
            MaxModelLength = 5120,
            MaxNumSeqs = 1,
            ParameterCountBillions = 9,
            DetectedArchitecture = "Qwen3ForCausalLM",
            DetectedHiddenSize = 3584,
            DetectedLayerCount = 40,
            EnablePrefixCaching = true
        };

        var estimate = VllmMemoryEstimator.Estimate(configuration);

        Assert.Multiple(() =>
        {
            Assert.That(estimate.EstimatedVramGb, Is.InRange(6.0d, 7.0d));
            Assert.That(estimate.EstimatedGpuUsageGb, Is.EqualTo(21.6d).Within(0.0001d));
            Assert.That(estimate.Summary, Does.Contain("Estimated GPU usage"));
        });
    }

    [Test]
    public void Estimate_WithoutMetadata_FallsBackToModelNameHeuristics()
    {
        var configuration = new VllmServerConfiguration
        {
            Name = "Llama 3.2 3B",
            Model = "meta-llama/Llama-3.2-3B-Instruct",
            WeightQuantization = "Int8",
            KCacheQuantization = "fp16",
            VCacheQuantization = "fp16",
            GpuMemoryUtilization = 0.92,
            MaxModelLength = 4096,
            MaxNumSeqs = 2,
            TensorParallelSize = 1
        };

        var estimate = VllmMemoryEstimator.Estimate(configuration);

        Assert.Multiple(() =>
        {
            Assert.That(estimate.EstimatedVramGb, Is.GreaterThan(3.0d));
            Assert.That(estimate.EstimatedGpuUsageGb, Is.Null);
            Assert.That(estimate.Summary, Does.Contain("Enter GPU VRAM"));
        });
    }
}
