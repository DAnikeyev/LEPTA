using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LEPTA.Controllers.Models;
using LEPTA.Theming;
using LEPTA.vLLM.Configuration;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Controllers;

internal sealed partial class ModelsController
{
    public void HandleConfigurationChanged()
    {
        if (isLoadingConfiguration || SelectedServer is not VllmServerConfiguration server)
        {
            return;
        }

        var localModelPath = string.IsNullOrWhiteSpace(config.LocalPathBox.Text) ? null : config.LocalPathBox.Text.Trim();
        if (!string.Equals(server.LocalModelPath, localModelPath, StringComparison.OrdinalIgnoreCase))
        {
            server.LocalModelPath = localModelPath;
            ClearLocalModelMetadata(server);
        }

        ServerProfileFormMapper.Apply(server, BuildFormStateFromControls());
        ApplyRequestOverridesFromUi(server);
        ApplyAutomaticLocalModelFields(server);
        ResetServerStatus(server);
        selection.ModelsList.Items.Refresh();
        selection.ChatServerCombo.Items.Refresh();
        RefreshConnectedServers();
        UpdateModeVisibility(server);
        UpdateEstimate(server);
        UpdateLocalModelMetadataDisplay(server);
        UpdateActionButtons();
        logger.Log(nameof(ModelsController), $"Configuration updated for '{server.Name}'. mode={(server.UseExistingHttpServer ? "ExistingHttpServer" : "LocalDeploy")}, endpoint={server.Endpoint}, model={server.Model}, verboseLogs={server.EnableVerboseLogs.ToString().ToLowerInvariant()}.");
        OnStateChanged();
    }

    /// <summary>
    /// Quick-fill for the external auth/overrides editor: applies the OpenRouter-recommended endpoint
    /// and <c>HTTP-Referer</c>/<c>X-Title</c> headers without touching the user's API key or model.
    /// No-op unless an external-server profile is selected.
    /// </summary>
    public void ApplyOpenRouterPreset()
    {
        if (SelectedServer is not VllmServerConfiguration server || !server.UseExistingHttpServer)
        {
            return;
        }

        config.HttpServerAddressBox.Text = VllmDefaults.OpenRouterEndpoint;
        config.AuthHeaderNameBox.Text = "Authorization";
        config.AuthHeaderSchemeBox.Text = "Bearer";
        config.ExtraHeadersBox.Text = ServerProfileFormMapper.FormatExtraHeaders(VllmDefaults.OpenRouterRecommendedHeaders);
        config.ExtraBodyBox.Text = string.Empty;
        HandleConfigurationChanged();
        logger.Log(nameof(ModelsController), "Applied OpenRouter preset defaults (endpoint + recommended headers).");
    }

    public void HandleVerboseLogsChanged()
    {
        if (isLoadingConfiguration)
        {
            return;
        }

        ApplyVerboseLogsSetting(config.VerboseLogsCheckBox.IsChecked == true);
    }

    public void OpenAdvancedConfiguration()
    {
        if (SelectedServer?.UseExistingHttpServer == true)
        {
            return;
        }

        deploy.AdvancedConfigurationPanel.Visibility = Visibility.Visible;
        logger.Log(nameof(ModelsController), "Opened advanced configuration panel.");
    }

    public void CloseAdvancedConfiguration()
    {
        deploy.AdvancedConfigurationPanel.Visibility = Visibility.Collapsed;
        logger.Log(nameof(ModelsController), "Closed advanced configuration panel.");
    }

    private void LoadConfiguration(VllmServerConfiguration server)
    {
        activeServer = server;
        isLoadingConfiguration = true;
        try
        {
            WriteFormStateToControls(ServerProfileFormMapper.Build(server));
            SetDeploymentMode(server.UseExistingHttpServer);
            config.ParameterCountText.Text = ServerProfileFormMapper.FormatParameterCount(server);
            LoadRequestOverridesIntoUi(server);
            ClearServedModels();
        }
        finally
        {
            isLoadingConfiguration = false;
        }

        UpdateModeVisibility(server);
        UpdateEstimate(server);
        UpdateLocalModelMetadataDisplay(server);
        UpdateActionButtons();
    }

