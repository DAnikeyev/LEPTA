using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class VllmDeploymentTests
{
    [Test]
    public void AssembleAssets_EmitsComposeDockerfileAndEntrypoint_ForLocalQwenModel()
    {
        var deployment = new VllmDeployment();
        var configuration = new DockerComposeConfiguration
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
                EnablePrefixCaching = true,
                MaxNumSeqs = 1,
                TensorParallelSize = 1,
                LanguageModelOnly = true,
                ReasoningParser = "qwen3",
                AdditionalVllmArguments = VllmServerConfiguration.QwenMtpSpeculativeArguments
            }
        };
        var assets = deployment.AssembleAssets(configuration);

        Assert.That(assets.ComposeText, Does.Contain("dockerfile: 'lepta-vllm-qwen-local.dockerfile'"));
        Assert.That(assets.ComposeText, Does.Contain("VLLM_PORT: '${VLLM_PORT:-8512}'"));
        Assert.That(assets.ComposeText, Does.Contain("TOKENIZERS_PARALLELISM: '${TOKENIZERS_PARALLELISM:-true}'"));
        Assert.That(assets.ComposeText, Does.Contain("source: 'D:/Models/Qwen3.5-9B-AWQ-4bit'"));
        Assert.That(assets.ComposeText, Does.Contain("target: '/models/Qwen3.5-9B-AWQ-4bit'"));
        Assert.That(assets.DockerfileText, Does.Contain("FROM vllm/vllm-openai:latest"));
        Assert.That(assets.DockerfileText, Does.Contain("ARG VLLM_PORT=8512"));
        Assert.That(assets.DockerfileText, Does.Contain("ARG TOKENIZERS_PARALLELISM=true"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("--model '/models/Qwen3.5-9B-AWQ-4bit'"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("--served-model-name 'Qwen3.5-9B-AWQ-4bit-local'"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("--quantization 'compressed-tensors'"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("--enable-prefix-caching"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("--language-model-only"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("--reasoning-parser 'qwen3'"));
        Assert.That(assets.EntrypointScriptText, Does.Not.Contain("--swap-space"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("--enable-log-requests"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("--speculative-config"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("'{\"method\":\"qwen3_next_mtp\",\"num_speculative_tokens\":2}'"));
    }

    [Test]
    public void AssembleAssets_UsesHuggingFaceModelIdAndDType_WhenNoLocalFolderIsProvided()
    {
        var deployment = new VllmDeployment();
        var configuration = new DockerComposeConfiguration
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
        };
        var assets = deployment.AssembleAssets(configuration);

        Assert.That(assets.ComposeText, Does.Contain("image: 'lepta-vllm-remote-hf-model:latest'"));
        Assert.That(assets.ComposeText, Does.Contain("VLLM_DTYPE: '${VLLM_DTYPE:-bfloat16}'"));
        Assert.That(assets.ComposeText, Does.Not.Contain("type: bind"));
        Assert.That(assets.DockerfileText, Does.Contain("FROM vllm/vllm-openai:v0.8.5"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("--model 'meta-llama/Llama-3.2-3B-Instruct'"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("--served-model-name 'llama-3.2-3b-local'"));
    }

    [Test]
    public void AssembleAssets_FallsBackToDefaultDockerImage_WhenConfiguredImageIsBlank()
    {
        var deployment = new VllmDeployment();
        var configuration = new DockerComposeConfiguration
        {
            ComposeDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            Server = new VllmServerConfiguration
            {
                Name = "Blank Image",
                DockerImage = "   ",
                LocalModelPath = @"D:\Models\Qwen3.5-9B-AWQ-4bit"
            }
        };

        var assets = deployment.AssembleAssets(configuration);

        Assert.That(assets.DockerfileText, Does.Contain($"FROM {VllmServerConfiguration.DefaultDockerImage}"));
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
                includeDockerOutput: true,
                new ImmediateProgress<string>(message => progressMessages.Add(message)));

            Assert.That(composePath, Is.EqualTo(configuration.ComposeFilePath));
            Assert.That(File.Exists(configuration.ComposeFilePath), Is.True);
            Assert.That(File.Exists(configuration.DockerfilePath), Is.True);
            Assert.That(File.Exists(configuration.EntrypointScriptPath), Is.True);
            Assert.That(await File.ReadAllTextAsync(configuration.EntrypointScriptPath), Does.Contain("Qwen3.5-9B-AWQ-4bit-local"));
            Assert.That(await File.ReadAllTextAsync(configuration.DockerfilePath), Does.Contain("ARG VLLM_PORT=8512"));
            Assert.That(await File.ReadAllTextAsync(configuration.EntrypointScriptPath), Does.Contain("--model '/models/Qwen3.5-9B-AWQ-4bit'"));
            Assert.That(commands, Is.EqualTo([$"compose -f \"{configuration.ComposeFilePath}\" up -d --build --force-recreate"]));
            Assert.That(progressMessages.Any(message => message.Contains("Compose file generated", StringComparison.Ordinal)), Is.True);
            Assert.That(progressMessages.Any(message => message.Contains("Dockerfile generated", StringComparison.Ordinal)), Is.True);
            Assert.That(progressMessages.Any(message => message.Contains("Entrypoint script generated", StringComparison.Ordinal)), Is.True);
            Assert.That(progressMessages.Any(message => message.Contains("Deployment assets changed", StringComparison.Ordinal)), Is.True);
            Assert.That(progressMessages.Any(message => message.Contains("[docker] started", StringComparison.Ordinal)), Is.True);
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
    public async Task DeployAsync_ReusesExistingImage_WhenGeneratedAssetsAreUnchanged()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var commands = new List<string>();

        try
        {
            var deployment = new VllmDeployment();
            var configuration = new DockerComposeConfiguration
            {
                ComposeDirectory = tempDirectory,
                Server = new VllmServerConfiguration
                {
                    Name = "Reuse Test",
                    Model = "meta-llama/Llama-3.2-3B-Instruct",
                    EnableVerboseLogs = false
                }
            };

            await deployment.DeployAsync(
                configuration,
                (arguments, _) =>
                {
                    commands.Add(arguments);
                    return Task.FromResult(new DockerCommandResult(0, "started", string.Empty));
                },
                includeDockerOutput: false);

            commands.Clear();

            await deployment.DeployAsync(
                configuration,
                (arguments, _) =>
                {
                    commands.Add(arguments);
                    return Task.FromResult(new DockerCommandResult(0, "started", string.Empty));
                },
                includeDockerOutput: false);

            Assert.That(commands, Is.EqualTo([$"compose -f \"{configuration.ComposeFilePath}\" up -d"]));
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
                includeDockerOutput: false,
                new ImmediateProgress<string>(message => progressMessages.Add(message)));

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

    [Test]
    public void AssembleAssets_ThrowsForMalformedAdditionalArguments()
    {
        var deployment = new VllmDeployment();

        Assert.That(
            () => deployment.AssembleAssets(new DockerComposeConfiguration
            {
                ComposeDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                Server = new VllmServerConfiguration
                {
                    Name = "Broken Flags",
                    Model = "meta-llama/Llama-3.2-3B-Instruct",
                    AdditionalVllmArguments = "--speculative-config 'broken"
                }
            }),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("unfinished quote or escape sequence"));
    }

    [Test]
    public void AssembleAssets_NormalizesLegacyAdditionalArgumentsBeforeWritingEntrypoint()
    {
        var deployment = new VllmDeployment();
        var assets = deployment.AssembleAssets(new DockerComposeConfiguration
        {
            ComposeDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            Server = new VllmServerConfiguration
            {
                Name = "Legacy Flags",
                Model = "meta-llama/Llama-3.2-3B-Instruct",
                AdditionalVllmArguments = "--swap-space 8 --disable-log-requests false --enable-log-requests=false"
            }
        });

        Assert.That(assets.EntrypointScriptText, Does.Not.Contain("--swap-space"));
        Assert.That(assets.EntrypointScriptText, Does.Not.Contain("--disable-log-requests"));
        Assert.That(assets.EntrypointScriptText, Does.Not.Contain("'false'"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("'--cpu-offload-gb'"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("'8'"));
        Assert.That(assets.EntrypointScriptText, Does.Contain("--no-enable-log-requests"));
    }
}

