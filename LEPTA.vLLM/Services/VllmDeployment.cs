using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class VllmDeployment(VllmDockerComposeBuilder? composeBuilder = null)
{
    private readonly VllmDockerComposeBuilder composeBuilder = composeBuilder ?? new VllmDockerComposeBuilder();

    public string Assemble(DockerComposeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Directory.CreateDirectory(configuration.ComposeDirectory);
        return composeBuilder.Build(configuration);
    }

    public VllmDockerDeploymentAssets AssembleAssets(DockerComposeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Directory.CreateDirectory(configuration.ComposeDirectory);
        return composeBuilder.BuildAssets(configuration);
    }

    public async Task<string> DeployAsync(
        DockerComposeConfiguration configuration,
        Func<string, CancellationToken, Task<DockerCommandResult>> dockerCommandRunner,
        bool includeDockerOutput,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dockerCommandRunner);

        var assets = AssembleAssets(configuration);
        var requiresRebuild = DeploymentAssetsHaveChanged(configuration, assets);
        await File.WriteAllTextAsync(configuration.ComposeFilePath, assets.ComposeText, cancellationToken);
        await File.WriteAllTextAsync(configuration.DockerfilePath, assets.DockerfileText, cancellationToken);
        await File.WriteAllTextAsync(configuration.EntrypointScriptPath, assets.EntrypointScriptText, cancellationToken);
        progress?.Report($"Compose file generated at {configuration.ComposeFilePath}.");
        progress?.Report($"Dockerfile generated at {configuration.DockerfilePath}.");
        progress?.Report($"Entrypoint script generated at {configuration.EntrypointScriptPath}.");
        if (requiresRebuild)
        {
            progress?.Report("Deployment assets changed. Rebuilding Docker image and recreating container...");
        }

        await RunComposeCommandAsync(
            configuration.ComposeFilePath,
            requiresRebuild ? "up -d --build --force-recreate" : "up -d",
            dockerCommandRunner,
            includeDockerOutput,
            progress,
            cancellationToken);
        return configuration.ComposeFilePath;
    }

    public async Task StopAsync(
        DockerComposeConfiguration configuration,
        Func<string, CancellationToken, Task<DockerCommandResult>> dockerCommandRunner,
        bool includeDockerOutput,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dockerCommandRunner);

        if (!File.Exists(configuration.ComposeFilePath))
        {
            progress?.Report("No compose file exists for this server yet.");
            return;
        }

        await RunComposeCommandAsync(configuration.ComposeFilePath, "down", dockerCommandRunner, includeDockerOutput, progress, cancellationToken);
    }

    private static async Task RunComposeCommandAsync(
        string composeFilePath,
        string arguments,
        Func<string, CancellationToken, Task<DockerCommandResult>> dockerCommandRunner,
        bool includeDockerOutput,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = await dockerCommandRunner($"compose -f \"{composeFilePath}\" {arguments}", cancellationToken);
        if (includeDockerOutput && !string.IsNullOrWhiteSpace(result.Output))
        {
            ReportDockerOutput(result.Output, progress);
        }

        if (includeDockerOutput && !string.IsNullOrWhiteSpace(result.Error))
        {
            ReportDockerOutput(result.Error, progress);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(VllmDeploymentService.TranslateDockerError(
                string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error,
                $"Docker compose failed with exit code {result.ExitCode}."));
        }
    }

    private static bool DeploymentAssetsHaveChanged(
        DockerComposeConfiguration configuration,
        VllmDockerDeploymentAssets assets)
        => HasFileChanged(configuration.ComposeFilePath, assets.ComposeText)
           || HasFileChanged(configuration.DockerfilePath, assets.DockerfileText)
           || HasFileChanged(configuration.EntrypointScriptPath, assets.EntrypointScriptText);

    private static bool HasFileChanged(string path, string newContent)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        return !string.Equals(File.ReadAllText(path), newContent, StringComparison.Ordinal);
    }

    private static void ReportDockerOutput(string text, IProgress<string>? progress)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            progress?.Report($"[docker] {line}");
        }
    }
}
