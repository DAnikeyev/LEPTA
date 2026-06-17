using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LEPTA.Controllers.Views;
using LEPTA.Models;
using LEPTA.Services;
using LEPTA.Shared.Diagnostics;
using LEPTA.Shared.Models;
using LEPTA.Shared.Services;
using LEPTA.Theming;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Controllers;

internal sealed partial class LeptaController
{
    private readonly List<LeptaDashboardDefinition> dashboards = [];
    private readonly ObservableCollection<ILeptaPanelState> panels = [];
    private readonly ObservableCollection<LeptaDashboardReference> dashboardEntries = [];
    private readonly ObservableCollection<LeptaPresetReference> presetEntries = [];
    private List<StoredLeptaPreset> cachedPresets = [];
    private readonly LeptaPanelsViews panelsView;
    private readonly LeptaInstructionsViews instructions;
    private readonly LeptaDashboardViews dashboardsView;
    private readonly LeptaPresetViews presets;
    private readonly LeptaRunViews run;
    private readonly LeptaHotkeyViews hotkeys;
    private readonly VllmDeploymentService deploymentService;
    private readonly LeptaRequestOrchestrator requestOrchestrator;
    private readonly LeptaPresetStore presetStore;
    private readonly ILeptaLogger logger;
    private readonly IActionLogEventStream actionLog;
    private readonly SemaphoreSlim runLock = new(1, 1);
    private readonly SemaphoreSlim clipboardPrefillLock = new(1, 1);
    private CancellationTokenSource? currentRunCts;
    private TaskCompletionSource<bool>? activeRunCompletion;
    private ILeptaPanelState? editingPanel;
    private bool isBusy;
    private bool suppressStateChanged;
    private bool keepVisibleWhenIdle;
    private ActionLogLevel currentStatusLevel = ActionLogLevel.Info;
    private string currentDashboardId = LeptaDashboardDefinition.DefaultDashboardId;
    private string currentDashboardName = "Default Dashboard";
    private string? currentPresetId;
    private string lastRunClipboardText = string.Empty;
    private string? lastResolvedModelName;
    private string lastClipboardPrefillSharedPromptPrefix = string.Empty;
    private string? lastClipboardPrefillServerId;
    private string? lastClipboardPrefillModelName;
    private string? lastClipboardPrefillCacheSalt;
    private string? preferredServerId;
    private LeptaSettings settings = LeptaSettings.CreateDefault();
    private double currentTemperature = LeptaSettings.DefaultTemperature;

    public LeptaController(
        LeptaControllerViews views,
        VllmDeploymentService deploymentService,
        VllmConversationService conversationService,
        LeptaPresetStore presetStore,
        LeptaControllerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(views);
        options ??= new LeptaControllerOptions();

        panelsView = views.Panels;
        instructions = views.Instructions;
        dashboardsView = views.Dashboards;
        presets = views.Presets;
        run = views.Run;
        hotkeys = views.Hotkeys;
        this.deploymentService = deploymentService;
        this.presetStore = presetStore;
        logger = options.Logger ?? NullLeptaLogger.Instance;
        actionLog = options.ActionLog ?? NullActionLogEventStream.Instance;
        requestOrchestrator = new LeptaRequestOrchestrator(conversationService, logger);

        panelsView.ItemsControl.ItemsSource = panels;
        dashboardsView.ListCombo.ItemsSource = dashboardEntries;
        presets.ListCombo.ItemsSource = presetEntries;
        instructions.GeneralInstructionBox.TextChanged += (_, _) => OnStateChanged();
        run.ThinkingCheckBox.Checked += (_, _) => OnStateChanged();
        run.ThinkingCheckBox.Unchecked += (_, _) => OnStateChanged();
        run.TemperatureTextBox.TextChanged += (_, _) => HandleTemperatureTextChanged();
        dashboardsView.NameBox.TextChanged += (_, _) =>
        {
            UpdateCurrentDashboardReferenceName();
            OnStateChanged();
        };
        run.ServerCombo.SelectionChanged += (_, _) =>
        {
            if (!suppressStateChanged && run.ServerCombo.SelectedItem is VllmServerConfiguration server)
            {
                preferredServerId = server.Id;
            }

            OnStateChanged();
        };
        presets.NameBox.TextChanged += (_, _) => OnStateChanged();
        SeedHotkeyKeys();
        ApplyHotkeySettings(HotkeySettings.CreateDefault());
        LoadDashboards([LeptaDashboardDefinition.CreateDefault()], LeptaDashboardDefinition.DefaultDashboardId);
        SetStatusMessage("Configure panel instructions, then run from the button or global clipboard shortcut.");
        SetHotkeyRegistrationStatus("The shortcut will be registered when the window finishes loading.");
        StartupWarnings = ReloadPresetEntries();
    }