    /// <summary>Reads the editable controls into a WPF-free form state (the only control-coupled read path).</summary>
    private ServerProfileFormState BuildFormStateFromControls()
    {
        var state = new ServerProfileFormState
        {
            Name = config.NameBox.Text,
            UseExistingHttpServer = IsExistingHttpServerSelected(),
            HttpServerAddress = config.HttpServerAddressBox.Text,
            Model = config.ModelBox.Text,
            ServedModelName = config.ServedModelNameBox.Text,
            DockerImage = config.DockerImageBox.Text,
            AdditionalVllmArguments = config.AdditionalVllmArgumentsBox.Text,
            EnableVerboseLogs = config.VerboseLogsCheckBox.IsChecked == true,
            DType = TryGetComboBoxValue(config.DTypeBox),
            WeightQuantization = TryGetComboBoxValue(config.WeightQuantizationBox),
            KCacheQuantization = TryGetComboBoxValue(config.KCacheQuantizationBox),
            VCacheQuantization = TryGetComboBoxValue(config.VCacheQuantizationBox),
        };

        if (int.TryParse(config.PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) state.HostPort = port;
        if (double.TryParse(config.GpuBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gpu)) state.GpuMemoryUtilization = gpu;
        if (double.TryParse(config.GpuVramBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gpuVramGb)) state.GpuVramGb = gpuVramGb;
        if (int.TryParse(config.MaxLenBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxLen)) state.MaxModelLength = maxLen;
        if (int.TryParse(config.ReadyTimeoutBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var readyTimeoutMinutes)) state.ReadyTimeoutMinutes = readyTimeoutMinutes;
        if (double.TryParse(config.CpuOffloadBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpuOffload)) state.CpuOffloadGb = cpuOffload;
        if (int.TryParse(config.TensorParallelBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tensorParallel)) state.TensorParallelSize = tensorParallel;
        if (int.TryParse(config.MaxNumSeqsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxNumSeqs)) state.MaxNumSeqs = maxNumSeqs;
        if (TryGetComboBoxValue(config.TokenizersParallelismBox) is { } tokenizersParallelism)
        {
            state.EnableTokenizersParallelism = string.Equals(tokenizersParallelism, "true", StringComparison.OrdinalIgnoreCase);
        }

        return state;
    }

    /// <summary>Pushes a WPF-free form state back into the editable controls (the only control-coupled write path).</summary>
    private void WriteFormStateToControls(ServerProfileFormState state)
    {
        config.NameBox.Text = state.Name;
        config.HttpServerAddressBox.Text = state.HttpServerAddress;
        config.ModelBox.Text = state.Model;
        config.LocalPathBox.Text = state.LocalModelPath ?? string.Empty;
        config.ServedModelNameBox.Text = state.ServedModelName ?? string.Empty;
        config.DockerImageBox.Text = state.DockerImage;
        config.PortBox.Text = state.HostPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SetComboBoxValue(config.DTypeBox, state.DType);
        config.GpuBox.Text = state.GpuMemoryUtilization?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        config.GpuVramBox.Text = state.GpuVramGb is { } vram and > 0 ? vram.ToString(CultureInfo.InvariantCulture) : string.Empty;
        config.MaxLenBox.Text = state.MaxModelLength?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        config.ReadyTimeoutBox.Text = state.ReadyTimeoutMinutes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        config.CpuOffloadBox.Text = state.CpuOffloadGb?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SetComboBoxValue(config.WeightQuantizationBox, state.WeightQuantization);
        config.TensorParallelBox.Text = state.TensorParallelSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SetComboBoxValue(config.KCacheQuantizationBox, state.KCacheQuantization);
        SetComboBoxValue(config.VCacheQuantizationBox, state.VCacheQuantization);
        SetComboBoxValue(config.TokenizersParallelismBox, state.EnableTokenizersParallelism is { } tokenizers ? (tokenizers ? "true" : "false") : null);
        config.AdditionalVllmArgumentsBox.Text = state.AdditionalVllmArguments;
        config.MaxNumSeqsBox.Text = state.MaxNumSeqs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        config.VerboseLogsCheckBox.IsChecked = state.EnableVerboseLogs;
    }

    private void ClearConfigurationInputs()
    {
        activeServer = null;
        isLoadingConfiguration = true;
        try
        {
            config.NameBox.Text = string.Empty;
            config.DeploymentModeBox.SelectedIndex = -1;
            config.HttpServerAddressBox.Text = string.Empty;
            config.ModelBox.Text = string.Empty;
            config.LocalPathBox.Text = string.Empty;
            config.ServedModelNameBox.Text = string.Empty;
            config.DockerImageBox.Text = string.Empty;
            config.PortBox.Text = string.Empty;
            config.DTypeBox.SelectedIndex = -1;
            config.GpuBox.Text = string.Empty;
            config.GpuVramBox.Text = string.Empty;
            config.MaxLenBox.Text = string.Empty;
            config.ReadyTimeoutBox.Text = string.Empty;
            config.CpuOffloadBox.Text = string.Empty;
            config.ParameterCountText.Text = string.Empty;
            config.WeightQuantizationBox.SelectedIndex = -1;
            config.TensorParallelBox.Text = string.Empty;
            config.KCacheQuantizationBox.SelectedIndex = -1;
            config.VCacheQuantizationBox.SelectedIndex = -1;
            config.TokenizersParallelismBox.SelectedIndex = -1;
            config.AdditionalVllmArgumentsBox.Text = string.Empty;
            ClearRequestOverridesUi();
            ClearServedModels();
            config.MaxNumSeqsBox.Text = string.Empty;
            config.VerboseLogsCheckBox.IsChecked = false;
            deploy.EstimatedVramText.Text = string.Empty;
            deploy.EstimateSummaryText.Text = "Select or add a model profile to edit its configuration.";
            config.LocalModelMetadataText.Text = "Select a local vLLM-compatible Hugging Face-style folder to scan metadata.";
            config.ConfigurationTitleText.Text = "Server configuration";
            config.ModelFieldLabelText.Text = "Model / HF model ID";
            config.ServedModelNameLabelText.Text = "Served model name";
            selection.ModelNoteText.Text = VllmDefaults.VllmModelNote;
        }
        finally
        {
            isLoadingConfiguration = false;
        }

        config.HttpServerRow.Visibility = Visibility.Collapsed;
        config.ApiKeyRow.Visibility = Visibility.Collapsed;
        config.LocalFolderRow.Visibility = Visibility.Visible;
        config.ServedModelNameRow.Visibility = Visibility.Visible;
        config.LocalMetadataBorder.Visibility = Visibility.Visible;
        config.LocalRuntimeSettingsPanel.Visibility = Visibility.Visible;
        deploy.EstimateBorder.Visibility = Visibility.Visible;
        deploy.DockerStatusBorder.Visibility = Visibility.Visible;
        deploy.DeploymentLogBorder.Visibility = Visibility.Visible;
        deploy.ModelActionsBorder.Visibility = Visibility.Collapsed;
        deploy.CheckServerButton.Visibility = Visibility.Collapsed;
        deploy.OpenAdvancedConfigurationButton.Visibility = Visibility.Visible;
        deploy.OpenAdvancedConfigurationButton.IsEnabled = false;
    }

    private void UpdateEstimate(VllmServerConfiguration server)
    {
        if (server.UseExistingHttpServer)
        {
            config.ParameterCountText.Text = string.IsNullOrWhiteSpace(server.Model)
                ? "Unknown (external server)"
                : ServerProfileFormMapper.FormatParameterCount(server);
            deploy.EstimatedVramText.Text = "n/a";
            deploy.EstimateSummaryText.Text = "Already deployed HTTP server. Use Check server to verify /v1/models. Switch the source to Docker deployment when you want LEPTA to generate and manage a compose-based local server.";
            UpdateLocalModelMetadataDisplay(server);
            return;
        }

        config.ParameterCountText.Text = ServerProfileFormMapper.FormatParameterCount(server);
        var estimate = VllmMemoryEstimator.Estimate(server);
        deploy.EstimatedVramText.Text = $"{estimate.EstimatedVramGb:F1} GB";
        deploy.EstimateSummaryText.Text = estimate.Summary;
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

        if (string.IsNullOrWhiteSpace(server.AdditionalVllmArguments)
            && !string.IsNullOrWhiteSpace(metadata.RecommendedAdditionalVllmArguments))
        {
            server.AdditionalVllmArguments = metadata.RecommendedAdditionalVllmArguments;
        }

        server.EnablePrefixCaching = metadata.EnablePrefixCaching;
        server.LanguageModelOnly = metadata.LanguageModelOnly;
        server.ReasoningParser = metadata.ReasoningParser;
        server.UseExistingHttpServer = false;
        LoadConfiguration(server);
        selection.ModelsList.Items.Refresh();
        selection.ChatServerCombo.Items.Refresh();
        RefreshConnectedServers();
        OnStateChanged();
    }

    private void ClearLocalModelMetadata(VllmServerConfiguration server)
    {
        server.MetadataSummary = string.IsNullOrWhiteSpace(server.LocalModelPath)
            ? "Select a local vLLM-compatible Hugging Face-style folder to scan metadata."
            : "Metadata not scanned yet for this folder. Use Browse or Scan metadata to inspect it and confirm that it looks like a vLLM-compatible Hugging Face-style model directory.";
        server.ParameterCountBillions = 0;
        server.ServedModelName = null;
        server.DetectedArchitecture = null;
        server.DetectedMaxTokenLength = null;
        server.DetectedHiddenSize = null;
        server.DetectedLayerCount = null;
        server.AvailableWeightQuantizations = Array.Empty<string>();
        UpdateLocalModelMetadataDisplay(server);
    }

    private void ApplyAutomaticLocalModelFields(VllmServerConfiguration server)
    {
        if (server.UseExistingHttpServer || string.IsNullOrWhiteSpace(server.LocalModelPath))
        {
            return;
        }

        var normalizedPath = Path.TrimEndingDirectorySeparator(server.LocalModelPath.Trim());
        var folderName = Path.GetFileName(normalizedPath);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        var suggestedModel = server.Model;
        if (string.IsNullOrWhiteSpace(suggestedModel)
            || string.Equals(suggestedModel, server.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(suggestedModel, server.ServedModelName, StringComparison.OrdinalIgnoreCase))
        {
            suggestedModel = folderName;
        }

        server.Model = suggestedModel;

        if (string.IsNullOrWhiteSpace(server.ServedModelName)
            || string.Equals(server.ServedModelName, $"{folderName}-local", StringComparison.OrdinalIgnoreCase)
            || string.Equals(server.ServedModelName, server.Name, StringComparison.OrdinalIgnoreCase))
        {
            server.ServedModelName = $"{folderName}-local";
        }

        if (string.IsNullOrWhiteSpace(server.Name)
            || server.Name.StartsWith("HTTP server ", StringComparison.OrdinalIgnoreCase))
        {
            server.Name = folderName;
        }

        config.NameBox.Text = server.Name;
        config.ModelBox.Text = server.Model;
        config.ServedModelNameBox.Text = server.ServedModelName ?? string.Empty;
    }

    private void UpdateLocalModelMetadataDisplay(VllmServerConfiguration server)
    {
        config.LocalModelMetadataText.Text = server.UseExistingHttpServer
            ? "Local model metadata is only used for Docker-managed local deployments."
            : server.MetadataSummary;
    }

    private void UpdateModeVisibility(VllmServerConfiguration server)
    {
        var isExternal = server.UseExistingHttpServer;
        config.ConfigurationTitleText.Text = isExternal ? "External server profile" : "Local deployment profile";
        config.ModelFieldLabelText.Text = isExternal ? "Model name" : "Model / HF model ID";
        config.ServedModelNameLabelText.Text = "Served model name";
        selection.ModelNoteText.Text = isExternal
            ? "External server profiles store an HTTP address plus optional request/model hints. LEPTA can verify them with /v1/models, but start and stop are managed outside the app."
            : VllmDefaults.VllmModelNote;

        config.HttpServerRow.Visibility = isExternal ? Visibility.Visible : Visibility.Collapsed;
        config.ApiKeyRow.Visibility = isExternal ? Visibility.Visible : Visibility.Collapsed;
        config.ServedModelsRow.Visibility = isExternal ? Visibility.Visible : Visibility.Collapsed;
        if (!isExternal)
        {
            ClearServedModels();
        }
        config.LocalFolderRow.Visibility = isExternal ? Visibility.Collapsed : Visibility.Visible;
        config.ServedModelNameRow.Visibility = isExternal ? Visibility.Collapsed : Visibility.Visible;
        config.LocalMetadataBorder.Visibility = isExternal ? Visibility.Collapsed : Visibility.Visible;
        config.LocalRuntimeSettingsPanel.Visibility = isExternal ? Visibility.Collapsed : Visibility.Visible;
        deploy.EstimateBorder.Visibility = isExternal ? Visibility.Collapsed : Visibility.Visible;
        deploy.DockerStatusBorder.Visibility = isExternal ? Visibility.Collapsed : Visibility.Visible;
        // The Server log panel (+ verbose-vLLM toggle) is Docker-deployment chrome.
        // External-server profiles get a focused endpoint/model/auth/test flow instead,
        // with probe results surfaced through the served-model picker and profile status.
        deploy.DeploymentLogBorder.Visibility = isExternal ? Visibility.Collapsed : Visibility.Visible;
        deploy.OpenAdvancedConfigurationButton.Visibility = isExternal ? Visibility.Collapsed : Visibility.Visible;
        deploy.OpenAdvancedConfigurationButton.IsEnabled = !IsBusy && !isExternal;

        if (isExternal && deploy.AdvancedConfigurationPanel.Visibility == Visibility.Visible)
        {
            deploy.AdvancedConfigurationPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ResetServerStatus(VllmServerConfiguration server)
    {
        if (server.UseExistingHttpServer)
        {
            SetServerStatus(
                server,
                ServerStatusKind.Unknown,
                string.IsNullOrWhiteSpace(server.HttpServerAddress) ? "Address required" : "Not checked",
                string.IsNullOrWhiteSpace(server.HttpServerAddress)
                    ? "Enter an HTTP server address for this external profile."
                    : "Use Check server to probe /v1/models.");
            return;
        }

        var composePath = deploymentService.CreateComposeConfiguration(server, composeDirectory).ComposeFilePath;
        SetServerStatus(
            server,
            File.Exists(composePath) ? ServerStatusKind.Warning : ServerStatusKind.Unknown,
            File.Exists(composePath) ? "Stopped" : "Configured",
            File.Exists(composePath)
                ? "Saved deployment assets exist for this local profile, but the server has not been verified yet."
                : "Configure the local model and choose Run server when you're ready.");
    }

    private void SetServerStatus(VllmServerConfiguration server, ServerStatusKind kind, string text, string details)
    {
        server.Runtime.StatusKind = kind;
        server.Runtime.StatusText = text;
        server.Runtime.StatusDetails = details;
    }

    private void UpdateActionButtons()
    {
        var server = SelectedServer;
        var isBusy = currentActionCancellation is not null;
        var isExternal = server?.UseExistingHttpServer == true;
        var hasServer = server is not null;
        var canCancelLocalDeployment = hasServer
            && !isExternal
            && isBusy
            && activeActionCanStopServer
            && ReferenceEquals(activeActionServer, server);

        deploy.StartServerButton.Content = "Run server";
        deploy.StartServerButton.ToolTip = "Generate deployment assets and start the selected LEPTA-managed local server.";
        deploy.StartServerButton.IsEnabled = hasServer && !isBusy && !isExternal;
        deploy.StartServerButton.Visibility = hasServer && !isExternal ? Visibility.Visible : Visibility.Collapsed;

        deploy.CheckServerButton.Visibility = isExternal && hasServer && !isBusy ? Visibility.Visible : Visibility.Collapsed;
        deploy.CheckServerButton.IsEnabled = isExternal && hasServer && !isBusy;

        deploy.ModelActionsBorder.Visibility = hasServer && !isExternal ? Visibility.Visible : Visibility.Collapsed;

        deploy.StopServerButton.Content = canCancelLocalDeployment ? "Cancel deployment" : "Stop server";
        deploy.StopServerButton.ToolTip = isExternal
            ? "External servers are managed outside LEPTA, so Stop is unavailable for this profile."
            : canCancelLocalDeployment
                ? $"Cancel the current action ({activeActionMessage ?? "deployment in progress"}) for '{server?.Name}' and stop any started container."
                : "Stop the selected LEPTA-managed local server.";
        deploy.StopServerButton.IsEnabled = hasServer && !isExternal && (!isBusy || canCancelLocalDeployment);
        deploy.StopServerButton.Opacity = isExternal ? 0.55 : 1.0;

        deploy.RestartServerButton.Content = "Restart server";
        deploy.RestartServerButton.ToolTip = isExternal
            ? "External servers are managed outside LEPTA, so Restart is unavailable for this profile."
            : "Restart the selected LEPTA-managed local server.";
        deploy.RestartServerButton.IsEnabled = hasServer && !isBusy && !isExternal;
        deploy.RestartServerButton.Opacity = isExternal ? 0.55 : 1.0;
        deploy.StopServerButton.Visibility = hasServer && !isExternal ? Visibility.Visible : Visibility.Collapsed;
        deploy.RestartServerButton.Visibility = hasServer && !isExternal ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetDockerStatusState(string summary, string details, string brushKey)
    {
        deploy.DockerStatusText.Text = summary;
        deploy.DockerStatusDetailsText.Text = details;
        deploy.DockerStatusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        deploy.DockerStatusIndicator.SetResourceReference(Border.BackgroundProperty, brushKey);
    }

    private static string? TryGetComboBoxValue(ComboBox comboBox)
        => comboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString()
            : null;

    private bool IsExistingHttpServerSelected()
        => config.DeploymentModeBox.SelectedItem is ComboBoxItem item
           && string.Equals(item.Tag?.ToString(), "ExistingHttpServer", StringComparison.OrdinalIgnoreCase);

    private void SetDeploymentMode(bool useExistingHttpServer)
    {
        foreach (var item in config.DeploymentModeBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem
                && string.Equals(
                    comboBoxItem.Tag?.ToString(),
                    useExistingHttpServer ? "ExistingHttpServer" : "LocalDeploy",
                    StringComparison.OrdinalIgnoreCase))
            {
                config.DeploymentModeBox.SelectedItem = item;
                return;
            }
        }

        config.DeploymentModeBox.SelectedIndex = -1;
    }

    private static void SetComboBoxValue(ComboBox comboBox, string? value)
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

    private static bool IsVerboseLogMessage(string message)
        => !message.StartsWith("[docker-diagnostic]", StringComparison.OrdinalIgnoreCase)
           && (message.StartsWith("[docker]", StringComparison.OrdinalIgnoreCase)
               || message.Contains("INFO", StringComparison.OrdinalIgnoreCase)
               || message.Contains("DEBUG", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Pulling", StringComparison.OrdinalIgnoreCase)
               || message.Contains("digest", StringComparison.OrdinalIgnoreCase));

    private void SetConfigurationInputsEnabled(bool isEnabled)
    {
        selection.ModelsList.IsEnabled = isEnabled;
        selection.ChatServerCombo.IsEnabled = isEnabled;
        config.NameBox.IsEnabled = isEnabled;
        config.DeploymentModeBox.IsEnabled = isEnabled;
        config.HttpServerAddressBox.IsEnabled = isEnabled;
        config.ModelBox.IsEnabled = isEnabled;
        config.LocalPathBox.IsEnabled = isEnabled;
        config.ServedModelNameBox.IsEnabled = isEnabled;
        config.DockerImageBox.IsEnabled = isEnabled;
        config.PortBox.IsEnabled = isEnabled;
        config.DTypeBox.IsEnabled = isEnabled;
        config.GpuBox.IsEnabled = isEnabled;
        config.GpuVramBox.IsEnabled = isEnabled;
        config.MaxLenBox.IsEnabled = isEnabled;
        config.ReadyTimeoutBox.IsEnabled = isEnabled;
        config.CpuOffloadBox.IsEnabled = isEnabled;
        config.WeightQuantizationBox.IsEnabled = isEnabled;
        config.TensorParallelBox.IsEnabled = isEnabled;
        config.KCacheQuantizationBox.IsEnabled = isEnabled;
        config.VCacheQuantizationBox.IsEnabled = isEnabled;
        config.TokenizersParallelismBox.IsEnabled = isEnabled;
        config.AdditionalVllmArgumentsBox.IsEnabled = isEnabled;
        config.ServedModelsCombo.IsEnabled = isEnabled;
        config.ApiKeyBox.IsEnabled = isEnabled;
        config.ApiKeyRevealBox.IsEnabled = isEnabled;
        config.ApiKeyRevealCheckBox.IsEnabled = isEnabled;
        config.AuthHeaderNameBox.IsEnabled = isEnabled;
        config.AuthHeaderSchemeBox.IsEnabled = isEnabled;
        config.ExtraHeadersBox.IsEnabled = isEnabled;
        config.ExtraBodyBox.IsEnabled = isEnabled;
        config.OpenRouterPresetButton.IsEnabled = isEnabled;
        config.MaxNumSeqsBox.IsEnabled = isEnabled;
        config.VerboseLogsCheckBox.IsEnabled = isEnabled;
        deploy.OpenAdvancedConfigurationButton.IsEnabled = isEnabled && SelectedServer?.UseExistingHttpServer != true;
    }

    private string GetCurrentApiKey()
        => config.ApiKeyRevealCheckBox.IsChecked == true
            ? config.ApiKeyRevealBox.Text
            : config.ApiKeyBox.Password;

    private void SetCurrentApiKey(string value)
    {
        config.ApiKeyBox.Password = value;
        config.ApiKeyRevealBox.Text = value;
    }

    /// <summary>Reveal-toggle changed: swap the masked PasswordBox for the plain TextBox, keeping values in sync.</summary>
    public void HandleApiKeyRevealChanged()
    {
        if (isLoadingConfiguration)
        {
            return;
        }

        var reveal = config.ApiKeyRevealCheckBox.IsChecked == true;
        if (reveal)
        {
            config.ApiKeyRevealBox.Text = config.ApiKeyBox.Password;
            config.ApiKeyRevealBox.Visibility = Visibility.Visible;
            config.ApiKeyBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            config.ApiKeyBox.Password = config.ApiKeyRevealBox.Text;
            config.ApiKeyRevealBox.Visibility = Visibility.Collapsed;
            config.ApiKeyBox.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Revealed text changed: propagate back to the PasswordBox and re-evaluate the profile.</summary>
    public void HandleRevealApiKeyChanged()
    {
        if (isLoadingConfiguration || config.ApiKeyRevealCheckBox.IsChecked != true)
        {
            return;
        }

        config.ApiKeyBox.Password = config.ApiKeyRevealBox.Text;
        HandleConfigurationChanged();
    }

    private void ApplyRequestOverridesFromUi(VllmServerConfiguration server)
    {
        var overrides = server.RequestOverrides;
        overrides.ApiKey = string.IsNullOrWhiteSpace(GetCurrentApiKey()) ? null : GetCurrentApiKey().Trim();
        overrides.AuthHeaderName = string.IsNullOrWhiteSpace(config.AuthHeaderNameBox.Text)
            ? "Authorization"
            : config.AuthHeaderNameBox.Text.Trim();
        overrides.AuthHeaderScheme = string.IsNullOrWhiteSpace(config.AuthHeaderSchemeBox.Text)
            ? "Bearer"
            : config.AuthHeaderSchemeBox.Text.Trim();
        overrides.Headers = ServerProfileFormMapper.ParseExtraHeaders(config.ExtraHeadersBox.Text);

        var extraBodyError = ServerProfileFormMapper.TryParseExtraBody(config.ExtraBodyBox.Text, out var extraBody);
        if (extraBodyError is null)
        {
            overrides.ExtraBody = extraBody ?? new Dictionary<string, JsonElement>();
        }

        var validation = new VllmServerProfileValidator().ValidateRequestOverrides(overrides);
        if (validation.IsValid && extraBodyError is null)
        {
            config.RequestOverridesErrorText.Visibility = Visibility.Collapsed;
            config.RequestOverridesErrorText.Text = string.Empty;
        }
        else
        {
            config.RequestOverridesErrorText.Text = extraBodyError ?? validation.Message;
            config.RequestOverridesErrorText.Visibility = Visibility.Visible;
        }
    }

    private void LoadRequestOverridesIntoUi(VllmServerConfiguration server)
    {
        var overrides = server.RequestOverrides;
        SetCurrentApiKey(server.ApiKey);
        config.ApiKeyRevealCheckBox.IsChecked = false;
        config.ApiKeyRevealBox.Visibility = Visibility.Collapsed;
        config.ApiKeyBox.Visibility = Visibility.Visible;
        config.AuthHeaderNameBox.Text = string.IsNullOrWhiteSpace(overrides.AuthHeaderName) ? "Authorization" : overrides.AuthHeaderName;
        config.AuthHeaderSchemeBox.Text = string.IsNullOrWhiteSpace(overrides.AuthHeaderScheme) ? "Bearer" : overrides.AuthHeaderScheme;
        config.ExtraHeadersBox.Text = ServerProfileFormMapper.FormatExtraHeaders(overrides.Headers);
        config.ExtraBodyBox.Text = ServerProfileFormMapper.FormatExtraBody(overrides.ExtraBody);
        config.RequestOverridesErrorText.Visibility = Visibility.Collapsed;
        config.RequestOverridesErrorText.Text = string.Empty;
    }

    private void ClearRequestOverridesUi()
    {
        SetCurrentApiKey(string.Empty);
        config.ApiKeyRevealCheckBox.IsChecked = false;
        config.ApiKeyRevealBox.Visibility = Visibility.Collapsed;
        config.ApiKeyBox.Visibility = Visibility.Visible;
        config.AuthHeaderNameBox.Text = string.Empty;
        config.AuthHeaderSchemeBox.Text = string.Empty;
        config.ExtraHeadersBox.Text = string.Empty;
        config.ExtraBodyBox.Text = string.Empty;
        config.RequestOverridesErrorText.Visibility = Visibility.Collapsed;
        config.RequestOverridesErrorText.Text = string.Empty;
    }

    /// <summary>User picked a model from the served-models list — apply it as the profile's model.</summary>
    public void HandleServedModelSelected()
    {
        if (isPopulatingServedModels || isLoadingConfiguration)
        {
            return;
        }

        if (SelectedServer is not VllmServerConfiguration server)
        {
            return;
        }

        if (config.ServedModelsCombo.SelectedItem is not string selected || string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        if (string.Equals(server.Model?.Trim(), selected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        server.Model = selected;
        config.ModelBox.Text = selected;
        selection.ModelsList.Items.Refresh();
        OnStateChanged();
        logger.Log(nameof(ModelsController), $"Served model '{selected}' applied to '{server.Name}'.");
    }

    /// <summary>Fills the served-model picker from a successful /v1/models probe.</summary>
    private void PopulateServedModels(VllmServerConfiguration server, IReadOnlyList<string> models)
    {
        isPopulatingServedModels = true;
        try
        {
            config.ServedModelsCombo.Items.Clear();
            if (models.Count == 0)
            {
                config.ServedModelsCombo.SelectedItem = null;
                config.ServedModelsHintText.Text = "The server responded but listed no served models.";
                return;
            }

            foreach (var name in models)
            {
                config.ServedModelsCombo.Items.Add(name);
            }

            var current = server.Model?.Trim();
            var match = models.FirstOrDefault(name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase));
            config.ServedModelsCombo.SelectedItem = match ?? models[0];
            config.ServedModelsHintText.Text = models.Count == 1
                ? $"1 served model available from /v1/models."
                : $"{models.Count} served models available from /v1/models. Pick one to use it.";
        }
        finally
        {
            isPopulatingServedModels = false;
        }
    }

    private void ClearServedModels()
    {
        isPopulatingServedModels = true;
        try
        {
            config.ServedModelsCombo.Items.Clear();
            config.ServedModelsCombo.SelectedItem = null;
            config.ServedModelsHintText.Text = "Use Check server to list the models this server serves.";
        }
        finally
        {
            isPopulatingServedModels = false;
        }
    }
}
