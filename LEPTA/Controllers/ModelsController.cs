using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LEPTA.Shared.Diagnostics;
using LEPTA.Theming;
using LEPTA.vLLM.Configuration;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;
using Microsoft.Win32;

namespace LEPTA.Controllers;

internal sealed class ModelsController
{
    private readonly ObservableCollection<VllmServerConfiguration> servers;
    private readonly ListBox modelsList;
    private readonly ComboBox chatServerCombo;
    private readonly TextBlock modelNoteText;
    private readonly TextBox nameBox;
    private readonly ComboBox deploymentModeBox;
    private readonly TextBox httpServerAddressBox;
    private readonly TextBox modelBox;
    private readonly TextBox localPathBox;
    private readonly TextBox servedModelNameBox;
    private readonly TextBox dockerImageBox;
    private readonly TextBlock localModelMetadataText;
    private readonly TextBox portBox;
    private readonly ComboBox dtypeBox;
    private readonly TextBox gpuBox;
    private readonly TextBox maxLenBox;
    private readonly TextBox swapBox;
    private readonly ComboBox kvCacheBox;
    private readonly TextBlock parameterCountText;
    private readonly TextBox gpuLayersBox;
    private readonly ComboBox weightQuantizationBox;
    private readonly TextBox tensorParallelBox;
    private readonly ComboBox kCacheQuantizationBox;
    private readonly ComboBox vCacheQuantizationBox;
    private readonly TextBox cpuOffloadBox;
    private readonly TextBox maxNumSeqsBox;
    private readonly CheckBox verboseLogsCheckBox;
    private readonly Border dockerStatusIndicator;
    private readonly TextBlock dockerStatusText;
    private readonly TextBlock dockerStatusDetailsText;
    private readonly TextBlock estimatedVramText;
    private readonly TextBlock estimatedRamText;
    private readonly TextBlock estimateSummaryText;
    private readonly TextBox deploymentLogBox;
    private readonly ProgressBar modelProgress;
    private readonly ProgressBar chatProgress;
    private readonly FrameworkElement advancedConfigurationPanel;
    private readonly VllmDeploymentService deploymentService;
    private readonly ILeptaLogger logger;
    private readonly IActionLogEventStream actionLog;
    private readonly string composeDirectory;

    private bool isLoadingConfiguration;
    private CancellationTokenSource? currentActionCancellation;

    public event Action? StateChanged;

