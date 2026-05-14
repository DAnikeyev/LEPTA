using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class VllmDeployment(VllmDockerComposeBuilder? composeBuilder = null)
{
    private readonly VllmDockerComposeBuilder composeBuilder = composeBuilder ?? new VllmDockerComposeBuilder();

    public string Assemble(DockerComposeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Directory.CreateDirectory(configuration.ComposeDirectory);
        return composeBuilder.Build(configuration.Server);
    }

    public async Task<string> DeployAsync(
        DockerComposeConfiguration configuration,
        Func<string, CancellationToken, Task<DockerCommandResult>> dockerCommandRunner,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dockerCommandRunner);

        var compose = Assemble(configuration);
        await File.WriteAllTextAsync(configuration.ComposeFilePath, compose, cancellationToken);
        progress?.Report($"Compose file generated at {configuration.ComposeFilePath}.");
        await RunComposeCommandAsync(configuration.ComposeFilePath, "up -d", dockerCommandRunner, progress, cancellationToken);
        return configuration.ComposeFilePath;
    }

    public async Task StopAsync(
        DockerComposeConfiguration configuration,
        Func<string, CancellationToken, Task<DockerCommandResult>> dockerCommandRunner,
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

        await RunComposeCommandAsync(configuration.ComposeFilePath, "down", dockerCommandRunner, progress, cancellationToken);
    }

    private static async Task RunComposeCommandAsync(
        string composeFilePath,
        string arguments,
        Func<string, CancellationToken, Task<DockerCommandResult>> dockerCommandRunner,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = await dockerCommandRunner($"compose -f \"{composeFilePath}\" {arguments}", cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            progress?.Report(result.Output.Trim());
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            progress?.Report(result.Error.Trim());
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(VllmDeploymentService.TranslateDockerError(
                string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error,
                $"Docker compose failed with exit code {result.ExitCode}."));
        }
    }
}
