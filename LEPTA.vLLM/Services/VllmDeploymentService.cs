using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json;
using LEPTA.Shared.Diagnostics;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class VllmDeploymentService
{
    private static readonly TimeSpan DeploymentReadyTimeout = TimeSpan.FromMinutes(2);
    private readonly VllmDeployment deployment = new();
    private readonly VllmModelMetadataScanner modelMetadataScanner = new();
    private readonly HttpClient httpClient;
    private readonly Func<string, CancellationToken, Task<DockerCommandResult>> dockerCommandRunner;
    private readonly ILeptaLogger logger;
    private readonly VllmServerProfileValidator serverProfileValidator;

    public VllmDeploymentService(
        HttpClient? httpClient = null,
        ILeptaLogger? logger = null,
        VllmServerProfileValidator? serverProfileValidator = null,
        Func<string, CancellationToken, Task<DockerCommandResult>>? dockerCommandRunner = null)
    {
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        this.logger = logger ?? NullLeptaLogger.Instance;
        this.serverProfileValidator = serverProfileValidator ?? new VllmServerProfileValidator();
        this.dockerCommandRunner = dockerCommandRunner ?? RunDockerProcessCoreAsync;
    }

    public async Task DeployAsync(VllmServerConfiguration configuration, string composeDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        logger.Log(nameof(VllmDeploymentService), $"Deploy requested for server '{configuration.Name}'. endpoint={configuration.Endpoint}, composeDirectory={composeDirectory}.");
        progress?.Report("Validating Docker deployment settings...");
        var validation = await ValidateDeploymentAsync(configuration, composeDirectory, cancellationToken);
        foreach (var warning in validation.Warnings)
        {
            progress?.Report($"Warning: {warning}");
        }

        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.BuildDisplayMessage());
        }

        var composeConfiguration = CreateComposeConfiguration(configuration, composeDirectory);
        string? composePath = null;

        try
        {
            progress?.Report("Generating compose file and starting Docker container...");
            composePath = await deployment.DeployAsync(composeConfiguration, dockerCommandRunner, progress, cancellationToken);
            logger.Log(nameof(VllmDeploymentService), $"Compose file written to {composePath}.");
            progress?.Report("Docker start requested. Waiting for /v1/models...");
            var probe = await WaitForServerAsync(configuration, progress, cancellationToken);
            progress?.Report($"Deployment ready. Using '{probe.FirstModelName}' from /v1/models.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (configuration.EnableVerboseLogs && composePath is not null)
            {
                await TryCollectDockerLogsAsync(composePath, progress, cancellationToken);
            }

            throw;
        }

        logger.Log(nameof(VllmDeploymentService), $"Compose file written to {composePath}.");
        if (configuration.EnableVerboseLogs)
        {
            progress?.Report("Collecting verbose docker logs...");
            await RunDockerComposeAsync(composePath!, "logs --tail 200", progress, cancellationToken);
        }

        logger.Log(nameof(VllmDeploymentService), $"Deploy request submitted for server '{configuration.Name}'.");
    }

    public async Task RestartAsync(VllmServerConfiguration configuration, string composeDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        logger.Log(nameof(VllmDeploymentService), $"Restart requested for server '{configuration.Name}'. endpoint={configuration.Endpoint}.");
        progress?.Report("Stopping existing Docker deployment before restart...");
        await StopAsync(configuration, composeDirectory, progress, cancellationToken);
        progress?.Report("Restarting Docker deployment...");
        await DeployAsync(configuration, composeDirectory, progress, cancellationToken);
    }

    public async Task StopAsync(VllmServerConfiguration configuration, string composeDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        logger.Log(nameof(VllmDeploymentService), $"Stop requested for server '{configuration.Name}'. endpoint={configuration.Endpoint}.");
        progress?.Report("Checking Docker availability...");
        await EnsureDockerAvailableAsync(cancellationToken);
        var composeConfiguration = CreateComposeConfiguration(configuration, composeDirectory);
        if (!File.Exists(composeConfiguration.ComposeFilePath))
        {
            progress?.Report("No compose file exists for this server yet.");
            logger.Log(nameof(VllmDeploymentService), $"Stop skipped for server '{configuration.Name}' because {composeConfiguration.ComposeFilePath} does not exist.");
            return;
        }

        await deployment.StopAsync(composeConfiguration, dockerCommandRunner, progress, cancellationToken);
        progress?.Report("Docker stop requested.");
        logger.Log(nameof(VllmDeploymentService), $"Stop request submitted for server '{configuration.Name}'.");
    }

    public Task<VllmModelMetadata> ScanLocalModelAsync(string modelDirectory, CancellationToken cancellationToken = default)
    {
        logger.Log(nameof(VllmDeploymentService), $"Scanning local model metadata from '{modelDirectory}'.");
        return modelMetadataScanner.ScanAsync(modelDirectory, cancellationToken);
    }

    public VllmServerValidationResult ValidateServerEndpoint(VllmServerConfiguration configuration)
    {
        if (configuration.UseExistingHttpServer)
        {
            return serverProfileValidator.ValidateExternalEndpoint(configuration.HttpServerAddress, configuration.HostPort);
        }

        return new VllmServerValidationResult(true, configuration.Endpoint, $"Using {configuration.Endpoint}.");
    }

    public async Task<VllmServerProbeResult> ProbeHttpServerAsync(VllmServerConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var validation = ValidateServerEndpoint(configuration);
        if (!validation.IsValid || string.IsNullOrWhiteSpace(validation.NormalizedEndpoint))
        {
            logger.Log(nameof(VllmDeploymentService), $"Server validation failed for '{configuration.Name}'. reason={validation.Message}");
            return VllmServerProbeResult.Failure(
                string.IsNullOrWhiteSpace(configuration.HttpServerAddress)
                    ? VllmServerProbeStatus.EmptyEndpoint
                    : VllmServerProbeStatus.InvalidEndpoint,
                validation.Message,
                validation.NormalizedEndpoint);
        }

        if (configuration.UseExistingHttpServer
            && !string.Equals(configuration.HttpServerAddress, validation.NormalizedEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            configuration.HttpServerAddress = validation.NormalizedEndpoint;
        }

        var endpoint = validation.NormalizedEndpoint;
        logger.Log(nameof(VllmDeploymentService), $"Checking model accessibility via GET {endpoint}/v1/models.");

        try
        {
            using var response = await httpClient.GetAsync($"{endpoint}/v1/models", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = $"The server at {endpoint} returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) from /v1/models.";
                logger.Log(nameof(VllmDeploymentService), $"Accessibility check failed with status {(int)response.StatusCode}.");
                return VllmServerProbeResult.Failure(VllmServerProbeStatus.HttpError, message, endpoint);
            }

            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (!TryReadModelNames(payload, out var modelNames))
            {
                const string invalidPayloadMessage = "The server responded to /v1/models, but the payload did not contain the expected model list.";
                logger.Log(nameof(VllmDeploymentService), invalidPayloadMessage);
                return VllmServerProbeResult.Failure(VllmServerProbeStatus.InvalidResponse, invalidPayloadMessage, endpoint);
            }

            if (modelNames.Count == 0)
            {
                var emptyMessage = $"The server at {endpoint} responded to /v1/models but returned an empty model list.";
                logger.Log(nameof(VllmDeploymentService), emptyMessage);
                return VllmServerProbeResult.Failure(VllmServerProbeStatus.EmptyModelList, emptyMessage, endpoint);
            }

            var successMessage = $"{endpoint} is reachable. Found {modelNames.Count} served model(s).";
            logger.Log(nameof(VllmDeploymentService), $"Accessibility check completed. accessible=true, modelCount={modelNames.Count}.");
            return VllmServerProbeResult.Success(endpoint, modelNames, successMessage);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var message = $"Timed out while contacting {endpoint}. Verify the server address and confirm /v1/models is reachable.";
            logger.Log(nameof(VllmDeploymentService), $"Accessibility check timed out for {endpoint}: {exception.Message}");
            return VllmServerProbeResult.Failure(VllmServerProbeStatus.Unreachable, message, endpoint);
        }
        catch (HttpRequestException exception)
        {
            var message = $"Could not reach {endpoint}. Verify the server address and confirm the server is running.";
            logger.Log(nameof(VllmDeploymentService), $"Accessibility check threw {exception.GetType().Name}: {exception.Message}");
            return VllmServerProbeResult.Failure(VllmServerProbeStatus.Unreachable, message, endpoint);
        }
        catch (JsonException exception)
        {
            var message = $"The server at {endpoint} returned invalid JSON from /v1/models.";
            logger.Log(nameof(VllmDeploymentService), $"Accessibility check threw {exception.GetType().Name}: {exception.Message}");
            return VllmServerProbeResult.Failure(VllmServerProbeStatus.InvalidResponse, message, endpoint);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var message = $"Could not verify {endpoint}: {exception.Message}";
            logger.Log(nameof(VllmDeploymentService), $"Accessibility check threw {exception.GetType().Name}: {exception.Message}");
            return VllmServerProbeResult.Failure(VllmServerProbeStatus.Unreachable, message, endpoint);
        }
    }

    public async Task<bool> IsAccessibleAsync(VllmServerConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeHttpServerAsync(configuration, cancellationToken);
        return probe.IsSuccess;
    }

    public async Task<VllmDeploymentValidationResult> ValidateDeploymentAsync(
        VllmServerConfiguration configuration,
        string composeDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<string>();
        var warnings = new List<string>();

        if (configuration.UseExistingHttpServer)
        {
            errors.Add("Switch the source to 'Deploy local folder with Docker' before starting a managed deployment.");
        }

        if (string.IsNullOrWhiteSpace(configuration.DockerImage))
        {
            errors.Add("Enter a Docker image, for example vllm/vllm-openai:latest.");
        }

        if (configuration.HostPort is < 1 or > 65535)
        {
            errors.Add("Choose a host port between 1 and 65535.");
        }

        if (configuration.GpuMemoryUtilization is <= 0 or > 1.0)
        {
            errors.Add("GPU memory utilization must be greater than 0 and at most 1.0.");
        }

        if (configuration.MaxModelLength <= 0)
        {
            errors.Add("Max model length must be greater than 0.");
        }

        if (configuration.TensorParallelSize <= 0)
        {
            errors.Add("Tensor parallel size must be at least 1.");
        }

        if (configuration.MaxNumSeqs <= 0)
        {
            errors.Add("Max parallel sequences must be at least 1.");
        }

        if (configuration.CpuOffloadGb < 0)
        {
            errors.Add("CPU offload cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(configuration.Model) && string.IsNullOrWhiteSpace(configuration.LocalModelPath))
        {
            errors.Add("Provide either a local Hugging Face folder or a Hugging Face model ID.");
        }

        if (!string.IsNullOrWhiteSpace(configuration.LocalModelPath))
        {
            configuration.LocalModelPath = Path.GetFullPath(configuration.LocalModelPath.Trim());
            if (!Directory.Exists(configuration.LocalModelPath))
            {
                errors.Add($"The local model folder '{configuration.LocalModelPath}' does not exist.");
            }
        }

        if (string.IsNullOrWhiteSpace(configuration.ContainerName)
            || !Regex.IsMatch(configuration.ContainerName, "^[a-z0-9][a-z0-9_.-]*$", RegexOptions.CultureInvariant))
        {
            errors.Add("The generated Docker container name is not valid. Rename the server so it contains at least one letter or number.");
        }

        var dockerStatus = await GetDockerAvailabilityAsync(cancellationToken);
        if (!dockerStatus.IsAvailable)
        {
            errors.Add(dockerStatus.Details);
        }
        else
        {
            var existingContainerNames = await GetExistingContainerNamesAsync(cancellationToken);
            var composeConfiguration = CreateComposeConfiguration(configuration, composeDirectory);
            if (existingContainerNames.Contains(configuration.ContainerName, StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(composeConfiguration.ComposeFilePath))
                {
                    warnings.Add($"Docker already has a container named '{configuration.ContainerName}'. LEPTA will reuse the saved compose file at {composeConfiguration.ComposeFilePath}.");
                }
                else
                {
                    errors.Add($"Docker already has a container named '{configuration.ContainerName}'. Rename this server or remove the existing container before deploying.");
                }
            }
        }

        if (configuration.HostPort > 0 && IsLocalPortInUse(configuration.HostPort))
        {
            warnings.Add($"Port {configuration.HostPort} is already in use on this machine. Docker compose may fail unless that port becomes free before deployment.");
        }

        return new VllmDeploymentValidationResult(errors, warnings);
    }

    public async Task<DockerStatusInfo> GetDockerStatusAsync(CancellationToken cancellationToken = default)
    {
        logger.Log(nameof(VllmDeploymentService), "Checking Docker engine status.");
        try
        {
            var result = await dockerCommandRunner("info --format \"{{.ServerVersion}}\"", cancellationToken);
            if (result.ExitCode == 0)
            {
                var version = FirstNonEmpty(result.Output, result.Error);
                var details = string.IsNullOrWhiteSpace(version)
                    ? "Docker engine is reachable."
                    : $"Docker engine is reachable. Server version: {version}.";
                logger.Log(nameof(VllmDeploymentService), $"Docker status check succeeded. version={version}.");
                return new DockerStatusInfo(true, "Ready", details);
            }

            logger.Log(nameof(VllmDeploymentService), $"Docker status check failed with exit code {result.ExitCode}.");
            return new DockerStatusInfo(false, "Unavailable", ToStatusDetails(TranslateDockerError(FirstNonEmpty(result.Error, result.Output), "Docker is not available.")));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Log(nameof(VllmDeploymentService), $"Docker status check threw {exception.GetType().Name}: {exception.Message}");
            return new DockerStatusInfo(false, "Unavailable", ToStatusDetails(exception.Message));
        }
    }

    public async Task<string> GetFirstModelNameAsync(VllmServerConfiguration configuration, CancellationToken cancellationToken = default)
    {
        logger.Log(nameof(VllmDeploymentService), $"Resolving first served model via GET {configuration.Endpoint}/v1/models.");
        var probe = await ProbeHttpServerAsync(configuration, cancellationToken);
        if (!probe.IsSuccess || string.IsNullOrWhiteSpace(probe.FirstModelName))
        {
            throw new InvalidOperationException(probe.Message);
        }

        var modelName = probe.FirstModelName;
        logger.Log(nameof(VllmDeploymentService), $"Resolved served model '{modelName}'.");
        return modelName;
    }

    public DockerComposeConfiguration CreateComposeConfiguration(VllmServerConfiguration configuration, string composeDirectory)
        => new()
        {
            Server = configuration,
            ComposeDirectory = composeDirectory
        };

    public static string TranslateDockerError(string? details, string fallbackMessage = "Docker command failed.")
    {
        var text = details?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallbackMessage;
        }

        if (text.Contains("dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase)
            || text.Contains("pipe/dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase))
        {
            return $"Docker Desktop's Linux engine is not available. Start Docker Desktop and switch to Linux containers, then retry.{Environment.NewLine}Raw Docker output: {text}";
        }

        if (text.Contains("open //./pipe/docker_engine", StringComparison.OrdinalIgnoreCase)
            || text.Contains("the docker daemon is not running", StringComparison.OrdinalIgnoreCase)
            || text.Contains("error during connect", StringComparison.OrdinalIgnoreCase))
        {
            return $"Docker is installed but the daemon is not reachable. Start Docker Desktop and wait until it reports that the engine is running, then retry.{Environment.NewLine}Raw Docker output: {text}";
        }

        if (text.Contains("is not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
        {
            return $"Docker CLI was not found. Install Docker Desktop and ensure the 'docker' command is available on PATH.{Environment.NewLine}Raw Docker output: {text}";
        }

        return $"{fallbackMessage}{Environment.NewLine}Raw Docker output: {text}";
    }

    private async Task EnsureDockerAvailableAsync(CancellationToken cancellationToken)
    {
        var status = await GetDockerAvailabilityAsync(cancellationToken);
        if (status.IsAvailable)
        {
            return;
        }

        logger.Log(nameof(VllmDeploymentService), $"Docker availability check failed: {status.Details}");
        throw new InvalidOperationException(status.Details);
    }

    private async Task RunDockerComposeAsync(string composePath, string arguments, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var result = await dockerCommandRunner($"compose -f \"{composePath}\" {arguments}", cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.Output)) progress?.Report(result.Output.Trim());
        if (!string.IsNullOrWhiteSpace(result.Error)) progress?.Report(result.Error.Trim());

        if (result.ExitCode != 0)
        {
            logger.Log(nameof(VllmDeploymentService), $"Docker compose command failed. composePath={composePath}, arguments={arguments}, exitCode={result.ExitCode}.");
            throw new InvalidOperationException(TranslateDockerError(FirstNonEmpty(result.Error, result.Output), $"Docker compose failed with exit code {result.ExitCode}."));
        }

        logger.Log(nameof(VllmDeploymentService), $"Docker compose command succeeded. composePath={composePath}, arguments={arguments}.");
    }

    private async Task<DockerCommandResult> RunDockerProcessCoreAsync(string arguments, CancellationToken cancellationToken)
    {
        logger.Log(nameof(VllmDeploymentService), $"Executing docker {arguments}");
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Failed to start Docker. Make sure Docker Desktop is installed and running.");

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var result = new DockerCommandResult(process.ExitCode, await outputTask, await errorTask);
            logger.Log(nameof(VllmDeploymentService), $"Docker command finished with exitCode={result.ExitCode}. outputLength={result.Output.Length}, errorLength={result.Error.Length}.");
            return result;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2 || exception.NativeErrorCode == 3)
        {
            logger.Log(nameof(VllmDeploymentService), $"Docker command failed to start: {exception.Message}");
            throw new InvalidOperationException(TranslateDockerError(exception.Message, "Docker CLI was not found."), exception);
        }
    }

    private async Task<DockerStatusInfo> GetDockerAvailabilityAsync(CancellationToken cancellationToken)
    {
        var result = await dockerCommandRunner("info --format \"{{.ServerVersion}}\"", cancellationToken);
        if (result.ExitCode == 0)
        {
            return new DockerStatusInfo(true, "Ready", FirstNonEmpty(result.Output, result.Error));
        }

        return new DockerStatusInfo(false, "Unavailable", TranslateDockerError(FirstNonEmpty(result.Error, result.Output), "Docker is not available."));
    }

    private static string FirstNonEmpty(string? primary, string? secondary)
        => !string.IsNullOrWhiteSpace(primary)
            ? primary.Trim()
            : secondary?.Trim() ?? string.Empty;

    private static string ToStatusDetails(string message)
        => message.Split(new[] { $"{Environment.NewLine}Raw Docker output:" }, 2, StringSplitOptions.None)[0].Trim();

    private async Task<IReadOnlyList<string>> GetExistingContainerNamesAsync(CancellationToken cancellationToken)
    {
        var result = await dockerCommandRunner("ps -a --format \"{{.Names}}\"", cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(TranslateDockerError(FirstNonEmpty(result.Error, result.Output), "Docker could not list existing containers."));
        }

        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static bool IsLocalPortInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }

    private async Task<VllmServerProbeResult> WaitForServerAsync(
        VllmServerConfiguration configuration,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var timeoutAt = DateTimeOffset.UtcNow + DeploymentReadyTimeout;
        VllmServerProbeResult? lastProbe = null;

        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastProbe = await ProbeHttpServerAsync(configuration, cancellationToken);
            if (lastProbe.IsSuccess)
            {
                return lastProbe;
            }

            progress?.Report($"Waiting for {configuration.Endpoint}/v1/models... {lastProbe.Message}");
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        throw new InvalidOperationException(
            $"Docker reported that the deployment started, but {configuration.Endpoint}/v1/models did not become reachable within {DeploymentReadyTimeout.TotalMinutes:0} minutes. Last result: {lastProbe?.Message ?? "No probe result was captured."}");
    }

    private async Task TryCollectDockerLogsAsync(string composePath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report("Deployment failed. Collecting docker logs...");
            await RunDockerComposeAsync(composePath, "logs --tail 200", progress, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Log(nameof(VllmDeploymentService), $"Docker log collection failed for composePath={composePath}. reason={exception.Message}");
        }
    }

    private static bool TryReadModelNames(JsonElement payload, out IReadOnlyList<string> modelNames)
    {
        modelNames = Array.Empty<string>();
        if (!payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var results = new List<string>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.TryGetProperty("id", out var idElement))
            {
                var modelName = idElement.GetString();
                if (!string.IsNullOrWhiteSpace(modelName))
                {
                    results.Add(modelName.Trim());
                }
            }
        }

        modelNames = results;
        return true;
    }

}

