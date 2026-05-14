using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LEPTA.Controllers;
using LEPTA.Shared.Diagnostics;
using LEPTA.Shared.Models;
using LEPTA.Shared.Services;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA;

public partial class MainWindow : Window
{
    private readonly ILeptaLogger logger;
    private readonly VllmDeploymentService deploymentService;
    private readonly VllmChatCompletionClient chatCompletionClient;
    private readonly VllmConversationService conversationService;
    private readonly AppDataPaths appDataPaths;
    private readonly AppSettingsStore appSettingsStore;
    private readonly LeptaDashboardStore dashboardStore;
    private readonly LeptaPresetStore presetStore;
    private readonly VllmServerConfigurationStore serverConfigurationStore;
    private readonly ActionLogEventStream actionLogStream;
    private readonly DispatcherTimer persistenceTimer;
    private readonly DispatcherTimer actionLogOverlayTimer;
    private readonly ObservableCollection<ActionLogEntry> actionLogOverlayEntries = [];
    private ModelsController? modelsController;
    private ChatController? chatController;
    private LeptaController? leptaController;
    private ThemeController? themeController;
    private HwndSource? hwndSource;
    private bool isShutdownConfirmed;
    private bool isStoppingOnClose;
    private bool isNavigationCollapsed;
    private bool suppressPersistenceQueue;
    private bool suppressSettingsChangeHandlers;
    private const int GlobalHotkeyId = 0x4C45;
    private const int WmHotKey = 0x0312;
    private static readonly TimeSpan ActionLogOverlayLifetime = TimeSpan.FromSeconds(12);
    private const int MaxOverlayEntries = 5;
    private readonly string composeDirectory;
    private List<string> startupWarnings = [];

