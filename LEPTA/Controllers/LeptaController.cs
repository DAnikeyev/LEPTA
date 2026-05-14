using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LEPTA.Models;
using LEPTA.Shared.Diagnostics;
using LEPTA.Shared.Models;
using LEPTA.Shared.Services;
using LEPTA.Theming;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Controllers;

internal sealed class LeptaController
{
    private readonly List<LeptaDashboardDefinition> dashboards = [];
    private readonly ObservableCollection<LeptaPanelState> panels = [];
    private readonly ObservableCollection<LeptaDashboardReference> dashboardEntries = [];
    private readonly ObservableCollection<LeptaPresetReference> presetEntries = [];
    private readonly ItemsControl panelsItemsControl;
    private readonly TextBox generalInstructionBox;
    private readonly ComboBox serverCombo;
    private readonly TextBox dashboardNameBox;
    private readonly ComboBox dashboardListCombo;
    private readonly TextBox presetNameBox;
    private readonly ComboBox presetListCombo;
    private readonly TextBlock statusText;
    private readonly ProgressBar progressBar;
    private readonly Button runButton;
    private readonly CheckBox hotkeyCtrlCheckBox;
    private readonly CheckBox hotkeyAltCheckBox;
    private readonly CheckBox hotkeyShiftCheckBox;
    private readonly CheckBox hotkeyWinCheckBox;
    private readonly ComboBox hotkeyKeyCombo;
    private readonly TextBlock hotkeyPreviewText;
    private readonly TextBlock hotkeyRegistrationStatusText;
    private readonly VllmDeploymentService deploymentService;
    private readonly LeptaRequestOrchestrator requestOrchestrator;
    private readonly LeptaPresetStore presetStore;
    private readonly ILeptaLogger logger;
    private readonly IActionLogEventStream actionLog;
    private readonly SemaphoreSlim runLock = new(1, 1);
    private CancellationTokenSource? currentRunCts;
    private bool isBusy;
    private bool suppressStateChanged;
    private string currentDashboardId = LeptaDashboardDefinition.DefaultDashboardId;
    private string currentDashboardName = "Default Dashboard";
    private string? pendingServerId;

    public LeptaController(
        ItemsControl panelsItemsControl,
        TextBox generalInstructionBox,
        ComboBox serverCombo,
        TextBox dashboardNameBox,
        ComboBox dashboardListCombo,
        TextBox presetNameBox,
        ComboBox presetListCombo,
        TextBlock statusText,
        ProgressBar progressBar,
        Button runButton,
        CheckBox hotkeyCtrlCheckBox,
        CheckBox hotkeyAltCheckBox,
        CheckBox hotkeyShiftCheckBox,
        CheckBox hotkeyWinCheckBox,
        ComboBox hotkeyKeyCombo,
        TextBlock hotkeyPreviewText,
        TextBlock hotkeyRegistrationStatusText,
        VllmDeploymentService deploymentService,
        VllmConversationService conversationService,
        LeptaPresetStore presetStore,
        ILeptaLogger? logger = null,
        IActionLogEventStream? actionLog = null)
    {
        this.panelsItemsControl = panelsItemsControl;
        this.generalInstructionBox = generalInstructionBox;
        this.serverCombo = serverCombo;
        this.dashboardNameBox = dashboardNameBox;
        this.dashboardListCombo = dashboardListCombo;
        this.presetNameBox = presetNameBox;
        this.presetListCombo = presetListCombo;
        this.statusText = statusText;
        this.progressBar = progressBar;
        this.runButton = runButton;
        this.hotkeyCtrlCheckBox = hotkeyCtrlCheckBox;
        this.hotkeyAltCheckBox = hotkeyAltCheckBox;
        this.hotkeyShiftCheckBox = hotkeyShiftCheckBox;
        this.hotkeyWinCheckBox = hotkeyWinCheckBox;
        this.hotkeyKeyCombo = hotkeyKeyCombo;
        this.hotkeyPreviewText = hotkeyPreviewText;
        this.hotkeyRegistrationStatusText = hotkeyRegistrationStatusText;
        this.deploymentService = deploymentService;
        this.presetStore = presetStore;
        this.logger = logger ?? NullLeptaLogger.Instance;
        this.actionLog = actionLog ?? NullActionLogEventStream.Instance;
        requestOrchestrator = new LeptaRequestOrchestrator(conversationService, this.logger);

        this.panelsItemsControl.ItemsSource = panels;
        this.dashboardListCombo.ItemsSource = dashboardEntries;
        this.presetListCombo.ItemsSource = presetEntries;
        this.generalInstructionBox.TextChanged += (_, _) => OnStateChanged();
        this.dashboardNameBox.TextChanged += (_, _) =>
        {
            UpdateCurrentDashboardReferenceName();
            OnStateChanged();
        };
        this.serverCombo.SelectionChanged += (_, _) => OnStateChanged();
        this.presetNameBox.TextChanged += (_, _) => OnStateChanged();
        SeedHotkeyKeys();
        ApplyHotkeySettings(HotkeySettings.CreateDefault());
        LoadDashboards([LeptaDashboardDefinition.CreateDefault()], LeptaDashboardDefinition.DefaultDashboardId);
        statusText.Text = "Configure panel instructions, then run from the button or global clipboard shortcut.";
        SetHotkeyRegistrationStatus("The shortcut will be registered when the window finishes loading.");
        StartupWarnings = ReloadPresetEntries();
    }