    public IReadOnlyList<string> StartupWarnings { get; private set; } = [];

    public string CurrentDashboardId => currentDashboardId;

    public bool IsBusy => isBusy;

    public string? LastResolvedModelName => lastResolvedModelName;

    public void ApplySettings(LeptaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = new LeptaSettings
        {
            EnableSharedPromptPrefill = settings.EnableSharedPromptPrefill,
            DocumentTrimMode = settings.DocumentTrimMode,
            DocumentTokenLimit = LeptaSettings.NormalizeDocumentTokenLimit(settings.DocumentTokenLimit)
        };
    }

    public LeptaSettings CaptureSettings() => new()
    {
        EnableSharedPromptPrefill = settings.EnableSharedPromptPrefill,
        DocumentTrimMode = settings.DocumentTrimMode,
        DocumentTokenLimit = settings.DocumentTokenLimit
    };

    public event Action? HotkeySettingsChanged;

    public event Action? StateChanged;

    public event Action? PanelMetadataChanged;

    public event Action? ThroughputReset;

    public event Action<int>? ThroughputTokensObserved;

    public event Action? ThroughputCompleted;

    public event Action? ThroughputFirstPanelCompleted;

    public event Action<string>? ThroughputModelResolved;

    public async Task CancelForShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (clipboardPrefillLock.CurrentCount == 0)
        {
            logger.Log(nameof(LeptaController), "Waiting for the active clipboard cache prefill to finish during shutdown.");
        }

        if (!isBusy)
        {
            await clipboardPrefillLock.WaitAsync(cancellationToken);
            clipboardPrefillLock.Release();
            return;
        }

        logger.Log(nameof(LeptaController), "Cancelling LEPTA run during shutdown.");
        PublishAction("Cancelling the active LEPTA run before shutdown.", ActionLogLevel.Warning);
        currentRunCts?.Cancel();
        var completion = activeRunCompletion?.Task;
        if (completion is null)
        {
            return;
        }