    public MainWindow()
    {
        appDataPaths = new AppDataPaths();
        appDataPaths.EnsureCreated();
        appSettingsStore = new AppSettingsStore(appDataPaths);
        dashboardStore = new LeptaDashboardStore(appDataPaths);
        presetStore = new LeptaPresetStore(appDataPaths);
        serverConfigurationStore = new VllmServerConfigurationStore(appDataPaths);
        actionLogStream = new ActionLogEventStream();
        composeDirectory = appDataPaths.VllmDirectory;
        persistenceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        persistenceTimer.Tick += (_, _) => PersistState();
        actionLogOverlayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        actionLogOverlayTimer.Tick += (_, _) => PruneActionLogOverlay();

        InitializeComponent();
        ActionLogOverlayItemsControl.ItemsSource = actionLogOverlayEntries;
        actionLogStream.EntryPublished += ActionLogStream_EntryPublished;
        logger = (Application.Current as App)?.RuntimeLogger ?? NullLeptaLogger.Instance;
        deploymentService = new VllmDeploymentService(logger: logger);
        chatCompletionClient = new VllmChatCompletionClient(logger: logger);
        conversationService = new VllmConversationService(chatCompletionClient, logger);
        logger.Log(nameof(MainWindow), "Main window initialized.");

        var settingsResult = appSettingsStore.Load();
        var modelConfigurationResult = serverConfigurationStore.Load();
        var dashboardResult = dashboardStore.LoadAll();
        var defaultServerId = string.IsNullOrWhiteSpace(settingsResult.Value.DefaultServerId)
            ? modelConfigurationResult.Value.SelectedServerId
            : settingsResult.Value.DefaultServerId;
        var activeDashboard = ResolveActiveDashboard(dashboardResult.Value, settingsResult.Value.DefaultDashboardId);
        startupWarnings = [.. settingsResult.Warnings, .. modelConfigurationResult.Warnings, .. dashboardResult.Warnings];
        suppressPersistenceQueue = true;
        modelsController = new ModelsController(
            ModelsList,
            ChatServerCombo,
            ModelNoteText,
            NameBox,
            DeploymentModeBox,
            HttpServerAddressBox,
            ModelBox,
            LocalPathBox,
            ServedModelNameBox,
            DockerImageBox,
            LocalModelMetadataText,
            PortBox,
            DTypeBox,
            GpuBox,
            MaxLenBox,
            SwapBox,
            KvCacheBox,
            ParameterCountText,
            GpuLayersBox,
            WeightQuantizationBox,
            TensorParallelBox,
            KCacheQuantizationBox,
            VCacheQuantizationBox,
            CpuOffloadBox,
            MaxNumSeqsBox,
            VerboseLogsCheckBox,
            DockerStatusIndicator,
            DockerStatusText,
            DockerStatusDetailsText,
            EstimatedVramText,
            EstimatedRamText,
            EstimateSummaryText,
            DeploymentLogBox,
            ModelProgress,
            ChatProgress,
            AdvancedConfigurationPanel,
            composeDirectory,
            deploymentService,
            logger,
            actionLogStream,
            modelConfigurationResult.Value.Servers,
            defaultServerId);
        themeController = new ThemeController();
        chatController = new ChatController(
            MessagesPanel,
            ChatInputBox,
            ChatSystemInstructionBox,
            ChatSystemInstructionHintText,
            ChatServerCombo,
            NewChatButton,
            SendButton,
            StopChatButton,
            ChatStatusText,
            MessagesScrollViewer,
            ChatProgress,
            deploymentService,
            conversationService,
            logger,
            actionLogStream);
        leptaController = new LeptaController(
            LeptaPanelsItemsControl,
            LeptaGeneralInstructionBox,
            LeptaServerCombo,
            LeptaDashboardNameBox,
            LeptaDashboardListCombo,
            LeptaPresetNameBox,
            LeptaPresetListCombo,
            LeptaStatusText,
            LeptaProgress,
            RunLeptaButton,
            HotkeyCtrlCheckBox,
            HotkeyAltCheckBox,
            HotkeyShiftCheckBox,
            HotkeyWinCheckBox,
            HotkeyKeyCombo,
            HotkeyPreviewText,
            HotkeyRegistrationStatusText,
            deploymentService,
            conversationService,
            presetStore,
            logger,
            actionLogStream);
        modelsController.StateChanged += HandleModelsStateChanged;
        chatController.StateChanged += HandleChatStateChanged;
        leptaController.HotkeySettingsChanged += HandleHotkeySettingsChanged;
        leptaController.StateChanged += HandleLeptaStateChanged;
        leptaController.BindServers(modelsController.Servers);
        leptaController.LoadDashboards(dashboardResult.Value, settingsResult.Value.DefaultDashboardId);
        leptaController.SelectServer(activeDashboard?.SelectedServerId ?? defaultServerId);
        leptaController.ApplyHotkeySettings(settingsResult.Value.Hotkey);
        chatController.ApplySettings(settingsResult.Value.Chat ?? ChatSettings.CreateDefault());
        startupWarnings.AddRange(leptaController.StartupWarnings);

        SettingsDefaultDashboardCombo.ItemsSource = LeptaDashboardListCombo.ItemsSource;
        SettingsDefaultServerCombo.ItemsSource = modelsController.Servers;

        suppressSettingsChangeHandlers = true;
        DarkThemeCheckBox.IsChecked = settingsResult.Value.IsDarkTheme;
        CollapseNavigationCheckBox.IsChecked = settingsResult.Value.IsNavigationCollapsed;
        EnableActionLogOverlayCheckBox.IsChecked = settingsResult.Value.IsActionLogOverlayEnabled;
        VerboseVllmLogsSettingsCheckBox.IsChecked = settingsResult.Value.EnableVerboseVllmLogs;
        suppressSettingsChangeHandlers = false;

        themeController.ApplyTheme(settingsResult.Value.IsDarkTheme);
        modelsController.ApplyVerboseLogsSetting(settingsResult.Value.EnableVerboseVllmLogs, publishAction: false);
        ApplyNavigationState(settingsResult.Value.IsNavigationCollapsed);
        UpdateGeneralInstructionSummary();
        chatController.HandleServerSelectionChanged();
        RefreshSettingsControls();
        UpdateActionLogOverlayVisibility();
        suppressPersistenceQueue = false;
        PersistState();
    }

    private VllmServerConfiguration? SelectedServer => modelsController?.SelectedServer;