    public ModelsController(
        ListBox modelsList,
        ComboBox chatServerCombo,
        TextBlock modelNoteText,
        TextBox nameBox,
        ComboBox deploymentModeBox,
        TextBox httpServerAddressBox,
        TextBox modelBox,
        TextBox localPathBox,
        TextBox servedModelNameBox,
        TextBox dockerImageBox,
        TextBlock localModelMetadataText,
        TextBox portBox,
        ComboBox dtypeBox,
        TextBox gpuBox,
        TextBox maxLenBox,
        TextBox swapBox,
        ComboBox kvCacheBox,
        TextBlock parameterCountText,
        TextBox gpuLayersBox,
        ComboBox weightQuantizationBox,
        TextBox tensorParallelBox,
        ComboBox kCacheQuantizationBox,
        ComboBox vCacheQuantizationBox,
        TextBox cpuOffloadBox,
        TextBox maxNumSeqsBox,
        CheckBox verboseLogsCheckBox,
        Border dockerStatusIndicator,
        TextBlock dockerStatusText,
        TextBlock dockerStatusDetailsText,
        TextBlock estimatedVramText,
        TextBlock estimatedRamText,
        TextBlock estimateSummaryText,
        TextBox deploymentLogBox,
        ProgressBar modelProgress,
        ProgressBar chatProgress,
        FrameworkElement advancedConfigurationPanel,
        string composeDirectory,
        VllmDeploymentService? deploymentService = null,
        ILeptaLogger? logger = null,
        IActionLogEventStream? actionLog = null,
        IEnumerable<VllmServerConfiguration>? initialServers = null,
        string? selectedServerId = null)
    {
        this.modelsList = modelsList;
        this.chatServerCombo = chatServerCombo;
        this.modelNoteText = modelNoteText;
        this.nameBox = nameBox;
        this.deploymentModeBox = deploymentModeBox;
        this.httpServerAddressBox = httpServerAddressBox;
        this.modelBox = modelBox;
        this.localPathBox = localPathBox;
        this.servedModelNameBox = servedModelNameBox;
        this.dockerImageBox = dockerImageBox;
        this.localModelMetadataText = localModelMetadataText;
        this.portBox = portBox;
        this.dtypeBox = dtypeBox;
        this.gpuBox = gpuBox;
        this.maxLenBox = maxLenBox;
        this.swapBox = swapBox;
        this.kvCacheBox = kvCacheBox;
        this.parameterCountText = parameterCountText;
        this.gpuLayersBox = gpuLayersBox;
        this.weightQuantizationBox = weightQuantizationBox;
        this.tensorParallelBox = tensorParallelBox;
        this.kCacheQuantizationBox = kCacheQuantizationBox;
        this.vCacheQuantizationBox = vCacheQuantizationBox;
        this.cpuOffloadBox = cpuOffloadBox;
        this.maxNumSeqsBox = maxNumSeqsBox;
        this.verboseLogsCheckBox = verboseLogsCheckBox;
        this.dockerStatusIndicator = dockerStatusIndicator;
        this.dockerStatusText = dockerStatusText;
        this.dockerStatusDetailsText = dockerStatusDetailsText;
        this.estimatedVramText = estimatedVramText;
        this.estimatedRamText = estimatedRamText;
        this.estimateSummaryText = estimateSummaryText;
        this.deploymentLogBox = deploymentLogBox;
        this.modelProgress = modelProgress;
        this.chatProgress = chatProgress;
        this.advancedConfigurationPanel = advancedConfigurationPanel;
        this.composeDirectory = composeDirectory;
        this.logger = logger ?? NullLeptaLogger.Instance;
        this.actionLog = actionLog ?? NullActionLogEventStream.Instance;
        this.deploymentService = deploymentService ?? new VllmDeploymentService(logger: this.logger);

        var seededServers = initialServers?.ToList() ?? VllmDefaults.CreateServers().ToList();
        if (seededServers.Count == 0)
        {
            seededServers = VllmDefaults.CreateServers().ToList();
        }

        servers = new ObservableCollection<VllmServerConfiguration>(seededServers);
        this.modelsList.ItemsSource = servers;
        this.chatServerCombo.ItemsSource = servers;
        this.modelNoteText.Text = VllmDefaults.VllmModelNote;
        SelectServer(selectedServerId);
        SetDockerStatusState("Not checked yet", "Refresh to verify Docker Desktop and the active engine.", ThemeResourceKeys.SecondaryTextBrush);
    }

    public VllmServerConfiguration? SelectedServer => modelsList.SelectedItem as VllmServerConfiguration
        ?? chatServerCombo.SelectedItem as VllmServerConfiguration;

    public string? SelectedServerId => SelectedServer?.Id;

    public IEnumerable<VllmServerConfiguration> Servers => servers;

    public bool IsVerboseVllmLogsEnabled => servers.Any(server => server.EnableVerboseLogs);

    public void SelectServer(string? serverId)
    {
        var server = string.IsNullOrWhiteSpace(serverId)
            ? servers.FirstOrDefault()
            : servers.FirstOrDefault(item => string.Equals(item.Id, serverId, StringComparison.OrdinalIgnoreCase))
              ?? servers.FirstOrDefault();

        modelsList.SelectedItem = server;
        chatServerCombo.SelectedItem = server;
    }

    public void ApplyVerboseLogsSetting(bool enabled, bool publishAction = true)
    {
        isLoadingConfiguration = true;
        try
        {
            foreach (var server in servers)
            {
                server.EnableVerboseLogs = enabled;
            }

            verboseLogsCheckBox.IsChecked = enabled;
            modelsList.Items.Refresh();
            chatServerCombo.Items.Refresh();
        }
        finally
        {
            isLoadingConfiguration = false;
        }

        logger.Log(nameof(ModelsController), $"Global verbose vLLM logging set to {enabled.ToString().ToLowerInvariant()}.");
        if (publishAction)
        {
            PublishAction(enabled
                ? "Verbose vLLM logs enabled."
                : "Verbose vLLM logs disabled.");
        }

        OnStateChanged();
    }

    public void HandleModelsSelectionChanged()
    {
        if (modelsList.SelectedItem is VllmServerConfiguration server)
        {
            logger.Log(nameof(ModelsController), $"Model selection changed to '{server.Name}'. endpoint={server.Endpoint}.");
            chatServerCombo.SelectedItem = server;
            LoadConfiguration(server);
            OnStateChanged();
        }
    }

