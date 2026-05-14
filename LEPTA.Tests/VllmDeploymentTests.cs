using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class VllmDeploymentTests
{
    [Test]
    public void Assemble_EmitsDockerfileCompatibleFlags_ForLocalQwenModel()
    {
        var deployment = new VllmDeployment();
        var compose = deployment.Assemble(new DockerComposeConfiguration
        {
            ComposeDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            Server = new VllmServerConfiguration
            {
                Name = "Qwen Local",
                LocalModelPath = @"D:\Models\Qwen3.5-9B-AWQ-4bit",
                HostPort = 8512,
                ServedModelName = "Qwen3.5-9B-AWQ-4bit-local",
                WeightQuantization = "compressed-tensors",
                GpuMemoryUtilization = 0.90,
                MaxModelLength = 5120,
                KvCacheDType = "fp8",
                SwapSpaceGb = 8,
                EnablePrefixCaching = true,
                MaxNumSeqs = 1,
                TensorParallelSize = 1,
                LanguageModelOnly = true,
                ReasoningParser = "qwen3"
            }
        });

        Assert.That(compose, Does.Contain("'8512:8000'"));
        Assert.That(compose, Does.Contain("D:/Models/Qwen3.5-9B-AWQ-4bit:/models/active:ro"));
        Assert.That(compose, Does.Contain("- '--served-model-name'"));
        Assert.That(compose, Does.Contain("- 'Qwen3.5-9B-AWQ-4bit-local'"));
        Assert.That(compose, Does.Contain("- '--quantization'"));
        Assert.That(compose, Does.Contain("- 'compressed-tensors'"));
        Assert.That(compose, Does.Contain("- '--enable-prefix-caching'"));
        Assert.That(compose, Does.Contain("- '--language-model-only'"));
        Assert.That(compose, Does.Contain("- '--reasoning-parser'"));
        Assert.That(compose, Does.Contain("- 'qwen3'"));
        Assert.That(compose, Does.Contain("- '--swap-space'"));
    }

    [Test]
    public void Assemble_UsesHuggingFaceModelIdAndDType_WhenNoLocalFolderIsProvided()
    {
        var deployment = new VllmDeployment();
        var compose = deployment.Assemble(new DockerComposeConfiguration
        {
            ComposeDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            Server = new VllmServerConfiguration
            {
                Name = "Remote HF model",
                Model = "meta-llama/Llama-3.2-3B-Instruct",
                DockerImage = "vllm/vllm-openai:v0.8.5",
                HostPort = 8611,
                DType = "bfloat16",
                ServedModelName = "llama-3.2-3b-local"
            }
        });

        Assert.That(compose, Does.Contain("image: 'vllm/vllm-openai:v0.8.5'"));
        Assert.That(compose, Does.Contain("- 'meta-llama/Llama-3.2-3B-Instruct'"));
        Assert.That(compose, Does.Not.Contain(":/models/active:ro"));
        Assert.That(compose, Does.Contain("- '--dtype'"));
        Assert.That(compose, Does.Contain("- 'bfloat16'"));
        Assert.That(compose, Does.Contain("- 'llama-3.2-3b-local'"));
    }

    [Test]
    public async Task DeployAsync_WritesComposeAndRunsDockerComposeUp()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var commands = new List<string>();
        var progressMessages = new List<string>();

        try
        {
            var deployment = new VllmDeployment();
            var configuration = new DockerComposeConfiguration
            {
                ComposeDirectory = tempDirectory,
                Server = new VllmServerConfiguration
                {
                    Name = "Deploy Test",
                    LocalModelPath = @"D:\Models\Qwen3.5-9B-AWQ-4bit",
                    ServedModelName = "Qwen3.5-9B-AWQ-4bit-local",
                    WeightQuantization = "compressed-tensors"
                }
            };

            var composePath = await deployment.DeployAsync(
                configuration,
                (arguments, _) =>
                {
                    commands.Add(arguments);
                    return Task.FromResult(new DockerCommandResult(0, "started", string.Empty));
                },
                new Progress<string>(message => progressMessages.Add(message)));

            Assert.That(composePath, Is.EqualTo(configuration.ComposeFilePath));
            Assert.That(File.Exists(configuration.ComposeFilePath), Is.True);
            Assert.That(await File.ReadAllTextAsync(configuration.ComposeFilePath), Does.Contain("Qwen3.5-9B-AWQ-4bit-local"));
            Assert.That(commands, Is.EqualTo([$"compose -f \"{configuration.ComposeFilePath}\" up -d"]));
            Assert.That(progressMessages.Any(message => message.Contains("Compose file generated", StringComparison.Ordinal)), Is.True);
            Assert.That(progressMessages.Any(message => message.Contains("started", StringComparison.Ordinal)), Is.True);
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
    public async Task StopAsync_SkipsDocker_WhenComposeFileDoesNotExist()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var commands = new List<string>();
        var progressMessages = new List<string>();

        try
        {
            var deployment = new VllmDeployment();
            await deployment.StopAsync(
                new DockerComposeConfiguration
                {
                    ComposeDirectory = tempDirectory,
                    Server = new VllmServerConfiguration { Name = "Stop Test" }
                },
                (arguments, _) =>
                {
                    commands.Add(arguments);
                    return Task.FromResult(new DockerCommandResult(0, string.Empty, string.Empty));
                },
                new Progress<string>(message => progressMessages.Add(message)));

            Assert.That(commands, Is.Empty);
            Assert.That(progressMessages, Contains.Item("No compose file exists for this server yet."));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}