    public IReadOnlyList<string> StartupWarnings { get; private set; } = [];

    public string CurrentDashboardId => currentDashboardId;

    public event Action? HotkeySettingsChanged;

    public event Action? StateChanged;

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
        SetServerSelection(serverId);
        HandleServerSelectionChanged();
    }

    public void BindServers(IEnumerable<VllmServerConfiguration> servers)
    {
        suppressStateChanged = true;
        try
        {
            serverCombo.ItemsSource = servers;
            SetServerSelection(pendingServerId);
        }
        finally
        {
            suppressStateChanged = false;
        }

        HandleServerSelectionChanged();
        logger.Log(nameof(LeptaController), "Bound LEPTA server list.");
    }

    public void LoadDashboards(IEnumerable<LeptaDashboardDefinition> availableDashboards, string? selectedDashboardId)
    {
        ArgumentNullException.ThrowIfNull(availableDashboards);

        dashboards.Clear();
        dashboards.AddRange(
            availableDashboards
                .Where(dashboard => dashboard is not null)
                .Select(CloneDashboard));

        if (dashboards.Count == 0)
        {
            dashboards.Add(LeptaDashboardDefinition.CreateDefault());
        }

        suppressStateChanged = true;
        try
        {
            dashboardEntries.Clear();
            foreach (var dashboard in dashboards)
            {
                dashboardEntries.Add(new LeptaDashboardReference
                {
                    Id = dashboard.Id,
                    Name = dashboard.Name
                });
            }
        }
        finally
        {
            suppressStateChanged = false;
        }

        var selectedDashboard = ResolveDashboard(selectedDashboardId) ?? dashboards[0];
        ApplyDashboardState(selectedDashboard, notifyStateChanged: false);
    }

    public IReadOnlyList<LeptaDashboardDefinition> CaptureDashboards()
    {
        SyncCurrentDashboardIntoCollection();
        return dashboards.Select(CloneDashboard).ToList();
    }

    public void ApplyDashboardState(LeptaDashboardDefinition dashboard, bool notifyStateChanged = true)
    {
        ArgumentNullException.ThrowIfNull(dashboard);

        suppressStateChanged = true;
        try
        {
            currentDashboardId = string.IsNullOrWhiteSpace(dashboard.Id) ? LeptaDashboardDefinition.DefaultDashboardId : dashboard.Id.Trim();
            currentDashboardName = NormalizeDashboardName(dashboard.Name);
            pendingServerId = dashboard.SelectedServerId;
            dashboardNameBox.Text = currentDashboardName;
            SelectDashboard(currentDashboardId);
            generalInstructionBox.Text = dashboard.GeneralInstruction ?? string.Empty;
            ReplacePanels(dashboard.Panels);
            SetServerSelection(pendingServerId);
        }
        finally
        {
            suppressStateChanged = false;
        }

        HandleServerSelectionChanged();
        if (notifyStateChanged)
        {
            OnStateChanged();
        }
    }

    public LeptaDashboardDefinition CaptureDashboardState() => new()
    {
        Id = currentDashboardId,
        Name = NormalizeDashboardName(dashboardNameBox.Text),
        SelectedServerId = (serverCombo.SelectedItem as VllmServerConfiguration)?.Id,
        GeneralInstruction = generalInstructionBox.Text.Trim(),
        Panels = panels
            .Select(panel => new LeptaPanelDefinition
            {
                Name = panel.Name,
                CustomInstruction = panel.CustomInstruction
            })
            .ToList()
    };

    public void HandleDashboardSelectionChanged()
    {
        if (suppressStateChanged || dashboardListCombo.SelectedItem is not LeptaDashboardReference selectedDashboard)
        {
            return;
        }

        SyncCurrentDashboardIntoCollection();
        var dashboard = ResolveDashboard(selectedDashboard.Id);
        if (dashboard is null)
        {
            return;
        }

        ApplyDashboardState(dashboard);
        logger.Log(nameof(LeptaController), $"Selected dashboard '{dashboard.Name}'. id={dashboard.Id}.");
    }

    public void SaveDashboard()
    {
        SyncCurrentDashboardIntoCollection();
        statusText.Text = $"Dashboard saved: {currentDashboardName}";
        logger.Log(nameof(LeptaController), $"Saved dashboard '{currentDashboardName}'. id={currentDashboardId}.");
        PublishAction($"Dashboard saved: {currentDashboardName}");
        OnStateChanged();
    }

    public void SaveDashboardAsNew()
    {
        SyncCurrentDashboardIntoCollection();
        var dashboard = CaptureDashboardState();
        dashboard.Id = Guid.NewGuid().ToString("N");
        dashboard.Name = EnsureUniqueDashboardName(NormalizeDashboardName(dashboardNameBox.Text));
        dashboards.Add(CloneDashboard(dashboard));
        dashboardEntries.Add(new LeptaDashboardReference
        {
            Id = dashboard.Id,
            Name = dashboard.Name
        });
        ApplyDashboardState(dashboard);
        statusText.Text = $"Dashboard saved as new: {dashboard.Name}";
        logger.Log(nameof(LeptaController), $"Saved new dashboard '{dashboard.Name}'. id={dashboard.Id}.");
        PublishAction($"Dashboard saved as new: {dashboard.Name}");
    }

    public void DeleteSelectedDashboard()
    {
        if (dashboardListCombo.SelectedItem is not LeptaDashboardReference selectedDashboard)
        {
            statusText.Text = "Select a saved dashboard to delete.";
            return;
        }

        if (MessageBox.Show(
                $"Delete dashboard '{selectedDashboard.Name}'?",
                "Delete dashboard",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var selectedIndex = dashboardEntries.IndexOf(selectedDashboard);
        dashboards.RemoveAll(item => string.Equals(item.Id, selectedDashboard.Id, StringComparison.OrdinalIgnoreCase));
        dashboardEntries.Remove(selectedDashboard);

        if (dashboards.Count == 0)
        {
            var defaultDashboard = LeptaDashboardDefinition.CreateDefault();
            dashboards.Add(defaultDashboard);
            dashboardEntries.Add(new LeptaDashboardReference
            {
                Id = defaultDashboard.Id,
                Name = defaultDashboard.Name
            });
            selectedIndex = 0;
        }

        var nextIndex = Math.Clamp(selectedIndex, 0, dashboardEntries.Count - 1);
        var nextDashboardId = dashboardEntries[nextIndex].Id;
        ApplyDashboardState(ResolveDashboard(nextDashboardId)!, notifyStateChanged: false);
        statusText.Text = $"Dashboard deleted: {selectedDashboard.Name}";
        logger.Log(nameof(LeptaController), $"Deleted dashboard '{selectedDashboard.Name}'. id={selectedDashboard.Id}.");
        PublishAction($"Dashboard deleted: {selectedDashboard.Name}", ActionLogLevel.Warning);
        OnStateChanged();
    }

    public HotkeySettings GetHotkeySettings() => new()
    {
        Ctrl = hotkeyCtrlCheckBox.IsChecked == true,
        Alt = hotkeyAltCheckBox.IsChecked == true,
        Shift = hotkeyShiftCheckBox.IsChecked == true,
        Win = hotkeyWinCheckBox.IsChecked == true,
        Key = hotkeyKeyCombo.SelectedItem as string ?? "F8"
    };

    public void ApplyHotkeySettings(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        suppressStateChanged = true;
        try
        {
            hotkeyCtrlCheckBox.IsChecked = settings.Ctrl;
            hotkeyAltCheckBox.IsChecked = settings.Alt;
            hotkeyShiftCheckBox.IsChecked = settings.Shift;
            hotkeyWinCheckBox.IsChecked = settings.Win;
            hotkeyKeyCombo.SelectedItem = hotkeyKeyCombo.Items.Cast<object>()
                .OfType<string>()
                .FirstOrDefault(item => string.Equals(item, settings.Key, StringComparison.OrdinalIgnoreCase))
                ?? "F8";
            hotkeyPreviewText.Text = $"Current shortcut: {BuildHotkeyDisplayText()}";
        }
        finally
        {
            suppressStateChanged = false;
        }
    }

    public void HandleServerSelectionChanged()
    {
        if (isBusy)
        {
            return;
        }

        var server = serverCombo.SelectedItem as VllmServerConfiguration;
        if (server is null)
        {
            runButton.IsEnabled = false;
            statusText.Text = "Select an already deployed HTTP server to run LEPTA from clipboard.";
            return;
        }

        if (!server.UseExistingHttpServer)
        {
            runButton.IsEnabled = false;
            statusText.Text = "LEPTA runs only against 'Already deployed HTTP server' profiles in this stage. Docker-managed local deployment remains later-stage behavior.";
            return;
        }

        runButton.IsEnabled = true;
        statusText.Text = $"Ready to run panels from clipboard through {server.Endpoint}. LEPTA will resolve the served model from /v1/models before sending requests.";
    }

    public void AddPanel()
    {
        var nextIndex = panels.Count + 1;
        panels.Add(CreatePanelState($"Panel {nextIndex}", "Answer with the perspective for this panel."));
        logger.Log(nameof(LeptaController), $"Added panel {nextIndex}. panelCount={panels.Count}.");
        OnStateChanged();
    }

    public void MovePanelLeft(Guid panelId) => MovePanel(panelId, -1);

    public void MovePanelRight(Guid panelId) => MovePanel(panelId, 1);

    public void RemovePanel(Guid panelId)
    {
        var panel = panels.FirstOrDefault(item => item.Id == panelId);
        if (panel is null)
        {
            return;
        }

        DetachPanel(panel);
        panels.Remove(panel);
        if (panels.Count == 0)
        {
            panels.Add(CreatePanelState("Panel 1", "Answer with the perspective for this panel."));
        }

        logger.Log(nameof(LeptaController), $"Removed panel '{panel.Name}'. panelCount={panels.Count}.");
        OnStateChanged();
    }

    public async Task RunFromClipboardAsync(CancellationToken cancellationToken = default)
    {
        if (!await runLock.WaitAsync(0, cancellationToken))
        {
            currentRunCts?.Cancel();
            statusText.Text = "Cancelling the previous clipboard run before starting a new one...";
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
            statusText.Text = $"Clipboard is unavailable: {exception.Message}";
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

    public void SavePreset()
    {
        var presetName = NormalizePresetName(presetNameBox.Text);
        var selectedPreset = presetListCombo.SelectedItem as LeptaPresetReference;
        var matchingPreset = presetEntries.FirstOrDefault(item => string.Equals(item.Name, presetName, StringComparison.OrdinalIgnoreCase));
        var presetId = selectedPreset?.Id ?? matchingPreset?.Id ?? Guid.NewGuid().ToString("N");
        var preset = BuildStoredPreset(presetId, presetName);
        presetStore.Save(preset);
        ReloadPresetEntries(preset.Id);
        presetNameBox.Text = preset.Name;
        statusText.Text = $"Preset saved: {preset.Name}";
        logger.Log(nameof(LeptaController), $"Saved preset '{preset.Name}'. id={preset.Id}.");
        PublishAction($"Preset saved: {preset.Name}");
        OnStateChanged();
    }

    public void SavePresetAsNew()
    {
        var presetName = EnsureUniquePresetName(NormalizePresetName(presetNameBox.Text));
        var preset = BuildStoredPreset(Guid.NewGuid().ToString("N"), presetName);
        presetStore.Save(preset);
        ReloadPresetEntries(preset.Id);
        presetNameBox.Text = preset.Name;
        statusText.Text = $"Preset saved as new: {preset.Name}";
        logger.Log(nameof(LeptaController), $"Saved new preset '{preset.Name}'. id={preset.Id}.");
        PublishAction($"Preset saved as new: {preset.Name}");
        OnStateChanged();
    }

    public void LoadPreset()
    {
        if (!TryLoadSelectedPreset(out var preset))
        {
            return;
        }

        suppressStateChanged = true;
        try
        {
            presetNameBox.Text = preset.Name;
        }
        finally
        {
            suppressStateChanged = false;
        }

        ApplyDashboardState(new LeptaDashboardDefinition
        {
            Id = currentDashboardId,
            Name = currentDashboardName,
            SelectedServerId = (serverCombo.SelectedItem as VllmServerConfiguration)?.Id,
            GeneralInstruction = preset.GeneralInstruction,
            Panels = preset.Panels
        });
        SelectPreset(preset.Id);
        statusText.Text = $"Preset loaded: {preset.Name}";
        logger.Log(nameof(LeptaController), $"Loaded preset '{preset.Name}'. panelCount={panels.Count}.");
        PublishAction($"Preset loaded: {preset.Name}");
    }

    public void DeleteSelectedPreset()
    {
        if (presetListCombo.SelectedItem is not LeptaPresetReference selectedPreset)
        {
            statusText.Text = "Select a saved preset to delete.";
            return;
        }

        if (MessageBox.Show(
                $"Delete preset '{selectedPreset.Name}'?",
                "Delete preset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        presetStore.Delete(selectedPreset.Id);
        ReloadPresetEntries();
        statusText.Text = $"Preset deleted: {selectedPreset.Name}";
        logger.Log(nameof(LeptaController), $"Deleted preset '{selectedPreset.Name}'. id={selectedPreset.Id}.");
        PublishAction($"Preset deleted: {selectedPreset.Name}", ActionLogLevel.Warning);
        OnStateChanged();
    }

    public void HandleHotkeySettingChanged()
    {
        hotkeyPreviewText.Text = $"Current shortcut: {BuildHotkeyDisplayText()}";
        HotkeySettingsChanged?.Invoke();
        logger.Log(nameof(LeptaController), $"Hotkey setting changed to '{BuildHotkeyDisplayText()}'.");
        OnStateChanged();
    }

    public bool TryGetHotkey(out uint modifiers, out uint virtualKey, out string displayText)
    {
        modifiers = 0;
        virtualKey = 0;
        displayText = BuildHotkeyDisplayText();

        if (hotkeyCtrlCheckBox.IsChecked == true)
        {
            modifiers |= 0x0002;
        }

        if (hotkeyAltCheckBox.IsChecked == true)
        {
            modifiers |= 0x0001;
        }

        if (hotkeyShiftCheckBox.IsChecked == true)
        {
            modifiers |= 0x0004;
        }

        if (hotkeyWinCheckBox.IsChecked == true)
        {
            modifiers |= 0x0008;
        }

        if (hotkeyKeyCombo.SelectedItem is not string keyName || !Enum.TryParse<Key>(keyName, out var key))
        {
            return false;
        }

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    public void SetHotkeyRegistrationStatus(string message, bool isError = false)
    {
        hotkeyRegistrationStatusText.Text = message;
        hotkeyRegistrationStatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            isError ? ThemeResourceKeys.ErrorBrush : ThemeResourceKeys.SecondaryTextBrush);
    }

    private async Task RunInternalAsync(string? clipboardText, CancellationToken cancellationToken)
    {
        var server = serverCombo.SelectedItem as VllmServerConfiguration;
        if (server is null)
        {
            statusText.Text = "Select a target server before running Lepta.";
            logger.Log(nameof(LeptaController), "LEPTA run rejected because no server is selected.");
            return;
        }

        if (!server.UseExistingHttpServer)
        {
            statusText.Text = "Lepta requests are available only for 'Already deployed HTTP server' profiles.";
            logger.Log(nameof(LeptaController), $"LEPTA run rejected because '{server.Name}' is not configured as an external HTTP server.");
            return;
        }

        logger.Log(nameof(LeptaController), $"LEPTA run starting for server '{server.Name}'. panelCount={panels.Count}, clipboardLength={clipboardText?.Length ?? 0}.");
        PublishAction($"Starting LEPTA run for {panels.Count} panel(s) on '{server.Name}'.");

        foreach (var panel in panels)
        {
            panel.Response = string.Empty;
            panel.Status = "Waiting...";
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        currentRunCts = linkedCts;
        SetBusyState(true, $"Checking {server.Endpoint}...");

        try
        {
            var probe = await deploymentService.ProbeHttpServerAsync(server, linkedCts.Token);
            if (!probe.IsSuccess || string.IsNullOrWhiteSpace(probe.FirstModelName))
            {
                statusText.Text = probe.Message;
                logger.Log(nameof(LeptaController), $"LEPTA run aborted because server '{server.Name}' failed validation or probing. reason={probe.Message}");
                return;
            }

            var model = probe.FirstModelName;
            var requests = panels
                .Select(panel => new LeptaPanelRequest(panel.Name.Trim(), panel.CustomInstruction))
                .ToArray();

            for (var i = 0; i < panels.Count; i++)
            {
                panels[i].Status = $"Generating with {model}...";
            }

            var results = await requestOrchestrator.GenerateForPanelsAsync(
                server.Endpoint,
                model,
                clipboardText,
                generalInstructionBox.Text,
                requests,
                (index, token) =>
                {
                    panelsItemsControl.Dispatcher.Invoke(() =>
                    {
                        if (index >= 0 && index < panels.Count)
                        {
                            panels[index].Response += token;
                        }
                    });
                },
                linkedCts.Token);

            for (var i = 0; i < results.Count && i < panels.Count; i++)
            {
                var result = results[i];
                var panel = panels[i];

                panel.Status = string.IsNullOrWhiteSpace(result.Error)
                    ? "Completed"
                    : $"Error: {result.Error}";

                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    panel.Response = result.Error!;
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

            statusText.Text = $"Generated responses for {results.Count} panel(s) using {model}.";
            logger.Log(nameof(LeptaController), $"LEPTA run completed for server '{server.Name}'. model='{model}', panelCount={results.Count}.");
            PublishAction($"LEPTA run completed on '{server.Name}' with model '{model}'.");
        }
        catch (OperationCanceledException)
        {
            statusText.Text = "Lepta generation was cancelled.";
            logger.Log(nameof(LeptaController), $"LEPTA run cancelled for server '{server.Name}'.");
            PublishAction("LEPTA generation was cancelled.", ActionLogLevel.Warning);
        }
        catch (Exception exception)
        {
            statusText.Text = exception.Message;
            logger.Log(nameof(LeptaController), $"LEPTA run failed for server '{server.Name}'. reason={exception.Message}");
            PublishAction($"LEPTA run failed for '{server.Name}': {exception.Message}", ActionLogLevel.Error);
        }
        finally
        {
            if (ReferenceEquals(currentRunCts, linkedCts))
            {
                currentRunCts = null;
            }

            SetBusyState(false, statusText.Text);
        }
    }

    private StoredLeptaPreset BuildStoredPreset(string presetId, string presetName) => new()
    {
        Id = presetId,
        Name = presetName,
        GeneralInstruction = generalInstructionBox.Text.Trim(),
        Panels = panels
            .Select(panel => new LeptaPanelDefinition
            {
                Name = panel.Name,
                CustomInstruction = panel.CustomInstruction
            })
            .ToList()
    };

    private bool TryLoadSelectedPreset(out StoredLeptaPreset preset)
    {
        if (presetListCombo.SelectedItem is not LeptaPresetReference selectedPreset)
        {
            statusText.Text = "Select a saved preset to load.";
            preset = null!;
            return false;
        }

        var result = presetStore.LoadAll();
        foreach (var warning in result.Warnings)
        {
            logger.Log(nameof(LeptaController), warning);
        }

        preset = result.Value.FirstOrDefault(item => string.Equals(item.Id, selectedPreset.Id, StringComparison.OrdinalIgnoreCase))!;
        if (preset is null)
        {
            statusText.Text = $"Preset '{selectedPreset.Name}' is no longer available.";
            ReloadPresetEntries();
            return false;
        }

        return true;
    }

    private IReadOnlyList<string> ReloadPresetEntries(string? selectedPresetId = null)
    {
        var selectedId = selectedPresetId ?? (presetListCombo.SelectedItem as LeptaPresetReference)?.Id;
        var result = presetStore.LoadAll();
        suppressStateChanged = true;
        try
        {
            presetEntries.Clear();
            foreach (var preset in result.Value.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                presetEntries.Add(new LeptaPresetReference
                {
                    Id = preset.Id,
                    Name = preset.Name
                });
            }

            SelectPreset(selectedId);
        }
        finally
        {
            suppressStateChanged = false;
        }

        foreach (var warning in result.Warnings)
        {
            logger.Log(nameof(LeptaController), warning);
        }

        return result.Warnings;
    }

    private void SelectPreset(string? presetId)
    {
        presetListCombo.SelectedItem = string.IsNullOrWhiteSpace(presetId)
            ? null
            : presetEntries.FirstOrDefault(item => string.Equals(item.Id, presetId, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectDashboard(string? dashboardId)
    {
        dashboardListCombo.SelectedItem = string.IsNullOrWhiteSpace(dashboardId)
            ? null
            : dashboardEntries.FirstOrDefault(item => string.Equals(item.Id, dashboardId, StringComparison.OrdinalIgnoreCase));
    }

    private LeptaDashboardDefinition? ResolveDashboard(string? dashboardId)
        => string.IsNullOrWhiteSpace(dashboardId)
            ? dashboards.FirstOrDefault()
            : dashboards.FirstOrDefault(item => string.Equals(item.Id, dashboardId, StringComparison.OrdinalIgnoreCase))
              ?? dashboards.FirstOrDefault();

    private void SyncCurrentDashboardIntoCollection()
    {
        var snapshot = CloneDashboard(CaptureDashboardState());
        currentDashboardId = snapshot.Id;
        currentDashboardName = snapshot.Name;

        var existingIndex = dashboards.FindIndex(item => string.Equals(item.Id, snapshot.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            dashboards[existingIndex] = snapshot;
        }
        else
        {
            dashboards.Add(snapshot);
            dashboardEntries.Add(new LeptaDashboardReference
            {
                Id = snapshot.Id,
                Name = snapshot.Name
            });
        }

        UpdateCurrentDashboardReferenceName();
    }

    private void UpdateCurrentDashboardReferenceName()
    {
        var reference = dashboardEntries.FirstOrDefault(item => string.Equals(item.Id, currentDashboardId, StringComparison.OrdinalIgnoreCase));
        if (reference is not null)
        {
            reference.Name = NormalizeDashboardName(dashboardNameBox.Text);
        }

        currentDashboardName = NormalizeDashboardName(dashboardNameBox.Text);
    }

    private void ReplacePanels(IEnumerable<LeptaPanelDefinition> definitions)
    {
        foreach (var panel in panels)
        {
            DetachPanel(panel);
        }

        panels.Clear();
        foreach (var definition in definitions.Where(definition => definition is not null))
        {
            panels.Add(CreatePanelState(
                string.IsNullOrWhiteSpace(definition.Name) ? $"Panel {panels.Count + 1}" : definition.Name.Trim(),
                definition.CustomInstruction ?? string.Empty));
        }

        if (panels.Count == 0)
        {
            panels.Add(CreatePanelState("Panel 1", "Answer with the perspective for this panel."));
        }
    }

    private LeptaPanelState CreatePanelState(string name, string customInstruction)
    {
        var panel = new LeptaPanelState
        {
            Name = name,
            CustomInstruction = customInstruction
        };

        panel.PropertyChanged += HandlePanelPropertyChanged;
        return panel;
    }

    private void DetachPanel(LeptaPanelState panel)
        => panel.PropertyChanged -= HandlePanelPropertyChanged;

    private void HandlePanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LeptaPanelState.Name) or nameof(LeptaPanelState.CustomInstruction))
        {
            OnStateChanged();
        }
    }

    private void SetBusyState(bool busy, string statusMessage)
    {
        isBusy = busy;
        runButton.IsEnabled = !busy && serverCombo.SelectedItem is VllmServerConfiguration server && server.UseExistingHttpServer;
        progressBar.IsIndeterminate = busy;
        statusText.Text = statusMessage;
    }

    private void MovePanel(Guid panelId, int offset)
    {
        var currentIndex = panels
            .Select((panel, index) => new { panel, index })
            .FirstOrDefault(item => item.panel.Id == panelId)
            ?.index ?? -1;

        if (currentIndex < 0)
        {
            return;
        }

        var targetIndex = currentIndex + offset;
        if (targetIndex < 0 || targetIndex >= panels.Count)
        {
            return;
        }

        panels.Move(currentIndex, targetIndex);
        logger.Log(nameof(LeptaController), $"Moved panel from index {currentIndex} to {targetIndex}.");
        OnStateChanged();
    }

    private void SetServerSelection(string? serverId)
    {
        pendingServerId = serverId;
        if (serverCombo.ItemsSource is not IEnumerable<VllmServerConfiguration> availableServers)
        {
            return;
        }

        var server = string.IsNullOrWhiteSpace(serverId)
            ? availableServers.FirstOrDefault()
            : availableServers.FirstOrDefault(item => string.Equals(item.Id, serverId, StringComparison.OrdinalIgnoreCase))
              ?? availableServers.FirstOrDefault();

        serverCombo.SelectedItem = server;
        pendingServerId = server?.Id;
    }

    private void SeedHotkeyKeys()
    {
        var keys = new List<string>();
        for (var i = Key.A; i <= Key.Z; i++)
        {
            keys.Add(i.ToString());
        }

        for (var i = 1; i <= 12; i++)
        {
            keys.Add($"F{i}");
        }

        hotkeyKeyCombo.ItemsSource = keys;
    }

    private string NormalizePresetName(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Preset" : value.Trim();

    private string NormalizeDashboardName(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Dashboard" : value.Trim();

    private string EnsureUniquePresetName(string baseName)
    {
        var candidate = baseName;
        var suffix = 2;
        while (presetEntries.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} ({suffix++})";
        }

        return candidate;
    }

    private string EnsureUniqueDashboardName(string baseName)
    {
        var candidate = baseName;
        var suffix = 2;
        while (dashboardEntries.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} ({suffix++})";
        }

        return candidate;
    }

    private static LeptaDashboardDefinition CloneDashboard(LeptaDashboardDefinition dashboard) => new()
    {
        SchemaVersion = LeptaDashboardDefinition.CurrentSchemaVersion,
        Id = string.IsNullOrWhiteSpace(dashboard.Id) ? Guid.NewGuid().ToString("N") : dashboard.Id.Trim(),
        Name = string.IsNullOrWhiteSpace(dashboard.Name) ? "Dashboard" : dashboard.Name.Trim(),
        SelectedServerId = string.IsNullOrWhiteSpace(dashboard.SelectedServerId) ? null : dashboard.SelectedServerId.Trim(),
        GeneralInstruction = dashboard.GeneralInstruction ?? string.Empty,
        Panels = dashboard.Panels
            .Where(panel => panel is not null)
            .Select(panel => new LeptaPanelDefinition
            {
                Name = string.IsNullOrWhiteSpace(panel.Name) ? "Panel" : panel.Name.Trim(),
                CustomInstruction = panel.CustomInstruction ?? string.Empty
            })
            .ToList()
    };

    private string BuildHotkeyDisplayText()
    {
        var parts = new List<string>();
        if (hotkeyCtrlCheckBox.IsChecked == true)
        {
            parts.Add("Ctrl");
        }

        if (hotkeyAltCheckBox.IsChecked == true)
        {
            parts.Add("Alt");
        }

        if (hotkeyShiftCheckBox.IsChecked == true)
        {
            parts.Add("Shift");
        }

        if (hotkeyWinCheckBox.IsChecked == true)
        {
            parts.Add("Win");
        }

        parts.Add(hotkeyKeyCombo.SelectedItem as string ?? "(key)");
        return string.Join("+", parts);
    }

    private void PublishAction(string message, ActionLogLevel level = ActionLogLevel.Info)
        => actionLog.Publish(nameof(LeptaController), message, level);

    private void OnStateChanged()
    {
        if (!suppressStateChanged)
        {
            StateChanged?.Invoke();
        }
    }
}