    public void HandleChatServerSelectionChanged()
    {
        if (chatServerCombo.SelectedItem is VllmServerConfiguration server)
        {
            logger.Log(nameof(ModelsController), $"Chat server selection changed to '{server.Name}'. endpoint={server.Endpoint}.");
            modelsList.SelectedItem = server;
            OnStateChanged();
        }
    }

    public void HandleConfigurationChanged()
    {
        if (isLoadingConfiguration || modelsList.SelectedItem is not VllmServerConfiguration server)
        {
            return;
        }

        server.Name = nameBox.Text;
        server.UseExistingHttpServer = IsExistingHttpServerSelected();
        server.HttpServerAddress = httpServerAddressBox.Text;
        server.Model = modelBox.Text;
        server.ServedModelName = string.IsNullOrWhiteSpace(servedModelNameBox.Text) ? null : servedModelNameBox.Text.Trim();
        server.DockerImage = dockerImageBox.Text;
        var localModelPath = string.IsNullOrWhiteSpace(localPathBox.Text) ? null : localPathBox.Text.Trim();
        if (!string.Equals(server.LocalModelPath, localModelPath, StringComparison.OrdinalIgnoreCase))
        {
            server.LocalModelPath = localModelPath;
            ClearLocalModelMetadata(server);
        }
        if (int.TryParse(portBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) server.HostPort = port;
        if (TryGetComboBoxValue(dtypeBox) is { } dtype) server.DType = dtype;
        if (double.TryParse(gpuBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gpu)) server.GpuMemoryUtilization = gpu;
        if (int.TryParse(maxLenBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxLen)) server.MaxModelLength = maxLen;
        if (int.TryParse(swapBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var swap)) server.SwapSpaceGb = swap;
        if (TryGetComboBoxValue(kvCacheBox) is { } kvCacheDType) server.KvCacheDType = kvCacheDType;
        if (int.TryParse(gpuLayersBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gpuLayers)) server.GpuLayers = gpuLayers;
        if (TryGetComboBoxValue(weightQuantizationBox) is { } weightQuantization) server.WeightQuantization = weightQuantization;
        if (int.TryParse(tensorParallelBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tensorParallel)) server.TensorParallelSize = tensorParallel;
        if (TryGetComboBoxValue(kCacheQuantizationBox) is { } kCacheQuantization) server.KCacheQuantization = kCacheQuantization;
        if (TryGetComboBoxValue(vCacheQuantizationBox) is { } vCacheQuantization) server.VCacheQuantization = vCacheQuantization;
        if (double.TryParse(cpuOffloadBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpuOffload)) server.CpuOffloadGb = cpuOffload;
        if (int.TryParse(maxNumSeqsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxNumSeqs)) server.MaxNumSeqs = maxNumSeqs;
        server.EnableVerboseLogs = verboseLogsCheckBox.IsChecked == true;
        modelsList.Items.Refresh();
        chatServerCombo.Items.Refresh();
        UpdateEstimate(server);
        UpdateLocalModelMetadataDisplay(server);
        logger.Log(nameof(ModelsController), $"Configuration updated for '{server.Name}'. mode={(server.UseExistingHttpServer ? "ExistingHttpServer" : "LocalDeploy")}, endpoint={server.Endpoint}, model={server.Model}, verboseLogs={server.EnableVerboseLogs.ToString().ToLowerInvariant()}.");
        OnStateChanged();
    }

    public void HandleVerboseLogsChanged()
    {
        if (isLoadingConfiguration)
        {
            return;
        }

        ApplyVerboseLogsSetting(verboseLogsCheckBox.IsChecked == true);
    }

    public void AddModel()
    {
        var server = new VllmServerConfiguration
        {
            Name = $"HTTP server {servers.Count + 1}",
            UseExistingHttpServer = true,
            HostPort = 8512,
            HttpServerAddress = "http://localhost:8512",
            EnableVerboseLogs = IsVerboseVllmLogsEnabled
        };

        servers.Add(server);
        modelsList.SelectedItem = server;
        logger.Log(nameof(ModelsController), $"Added model profile '{server.Name}' with endpoint {server.Endpoint}.");
        OnStateChanged();
    }

    public async Task BrowseModelAsync(Window owner)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a local Hugging Face-format model folder"
        };