        await AwaitCompletionAsync(completion, timeout, cancellationToken);
        await clipboardPrefillLock.WaitAsync(cancellationToken);
        clipboardPrefillLock.Release();
    }

    public void CancelCurrentRun()
    {
        if (!isBusy || currentRunCts is null || currentRunCts.IsCancellationRequested)
        {
            return;
        }

        run.StopButton.IsEnabled = false;
        SetStatusMessage("Cancelling LEPTA run...", ActionLogLevel.Warning, keepVisibleWhenIdle: true);
        logger.Log(nameof(LeptaController), "Cancelling the active LEPTA run from the sidebar stop button.");
        PublishAction("Cancelling the active LEPTA run.", ActionLogLevel.Warning);
        currentRunCts.Cancel();
    }

    public void SelectDashboardById(string? dashboardId)
    {
        if (string.Equals(currentDashboardId, dashboardId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SyncCurrentDashboardIntoCollection();
        var dashboard = ResolveDashboard(dashboardId);
        if (dashboard is null)
        {
            return;
        }

        ApplyDashboardState(dashboard);
    }

    public void SelectServer(string? serverId)
    {
        preferredServerId = serverId;
        ApplyServerSelection();
        HandleServerSelectionChanged();
    }

    public void BindServers(IEnumerable<VllmServerConfiguration> servers)
    {
        suppressStateChanged = true;
        try
        {
            run.ServerCombo.ItemsSource = servers;
            ApplyServerSelection();
        }
        finally
        {
            suppressStateChanged = false;
        }

        HandleServerSelectionChanged();
        logger.Log(nameof(LeptaController), "Bound LEPTA server list.");
    }

    public void RefreshAvailableServers()
    {
        if (isBusy)
        {
            return;
        }

        var previousSelection = run.ServerCombo.SelectedItem as VllmServerConfiguration;
        ApplyServerSelection();
        var currentSelection = run.ServerCombo.SelectedItem as VllmServerConfiguration;
        if (ReferenceEquals(previousSelection, currentSelection))
        {
            return;
        }

        HandleServerSelectionChanged();
        if (currentSelection is not null
            && !string.IsNullOrWhiteSpace(preferredServerId)
            && string.Equals(currentSelection.Id, preferredServerId, StringComparison.OrdinalIgnoreCase)
            && (previousSelection is null
                || !string.Equals(previousSelection.Id, preferredServerId, StringComparison.OrdinalIgnoreCase)))
        {
            logger.Log(nameof(LeptaController), $"LEPTA server selection restored to saved profile '{currentSelection.Name}'.");
        }
    }

    public void HandleServerSelectionChanged()
    {
        if (isBusy)
        {
            return;
        }

        var server = run.ServerCombo.SelectedItem as VllmServerConfiguration;
        if (server is null)
        {
            run.RunButton.IsEnabled = false;
            run.StopButton.IsEnabled = false;
            run.ThinkingCheckBox.IsEnabled = false;
            SetStatusMessage("Select a verified model server to run LEPTA from clipboard.");
            return;
        }

        if (!server.HasEstablishedConnection)
        {
            run.RunButton.IsEnabled = false;
            run.StopButton.IsEnabled = false;
            run.ThinkingCheckBox.IsEnabled = false;
            SetStatusMessage("LEPTA becomes available after the selected profile responds to /v1/models.");
            return;
        }

        run.RunButton.IsEnabled = true;
        run.StopButton.IsEnabled = false;
        run.ThinkingCheckBox.IsEnabled = server.SupportsThinking;
        run.ThinkingCheckBox.ToolTip = server.SupportsThinking
            ? "Allow the selected model to spend more effort on panel responses."
            : "Thinking is available only when the selected model profile advertises reasoning support.";
        SetStatusMessage($"Ready to run panels from clipboard through {server.Endpoint}. LEPTA will resolve the served model from /v1/models before sending requests.");
    }

    public async Task RunFromClipboardAsync(CancellationToken cancellationToken = default)
    {
        if (!await runLock.WaitAsync(0, cancellationToken))
        {
            currentRunCts?.Cancel();
            SetStatusMessage("Cancelling the previous clipboard run before starting a new one...");
            logger.Log(nameof(LeptaController), "A new LEPTA run requested cancellation of the previous run.");
            PublishAction("Cancelled the previous LEPTA clipboard run so a new one can start.", ActionLogLevel.Warning);
            await runLock.WaitAsync(cancellationToken);
        }

        logger.Log(nameof(LeptaController), "Run from clipboard requested.");
        var clipboardText = string.Empty;
        try
        {
            if (Clipboard.ContainsText())
            {
                clipboardText = Clipboard.GetText();
            }
        }
        catch (Exception exception)
        {
            SetStatusMessage($"Clipboard is unavailable: {exception.Message}", ActionLogLevel.Error, keepVisibleWhenIdle: true);
            logger.Log(nameof(LeptaController), $"Clipboard read failed: {exception.Message}");
        }

        try
        {
            await RunInternalAsync(clipboardText, cancellationToken);
        }
        finally
        {
            runLock.Release();
        }
    }

    public async Task RequestClipboardCachePrefillAsync(string? clipboardText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clipboardText) || isBusy)
        {
            return;
        }

        if (run.ServerCombo.SelectedItem is not VllmServerConfiguration server || !server.HasEstablishedConnection)
        {
            return;
        }

        var sharedPromptPrefix = LeptaRequestOrchestrator.BuildSharedPromptPrefix(
            instructions.SystemInstructionBox.Text,
            clipboardText,
            instructions.GeneralInstructionBox.Text,
            settings.DocumentTrimMode,
            settings.DocumentTokenLimit);
        if (string.IsNullOrWhiteSpace(sharedPromptPrefix))
        {
            return;
        }

        await clipboardPrefillLock.WaitAsync(cancellationToken);
        try
        {
            if (isBusy)
            {
                return;
            }

            var probe = await deploymentService.ProbeHttpServerAsync(server, cancellationToken);
            if (!probe.IsSuccess || string.IsNullOrWhiteSpace(probe.FirstModelName) || string.IsNullOrWhiteSpace(probe.NormalizedEndpoint))
            {
                logger.Log(nameof(LeptaController), $"Clipboard cache prefill skipped because '{server.Name}' is not ready. reason={probe.Message}");
                return;
            }

            var prefillModel = ResolveRunModel(server, probe);
            if (string.Equals(lastClipboardPrefillServerId, server.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(lastClipboardPrefillModelName, prefillModel, StringComparison.Ordinal)
                && string.Equals(lastClipboardPrefillSharedPromptPrefix, sharedPromptPrefix, StringComparison.Ordinal))
            {
                return;
            }

            var cacheSalt = Guid.NewGuid().ToString("N");
            await requestOrchestrator.PrefillSharedPromptPrefixAsync(
                probe.NormalizedEndpoint,
                prefillModel,
                sharedPromptPrefix,
                new VllmRequestOptions
                {
                    CacheSalt = cacheSalt
                },
                LeptaRequestOrchestrator.ClipboardCachePrefillMaxTokens,
                apiKey: server.ApiKey,
                cancellationToken: cancellationToken);

            lastClipboardPrefillServerId = server.Id;
            lastClipboardPrefillModelName = prefillModel;
            lastClipboardPrefillSharedPromptPrefix = sharedPromptPrefix;
            lastClipboardPrefillCacheSalt = cacheSalt;
            logger.Log(nameof(LeptaController), $"Clipboard cache prefill completed for server '{server.Name}' using model '{prefillModel}'. clipboardLength={clipboardText.Length}, prefixLength={sharedPromptPrefix.Length}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.Log(nameof(LeptaController), $"Clipboard cache prefill failed. reason={exception.Message}");
        }
        finally
        {
            clipboardPrefillLock.Release();
        }
    }

    private async Task RunInternalAsync(string? clipboardText, CancellationToken cancellationToken)
    {
        var server = run.ServerCombo.SelectedItem as VllmServerConfiguration;
        if (server is null)
        {
            SetStatusMessage("Select a target server before running Lepta.", ActionLogLevel.Warning, keepVisibleWhenIdle: true);
            logger.Log(nameof(LeptaController), "LEPTA run rejected because no server is selected.");
            return;
        }

        if (!server.HasEstablishedConnection)
        {
            SetStatusMessage("Lepta requests are available only for verified model servers.", ActionLogLevel.Warning, keepVisibleWhenIdle: true);
            logger.Log(nameof(LeptaController), $"LEPTA run rejected because '{server.Name}' has not established a verified connection.");
            return;
        }

        logger.Log(nameof(LeptaController), $"LEPTA run starting for server '{server.Name}'. panelCount={panels.Count}, clipboardLength={clipboardText?.Length ?? 0}.");
        PublishAction($"Starting LEPTA run for {panels.Count} panel(s) on '{server.Name}'.");
        lastRunClipboardText = clipboardText ?? string.Empty;
        lastResolvedModelName = null;
        CancelAllMermaidRepairs();
        var runPanels = panels.ToArray();

        foreach (var panel in runPanels)
        {
            panel.Response = string.Empty;
            panel.Status = string.Empty;
            panel.IsStreaming = false;
            if (panel is LeptaPanelStateBase panelState)
            {
                panelState.ResetRunState();
            }
        }

        MermaidRenderService.Shared.ClearCache();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        currentRunCts = linkedCts;
        activeRunCompletion = runCompletion;
        SetBusyState(true, $"Checking {server.Endpoint}...");
        ThroughputReset?.Invoke();
        PanelResponseUpdatePump? updatePump = null;

        try
        {
            var probe = await deploymentService.ProbeHttpServerAsync(server, linkedCts.Token);
            if (!probe.IsSuccess || string.IsNullOrWhiteSpace(probe.FirstModelName))
            {
                SetStatusMessage(probe.Message, ActionLogLevel.Warning, keepVisibleWhenIdle: true);
                logger.Log(nameof(LeptaController), $"LEPTA run aborted because server '{server.Name}' failed validation or probing. reason={probe.Message}");
                return;
            }

            var model = ResolveRunModel(server, probe);
            lastResolvedModelName = model;
            ThroughputModelResolved?.Invoke(model);
            var sharedPromptPrefix = LeptaRequestOrchestrator.BuildSharedPromptPrefix(
                instructions.SystemInstructionBox.Text,
                clipboardText,
                instructions.GeneralInstructionBox.Text,
                settings.DocumentTrimMode,
                settings.DocumentTokenLimit);
            var clipboardPrefillCacheSalt = ResolveClipboardPrefillCacheSalt(server.Id, model, sharedPromptPrefix);
            var requests = runPanels
                .Select(panel => new LeptaPanelRequest(panel.Name.Trim(), panel.CustomInstruction, panel.Format))
                .ToArray();

            for (var i = 0; i < runPanels.Length; i++)
            {
                runPanels[i].Status = string.Empty;
                runPanels[i].IsStreaming = true;
            }

            updatePump = CreatePanelResponseUpdatePump(runPanels);
            var firstPanelCompletedReported = false;

            var results = await requestOrchestrator.GenerateForPanelsAsync(
                server.Endpoint,
                model,
                instructions.SystemInstructionBox.Text,
                clipboardText,
                instructions.GeneralInstructionBox.Text,
                requests,
                (index, token) =>
                {
                    var estimatedTokens = EstimateTokenCount(token);
                    if (estimatedTokens > 0)
                    {
                        ThroughputTokensObserved?.Invoke(estimatedTokens);
                    }

                    updatePump.PostToken(index, token);
                },
                onPanelCompleted: index =>
                {
                    updatePump.FinalizePanel(index, () =>
                    {
                        if ((uint)index < (uint)runPanels.Length)
                        {
                            runPanels[index].IsStreaming = false;
                        }
                    });

                    if (!firstPanelCompletedReported)
                    {
                        firstPanelCompletedReported = true;
                        ThroughputFirstPanelCompleted?.Invoke();
                    }
                },
                warmSharedPrefix: settings.EnableSharedPromptPrefill,
                enableThinking: server.SupportsThinking && run.ThinkingCheckBox.IsChecked == true,
                documentTrimMode: settings.DocumentTrimMode,
                documentTokenLimit: settings.DocumentTokenLimit,
                temperature: currentTemperature,
                sharedCacheSalt: clipboardPrefillCacheSalt,
                sharedPrefixAlreadyWarm: !string.IsNullOrWhiteSpace(clipboardPrefillCacheSalt),
                apiKey: server.ApiKey,
                cancellationToken: linkedCts.Token);

            await CompletePanelResponseUpdatePumpAsync(updatePump);
            updatePump = null;

            for (var i = 0; i < results.Count && i < runPanels.Length; i++)
            {
                var result = results[i];
                var panel = runPanels[i];
                var generationDuration = result.GenerationDuration ?? TimeSpan.Zero;
                if (panel is LeptaPanelStateBase panelState)
                {
                    panelState.ApplyGenerationOutcome(result.EstimatedVisibleTokenCount, generationDuration, result.Error);
                }

                panel.Status = string.IsNullOrWhiteSpace(result.Error)
                    ? string.Empty
                    : $"Error: {result.Error}";
                panel.IsStreaming = false;

                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    PublishAction($"Panel '{panel.Name}' failed: {result.Error}", ActionLogLevel.Error);
                }
                else if (string.IsNullOrWhiteSpace(panel.Response))
                {
                    panel.Response = result.Text;
                }

                if (string.IsNullOrWhiteSpace(result.Error))
                {
                    PublishAction($"Panel '{panel.Name}' completed.");
                }
            }

            SetStatusMessage($"Generated responses for {results.Count} panel(s) using {model}.");
            logger.Log(nameof(LeptaController), $"LEPTA run completed for server '{server.Name}'. model='{model}', panelCount={results.Count}.");
            PublishAction($"LEPTA run completed on '{server.Name}' with model '{model}'.");
        }
        catch (OperationCanceledException)
        {
            await CompletePanelResponseUpdatePumpAsync(updatePump);
            updatePump = null;

            foreach (var panel in runPanels)
            {
                panel.IsStreaming = false;
            }

            SetStatusMessage("Lepta generation was cancelled.", ActionLogLevel.Warning, keepVisibleWhenIdle: true);
            logger.Log(nameof(LeptaController), $"LEPTA run cancelled for server '{server.Name}'.");
            PublishAction("LEPTA generation was cancelled.", ActionLogLevel.Warning);
        }
        catch (Exception exception)
        {
            await CompletePanelResponseUpdatePumpAsync(updatePump);
            updatePump = null;

            foreach (var panel in runPanels)
            {
                panel.IsStreaming = false;
                if (panel is LeptaPanelStateBase panelState)
                {
                    panelState.ApplyGenerationOutcome(EstimateTokenCount(panel.Response), TimeSpan.Zero, exception.Message);
                }
            }

            SetStatusMessage(exception.Message, ActionLogLevel.Error, keepVisibleWhenIdle: true);
            logger.Log(nameof(LeptaController), $"LEPTA run failed for server '{server.Name}'. reason={exception.Message}");
            PublishAction($"LEPTA run failed for '{server.Name}': {exception.Message}", ActionLogLevel.Error);
        }
        finally
        {
            await CompletePanelResponseUpdatePumpAsync(updatePump);

            if (ReferenceEquals(currentRunCts, linkedCts))
            {
                currentRunCts = null;
            }

            if (ReferenceEquals(activeRunCompletion, runCompletion))
            {
                activeRunCompletion = null;
            }

            runCompletion.TrySetResult(true);

            SetBusyState(false, run.StatusText.Text);
            ThroughputCompleted?.Invoke();
        }
    }

    private void SetBusyState(bool busy, string statusMessage)
    {
        isBusy = busy;
        run.RunButton.IsEnabled = !busy && run.ServerCombo.SelectedItem is VllmServerConfiguration server && server.HasEstablishedConnection;
        run.StopButton.IsEnabled = busy && currentRunCts is not null && !currentRunCts.IsCancellationRequested;
        run.ThinkingCheckBox.IsEnabled = !busy && (run.ServerCombo.SelectedItem as VllmServerConfiguration)?.SupportsThinking == true;
        run.TemperatureTextBox.IsEnabled = !busy;
        run.ProgressBar.IsIndeterminate = busy;
        if (busy)
        {
            currentStatusLevel = ActionLogLevel.Info;
            keepVisibleWhenIdle = false;
        }

        run.StatusText.Text = statusMessage;
        ApplyStatusPresentation();
    }

    private void ApplyServerSelection()
    {
        var servers = GetAvailableServers();
        if (servers.Count == 0)
        {
            if (run.ServerCombo.SelectedItem is not null)
            {
                run.ServerCombo.SelectedItem = null;
            }

            return;
        }

        var server = ResolvePreferredServer(servers);
        if (!ReferenceEquals(run.ServerCombo.SelectedItem, server))
        {
            suppressStateChanged = true;
            try
            {
                run.ServerCombo.SelectedItem = server;
            }
            finally
            {
                suppressStateChanged = false;
            }
        }
    }

    private VllmServerConfiguration ResolvePreferredServer(IReadOnlyList<VllmServerConfiguration> servers)
    {
        if (!string.IsNullOrWhiteSpace(preferredServerId))
        {
            var preferred = servers.FirstOrDefault(item => string.Equals(item.Id, preferredServerId, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return servers[0];
    }

    private List<VllmServerConfiguration> GetAvailableServers()
        => run.ServerCombo.ItemsSource is IEnumerable<VllmServerConfiguration> availableServers
            ? availableServers.ToList()
            : [];


    private void PublishAction(string message, ActionLogLevel level = ActionLogLevel.Info)
        => actionLog.Publish(nameof(LeptaController), message, level);

    private void SetStatusMessage(
        string message,
        ActionLogLevel level = ActionLogLevel.Info,
        bool keepVisibleWhenIdle = false)
    {
        run.StatusText.Text = message;
        currentStatusLevel = level;
        this.keepVisibleWhenIdle = keepVisibleWhenIdle;
        ApplyStatusPresentation();
    }

    private void ApplyStatusPresentation()
    {
        run.StatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            currentStatusLevel switch
            {
                ActionLogLevel.Warning => ThemeResourceKeys.WarningBrush,
                ActionLogLevel.Error => ThemeResourceKeys.ErrorBrush,
                _ => ThemeResourceKeys.SecondaryTextBrush
            });

        run.StatusText.Visibility = string.IsNullOrWhiteSpace(run.StatusText.Text)
            ? Visibility.Collapsed
            : isBusy || keepVisibleWhenIdle || currentStatusLevel is ActionLogLevel.Warning or ActionLogLevel.Error
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static int EstimateTokenCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return Math.Max(1, text.Trim().Length / 4);
    }

    private static string ResolveRunModel(VllmServerConfiguration server, VllmServerProbeResult probe)
    {
        // External hubs (e.g. OpenRouter) return thousands of models from /v1/models, and the first
        // entry is an arbitrary — often paid — model (e.g. z-ai/glm-5.2). For external profiles,
        // honor the model slug the user configured so a chosen free model is actually used. Local
        // deployments keep using the served model name reported by the probe.
        if (server.UseExistingHttpServer && !string.IsNullOrWhiteSpace(server.Model))
        {
            return server.Model.Trim();
        }

        return probe.FirstModelName;
    }

    private string? ResolveClipboardPrefillCacheSalt(string? serverId, string? model, string sharedPromptPrefix)
    {
        if (string.IsNullOrWhiteSpace(sharedPromptPrefix)
            || string.IsNullOrWhiteSpace(lastClipboardPrefillCacheSalt)
            || !string.Equals(lastClipboardPrefillServerId, serverId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(lastClipboardPrefillModelName, model, StringComparison.Ordinal)
            || !string.Equals(lastClipboardPrefillSharedPromptPrefix, sharedPromptPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return lastClipboardPrefillCacheSalt;
    }

    private static async Task AwaitCompletionAsync(Task completionTask, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        await Task.WhenAny(completionTask, timeoutTask);
    }

    private void OnStateChanged()
    {
        if (!suppressStateChanged)
        {
            StateChanged?.Invoke();
        }
    }
}
