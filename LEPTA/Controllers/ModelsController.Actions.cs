using System.IO;
using System.Windows;
using LEPTA.Shared.Diagnostics;
using LEPTA.Theming;
using LEPTA.vLLM.Models;
using Microsoft.Win32;

namespace LEPTA.Controllers;

internal sealed partial class ModelsController
{
    public async Task BrowseModelAsync(Window owner)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a local vLLM-compatible Hugging Face-format model folder"
        };

        if (dialog.ShowDialog(owner) == true)
        {
            SetDeploymentMode(useExistingHttpServer: false);
            config.LocalPathBox.Text = dialog.FolderName;
            logger.Log(nameof(ModelsController), $"Selected local model folder '{dialog.FolderName}'.");
            await ScanSelectedModelAsync();
        }
    }

    public Task ScanSelectedModelAsync()
    {
        if (SelectedServer is not { } server)
        {
            return Task.CompletedTask;
        }

        var localModelPath = string.IsNullOrWhiteSpace(config.LocalPathBox.Text) ? server.LocalModelPath : config.LocalPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(localModelPath))
        {
            AppendLog("Select a local folder before scanning model metadata.");
            return Task.CompletedTask;
        }

        SetDeploymentMode(useExistingHttpServer: false);
        server.LocalModelPath = localModelPath;
        return ExecuteDeploymentActionAsync(server, "Scanning model metadata...", async (selectedServer, progress, cancellationToken) =>
        {
            var metadata = await deploymentService.ScanLocalModelAsync(localModelPath, cancellationToken);
            ApplyLocalModelMetadata(selectedServer, metadata);
            progress.Report($"Scanned metadata for '{metadata.DisplayName}'.");
            progress.Report(metadata.BuildSummary());
        });
    }

    public async Task StartSelectedServerAsync()
    {
        if (SelectedServer is null)
        {
            return;
        }

        if (SelectedServer.UseExistingHttpServer)
        {
            logger.Log(nameof(ModelsController), $"Start/Test requested for externally managed server '{SelectedServer.Name}'.");
            await ExecuteDeploymentActionAsync(SelectedServer, "Checking /v1/models...", async (server, progress, cancellationToken) =>
            {
                var probe = await deploymentService.ProbeHttpServerAsync(server, cancellationToken);
                if (!probe.IsSuccess)
                {
                    throw new InvalidOperationException(probe.Message);
                }

                progress.Report(probe.Message);
                progress.Report($"Chat and LEPTA can now use '{probe.FirstModelName}' from /v1/models.");
            });
            await RefreshSelectedServerStatusAsync();
            return;
        }

        if (!string.IsNullOrWhiteSpace(SelectedServer.LocalModelPath)
            && SelectedServer.AvailableWeightQuantizations.Count == 0
            && Directory.Exists(SelectedServer.LocalModelPath))
        {
            await ScanSelectedModelAsync();
        }

        logger.Log(nameof(ModelsController), $"Deployment start requested for '{SelectedServer.Name}'.");
        await ExecuteDeploymentActionAsync(
            SelectedServer,
            "Starting deployment...",
            (server, progress, cancellationToken) => deploymentService.DeployAsync(server, composeDirectory, progress, cancellationToken),
            allowStopWhileBusy: true);
        await RefreshSelectedServerStatusAsync();
    }

    public async Task StopSelectedServerAsync()
    {
        var selectedServer = SelectedServer;
        if (selectedServer is null)
        {
            return;
        }

        if (selectedServer.UseExistingHttpServer)
        {
            deploy.DeploymentLogBox.Clear();
            AppendLog($"{selectedServer.Endpoint} is managed externally, so LEPTA will not stop it.");
            logger.Log(nameof(ModelsController), $"Stop skipped for externally managed server '{selectedServer.Name}'.");
            PublishAction($"Stop skipped for '{selectedServer.Name}' because the HTTP server is managed externally.", ActionLogLevel.Warning);
            return;
        }

        if (currentActionCancellation is not null)
        {
            var cancellationCompleted = await CancelCurrentDeploymentActionAsync(
                $"Cancelling current deployment action for '{selectedServer.Name}' before stopping the server...",
                $"Cancelling in-flight deployment action for '{selectedServer.Name}'.",
                $"Cancelling the active deployment task for '{selectedServer.Name}' before stopping it.");
            if (!cancellationCompleted)
            {
                return;
            }
        }

        logger.Log(nameof(ModelsController), $"Deployment stop requested for '{selectedServer.Name}'.");
        await ExecuteDeploymentActionAsync(
            selectedServer,
            "Stopping deployment...",
            (server, progress, cancellationToken) => deploymentService.StopAsync(server, composeDirectory, progress, cancellationToken));
        await RefreshSelectedServerStatusAsync();
    }

    public async Task RestartSelectedServerAsync()
    {
        var selectedServer = SelectedServer;
        if (selectedServer is null)
        {
            return;
        }

        if (selectedServer.UseExistingHttpServer)
        {
            deploy.DeploymentLogBox.Clear();
            AppendLog($"{selectedServer.Endpoint} is managed externally, so LEPTA cannot restart it.");
            logger.Log(nameof(ModelsController), $"Restart skipped for externally managed server '{selectedServer.Name}'.");
            PublishAction($"Restart skipped for '{selectedServer.Name}' because the HTTP server is managed externally.", ActionLogLevel.Warning);
            return;
        }

        if (!string.IsNullOrWhiteSpace(selectedServer.LocalModelPath)
            && selectedServer.AvailableWeightQuantizations.Count == 0
            && Directory.Exists(selectedServer.LocalModelPath))
        {
            await ScanSelectedModelAsync();
        }

        logger.Log(nameof(ModelsController), $"Deployment restart requested for '{selectedServer.Name}'.");
        if (currentActionCancellation is not null)
        {
            var cancellationCompleted = await CancelCurrentDeploymentActionAsync(
                $"Cancelling current deployment action for '{selectedServer.Name}' before restarting the server...",
                $"Cancelling in-flight deployment action for '{selectedServer.Name}' before restart.",
                $"Cancelling the active deployment task for '{selectedServer.Name}' before restarting it.");
            if (!cancellationCompleted)
            {
                return;
            }
        }

        await ExecuteDeploymentActionAsync(
            selectedServer,
            "Restarting deployment...",
            (server, progress, cancellationToken) => deploymentService.RestartAsync(server, composeDirectory, progress, cancellationToken),
            allowStopWhileBusy: true);
        await RefreshSelectedServerStatusAsync();
    }

    public Task TestSelectedServerAsync()
    {
        if (SelectedServer is null)
        {
            return Task.CompletedTask;
        }

        logger.Log(nameof(ModelsController), $"Connectivity test requested for '{SelectedServer.Name}'.");

        return TestAndRefreshSelectedServerAsync();
    }

    public async Task RefreshSelectedServerStatusAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedServer is not { } server)
        {
            return;
        }

        await RefreshServerStatusAsync(server, cancellationToken);
    }

    public async Task RefreshAllServerStatusesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var server in servers)
        {
            await RefreshServerStatusAsync(server, cancellationToken);
        }
    }

    public async Task RefreshDockerStatusAsync(CancellationToken cancellationToken = default)
    {
        logger.Log(nameof(ModelsController), "Docker status refresh requested.");
        SetDockerStatusState("Checking...", "Verifying Docker Desktop and engine availability.", ThemeResourceKeys.SecondaryTextBrush);
        var status = await deploymentService.GetDockerStatusAsync(cancellationToken);
        SetDockerStatusState(status.Summary, status.Details, status.IsAvailable ? ThemeResourceKeys.SuccessBrush : ThemeResourceKeys.ErrorBrush);
        logger.Log(nameof(ModelsController), $"Docker status refresh completed. summary={status.Summary}.");
    }

    public async Task StopForShutdownAsync(VllmServerConfiguration server)
    {
        if (server.UseExistingHttpServer)
        {
            return;
        }

        logger.Log(nameof(ModelsController), $"Shutdown stop requested for '{server.Name}'.");

        if (currentActionCancellation is not null)
        {
            var cancellationCompleted = await CancelCurrentDeploymentActionAsync(
                $"Cancelling the active deployment action for '{server.Name}' before shutdown...",
                $"Cancelling in-flight deployment action for '{server.Name}' during shutdown.",
                $"Cancelling the active deployment task for '{server.Name}' during shutdown.");
            if (!cancellationCompleted)
            {
                return;
            }
        }

        await ExecuteDeploymentActionAsync(
            server,
            "Stopping deployment before closing...",
            (selectedServer, progress, cancellationToken) => deploymentService.StopAsync(selectedServer, composeDirectory, progress, cancellationToken));
    }

    public async Task CancelForShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var cancellation = currentActionCancellation;
        if (cancellation is null)
        {
            return;
        }

        logger.Log(nameof(ModelsController), "Cancelling deployment action during shutdown.");
        PublishAction("Cancelling the active model action before shutdown.", ActionLogLevel.Warning);
        cancellation.Cancel();
        var completion = activeActionCompletion?.Task;
        if (completion is null)
        {
            return;
        }

        await AwaitCompletionAsync(completion, timeout, cancellationToken);
    }

    private async Task ExecuteDeploymentActionAsync(
        VllmServerConfiguration server,
        string initialMessage,
        Func<VllmServerConfiguration, IProgress<string>, CancellationToken, Task> action,
        bool allowCancellationOfPreviousAction = false,
        bool allowStopWhileBusy = false)
    {
        if (currentActionCancellation is not null && !allowCancellationOfPreviousAction)
        {
            AppendLog("Another deployment action is already running.");
            logger.Log(nameof(ModelsController), $"Ignored deployment action for '{server.Name}' because another action is running.");
            PublishAction($"Skipped '{server.Name}' because another deployment action is already running.", ActionLogLevel.Warning);
            return;
        }

        using var cancellationSource = new CancellationTokenSource();
        var actionCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        currentActionCancellation = cancellationSource;
        activeActionCompletion = actionCompletion;
        activeActionServer = server;
        activeActionCanStopServer = allowStopWhileBusy;
        activeActionMessage = initialMessage;
        deploy.ModelProgress.IsIndeterminate = true;
        deploy.ChatProgress.IsIndeterminate = true;
        SetConfigurationInputsEnabled(false);
        SetServerStatus(server, "Busy", initialMessage.Contains("Stopping", StringComparison.OrdinalIgnoreCase) ? "Stopping" : "Working", initialMessage);
        selection.ModelsList.Items.Refresh();
        selection.ChatServerCombo.Items.Refresh();
        RefreshConnectedServers();
        UpdateActionButtons();
        OnStateChanged();
        deploy.DeploymentLogBox.Clear();
        AppendLog(initialMessage);
        var progress = new Progress<string>(AppendLog);
        logger.Log(nameof(ModelsController), $"Executing deployment action for '{server.Name}'. initialMessage={initialMessage}");
        PublishAction($"{server.Name}: {initialMessage}");

        try
        {
            await action(server, progress, cancellationSource.Token);
            logger.Log(nameof(ModelsController), $"Deployment action completed for '{server.Name}'.");
            PublishAction($"Completed action for '{server.Name}'.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Deployment action cancelled.");
            logger.Log(nameof(ModelsController), $"Deployment action cancelled for '{server.Name}'.");
            PublishAction($"Cancelled action for '{server.Name}'.", ActionLogLevel.Warning);
        }
        catch (Exception exception)
        {
            AppendLog(exception.Message);
            logger.Log(nameof(ModelsController), $"Deployment action failed for '{server.Name}'. reason={exception.Message}");
            PublishAction($"Action failed for '{server.Name}': {exception.Message}", ActionLogLevel.Error);
        }
        finally
        {
            if (ReferenceEquals(currentActionCancellation, cancellationSource))
            {
                currentActionCancellation = null;
            }

            if (ReferenceEquals(activeActionCompletion, actionCompletion))
            {
                activeActionCompletion = null;
            }

            if (ReferenceEquals(activeActionServer, server))
            {
                activeActionServer = null;
                activeActionCanStopServer = false;
                activeActionMessage = null;
            }

            deploy.ModelProgress.IsIndeterminate = false;
            deploy.ChatProgress.IsIndeterminate = false;
            SetConfigurationInputsEnabled(true);
            RefreshConnectedServers();
            UpdateActionButtons();
            OnStateChanged();
            actionCompletion.TrySetResult(true);
        }
    }

    private async Task<bool> CancelCurrentDeploymentActionAsync(
        string logMessage,
        string loggerMessage,
        string actionMessage,
        TimeSpan? timeout = null)
    {
        var cancellation = currentActionCancellation;
        if (cancellation is null)
        {
            return true;
        }

        AppendLog(logMessage);
        logger.Log(nameof(ModelsController), loggerMessage);
        PublishAction(actionMessage, ActionLogLevel.Warning);
        cancellation.Cancel();

        var completion = activeActionCompletion?.Task;
        if (completion is null)
        {
            return true;
        }

        var completed = await AwaitCompletionAsync(completion, timeout ?? TimeSpan.FromSeconds(15), CancellationToken.None);
        if (completed)
        {
            return true;
        }

        AppendLog("Cancellation is still in progress. Try Stop again in a moment.");
        logger.Log(nameof(ModelsController), "Timed out while waiting for the current deployment action to cancel.");
        PublishAction("Timed out while waiting for the active deployment task to cancel.", ActionLogLevel.Warning);
        return false;
    }

    private async Task TestAndRefreshSelectedServerAsync()
    {
        var server = SelectedServer;
        if (server is null)
        {
            return;
        }

        await ExecuteDeploymentActionAsync(server, "Checking /v1/models...", async (selectedServer, progress, cancellationToken) =>
        {
            var probe = await deploymentService.ProbeHttpServerAsync(selectedServer, cancellationToken);
            if (!probe.IsSuccess)
            {
                throw new InvalidOperationException(probe.Message);
            }

            progress.Report(probe.Message);
            progress.Report($"Verified served model: {probe.FirstModelName}");
        });

        await RefreshSelectedServerStatusAsync();
    }

    private async Task RefreshServerStatusAsync(VllmServerConfiguration server, CancellationToken cancellationToken)
    {
        if (currentActionCancellation is not null && ReferenceEquals(server, SelectedServer))
        {
            return;
        }

        if (server.UseExistingHttpServer)
        {
            var probe = await deploymentService.ProbeHttpServerAsync(server, cancellationToken);
            ApplyProbeStatus(server, probe, unreachableText: "Offline", invalidText: "Needs address");
        }
        else
        {
            var probe = await deploymentService.ProbeHttpServerAsync(server, cancellationToken);
            if (probe.IsSuccess)
            {
                ApplyProbeStatus(server, probe, unreachableText: "Stopped", invalidText: "Stopped");
            }
            else
            {
                var composePath = deploymentService.CreateComposeConfiguration(server, composeDirectory).ComposeFilePath;
                if (File.Exists(composePath))
                {
                    SetServerStatus(server, "Warning", "Stopped", probe.Message);
                }
                else
                {
                    SetServerStatus(server, "Unknown", "Configured", "This local profile is saved but not currently responding on /v1/models.");
                }
            }
        }

        selection.ModelsList.Items.Refresh();
        selection.ChatServerCombo.Items.Refresh();
        RefreshConnectedServers();
        UpdateActionButtons();
        OnStateChanged();
    }

    private void ApplyProbeStatus(VllmServerConfiguration server, VllmServerProbeResult probe, string unreachableText, string invalidText)
    {
        if (probe.IsSuccess)
        {
            SetServerStatus(server, "Ready", "Ready", probe.Message);
            return;
        }

        var kind = probe.Status is VllmServerProbeStatus.EmptyEndpoint or VllmServerProbeStatus.InvalidEndpoint
            ? "Warning"
            : "Error";
        var text = probe.Status is VllmServerProbeStatus.EmptyEndpoint or VllmServerProbeStatus.InvalidEndpoint
            ? invalidText
            : unreachableText;
        SetServerStatus(server, kind, text, probe.Message);
    }

    private void AppendLog(string message)
    {
        if (SelectedServer?.EnableVerboseLogs == false && IsVerboseLogMessage(message))
        {
            return;
        }

        deploy.DeploymentLogBox.AppendText($"[{DateTime.Now:T}] {message}{Environment.NewLine}");
        deploy.DeploymentLogBox.ScrollToEnd();
        logger.Log(nameof(ModelsController), $"Deployment log appended: {message}");
    }

    private static async Task<bool> AwaitCompletionAsync(Task completionTask, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completedTask = await Task.WhenAny(completionTask, timeoutTask);
        return ReferenceEquals(completedTask, completionTask);
    }

    private void PublishAction(string message, ActionLogLevel level = ActionLogLevel.Info)
        => actionLog.Publish(nameof(ModelsController), message, level);
}