        if (dialog.ShowDialog(owner) == true)
        {
            SetDeploymentMode(useExistingHttpServer: false);
            localPathBox.Text = dialog.FolderName;
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

        var localModelPath = string.IsNullOrWhiteSpace(localPathBox.Text) ? server.LocalModelPath : localPathBox.Text.Trim();
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
            await ExecuteDeploymentActionAsync(SelectedServer, "Server is already deployed. Testing /v1/models...", async (server, progress, cancellationToken) =>
            {
                var probe = await deploymentService.ProbeHttpServerAsync(server, cancellationToken);
                if (!probe.IsSuccess)
                {
                    throw new InvalidOperationException(probe.Message);
                }

                progress.Report(probe.Message);
                progress.Report($"Chat and LEPTA will use '{probe.FirstModelName}' from /v1/models.");
            });
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
            (server, progress, cancellationToken) => deploymentService.DeployAsync(server, composeDirectory, progress, cancellationToken));
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
            deploymentLogBox.Clear();
            AppendLog($"{selectedServer.Endpoint} is managed externally, so LEPTA will not stop it.");
            logger.Log(nameof(ModelsController), $"Stop skipped for externally managed server '{selectedServer.Name}'.");
            PublishAction($"Stop skipped for '{selectedServer.Name}' because the HTTP server is managed externally.", ActionLogLevel.Warning);
            return;
        }

        if (currentActionCancellation is not null)
        {
            currentActionCancellation.Cancel();
            AppendLog("Cancelling current deployment action...");
            logger.Log(nameof(ModelsController), $"Cancelling in-flight deployment action for '{selectedServer.Name}'.");
        }

        logger.Log(nameof(ModelsController), $"Deployment stop requested for '{selectedServer.Name}'.");
        await ExecuteDeploymentActionAsync(
            selectedServer,
            "Stopping deployment...",
            (server, progress, cancellationToken) => deploymentService.StopAsync(server, composeDirectory, progress, cancellationToken),
            allowCancellationOfPreviousAction: true);
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
            deploymentLogBox.Clear();
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
        await ExecuteDeploymentActionAsync(
            selectedServer,
            "Restarting deployment...",
            (server, progress, cancellationToken) => deploymentService.RestartAsync(server, composeDirectory, progress, cancellationToken),
            allowCancellationOfPreviousAction: true);
    }

    public Task TestSelectedServerAsync()
    {
        if (SelectedServer is null)
        {
            return Task.CompletedTask;
        }

        logger.Log(nameof(ModelsController), $"Connectivity test requested for '{SelectedServer.Name}'.");

        return ExecuteDeploymentActionAsync(SelectedServer, "Testing /v1/models...", async (server, progress, cancellationToken) =>
        {
            var probe = await deploymentService.ProbeHttpServerAsync(server, cancellationToken);
            if (!probe.IsSuccess)
            {
                throw new InvalidOperationException(probe.Message);
            }

            progress.Report(probe.Message);
            progress.Report($"First served model: {probe.FirstModelName}");
        });
    }

    public void OpenAdvancedConfiguration()
    {
        advancedConfigurationPanel.Visibility = Visibility.Visible;
        logger.Log(nameof(ModelsController), "Opened advanced configuration panel.");
    }

    public void CloseAdvancedConfiguration()
    {
        advancedConfigurationPanel.Visibility = Visibility.Collapsed;
        logger.Log(nameof(ModelsController), "Closed advanced configuration panel.");
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

        await ExecuteDeploymentActionAsync(
            server,
            "Stopping deployment before closing...",
            (selectedServer, progress, cancellationToken) => deploymentService.StopAsync(selectedServer, composeDirectory, progress, cancellationToken),
            allowCancellationOfPreviousAction: true);
    }

    private void LoadConfiguration(VllmServerConfiguration server)
    {
        isLoadingConfiguration = true;
        nameBox.Text = server.Name;
        SetDeploymentMode(server.UseExistingHttpServer);
        httpServerAddressBox.Text = server.HttpServerAddress;
        modelBox.Text = server.Model;
        localPathBox.Text = server.LocalModelPath ?? string.Empty;
        servedModelNameBox.Text = server.ServedModelName ?? string.Empty;
        dockerImageBox.Text = server.DockerImage;
        portBox.Text = server.HostPort.ToString(CultureInfo.InvariantCulture);
        SetComboBoxValue(dtypeBox, server.DType);
        gpuBox.Text = server.GpuMemoryUtilization.ToString(CultureInfo.InvariantCulture);
        maxLenBox.Text = server.MaxModelLength.ToString(CultureInfo.InvariantCulture);
        swapBox.Text = server.SwapSpaceGb.ToString(CultureInfo.InvariantCulture);
        SetComboBoxValue(kvCacheBox, server.KvCacheDType);
        parameterCountText.Text = FormatParameterCount(server);
        gpuLayersBox.Text = server.GpuLayers.ToString(CultureInfo.InvariantCulture);
        SetComboBoxValue(weightQuantizationBox, server.WeightQuantization);
        tensorParallelBox.Text = server.TensorParallelSize.ToString(CultureInfo.InvariantCulture);
        SetComboBoxValue(kCacheQuantizationBox, server.KCacheQuantization);
        SetComboBoxValue(vCacheQuantizationBox, server.VCacheQuantization);
        cpuOffloadBox.Text = server.CpuOffloadGb.ToString(CultureInfo.InvariantCulture);
        maxNumSeqsBox.Text = server.MaxNumSeqs.ToString(CultureInfo.InvariantCulture);
        verboseLogsCheckBox.IsChecked = server.EnableVerboseLogs;
        isLoadingConfiguration = false;
        UpdateEstimate(server);
        UpdateLocalModelMetadataDisplay(server);
    }

    private async Task ExecuteDeploymentActionAsync(
        VllmServerConfiguration server,
        string initialMessage,
        Func<VllmServerConfiguration, IProgress<string>, CancellationToken, Task> action,
        bool allowCancellationOfPreviousAction = false)
    {
        if (currentActionCancellation is not null && !allowCancellationOfPreviousAction)
        {
            AppendLog("Another deployment action is already running.");
            logger.Log(nameof(ModelsController), $"Ignored deployment action for '{server.Name}' because another action is running.");
            PublishAction($"Skipped '{server.Name}' because another deployment action is already running.", ActionLogLevel.Warning);
            return;
        }

        using var cancellationSource = new CancellationTokenSource();
        currentActionCancellation = cancellationSource;
        modelProgress.IsIndeterminate = true;
        chatProgress.IsIndeterminate = true;
        deploymentLogBox.Clear();
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

            modelProgress.IsIndeterminate = false;
            chatProgress.IsIndeterminate = false;
        }
    }

    private void AppendLog(string message)
    {
        if (SelectedServer?.EnableVerboseLogs == false && IsVerboseLogMessage(message))
        {
            return;
        }

        deploymentLogBox.AppendText($"[{DateTime.Now:T}] {message}{Environment.NewLine}");
        deploymentLogBox.ScrollToEnd();
        logger.Log(nameof(ModelsController), $"Deployment log appended: {message}");
    }

    private void UpdateEstimate(VllmServerConfiguration server)
    {
        if (server.UseExistingHttpServer)
        {
            parameterCountText.Text = string.IsNullOrWhiteSpace(server.Model)
                ? "Unknown (external server)"
                : FormatParameterCount(server);
            estimatedVramText.Text = "n/a";
            estimatedRamText.Text = "n/a";
            estimateSummaryText.Text = "Already deployed HTTP server. Use Test access to verify /v1/models. Switch the source to Docker deployment when you want LEPTA to generate and manage a compose-based local server.";
            UpdateLocalModelMetadataDisplay(server);
            return;
        }

        parameterCountText.Text = FormatParameterCount(server);
        var estimate = VllmMemoryEstimator.Estimate(server);
        estimatedVramText.Text = $"{estimate.EstimatedVramGb:F1} GB";
        estimatedRamText.Text = $"{estimate.EstimatedRamGb:F1} GB";
        estimateSummaryText.Text = estimate.Summary;
        UpdateLocalModelMetadataDisplay(server);
    }

    private void ApplyLocalModelMetadata(VllmServerConfiguration server, VllmModelMetadata metadata)
    {
        server.LocalModelPath = metadata.ModelDirectory;
        server.Model = metadata.ModelId ?? metadata.DisplayName;
        server.ServedModelName = metadata.SuggestedServedModelName;
        server.DetectedArchitecture = metadata.Architecture;
        server.DetectedMaxTokenLength = metadata.MaxTokenLength;
        server.DetectedHiddenSize = metadata.HiddenSize;
        server.DetectedLayerCount = metadata.LayerCount;
        server.AvailableWeightQuantizations = metadata.AvailableQuantizations;
        server.MetadataSummary = metadata.BuildSummary();

        if (metadata.ParameterCountBillions is { } parameterCountBillions)
        {
            server.ParameterCountBillions = parameterCountBillions;
        }

        if (!string.IsNullOrWhiteSpace(metadata.PreferredWeightQuantization))
        {
            server.WeightQuantization = metadata.PreferredWeightQuantization;
        }

        if (!string.IsNullOrWhiteSpace(metadata.PreferredKvCacheDType))
        {
            server.KvCacheDType = metadata.PreferredKvCacheDType;
            server.KCacheQuantization = metadata.PreferredKvCacheDType;
            server.VCacheQuantization = metadata.PreferredKvCacheDType;
        }

        if (metadata.RecommendedMaxModelLength is { } recommendedMaxModelLength)
        {
            server.MaxModelLength = recommendedMaxModelLength;
        }

        if (metadata.RecommendedGpuMemoryUtilization is { } recommendedGpuMemoryUtilization)
        {
            server.GpuMemoryUtilization = recommendedGpuMemoryUtilization;
        }

        if (metadata.RecommendedMaxNumSeqs is { } recommendedMaxNumSeqs)
        {
            server.MaxNumSeqs = recommendedMaxNumSeqs;
        }

        server.EnablePrefixCaching = metadata.EnablePrefixCaching;
        server.LanguageModelOnly = metadata.LanguageModelOnly;
        server.ReasoningParser = metadata.ReasoningParser;
        server.UseExistingHttpServer = false;
        LoadConfiguration(server);
        modelsList.Items.Refresh();
        chatServerCombo.Items.Refresh();
        OnStateChanged();
    }

    private void ClearLocalModelMetadata(VllmServerConfiguration server)
    {
        server.MetadataSummary = string.IsNullOrWhiteSpace(server.LocalModelPath)
            ? "Select a local folder to scan metadata."
            : "Metadata not scanned yet for this folder. Use Browse or Scan metadata to inspect it.";
        server.ParameterCountBillions = 0;
        server.ServedModelName = null;
        server.DetectedArchitecture = null;
        server.DetectedMaxTokenLength = null;
        server.DetectedHiddenSize = null;
        server.DetectedLayerCount = null;
        server.AvailableWeightQuantizations = Array.Empty<string>();
        UpdateLocalModelMetadataDisplay(server);
    }

    private void UpdateLocalModelMetadataDisplay(VllmServerConfiguration server)
    {
        localModelMetadataText.Text = server.UseExistingHttpServer
            ? "Local model metadata is only used for Docker-managed local deployments."
            : server.MetadataSummary;
    }

    private void SetDockerStatusState(string summary, string details, string brushKey)
    {
        dockerStatusText.Text = summary;
        dockerStatusDetailsText.Text = details;
        dockerStatusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        dockerStatusIndicator.SetResourceReference(Border.BackgroundProperty, brushKey);
    }

    private static string? TryGetComboBoxValue(ComboBox comboBox)
        => comboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString()
            : null;

    private bool IsExistingHttpServerSelected()
        => deploymentModeBox.SelectedItem is ComboBoxItem item
           && string.Equals(item.Tag?.ToString(), "ExistingHttpServer", StringComparison.OrdinalIgnoreCase);

    private void SetDeploymentMode(bool useExistingHttpServer)
    {
        foreach (var item in deploymentModeBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem
                && string.Equals(
                    comboBoxItem.Tag?.ToString(),
                    useExistingHttpServer ? "ExistingHttpServer" : "LocalDeploy",
                    StringComparison.OrdinalIgnoreCase))
            {
                deploymentModeBox.SelectedItem = item;
                return;
            }
        }

        deploymentModeBox.SelectedIndex = -1;
    }

    private static void SetComboBoxValue(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem
                && string.Equals(comboBoxItem.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = -1;
    }

    private static string FormatParameterCount(VllmServerConfiguration server)
    {
        var resolved = VllmMemoryEstimator.ResolveParameterCountBillions(server);
        var source = server.ParameterCountBillions > 0
            ? "model metadata"
            : "derived from model ID/name";
        return $"{resolved.ToString("0.###", CultureInfo.InvariantCulture)} B ({source})";
    }

    private static bool IsVerboseLogMessage(string message)
        => message.Contains("INFO", StringComparison.OrdinalIgnoreCase)
           || message.Contains("DEBUG", StringComparison.OrdinalIgnoreCase)
           || message.Contains("Pulling", StringComparison.OrdinalIgnoreCase)
           || message.Contains("digest", StringComparison.OrdinalIgnoreCase);

    private void PublishAction(string message, ActionLogLevel level = ActionLogLevel.Info)
        => actionLog.Publish(nameof(ModelsController), message, level);

    private void OnStateChanged() => StateChanged?.Invoke();
}