    private void NavigationButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        LeptaView.Visibility = LeptaTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ModelsView.Visibility = ModelsTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = SettingsTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ChatView.Visibility = ChatTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyNavigationState(!isNavigationCollapsed);
        QueuePersistence();
    }

    private void CollapseNavigationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (suppressSettingsChangeHandlers)
        {
            return;
        }

        ApplyNavigationState(CollapseNavigationCheckBox.IsChecked == true);
        QueuePersistence();
    }

    private void EnableActionLogOverlayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (suppressSettingsChangeHandlers)
        {
            return;
        }

        UpdateActionLogOverlayVisibility();
        QueuePersistence();
    }

    private void VerboseVllmLogsSettingsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (suppressSettingsChangeHandlers || modelsController is null)
        {
            return;
        }

        modelsController.ApplyVerboseLogsSetting(VerboseVllmLogsSettingsCheckBox.IsChecked == true);
    }

    private void SettingsDefaultDashboardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSettingsChangeHandlers || leptaController is null || SettingsDefaultDashboardCombo.SelectedItem is not LeptaDashboardReference dashboard)
        {
            return;
        }

        leptaController.SelectDashboardById(dashboard.Id);
    }

    private void SettingsDefaultServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSettingsChangeHandlers || modelsController is null || SettingsDefaultServerCombo.SelectedItem is not VllmServerConfiguration server)
        {
            return;
        }

        modelsController.SelectServer(server.Id);
        leptaController?.SelectServer(server.Id);
    }

    private void OpenSettingsFromHeaderButton_Click(object sender, RoutedEventArgs e)
        => SettingsTabButton.IsChecked = true;

    private void OpenGeneralInstructionButton_Click(object sender, RoutedEventArgs e)
    {
        GeneralInstructionPanel.Visibility = Visibility.Visible;
        LeptaGeneralInstructionBox.Focus();
        LeptaGeneralInstructionBox.Select(LeptaGeneralInstructionBox.Text.Length, 0);
    }

    private void CloseGeneralInstructionButton_Click(object sender, RoutedEventArgs e)
        => GeneralInstructionPanel.Visibility = Visibility.Collapsed;

    private void LeptaGeneralInstructionBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateGeneralInstructionSummary();

    private void ModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => modelsController?.HandleModelsSelectionChanged();

    private void ChatServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        modelsController?.HandleChatServerSelectionChanged();
        chatController?.HandleServerSelectionChanged();
    }

    private void LeptaServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => leptaController?.HandleServerSelectionChanged();

    private void LeptaDashboardListCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => leptaController?.HandleDashboardSelectionChanged();

    private void ConfigurationBox_TextChanged(object sender, TextChangedEventArgs e)
        => modelsController?.HandleConfigurationChanged();

    private void ConfigurationSelectionChanged(object sender, SelectionChangedEventArgs e)
        => modelsController?.HandleConfigurationChanged();

    private void AddModelButton_Click(object sender, RoutedEventArgs e) => modelsController?.AddModel();

    private async void BrowseModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (modelsController is null)
        {
            return;
        }

        await modelsController.BrowseModelAsync(this);
    }

    private async void ScanModelMetadataButton_Click(object sender, RoutedEventArgs e)
    {
        if (modelsController is null)
        {
            return;
        }

        await modelsController.ScanSelectedModelAsync();
    }

    private async void StartServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (modelsController is null)
        {
            return;
        }

        await modelsController.StartSelectedServerAsync();
    }

    private async void StopServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (modelsController is null)
        {
            return;
        }

        await modelsController.StopSelectedServerAsync();
    }

    private async void RestartServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (modelsController is null)
        {
            return;
        }

        await modelsController.RestartSelectedServerAsync();
    }

    private async void TestServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (modelsController is null)
        {
            return;
        }

        await modelsController.TestSelectedServerAsync();
    }

    private void ThemeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        themeController?.ApplyTheme(DarkThemeCheckBox.IsChecked == true);
        UpdateActionLogOverlayVisibility();
        QueuePersistence();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (modelsController is null)
        {
            return;
        }

        await modelsController.RefreshDockerStatusAsync();
        await modelsController.TestSelectedServerAsync();
        RegisterConfiguredHotkey();
        if (startupWarnings.Count > 0)
        {
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine + Environment.NewLine, startupWarnings),
                "LEPTA restored app data with warnings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void VerboseLogsCheckBox_Changed(object sender, RoutedEventArgs e) => modelsController?.HandleVerboseLogsChanged();

    private async void RefreshDockerStatusButton_Click(object sender, RoutedEventArgs e)
    {
        if (modelsController is null)
        {
            return;
        }

        if (sender is ButtonBase button)
        {
            button.IsEnabled = false;
            try
            {
                await modelsController.RefreshDockerStatusAsync();
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }

    private void OpenAdvancedConfigurationButton_Click(object sender, RoutedEventArgs e)
        => modelsController?.OpenAdvancedConfiguration();

    private void CloseAdvancedConfigurationButton_Click(object sender, RoutedEventArgs e)
        => modelsController?.CloseAdvancedConfiguration();

    private void NewChatButton_Click(object sender, RoutedEventArgs e) => chatController?.StartNewChat();

    private void StopChatButton_Click(object sender, RoutedEventArgs e) => chatController?.CancelCurrentMessage();

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (chatController is null)
        {
            return;
        }

        await chatController.SendCurrentMessageAsync();
    }

    private async void ChatInputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        e.Handled = true;
        if (chatController is null)
        {
            return;
        }

        await chatController.SendCurrentMessageAsync();
    }

    private void AddLeptaPanelButton_Click(object sender, RoutedEventArgs e) => leptaController?.AddPanel();

    private void MoveLeptaPanelLeftButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is Guid panelId)
        {
            leptaController?.MovePanelLeft(panelId);
        }
    }

    private void MoveLeptaPanelRightButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is Guid panelId)
        {
            leptaController?.MovePanelRight(panelId);
        }
    }

    private void DeleteLeptaPanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is Guid panelId)
        {
            leptaController?.RemovePanel(panelId);
        }
    }

    private async void RunLeptaButton_Click(object sender, RoutedEventArgs e)
    {
        if (leptaController is null)
        {
            return;
        }

        await leptaController.RunFromClipboardAsync();
    }

    private void SaveDashboardButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            leptaController?.SaveDashboard();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Dashboard save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveDashboardAsNewButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            leptaController?.SaveDashboardAsNew();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Dashboard save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteDashboardButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            leptaController?.DeleteSelectedDashboard();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Dashboard delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveLeptaPresetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            leptaController?.SavePreset();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Preset save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadLeptaPresetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            leptaController?.LoadPreset();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Preset load failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveLeptaPresetAsNewButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            leptaController?.SavePresetAsNew();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Preset save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteLeptaPresetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            leptaController?.DeleteSelectedPreset();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Preset delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void HotkeySetting_Changed(object sender, RoutedEventArgs e) => leptaController?.HandleHotkeySettingChanged();

    private void HotkeyKeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => leptaController?.HandleHotkeySettingChanged();

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (isShutdownConfirmed || isStoppingOnClose)
        {
            return;
        }

        e.Cancel = true;
        PersistState(showErrors: true);

        var selectedServer = SelectedServer;
        var shouldStopServer = selectedServer?.SupportsLifecycleManagement == true
            && MessageBox.Show(
                "Do you want to stop the LLM server before closing LEPTA?",
                "Stop LLM server?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;

        if (shouldStopServer && selectedServer is not null && modelsController is not null)
        {
            isStoppingOnClose = true;
            try
            {
                await modelsController.StopForShutdownAsync(selectedServer);
            }
            finally
            {
                isStoppingOnClose = false;
            }
        }

        isShutdownConfirmed = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        persistenceTimer.Stop();
        actionLogOverlayTimer.Stop();
        actionLogStream.EntryPublished -= ActionLogStream_EntryPublished;
        if (hwndSource is not null)
        {
            UnregisterHotKey(hwndSource.Handle, GlobalHotkeyId);
            hwndSource.RemoveHook(WndProc);
            hwndSource = null;
        }

        base.OnClosed(e);
    }

    private void HandleHotkeySettingsChanged() => RegisterConfiguredHotkey();

    private void HandleModelsStateChanged()
    {
        RefreshSettingsControls();
        QueuePersistence();
    }

    private void HandleLeptaStateChanged()
    {
        RefreshSettingsControls();
        QueuePersistence();
    }

    private void HandleChatStateChanged() => QueuePersistence();

    private void QueuePersistence()
    {
        if (suppressPersistenceQueue)
        {
            return;
        }

        persistenceTimer.Stop();
        persistenceTimer.Start();
    }

    private void PersistState(bool showErrors = false)
    {
        persistenceTimer.Stop();
        if (themeController is null || modelsController is null || leptaController is null || chatController is null)
        {
            return;
        }

        try
        {
            appDataPaths.EnsureCreated();
            appSettingsStore.Save(new AppSettings
            {
                IsDarkTheme = themeController.IsDarkTheme,
                IsNavigationCollapsed = isNavigationCollapsed,
                IsActionLogOverlayEnabled = EnableActionLogOverlayCheckBox.IsChecked == true,
                EnableVerboseVllmLogs = modelsController.IsVerboseVllmLogsEnabled,
                DefaultDashboardId = leptaController.CurrentDashboardId,
                DefaultServerId = modelsController.SelectedServerId,
                Hotkey = leptaController.GetHotkeySettings(),
                Chat = chatController.CaptureSettings()
            });
            dashboardStore.SaveAll(leptaController.CaptureDashboards());
            serverConfigurationStore.Save(new VllmServerConfigurationsDocument
            {
                SelectedServerId = modelsController.SelectedServerId,
                Servers = modelsController.Servers.Select(CloneServer).ToList()
            });
        }
        catch (Exception exception)
        {
            logger.Log(nameof(MainWindow), $"Failed to persist app data. reason={exception.Message}");
            if (showErrors)
            {
                MessageBox.Show(this, exception.Message, "LEPTA could not save app data", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private static VllmServerConfiguration CloneServer(VllmServerConfiguration server) => server with
    {
        AvailableWeightQuantizations = server.AvailableWeightQuantizations.ToArray()
    };

    private static LeptaDashboardDefinition? ResolveActiveDashboard(
        IReadOnlyList<LeptaDashboardDefinition> dashboards,
        string? activeDashboardId)
        => string.IsNullOrWhiteSpace(activeDashboardId)
            ? dashboards.FirstOrDefault()
            : dashboards.FirstOrDefault(dashboard => string.Equals(dashboard.Id, activeDashboardId, StringComparison.OrdinalIgnoreCase))
              ?? dashboards.FirstOrDefault();

    private void ApplyNavigationState(bool isCollapsed)
    {
        isNavigationCollapsed = isCollapsed;
        NavigationColumn.Width = new GridLength(isCollapsed ? 84 : 220);
        NavigationPanel.Padding = isCollapsed ? new Thickness(10) : new Thickness(14);
        NavigationTitleText.Visibility = isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        NavigationSubtitleText.Visibility = isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ToggleNavigationButton.Content = isCollapsed ? "»" : "«";

        SetNavigationContent(LeptaTabButton, "◫", "Lepta");
        SetNavigationContent(ModelsTabButton, "◎", "Models");
        SetNavigationContent(SettingsTabButton, "⚙", "Settings");
        SetNavigationContent(ChatTabButton, "💬", "Chat");
        if (CollapseNavigationCheckBox is not null)
        {
            suppressSettingsChangeHandlers = true;
            CollapseNavigationCheckBox.IsChecked = isCollapsed;
            suppressSettingsChangeHandlers = false;
        }
    }

    private void SetNavigationContent(RadioButton button, string symbol, string label)
    {
        button.Content = isNavigationCollapsed ? symbol : $"{symbol}  {label}";
        button.Padding = isNavigationCollapsed ? new Thickness(12) : new Thickness(16, 12, 16, 12);
        button.HorizontalContentAlignment = isNavigationCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }

    private void UpdateGeneralInstructionSummary()
    {
        if (!IsInitialized)
        {
            return;
        }

        var text = LeptaGeneralInstructionBox.Text.Trim();
        GeneralInstructionSummaryText.Text = string.IsNullOrWhiteSpace(text)
            ? "No general instruction yet. LEPTA will use clipboard text together with each panel instruction."
            : text.Length <= 240
                ? text
                : text[..237] + "...";
    }

    private void RegisterConfiguredHotkey()
    {
        EnsureWindowHook();
        if (leptaController is null)
        {
            return;
        }

        if (hwndSource is null)
        {
            leptaController.SetHotkeyRegistrationStatus("The shortcut will be registered when the LEPTA window handle is ready.");
            return;
        }

        UnregisterHotKey(hwndSource.Handle, GlobalHotkeyId);
        if (!leptaController.TryGetHotkey(out var modifiers, out var virtualKey, out var displayText))
        {
            leptaController.SetHotkeyRegistrationStatus("Select a valid global shortcut key to register.", isError: true);
            return;
        }

        if (RegisterHotKey(hwndSource.Handle, GlobalHotkeyId, modifiers, virtualKey))
        {
            leptaController.SetHotkeyRegistrationStatus($"Shortcut registered: {displayText}");
            logger.Log(nameof(MainWindow), $"Registered global hotkey '{displayText}'.");
            actionLogStream.Publish(nameof(MainWindow), $"Global shortcut registered: {displayText}");
            return;
        }

        var error = Marshal.GetLastWin32Error();
        var message = error switch
        {
            1409 => $"Shortcut conflict: {displayText} is already registered by another app or by another LEPTA instance.",
            87 => $"Shortcut '{displayText}' is not valid for Windows global hotkey registration.",
            _ => $"Failed to register shortcut '{displayText}'. Windows error code: {error}."
        };

        leptaController.SetHotkeyRegistrationStatus(message, isError: true);
        logger.Log(nameof(MainWindow), $"Failed to register global hotkey '{displayText}'. errorCode={error}.");
        actionLogStream.Publish(nameof(MainWindow), message, ActionLogLevel.Warning);
    }

    private void RefreshSettingsControls()
    {
        if (modelsController is null || leptaController is null)
        {
            return;
        }

        suppressSettingsChangeHandlers = true;
        try
        {
            CollapseNavigationCheckBox.IsChecked = isNavigationCollapsed;
            VerboseVllmLogsSettingsCheckBox.IsChecked = modelsController.IsVerboseVllmLogsEnabled;
            SettingsDefaultDashboardCombo.SelectedItem = SettingsDefaultDashboardCombo.Items
                .OfType<LeptaDashboardReference>()
                .FirstOrDefault(item => string.Equals(item.Id, leptaController.CurrentDashboardId, StringComparison.OrdinalIgnoreCase));
            SettingsDefaultServerCombo.SelectedItem = SettingsDefaultServerCombo.Items
                .OfType<VllmServerConfiguration>()
                .FirstOrDefault(item => string.Equals(item.Id, modelsController.SelectedServerId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            suppressSettingsChangeHandlers = false;
        }
    }

    private void ActionLogStream_EntryPublished(object? sender, ActionLogEntry entry)
    {
        if (Dispatcher.CheckAccess())
        {
            AddOverlayEntry(entry);
            return;
        }

        Dispatcher.Invoke(() => AddOverlayEntry(entry));
    }

    private void AddOverlayEntry(ActionLogEntry entry)
    {
        actionLogOverlayEntries.Add(entry);
        while (actionLogOverlayEntries.Count > MaxOverlayEntries)
        {
            actionLogOverlayEntries.RemoveAt(0);
        }

        PruneActionLogOverlay();
        if (actionLogOverlayEntries.Count > 0)
        {
            actionLogOverlayTimer.Start();
        }
    }

    private void PruneActionLogOverlay()
    {
        var cutoff = DateTimeOffset.UtcNow - ActionLogOverlayLifetime;
        for (var index = actionLogOverlayEntries.Count - 1; index >= 0; index--)
        {
            if (actionLogOverlayEntries[index].TimestampUtc < cutoff)
            {
                actionLogOverlayEntries.RemoveAt(index);
            }
        }

        if (actionLogOverlayEntries.Count == 0)
        {
            actionLogOverlayTimer.Stop();
        }

        UpdateActionLogOverlayVisibility();
    }

    private void UpdateActionLogOverlayVisibility()
        => ActionLogOverlayRoot.Visibility = EnableActionLogOverlayCheckBox.IsChecked == true && actionLogOverlayEntries.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void EnsureWindowHook()
    {
        if (hwndSource is not null)
        {
            return;
        }

        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source is null)
        {
            return;
        }

        hwndSource = source;
        hwndSource.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == GlobalHotkeyId)
        {
            _ = Dispatcher.InvokeAsync(HandleGlobalHotkeyAsync);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private async Task HandleGlobalHotkeyAsync()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        if (leptaController is not null)
        {
            await leptaController.RunFromClipboardAsync();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}