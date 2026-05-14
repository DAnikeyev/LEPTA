using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class VllmModelMetadataScannerTests
{
    [Test]
    public async Task ScanAsync_ReadsQwenMetadataAndRecommendsCurrentDockerfileSettings()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"Qwen3.5-9B-AWQ-4bit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory, "config.json"),
                """
                {
                  "_name_or_path": "Qwen/Qwen3.5-9B-AWQ-4bit",
                  "architectures": ["Qwen3ForCausalLM"],
                  "hidden_size": 3584,
                  "intermediate_size": 18944,
                  "num_hidden_layers": 40,
                  "num_attention_heads": 28,
                  "num_key_value_heads": 4,
                  "max_position_embeddings": 131072,
                  "quantization_config": {
                    "quant_method": "compressed-tensors",
                    "bits": 4,
                    "format": "awq"
                  }
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory, "tokenizer_config.json"),
                """
                {
                  "model_max_length": 131072
                }
                """);

            var scanner = new VllmModelMetadataScanner();
            var metadata = await scanner.ScanAsync(tempDirectory);

            Assert.That(metadata.DisplayName, Does.StartWith("Qwen3.5-9B-AWQ-4bit-"));
            Assert.That(metadata.ModelId, Is.EqualTo("Qwen/Qwen3.5-9B-AWQ-4bit"));
            Assert.That(metadata.Architecture, Is.EqualTo("Qwen3ForCausalLM"));
            Assert.That(metadata.MaxTokenLength, Is.EqualTo(131072));
            Assert.That(metadata.HiddenSize, Is.EqualTo(3584));
            Assert.That(metadata.LayerCount, Is.EqualTo(40));
            Assert.That(metadata.ParameterCountBillions, Is.EqualTo(9d).Within(0.001));
            Assert.That(metadata.AvailableQuantizations, Does.Contain("compressed-tensors"));
            Assert.That(metadata.AvailableQuantizations, Does.Contain("AWQ"));
            Assert.That(metadata.AvailableQuantizations, Does.Contain("Int4"));
            Assert.That(metadata.PreferredWeightQuantization, Is.EqualTo("compressed-tensors"));
            Assert.That(metadata.PreferredKvCacheDType, Is.EqualTo("fp8"));
            Assert.That(metadata.RecommendedMaxModelLength, Is.EqualTo(5120));
            Assert.That(metadata.RecommendedGpuMemoryUtilization, Is.EqualTo(0.90).Within(0.0001));
            Assert.That(metadata.RecommendedMaxNumSeqs, Is.EqualTo(1));
            Assert.That(metadata.LanguageModelOnly, Is.True);
            Assert.That(metadata.EnablePrefixCaching, Is.True);
            Assert.That(metadata.ReasoningParser, Is.EqualTo("qwen3"));
            Assert.That(metadata.SuggestedServedModelName, Does.StartWith("Qwen3.5-9B-AWQ-4bit-"));
            Assert.That(metadata.BuildSummary(), Does.Contain("Max tokens"));
            Assert.That(metadata.BuildSummary(), Does.Contain("Recommended"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    public void ScanAsync_ThrowsForMissingDirectory()
    {
        var scanner = new VllmModelMetadataScanner();

        Assert.That(
            async () => await scanner.ScanAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            Throws.TypeOf<DirectoryNotFoundException>());
    }
}
