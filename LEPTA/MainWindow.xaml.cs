using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using LEPTA.Controllers;
using LEPTA.Controllers.Views;
using LEPTA.Controls;
using LEPTA.Models;
using LEPTA.Services;
using LEPTA.Shared.Diagnostics;
using LEPTA.Shared.Models;
using LEPTA.Shared.Services;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA;

public partial class MainWindow : Window
{
    private static readonly double[] ResponseFontSizeOptions = [4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24];
    public static readonly DependencyProperty ResponseFontSizeProperty = DependencyProperty.Register(
        nameof(ResponseFontSize),
        typeof(double),
        typeof(MainWindow),
        new PropertyMetadata(14d));

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
    private readonly DispatcherTimer leptaThroughputTimer;
    private readonly DispatcherTimer clipboardCachePrefillTimer;
    private readonly ObservableCollection<ActionLogEntry> actionLogOverlayEntries = [];
    private readonly List<LeptaThroughputSample> leptaThroughputSamples = [];
    private readonly List<Button> leptaPanelColorOptionButtons = [];
    private ModelsController? modelsController;
    private ChatController? chatController;
    private LeptaController? leptaController;
    private ThemeController? themeController;
    private HwndSource? hwndSource;
    private bool isShutdownConfirmed;
    private bool isStoppingOnClose;
    private bool isLeptaSidebarCollapsed;
    private bool suppressPersistenceQueue;
    private bool suppressSettingsChangeHandlers;
    private bool suppressLeptaPanelColorSync;
    private IInputElement? lastFocusedElementBeforeOverlay;
    private long leptaTokensSinceLastSample;
    private DateTimeOffset? leptaLastThroughputSampleUtc;
    private DateTimeOffset? leptaRunStartedUtc;
    private DateTimeOffset? leptaFirstTokenReceivedUtc;
    private DateTimeOffset? leptaFirstResponseEndedUtc;
    private DateTimeOffset? leptaRunCompletedUtc;
    private bool hasSeenLeptaFirstToken;
    private double? leptaFirstTokenDelaySeconds;
    private double? leptaFirstResponseEndedSeconds;
    private long leptaTokensAtFirstResponseEnd;
    private string leptaResolvedModelName = string.Empty;
    private double? leptaHoveredSeconds;
    private Guid? leptaDraggingPanelId;
    private Point? leptaPanelDragStartPoint;
    private FrameworkElement? leptaDraggingPanelElement;
    private UIElement? leptaActiveDragSource;
    private FrameworkElement? leptaDragPreviewElement;
    private Popup? leptaDragGhostPopup;
    private Point? leptaDragGhostPointerOffset;
    private int leptaDragPreviewDirection;
    private UIElement? leptaDraggingPanelHandle;
    private Guid? leptaPreviewPanelId;
    private int leptaPanelLayoutRows = -1;
    private int overlaySuppressionDepth;
    private readonly List<LeptaPanelDragSlot> leptaPanelDragSlots = [];
    private int leptaDraggingPanelSourceIndex = -1;
    private int leptaDraggingPanelTargetIndex = -1;
    private bool isLeptaPanelDragActive;
    private long leptaTotalObservedTokens;
    private const int GlobalHotkeyId = 0x4C45;
    private const int WmHotKey = 0x0312;
    private const int WmClipboardUpdate = 0x031D;
    private static readonly TimeSpan ActionLogOverlayLifetime = TimeSpan.FromSeconds(12);
    private const int MaxOverlayEntries = 5;
    private readonly string composeDirectory;
    private List<string> startupWarnings = [];
    private bool isClipboardListenerRegistered;
    private string? pendingClipboardCacheText;

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
        leptaThroughputTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        leptaThroughputTimer.Tick += LeptaThroughputTimer_Tick;
        clipboardCachePrefillTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        clipboardCachePrefillTimer.Tick += ClipboardCachePrefillTimer_Tick;

        InitializeComponent();
        LeptaPanelEditorFormatCombo.ItemsSource = LeptaPanelFormats.All;
        InitializeLeptaPanelColorOptions();
        ApplyLeptaSidebarState(isCollapsed: false);
        ActionLogOverlayItemsControl.ItemsSource = actionLogOverlayEntries;
        actionLogStream.EntryPublished += ActionLogStream_EntryPublished;
        ResponseFontSizeCoordinator.StepRequested += HandleResponseFontSizeStepRequested;
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
            new ModelsControllerViews
            {
                Selection = new ModelsSelectionViews
                {
                    ModelsList = ModelsList,
                    ChatServerCombo = ChatViewControl.ServerCombo,
                    ModelNoteText = ModelNoteText
                },
                Configuration = new ModelsConfigurationViews
                {
                    ConfigurationTitleText = ModelConfigurationTitleText,
                    NameBox = NameBox,
                    DeploymentModeBox = DeploymentModeBox,
                    HttpServerRow = HttpServerRow,
                    ApiKeyRow = ApiKeyRow,
                    ServedModelsRow = ServedModelsRow,
                    ServedModelsCombo = ServedModelsCombo,
                    ServedModelsHintText = ServedModelsHintText,
                    HttpServerAddressBox = HttpServerAddressBox,
                    ModelFieldLabelText = ModelFieldLabelText,
                    ModelBox = ModelBox,
                    LocalFolderRow = LocalFolderRow,
                    LocalPathBox = LocalPathBox,
                    ServedModelNameLabelText = ServedModelNameLabelText,
                    ServedModelNameRow = ServedModelNameRow,
                    ServedModelNameBox = ServedModelNameBox,
                    DockerImageBox = DockerImageBox,
                    LocalMetadataBorder = LocalMetadataBorder,
                    LocalModelMetadataText = LocalModelMetadataText,
                    PortBox = PortBox,
                    DTypeBox = DTypeBox,
                    GpuBox = GpuBox,
                    GpuVramBox = GpuVramBox,
                    MaxLenBox = MaxLenBox,
                    ReadyTimeoutBox = ReadyTimeoutBox,
                    LocalRuntimeSettingsPanel = LocalRuntimeSettingsPanel,
                    ParameterCountText = ParameterCountText,
                    WeightQuantizationBox = WeightQuantizationBox,
                    TensorParallelBox = TensorParallelBox,
                    KCacheQuantizationBox = KCacheQuantizationBox,
                    VCacheQuantizationBox = VCacheQuantizationBox,
                    TokenizersParallelismBox = TokenizersParallelismBox,
                    AdditionalVllmArgumentsBox = AdditionalVllmArgumentsBox,
                    ApiKeyBox = ApiKeyBox,
                    ApiKeyRevealBox = ApiKeyRevealBox,
                    ApiKeyRevealCheckBox = ApiKeyRevealCheckBox,
                    AuthHeaderNameBox = AuthHeaderNameBox,
                    AuthHeaderSchemeBox = AuthHeaderSchemeBox,
                    ExtraHeadersBox = ExtraHeadersBox,
                    ExtraBodyBox = ExtraBodyBox,
                    RequestOverridesErrorText = RequestOverridesErrorText,
                    OpenRouterPresetButton = OpenRouterPresetButton,
                    CpuOffloadBox = CpuOffloadBox,
                    MaxNumSeqsBox = MaxNumSeqsBox,
                    VerboseLogsCheckBox = VerboseLogsCheckBox
                },
                Deployment = new ModelsDeploymentViews
                {
                    DockerStatusIndicator = DockerStatusIndicator,
                    DockerStatusText = DockerStatusText,
                    DockerStatusDetailsText = DockerStatusDetailsText,
                    EstimatedVramText = EstimatedVramText,
                    EstimateSummaryText = EstimateSummaryText,
                    CheckServerButton = CheckServerButton,
                    OpenAdvancedConfigurationButton = OpenAdvancedConfigurationButton,
                    EstimateBorder = EstimateBorder,
                    DockerStatusBorder = DockerStatusBorder,
                    DeploymentLogBorder = DeploymentLogBorder,
                    ModelActionsBorder = ModelActionsBorder,
                    StartServerButton = StartServerButton,
                    StopServerButton = StopServerButton,
                    RestartServerButton = RestartServerButton,
                    DeploymentLogBox = DeploymentLogBox,
                    ModelProgress = ModelProgress,
                    ChatProgress = ChatViewControl.Progress,
                    AdvancedConfigurationPanel = AdvancedConfigurationPanel
                }
            },
            composeDirectory,
            new ModelsControllerOptions
            {
                DeploymentService = deploymentService,
                Logger = logger,
                ActionLog = actionLogStream,
                InitialServers = modelConfigurationResult.Value.Servers,
                SelectedServerId = defaultServerId
            });
        themeController = new ThemeController();
        chatController = new ChatController(
            ChatViewControl.BuildViews(),
            deploymentService,
            conversationService,
            new JsonFileStore(),
            appDataPaths.ChatHistoryFilePath,
            new ChatControllerOptions
            {
                Logger = logger,
                ActionLog = actionLogStream
            });
        ChatViewControl.Controller = chatController;
        ChatViewControl.ServerCombo.SelectionChanged += ChatServerCombo_SelectionChanged;
        leptaController = new LeptaController(
            new LeptaControllerViews
            {
                Panels = new LeptaPanelsViews
                {
                    ItemsControl = LeptaPanelsItemsControl
                },
                Instructions = new LeptaInstructionsViews
                {
                    SystemInstructionBox = LeptaSystemInstructionBox,
                    GeneralInstructionBox = LeptaGeneralInstructionBox
                },
                Dashboards = new LeptaDashboardViews
                {
                    NameBox = LeptaDashboardNameBox,
                    ListCombo = LeptaDashboardListCombo
                },
                Presets = new LeptaPresetViews
                {
                    NameBox = LeptaPresetNameBox,
                    ListCombo = LeptaPresetListCombo
                },
                Run = new LeptaRunViews
                {
                    ServerCombo = LeptaServerCombo,
                    StatusText = LeptaStatusText,
                    ProgressBar = LeptaProgress,
                    RunButton = RunLeptaButton,
                    StopButton = StopLeptaButton,
                    ThinkingCheckBox = LeptaThinkingCheckBox,
                    TemperatureTextBox = LeptaTemperatureBox
                },
                Hotkeys = new LeptaHotkeyViews
                {
                    CtrlCheckBox = HotkeyCtrlCheckBox,
                    AltCheckBox = HotkeyAltCheckBox,
                    ShiftCheckBox = HotkeyShiftCheckBox,
                    WinCheckBox = HotkeyWinCheckBox,
                    KeyCombo = HotkeyKeyCombo,
                    PreviewText = HotkeyPreviewText,
                    RegistrationStatusText = HotkeyRegistrationStatusText
                }
            },
            deploymentService,
            conversationService,
            presetStore,
            new LeptaControllerOptions
            {
                Logger = logger,
                ActionLog = actionLogStream
            });
        modelsController.StateChanged += HandleModelsStateChanged;
        chatController.StateChanged += HandleChatStateChanged;
        leptaController.HotkeySettingsChanged += HandleHotkeySettingsChanged;
        leptaController.StateChanged += HandleLeptaStateChanged;
        leptaController.PanelMetadataChanged += HandleLeptaPanelMetadataChanged;
        leptaController.ThroughputReset += HandleLeptaThroughputReset;
        leptaController.ThroughputModelResolved += HandleLeptaThroughputModelResolved;
        leptaController.ThroughputTokensObserved += HandleLeptaThroughputTokensObserved;
        leptaController.ThroughputCompleted += HandleLeptaThroughputCompleted;
        leptaController.ThroughputFirstPanelCompleted += HandleLeptaThroughputFirstPanelCompleted;
        leptaController.BindServers(modelsController.ConnectedServers);
        _ = MermaidRenderService.Shared.WarmupAsync();
leptaController.LoadDashboards(dashboardResult.Value, settingsResult.Value.DefaultDashboardId);
        leptaController.SelectServer(activeDashboard?.SelectedServerId ?? defaultServerId);
        leptaController.ApplyHotkeySettings(settingsResult.Value.Hotkey);
        HotkeyKeyCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(HotkeyKeyCombo_TextChanged));
        chatController.ApplySettings(settingsResult.Value.Chat ?? ChatSettings.CreateDefault());
        leptaController.ApplySettings(settingsResult.Value.Lepta ?? LeptaSettings.CreateDefault());
        startupWarnings.AddRange(leptaController.StartupWarnings);

        SettingsDefaultDashboardCombo.ItemsSource = LeptaDashboardListCombo.ItemsSource;
        SettingsDefaultServerCombo.ItemsSource = modelsController.Servers;

        suppressSettingsChangeHandlers = true;
        DarkThemeCheckBox.IsChecked = settingsResult.Value.IsDarkTheme;
        CollapseNavigationCheckBox.IsChecked = true;
        EnableActionLogOverlayCheckBox.IsChecked = settingsResult.Value.IsActionLogOverlayEnabled;
        EnableClipboardCachePrefillCheckBox.IsChecked = settingsResult.Value.EnableClipboardCachePrefill;
        VerboseVllmLogsSettingsCheckBox.IsChecked = settingsResult.Value.EnableVerboseVllmLogs;
        LeptaSystemInstructionBox.Text = settingsResult.Value.LeptaSystemInstructions;
        SelectLeptaDocumentTokenLimit((settingsResult.Value.Lepta ?? LeptaSettings.CreateDefault()).DocumentTokenLimit);
        SelectLeptaDocumentTrimMode((settingsResult.Value.Lepta ?? LeptaSettings.CreateDefault()).DocumentTrimMode);
        SelectUiFontSize(settingsResult.Value.UiFontSize);
        SelectResponseFontSize(settingsResult.Value.ResponseFontSize);
        suppressSettingsChangeHandlers = false;

        themeController.ApplyTheme(settingsResult.Value.IsDarkTheme);
        modelsController.ApplyVerboseLogsSetting(settingsResult.Value.EnableVerboseVllmLogs, publishAction: false);
        ApplyNavigationState(true);
        ApplyUiFontSize(settingsResult.Value.UiFontSize);
        ApplyResponseFontSize(settingsResult.Value.ResponseFontSize);
        UpdateGeneralInstructionSummary();
        UpdateLeptaHotkeyText();
        UpdateLeptaDocumentTrimSummary();
        ResetLeptaThroughputGraph();
        UpdateLeptaPanelsLayout();
        chatController.HandleServerSelectionChanged();
        RefreshSettingsControls();
        UpdateActionLogOverlayVisibility();
        suppressPersistenceQueue = false;
        PersistState();
        QueueClipboardCachePrefillFromCurrentClipboard();
    }

    private VllmServerConfiguration? SelectedServer => modelsController?.SelectedServer;

    public double ResponseFontSize
    {
        get => (double)GetValue(ResponseFontSizeProperty);
        set => SetValue(ResponseFontSizeProperty, value);
    }

    private void NavigationButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        if (LeptaTabButton.IsChecked != true)
        {
            HideLeptaPanelPreview();
        }

        LeptaView.Visibility = LeptaTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ModelsView.Visibility = ModelsTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = SettingsTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ChatViewControl.Visibility = ChatTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyNavigationState(true);
    }

    private void CollapseNavigationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (suppressSettingsChangeHandlers)
        {
            return;
        }

        ApplyNavigationState(true);
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

    private void EnableClipboardCachePrefillCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (suppressSettingsChangeHandlers)
        {
            return;
        }

        if (EnableClipboardCachePrefillCheckBox.IsChecked == true)
        {
            QueueClipboardCachePrefillFromCurrentClipboard();
        }
        else
        {
            pendingClipboardCacheText = null;
            clipboardCachePrefillTimer.Stop();
        }

        QueuePersistence();
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

    private void UiFontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSettingsChangeHandlers)
        {
            return;
        }

        ApplyUiFontSize(GetSelectedUiFontSize());
        QueuePersistence();
    }

    private void ResponseFontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSettingsChangeHandlers)
        {
            return;
        }

        ApplyResponseFontSize(GetSelectedResponseFontSize());
        QueuePersistence();
    }

    private void OpenSettingsFromHeaderButton_Click(object sender, RoutedEventArgs e)
        => SettingsTabButton.IsChecked = true;

    private void OpenGeneralInstructionButton_Click(object sender, RoutedEventArgs e)
        => OpenOverlay(GeneralInstructionPanel, LeptaGeneralInstructionBox);

    private void CloseGeneralInstructionButton_Click(object sender, RoutedEventArgs e)
        => CloseOverlay(GeneralInstructionPanel);

    private void LeptaGeneralInstructionBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateGeneralInstructionSummary();

    private void LeptaSystemInstructionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressSettingsChangeHandlers)
        {
            return;
        }

        QueuePersistence();
    }

    private void LeptaDocumentTrimModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSettingsChangeHandlers)
        {
            return;
        }

        ApplyLeptaDocumentTrimModeSetting();
        QueuePersistence();
    }

    private void LeptaDocumentTokenLimitBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressSettingsChangeHandlers)
        {
            return;
        }

        if (ApplyLeptaDocumentTokenLimitSetting())
        {
            QueuePersistence();
        }
    }

    private void ModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (modelsController is null)
        {
            return;
        }

        modelsController.HandleModelsSelectionChanged();
    }

    private void ModelsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (modelsController is null)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject d
            && FindAncestor<ListBoxItem>(d) is { } item
            && item.Content is VllmServerConfiguration server)
        {
            modelsController.SelectServer(server.Id, forceReload: true);
        }
    }

    private async void ChatServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (modelsController is not null)
        {
            modelsController.HandleChatServerSelectionChanged();
        }

        chatController?.HandleServerSelectionChanged();
        await (modelsController?.RefreshSelectedServerStatusAsync() ?? Task.CompletedTask);
    }

    private void LeptaServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LeptaServerCombo.SelectedItem is VllmServerConfiguration server)
        {
            modelsController?.SelectServer(server.Id);
            chatController?.HandleServerSelectionChanged();
        }

        leptaController?.HandleServerSelectionChanged();
        QueueClipboardCachePrefillFromCurrentClipboard();
    }

    private void LeptaDashboardListCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        leptaController?.HandleDashboardSelectionChanged();
        QueueClipboardCachePrefillFromCurrentClipboard();
    }

    private void LeptaPresetListCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        leptaController?.HandlePresetSelectionChanged();
        QueueClipboardCachePrefillFromCurrentClipboard();
    }

    private void ConfigurationBox_TextChanged(object sender, TextChangedEventArgs e)
        => modelsController?.HandleConfigurationChanged();

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        => modelsController?.HandleConfigurationChanged();

    private void ApiKeyRevealBox_TextChanged(object sender, TextChangedEventArgs e)
        => modelsController?.HandleRevealApiKeyChanged();

    private void ApiKeyRevealCheckBox_Changed(object sender, RoutedEventArgs e)
        => modelsController?.HandleApiKeyRevealChanged();

    private void ServedModelsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => modelsController?.HandleServedModelSelected();

    private void DecimalBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == "," && sender is TextBox textBox)
        {
            var caretIndex = textBox.CaretIndex;
            var selectionLength = textBox.SelectionLength;
            textBox.Text = textBox.Text.Remove(caretIndex, selectionLength).Insert(caretIndex, ".");
            textBox.CaretIndex = caretIndex + 1;
            e.Handled = true;
        }
    }

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

    private async void CheckServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (modelsController is null)
        {
            return;
        }

        await modelsController.TestSelectedServerAsync();
    }

    private void OpenRouterPresetButton_Click(object sender, RoutedEventArgs e)
        => modelsController?.ApplyOpenRouterPreset();

    private void SaveModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (modelsController is null)
        {
            return;
        }

        modelsController.HandleConfigurationChanged();
        PersistState(showErrors: true);
        if (modelsController.SelectedServer is { } server)
        {
            UserNotificationService.ShowInfo(
                "Configuration saved",
                $"Model profile '{server.Name}' has been saved.",
                this,
                logger,
                actionLogStream,
                nameof(MainWindow));
        }
    }

    private void DeleteModelButton_Click(object sender, RoutedEventArgs e)
    {
        var serverName = modelsController?.SelectedServer?.Name;
        modelsController?.DeleteSelectedModel();
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            UserNotificationService.ShowWarning(
                "Model profile deleted",
                $"Model profile '{serverName}' has been deleted.",
                this,
                logger,
                actionLogStream,
                nameof(MainWindow));
        }
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
        await modelsController.RefreshAllServerStatusesAsync();
        RegisterConfiguredHotkey();
        if (startupWarnings.Count > 0)
        {
            UserNotificationService.ShowWarning(
                "LEPTA restored app data with warnings",
                string.Join(Environment.NewLine + Environment.NewLine, startupWarnings),
                this,
                logger,
                actionLogStream,
                nameof(MainWindow));
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
    {
        modelsController?.OpenAdvancedConfiguration();
        OpenOverlay(AdvancedConfigurationPanel, DockerImageBox);
    }

    private void CloseAdvancedConfigurationButton_Click(object sender, RoutedEventArgs e)
        => CloseOverlay(AdvancedConfigurationPanel);

    private void AddLeptaPanelButton_Click(object sender, RoutedEventArgs e) => leptaController?.AddPanel();

    private void ToggleLeptaSidebarButton_Click(object sender, RoutedEventArgs e)
        => ApplyLeptaSidebarState(!isLeptaSidebarCollapsed);

    private void OpenLeptaPanelEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (leptaController is null || sender is not Button { Tag: Guid panelId })
        {
            return;
        }

        HideLeptaPanelPreview();

        if (!leptaController.TryOpenPanelEditor(panelId, out var panelName, out var customInstruction, out var accentColorHex, out var format))
        {
            return;
        }

        LeptaPanelEditorTitleText.Text = string.IsNullOrWhiteSpace(panelName) ? "Edit panel" : $"Edit panel {panelName}";
        LeptaPanelEditorNameBox.Text = panelName;
        LeptaPanelEditorInstructionBox.Text = customInstruction;
        LeptaPanelEditorFormatCombo.SelectedItem = LeptaPanelFormats.Normalize(format);
        SetLeptaPanelColorEditor(accentColorHex);
        OpenOverlay(LeptaPanelEditorPanel, LeptaPanelEditorNameBox);
    }

    private void CloseLeptaPanelEditorButton_Click(object sender, RoutedEventArgs e)
        => CloseOverlay(LeptaPanelEditorPanel);

    private void DeleteLeptaPanelFromEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (leptaController is null)
        {
            return;
        }

        leptaController.DeleteEditingPanel();
        CloseOverlay(LeptaPanelEditorPanel);
    }

    private void LeptaPanelEditorNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LeptaPanelEditorPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var panelName = LeptaPanelEditorNameBox.Text.Trim();
        LeptaPanelEditorTitleText.Text = string.IsNullOrWhiteSpace(panelName) ? "Edit panel" : $"Edit panel {panelName}";
        leptaController?.UpdateEditingPanelName(LeptaPanelEditorNameBox.Text);
    }

    private void LeptaPanelEditorInstructionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LeptaPanelEditorPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        leptaController?.UpdateEditingPanelInstruction(LeptaPanelEditorInstructionBox.Text);
    }

    private void LeptaPanelEditorFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LeptaPanelEditorPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        leptaController?.UpdateEditingPanelFormat(LeptaPanelEditorFormatCombo.SelectedItem as string);
    }

    private void LeptaPanelColorHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressLeptaPanelColorSync)
        {
            return;
        }

        if (TryParseColor(LeptaPanelColorHexBox.Text, out var color))
        {
            ApplyLeptaPanelAccentColor(ColorToHex(color));
        }
    }

    private void LeptaPanelColorOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (LeptaPanelEditorPanel.Visibility != Visibility.Visible || sender is not Button { Tag: string accentColorHex })
        {
            return;
        }

        ApplyLeptaPanelAccentColor(accentColorHex, updateTextBox: true);
    }

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
            if (leptaPreviewPanelId == panelId)
            {
                HideLeptaPanelPreview();
            }

            leptaController?.RemovePanel(panelId);
        }
    }

    private void LeptaPanelPreviewRequested(object sender, RoutedEventArgs e)
    {
        if (!TryGetLeptaPanelId(sender, e.OriginalSource, out var panelId))
        {
            return;
        }

        if (leptaPreviewPanelId == panelId)
        {
            HideLeptaPanelPreview();
            e.Handled = true;
            return;
        }

        ShowLeptaPanelPreview(panelId);
        e.Handled = true;
    }

    private void LeptaPanelHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid panelId })
        {
            return;
        }

        if (e.OriginalSource is DependencyObject originalSource
            && (FindAncestor<Button>(originalSource) is not null
                || FindAncestor<TextBox>(originalSource) is not null
                || FindAncestor<Slider>(originalSource) is not null
                || FindAncestor<ScrollBar>(originalSource) is not null))
        {
            leptaDraggingPanelId = null;
            leptaPanelDragStartPoint = null;
            return;
        }

        leptaDraggingPanelId = panelId;
        leptaPanelDragStartPoint = e.GetPosition(this);
        leptaDraggingPanelElement = FindDraggableLeptaPanel(sender as DependencyObject, panelId);
        leptaDraggingPanelHandle = sender as UIElement;
        leptaDraggingPanelSourceIndex = -1;
        leptaDraggingPanelTargetIndex = -1;
        isLeptaPanelDragActive = false;
        leptaPanelDragSlots.Clear();
    }

    private void LeptaPanelHeader_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Released)
        {
            return;
        }

        if (isLeptaPanelDragActive)
        {
            CompleteLeptaPanelDrag();
            e.Handled = true;
            return;
        }

        ResetLeptaPanelDragTracking();
    }

    private void LeptaPanelHeader_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || leptaDraggingPanelId is not Guid
            || leptaPanelDragStartPoint is not Point dragStartPoint)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (!isLeptaPanelDragActive)
        {
            BeginLeptaPanelDrag(sender as UIElement);
        }

        if (!isLeptaPanelDragActive)
        {
            return;
        }

        UpdateLeptaPanelDrag(currentPoint);
        e.Handled = true;
    }

    private void BeginLeptaPanelDrag(UIElement? dragHandle)
    {
        if (leptaDraggingPanelId is not Guid panelId
            || leptaDraggingPanelElement is null
            || dragHandle is null)
        {
            return;
        }

        LeptaPanelsItemsControl.UpdateLayout();
        var dragSlots = CaptureLeptaPanelDragSlots();
        var sourceIndex = dragSlots.FindIndex(slot => slot.PanelId == panelId);
        if (sourceIndex < 0)
        {
            return;
        }

        leptaDraggingPanelHandle = dragHandle;
        leptaDraggingPanelHandle.CaptureMouse();
        leptaPanelDragSlots.Clear();
        leptaPanelDragSlots.AddRange(dragSlots);
        leptaDraggingPanelSourceIndex = sourceIndex;
        leptaDraggingPanelTargetIndex = sourceIndex;
        isLeptaPanelDragActive = true;

        AnimateLeptaPanelDragVisual(leptaDraggingPanelElement, lifted: true);
        AnimateLeptaPanelPreviewLayout(sourceIndex);
    }

    private void UpdateLeptaPanelDrag(Point currentPoint)
    {
        if (!isLeptaPanelDragActive
            || leptaPanelDragStartPoint is not Point dragStartPoint
            || leptaDraggingPanelElement is null
            || leptaDraggingPanelSourceIndex < 0
            || leptaDraggingPanelSourceIndex >= leptaPanelDragSlots.Count)
        {
            return;
        }

        var delta = currentPoint - dragStartPoint;
        SetLeptaPanelOffset(leptaDraggingPanelElement, delta.X, delta.Y);

        var sourceSlot = leptaPanelDragSlots[leptaDraggingPanelSourceIndex];
        var draggedCenter = new Point(
            sourceSlot.Origin.X + (sourceSlot.Size.Width / 2) + delta.X,
            sourceSlot.Origin.Y + (sourceSlot.Size.Height / 2) + delta.Y);
        var targetIndex = GetNearestLeptaPanelDragIndex(draggedCenter);
        if (targetIndex == leptaDraggingPanelTargetIndex)
        {
            return;
        }

        leptaDraggingPanelTargetIndex = targetIndex;
        AnimateLeptaPanelPreviewLayout(targetIndex);
    }

    private void CompleteLeptaPanelDrag()
    {
        var draggedPanelId = leptaDraggingPanelId;
        var draggedElement = leptaDraggingPanelElement;
        var sourceIndex = leptaDraggingPanelSourceIndex;
        var targetIndex = leptaDraggingPanelTargetIndex;

        ReleaseLeptaPanelDragCapture();

        if (draggedPanelId is not Guid panelId
            || draggedElement is null
            || sourceIndex < 0
            || targetIndex < 0
            || sourceIndex >= leptaPanelDragSlots.Count
            || targetIndex >= leptaPanelDragSlots.Count)
        {
            ResetVisibleLeptaPanelTransforms(animate: true);
            ResetLeptaPanelDragTracking();
            return;
        }

        if (sourceIndex == targetIndex)
        {
            ResetVisibleLeptaPanelTransforms(animate: true);
            ResetLeptaPanelDragTracking();
            return;
        }

        var targetOffset = GetLeptaPanelDragOffset(sourceIndex, targetIndex);
        AnimateLeptaPanelOffset(draggedElement, targetOffset.X, targetOffset.Y, 120);

        var completionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(130)
        };
        completionTimer.Tick += (_, _) =>
        {
            completionTimer.Stop();
            leptaController?.MovePanelToIndex(panelId, targetIndex);
            UpdateLeptaPanelsLayout();
            _ = Dispatcher.InvokeAsync(() => ResetVisibleLeptaPanelTransforms(animate: true), DispatcherPriority.Loaded);
        };
        completionTimer.Start();

        ResetLeptaPanelDragTracking();
    }

    private void ReleaseLeptaPanelDragCapture()
    {
        if (leptaDraggingPanelHandle?.IsMouseCaptured == true)
        {
            leptaDraggingPanelHandle.ReleaseMouseCapture();
        }
    }

    private void ResetLeptaPanelDragTracking()
    {
        leptaDraggingPanelId = null;
        leptaPanelDragStartPoint = null;
        leptaDraggingPanelElement = null;
        leptaDraggingPanelHandle = null;
        leptaDraggingPanelSourceIndex = -1;
        leptaDraggingPanelTargetIndex = -1;
        isLeptaPanelDragActive = false;
        leptaPanelDragSlots.Clear();
    }

    private List<LeptaPanelDragSlot> CaptureLeptaPanelDragSlots()
    {
        var slots = new List<LeptaPanelDragSlot>();
        foreach (var panel in LeptaPanelsItemsControl.Items.OfType<ILeptaPanelState>())
        {
            var element = FindLeptaPanelElement(panel.Id);
            if (element is null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                continue;
            }

            slots.Add(new LeptaPanelDragSlot(
                panel.Id,
                element,
                element.TranslatePoint(new Point(0, 0), LeptaPanelsItemsControl),
                new Size(element.ActualWidth, element.ActualHeight)));
        }

        return slots;
    }

    private FrameworkElement? FindLeptaPanelElement(Guid panelId)
    {
        var panel = LeptaPanelsItemsControl.Items.OfType<ILeptaPanelState>().FirstOrDefault(item => item.Id == panelId);
        if (panel is null
            || LeptaPanelsItemsControl.ItemContainerGenerator.ContainerFromItem(panel) is not DependencyObject container)
        {
            return null;
        }

        return FindLeptaPanelElement(container, panelId);
    }

    private static FrameworkElement? FindLeptaPanelElement(DependencyObject? current, Guid panelId)
    {
        if (current is null)
        {
            return null;
        }

        if (current is Border border && border.Tag is Guid taggedPanelId && taggedPanelId == panelId)
        {
            return border;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
        {
            var match = FindLeptaPanelElement(VisualTreeHelper.GetChild(current, index), panelId);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private int GetNearestLeptaPanelDragIndex(Point draggedCenter)
    {
        var nearestIndex = leptaDraggingPanelSourceIndex;
        var nearestDistance = double.MaxValue;

        for (var index = 0; index < leptaPanelDragSlots.Count; index++)
        {
            var slot = leptaPanelDragSlots[index];
            var center = new Point(slot.Origin.X + (slot.Size.Width / 2), slot.Origin.Y + (slot.Size.Height / 2));
            var distanceX = draggedCenter.X - center.X;
            var distanceY = draggedCenter.Y - center.Y;
            var distance = (distanceX * distanceX) + (distanceY * distanceY);
            if (distance >= nearestDistance)
            {
                continue;
            }

            nearestDistance = distance;
            nearestIndex = index;
        }

        return nearestIndex;
    }

    private void AnimateLeptaPanelPreviewLayout(int targetIndex)
    {
        if (leptaDraggingPanelSourceIndex < 0 || leptaDraggingPanelSourceIndex >= leptaPanelDragSlots.Count)
        {
            return;
        }

        for (var index = 0; index < leptaPanelDragSlots.Count; index++)
        {
            if (index == leptaDraggingPanelSourceIndex)
            {
                continue;
            }

            var slot = leptaPanelDragSlots[index];
            var previewIndex = GetLeptaPanelPreviewIndex(index, leptaDraggingPanelSourceIndex, targetIndex);
            var previewOrigin = leptaPanelDragSlots[previewIndex].Origin;
            AnimateLeptaPanelOffset(slot.Element, previewOrigin.X - slot.Origin.X, previewOrigin.Y - slot.Origin.Y, 140);
        }
    }

    private Point GetLeptaPanelDragOffset(int sourceIndex, int targetIndex)
    {
        var sourceOrigin = leptaPanelDragSlots[sourceIndex].Origin;
        var targetOrigin = leptaPanelDragSlots[targetIndex].Origin;
        return new Point(targetOrigin.X - sourceOrigin.X, targetOrigin.Y - sourceOrigin.Y);
    }

    private void ResetVisibleLeptaPanelTransforms(bool animate)
    {
        foreach (var panel in LeptaPanelsItemsControl.Items.OfType<ILeptaPanelState>())
        {
            var element = FindLeptaPanelElement(panel.Id);
            if (element is null)
            {
                continue;
            }

            if (animate)
            {
                AnimateLeptaPanelToRest(element);
            }
            else
            {
                SetLeptaPanelToRest(element);
            }
        }
    }

    private static int GetLeptaPanelPreviewIndex(int index, int sourceIndex, int targetIndex)
    {
        if (targetIndex > sourceIndex && index > sourceIndex && index <= targetIndex)
        {
            return index - 1;
        }

        if (targetIndex < sourceIndex && index >= targetIndex && index < sourceIndex)
        {
            return index + 1;
        }

        return index;
    }

    private void LeptaPanel_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("LeptaPanelId")
            || sender is not FrameworkElement targetElement
            || targetElement.Tag is not Guid targetPanelId
            || e.Data.GetData("LeptaPanelId") is not Guid sourcePanelId)
        {
            ClearLeptaPanelDropPreview();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        if (sourcePanelId == targetPanelId)
        {
            ClearLeptaPanelDropPreview();
            e.Handled = true;
            return;
        }

        var dropPosition = e.GetPosition(targetElement);
        UpdateLeptaPanelDropPreview(targetElement, insertAfter: dropPosition.X >= targetElement.ActualWidth / 2);
        e.Handled = true;
    }

    private void LeptaPanel_DragLeave(object sender, DragEventArgs e)
    {
        if (sender == leptaDragPreviewElement)
        {
            ClearLeptaPanelDropPreview();
        }
    }

    private void LeptaPanel_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("LeptaPanelId")
            || sender is not FrameworkElement targetElement
            || targetElement.Tag is not Guid targetPanelId
            || e.Data.GetData("LeptaPanelId") is not Guid sourcePanelId
            || leptaController is null)
        {
            return;
        }

        var panelItems = LeptaPanelsItemsControl.Items.OfType<ILeptaPanelState>().ToList();
        var sourceIndex = panelItems.FindIndex(panel => panel.Id == sourcePanelId);
        var targetIndex = panelItems.FindIndex(panel => panel.Id == targetPanelId);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        var dropPosition = e.GetPosition(targetElement);
        var insertAfter = dropPosition.X >= targetElement.ActualWidth / 2;
        var finalIndex = insertAfter
            ? (sourceIndex < targetIndex ? targetIndex : targetIndex + 1)
            : (sourceIndex < targetIndex ? targetIndex - 1 : targetIndex);

        ClearLeptaPanelDropPreview();
        leptaController.MovePanelToIndex(sourcePanelId, finalIndex);
        UpdateLeptaPanelsLayout();
        e.Handled = true;
    }

    private void LeptaPanelChatRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid panelId } || leptaController is null || chatController is null)
        {
            return;
        }

        HideLeptaPanelPreview();

        if (!leptaController.TryBuildChatContinuation(panelId, out var serverId, out var sourceName, out var userPrompt, out var assistantResponse))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(serverId))
        {
            modelsController?.SelectServer(serverId);
            leptaController.SelectServer(serverId);
            chatController.HandleServerSelectionChanged();
        }

        ChatTabButton.IsChecked = true;
        chatController.LoadLeptaConversation(userPrompt, assistantResponse, sourceName);
    }

    private async void RunLeptaButton_Click(object sender, RoutedEventArgs e)
    {
        if (leptaController is null)
        {
            return;
        }

        await leptaController.RunFromClipboardAsync();
    }

    private void StopLeptaButton_Click(object sender, RoutedEventArgs e)
        => leptaController?.CancelCurrentRun();

    private void SaveDashboardButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            leptaController?.SaveDashboard();
        }
        catch (Exception exception)
        {
            UserNotificationService.ShowError("Dashboard save failed", exception.Message, this, logger, actionLogStream, nameof(MainWindow));
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
            UserNotificationService.ShowError("Dashboard save failed", exception.Message, this, logger, actionLogStream, nameof(MainWindow));
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
            UserNotificationService.ShowError("Dashboard delete failed", exception.Message, this, logger, actionLogStream, nameof(MainWindow));
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
            UserNotificationService.ShowError("Preset save failed", exception.Message, this, logger, actionLogStream, nameof(MainWindow));
        }
    }

    private void LoadLeptaPresetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            leptaController?.LoadPreset();
            if (LeptaServerCombo.SelectedItem is VllmServerConfiguration server)
            {
                modelsController?.SelectServer(server.Id);
            }
        }
        catch (Exception exception)
        {
            UserNotificationService.ShowError("Preset load failed", exception.Message, this, logger, actionLogStream, nameof(MainWindow));
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
            UserNotificationService.ShowError("Preset save failed", exception.Message, this, logger, actionLogStream, nameof(MainWindow));
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
            UserNotificationService.ShowError("Preset delete failed", exception.Message, this, logger, actionLogStream, nameof(MainWindow));
        }
    }

    private void HotkeySetting_Changed(object sender, RoutedEventArgs e) => leptaController?.HandleHotkeySettingChanged();

    private void HotkeyKeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => leptaController?.HandleHotkeySettingChanged();

    private void HotkeyKeyCombo_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!HotkeyKeyCombo.IsKeyboardFocusWithin)
        {
            return;
        }

        leptaController?.HandleHotkeySettingChanged();
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (isShutdownConfirmed || isStoppingOnClose)
        {
            return;
        }

        e.Cancel = true;
        PersistState(showErrors: true);
        await CancelInFlightOperationsAsync();

        if (modelsController is not null)
        {
            await modelsController.RefreshSelectedServerStatusAsync();
        }

        var selectedServer = SelectedServer;

        if (selectedServer?.IsLeptaManagedDeploymentActive == true)
        {
            var confirmResult = UserNotificationService.Confirm(
                "Stop LLM server?",
                "Do you want to stop the LLM server before closing LEPTA?",
                buttons: MessageBoxButton.YesNoCancel,
                owner: this);

            if (confirmResult == MessageBoxResult.Cancel)
            {
                return;
            }

            if (confirmResult == MessageBoxResult.Yes
                && modelsController is not null)
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
        }

        isShutdownConfirmed = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        persistenceTimer.Stop();
        actionLogOverlayTimer.Stop();
        leptaThroughputTimer.Stop();
        clipboardCachePrefillTimer.Stop();
        actionLogStream.EntryPublished -= ActionLogStream_EntryPublished;
        if (hwndSource is not null)
        {
            if (isClipboardListenerRegistered)
            {
                RemoveClipboardFormatListener(hwndSource.Handle);
                isClipboardListenerRegistered = false;
            }

            UnregisterHotKey(hwndSource.Handle, GlobalHotkeyId);
            hwndSource.RemoveHook(WndProc);
            hwndSource = null;
        }

        ResponseFontSizeCoordinator.StepRequested -= HandleResponseFontSizeStepRequested;

        base.OnClosed(e);
    }

    private void HandleHotkeySettingsChanged()
    {
        RegisterConfiguredHotkey();
        UpdateLeptaHotkeyText();
    }

    private void HandleModelsStateChanged()
    {
        leptaController?.RefreshAvailableServers();
        RefreshSettingsControls();
        QueuePersistence();
    }

    private void HandleLeptaStateChanged()
    {
        UpdateLeptaPanelsLayout();
        SyncLeptaPanelPreview();
        RefreshSettingsControls();
        QueuePersistence();
    }

    private void HandleLeptaPanelMetadataChanged()
        => QueuePersistence();

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
                IsNavigationCollapsed = true,
                IsActionLogOverlayEnabled = EnableActionLogOverlayCheckBox.IsChecked == true,
                EnableClipboardCachePrefill = EnableClipboardCachePrefillCheckBox.IsChecked == true,
                EnableVerboseVllmLogs = modelsController.IsVerboseVllmLogsEnabled,
                UiFontSize = GetSelectedUiFontSize(),
                ResponseFontSize = GetSelectedResponseFontSize(),
                DefaultDashboardId = leptaController.CurrentDashboardId,
                DefaultServerId = modelsController.SelectedServerId,
                Hotkey = leptaController.GetHotkeySettings(),
                Chat = chatController.CaptureSettings(),
                Lepta = leptaController.CaptureSettings(),
                LeptaSystemInstructions = LeptaSystemInstructionBox.Text.Trim()
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
                UserNotificationService.ShowError("LEPTA could not save app data", exception.Message, this, logger, actionLogStream, nameof(MainWindow));
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
        NavigationColumn.Width = new GridLength(46);
        NavigationPanel.Padding = new Thickness(6);

        SetNavigationContent(LeptaTabButton, "◫", "Lepta");
        SetNavigationContent(ModelsTabButton, "◎", "Models");
        SetNavigationContent(SettingsTabButton, "⚙", "Settings");
        SetNavigationContent(ChatTabButton, "\uE8BD", "Chat", useSymbolFont: true);
        if (CollapseNavigationCheckBox is not null)
        {
            suppressSettingsChangeHandlers = true;
            CollapseNavigationCheckBox.IsChecked = isCollapsed;
            suppressSettingsChangeHandlers = false;
        }
    }

    private void ApplyLeptaSidebarState(bool isCollapsed)
    {
        isLeptaSidebarCollapsed = isCollapsed;
        if (LeptaSidebarColumn is null || LeptaSidebar is null || ToggleLeptaSidebarButton is null)
        {
            return;
        }

        LeptaSidebarColumn.Width = isCollapsed ? new GridLength(0) : new GridLength(320);
        LeptaSidebar.Visibility = isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ToggleLeptaSidebarButton.Content = isCollapsed ? "<" : ">";
        ToggleLeptaSidebarButton.Margin = isCollapsed
            ? new Thickness(0, 0, -11, 0)
            : new Thickness(0, 0, -11, 0);
    }

    private void SetNavigationContent(RadioButton button, string symbol, string label, bool useSymbolFont = false)
    {
        button.Content = symbol;
        button.FontFamily = useSymbolFont ? new FontFamily("Segoe MDL2 Assets") : SystemFonts.MessageFontFamily;
        button.Padding = new Thickness(6);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
    }

    private void UpdateGeneralInstructionSummary()
    {
        if (!IsInitialized)
        {
            return;
        }

        var text = LeptaGeneralInstructionBox.Text.Trim();
        GeneralInstructionSummaryText.Text = string.IsNullOrWhiteSpace(text)
            ? "No global instructions yet. LEPTA will send the system instructions from Settings, then clipboard text, then each panel instruction."
            : text.Length <= 240
                ? text
                : text[..237] + "...";
    }

    private void UpdateLeptaPanelsLayout()
    {
        if (LeptaPanelsItemsControl is null)
        {
            return;
        }

        var rows = LeptaPanelsItemsControl.Items.Count > 5 ? 2 : 1;
        if (rows == leptaPanelLayoutRows)
        {
            return;
        }

        leptaPanelLayoutRows = rows;
        var panelFactory = new FrameworkElementFactory(typeof(UniformGrid));
        panelFactory.SetValue(UniformGrid.RowsProperty, rows);
        LeptaPanelsItemsControl.ItemsPanel = new ItemsPanelTemplate(panelFactory);
    }

    private void ShowLeptaPanelPreview(Guid panelId)
    {
        var panel = FindLeptaPanel(panelId);
        if (panel is null || string.IsNullOrWhiteSpace(panel.Response))
        {
            return;
        }

        leptaPreviewPanelId = panelId;
        LeptaPanelPreviewContent.Content = panel;
        LeptaPanelsItemsControl.Visibility = Visibility.Collapsed;
        LeptaPanelPreviewOverlay.Visibility = Visibility.Visible;
    }

    private void HideLeptaPanelPreview()
    {
        leptaPreviewPanelId = null;
        LeptaPanelPreviewContent.Content = null;
        LeptaPanelPreviewOverlay.Visibility = Visibility.Collapsed;
        LeptaPanelsItemsControl.Visibility = Visibility.Visible;
    }

    private void SyncLeptaPanelPreview()
    {
        if (leptaPreviewPanelId is not Guid panelId)
        {
            return;
        }

        var panel = FindLeptaPanel(panelId);
        if (panel is null || string.IsNullOrWhiteSpace(panel.Response))
        {
            HideLeptaPanelPreview();
            return;
        }

        LeptaPanelPreviewContent.Content = panel;
        LeptaPanelPreviewOverlay.Visibility = Visibility.Visible;
    }

    private ILeptaPanelState? FindLeptaPanel(Guid panelId)
        => LeptaPanelsItemsControl?.Items.OfType<ILeptaPanelState>().FirstOrDefault(item => item.Id == panelId);

    private static bool TryGetLeptaPanelId(object? sender, object? originalSource, out Guid panelId)
    {
        panelId = Guid.Empty;

        if (sender is FrameworkElement { Tag: Guid senderPanelId })
        {
            panelId = senderPanelId;
            return true;
        }

        if (originalSource is FrameworkElement { Tag: Guid originalPanelId })
        {
            panelId = originalPanelId;
            return true;
        }

        return false;
    }

    private void SetLeptaPanelColorEditor(string accentColorHex)
    {
        suppressLeptaPanelColorSync = true;
        try
        {
            var color = ParseColor(accentColorHex);
            var normalizedHex = ColorToHex(color);
            LeptaPanelColorPreviewBorder.Background = new SolidColorBrush(color);
            LeptaPanelColorHexBox.Text = normalizedHex;
            UpdateLeptaPanelColorOptionSelection(normalizedHex);
        }
        finally
        {
            suppressLeptaPanelColorSync = false;
        }
    }

    private void ApplyLeptaPanelAccentColor(string accentColorHex, bool updateTextBox = false)
    {
        if (!TryParseColor(accentColorHex, out var color))
        {
            return;
        }

        var normalizedHex = ColorToHex(color);
        suppressLeptaPanelColorSync = true;
        try
        {
            LeptaPanelColorPreviewBorder.Background = new SolidColorBrush(color);
            UpdateLeptaPanelColorOptionSelection(normalizedHex);
            if (updateTextBox)
            {
                LeptaPanelColorHexBox.Text = normalizedHex;
            }
        }
        finally
        {
            suppressLeptaPanelColorSync = false;
        }

        leptaController?.UpdateEditingPanelAccentColor(normalizedHex);
    }

    private void InitializeLeptaPanelColorOptions()
    {
        LeptaPanelColorOptionsGrid.Children.Clear();
        leptaPanelColorOptionButtons.Clear();

        foreach (var accentColorHex in LeptaPanelAccentPalette.Options)
        {
            var color = ParseColor(accentColorHex);
            var button = new Button
            {
                Tag = accentColorHex,
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(1),
                MinHeight = 18,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                BorderBrush = GetPanelColorOptionBorderBrush(color),
                Foreground = GetPanelColorOptionContrastBrush(color),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                ToolTip = accentColorHex
            };
            button.Click += LeptaPanelColorOptionButton_Click;

            LeptaPanelColorOptionsGrid.Children.Add(button);
            leptaPanelColorOptionButtons.Add(button);
        }
    }

    private void UpdateLeptaPanelColorOptionSelection(string accentColorHex)
    {
        var normalizedHex = LeptaPanelAccentPalette.Normalize(accentColorHex);
        foreach (var button in leptaPanelColorOptionButtons)
        {
            if (button.Tag is not string optionHex)
            {
                continue;
            }

            var optionColor = ParseColor(optionHex);
            var isSelected = string.Equals(optionHex, normalizedHex, StringComparison.OrdinalIgnoreCase);
            button.Content = isSelected ? "✓" : null;
            button.BorderThickness = isSelected ? new Thickness(2) : new Thickness(1);
            button.BorderBrush = isSelected
                ? GetPanelColorOptionContrastBrush(optionColor)
                : GetPanelColorOptionBorderBrush(optionColor);
        }
    }

    private static Brush GetPanelColorOptionBorderBrush(Color color)
    {
        var alpha = (byte)90;
        return GetPerceivedBrightness(color) >= 0.6
            ? new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0))
            : new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
    }

    private static Brush GetPanelColorOptionContrastBrush(Color color)
        => GetPerceivedBrightness(color) >= 0.6 ? Brushes.Black : Brushes.White;

    private static double GetPerceivedBrightness(Color color)
        => ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255d;

    private void ApplyUiFontSize(double fontSize)
    {
        var normalizedFontSize = Math.Clamp(fontSize, 12, 18);
        FontSize = normalizedFontSize;
        RenderLeptaThroughputGraph();
    }

    private void ApplyResponseFontSize(double fontSize)
    {
        ResponseFontSize = NormalizeResponseFontSize(fontSize);
    }

    private void SelectUiFontSize(double fontSize)
    {
        if (UiFontSizeCombo is null)
        {
            return;
        }

        var normalized = Math.Clamp(Math.Round(fontSize), 12, 18).ToString("0");
        UiFontSizeCombo.SelectedItem = UiFontSizeCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), normalized, StringComparison.Ordinal))
            ?? UiFontSizeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Content?.ToString(), "14", StringComparison.Ordinal));
    }

    private double GetSelectedUiFontSize()
        => UiFontSizeCombo.SelectedItem is ComboBoxItem item
           && double.TryParse(item.Content?.ToString(), out var fontSize)
            ? fontSize
            : 14;

    private void SelectResponseFontSize(double fontSize)
    {
        if (ResponseFontSizeCombo is null)
        {
            return;
        }

        var normalized = NormalizeResponseFontSize(fontSize).ToString("0");
        ResponseFontSizeCombo.SelectedItem = ResponseFontSizeCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), normalized, StringComparison.Ordinal))
            ?? ResponseFontSizeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Content?.ToString(), "14", StringComparison.Ordinal));
    }

    private void SelectLeptaDocumentTrimMode(LeptaDocumentTrimMode trimMode)
    {
        if (LeptaDocumentTrimModeCombo is null)
        {
            return;
        }

        LeptaDocumentTrimModeCombo.SelectedItem = LeptaDocumentTrimModeCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), trimMode.ToString(), StringComparison.Ordinal))
            ?? LeptaDocumentTrimModeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag?.ToString(), LeptaDocumentTrimMode.TrimStart.ToString(), StringComparison.Ordinal));
    }

    private void SelectLeptaDocumentTokenLimit(int tokenLimit)
    {
        if (LeptaDocumentTokenLimitBox is null)
        {
            return;
        }

        LeptaDocumentTokenLimitBox.Text = LeptaSettings.NormalizeDocumentTokenLimit(tokenLimit).ToString(CultureInfo.InvariantCulture);
    }

    private LeptaDocumentTrimMode GetSelectedLeptaDocumentTrimMode()
        => LeptaDocumentTrimModeCombo?.SelectedItem is ComboBoxItem item
           && Enum.TryParse<LeptaDocumentTrimMode>(item.Tag?.ToString(), out var trimMode)
            ? trimMode
            : LeptaDocumentTrimMode.TrimStart;

    private void ApplyLeptaDocumentTrimModeSetting()
    {
        if (leptaController is null)
        {
            return;
        }

        var settings = leptaController.CaptureSettings();
        settings.DocumentTrimMode = GetSelectedLeptaDocumentTrimMode();
        leptaController.ApplySettings(settings);
        UpdateLeptaDocumentTrimSummary();
    }

    private bool ApplyLeptaDocumentTokenLimitSetting()
    {
        if (leptaController is null || !TryGetSelectedLeptaDocumentTokenLimit(out var tokenLimit))
        {
            return false;
        }

        var settings = leptaController.CaptureSettings();
        settings.DocumentTokenLimit = tokenLimit;
        leptaController.ApplySettings(settings);
        UpdateLeptaDocumentTrimSummary();
        return true;
    }

    private bool TryGetSelectedLeptaDocumentTokenLimit(out int tokenLimit)
    {
        if (int.TryParse(LeptaDocumentTokenLimitBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out tokenLimit)
            || int.TryParse(LeptaDocumentTokenLimitBox?.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out tokenLimit))
        {
            tokenLimit = LeptaSettings.NormalizeDocumentTokenLimit(tokenLimit);
            return true;
        }

        tokenLimit = leptaController?.CaptureSettings().DocumentTokenLimit ?? LeptaSettings.DefaultDocumentTokenLimit;
        return false;
    }

    private void UpdateLeptaDocumentTrimSummary()
    {
        if (LeptaDocumentTrimSummaryText is null || leptaController is null)
        {
            return;
        }

        var settings = leptaController.CaptureSettings();
        var directionText = settings.DocumentTrimMode == LeptaDocumentTrimMode.TrimEnd
            ? "overflow is trimmed from the end"
            : "oldest text is trimmed from the start";
        var characterLimit = LeptaRequestOrchestrator.GetDocumentCharacterLimit(settings.DocumentTokenLimit);
        LeptaDocumentTrimSummaryText.Text =
            $"Current limit: about {settings.DocumentTokenLimit:N0} tokens (~{characterLimit:N0} chars), and {directionText}.";
    }

    private double GetSelectedResponseFontSize()
        => ResponseFontSizeCombo.SelectedItem is ComboBoxItem item
           && double.TryParse(item.Content?.ToString(), out var fontSize)
            ? fontSize
            : 14;

    private void HandleResponseFontSizeStepRequested(int direction)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => HandleResponseFontSizeStepRequested(direction));
            return;
        }

        if (direction == 0)
        {
            return;
        }

        var current = GetSelectedResponseFontSize();
        var currentIndex = Array.FindIndex(ResponseFontSizeOptions, option => Math.Abs(option - current) < 0.1);
        if (currentIndex < 0)
        {
            currentIndex = Array.FindIndex(ResponseFontSizeOptions, option => option >= current);
            currentIndex = currentIndex < 0 ? ResponseFontSizeOptions.Length - 1 : currentIndex;
        }

        var nextIndex = Math.Clamp(currentIndex + Math.Sign(direction), 0, ResponseFontSizeOptions.Length - 1);
        if (nextIndex == currentIndex)
        {
            return;
        }

        suppressSettingsChangeHandlers = true;
        try
        {
            SelectResponseFontSize(ResponseFontSizeOptions[nextIndex]);
        }
        finally
        {
            suppressSettingsChangeHandlers = false;
        }

        ApplyResponseFontSize(ResponseFontSizeOptions[nextIndex]);
        QueuePersistence();
    }

    private static double NormalizeResponseFontSize(double fontSize)
    {
        var rounded = Math.Round(fontSize);
        var nearest = ResponseFontSizeOptions
            .OrderBy(option => Math.Abs(option - rounded))
            .ThenBy(option => option)
            .FirstOrDefault();
        return nearest <= 0 ? 14 : nearest;
    }

    private void UpdateLeptaHotkeyText()
    {
        if (leptaController is null || LeptaRunHotkeyText is null)
        {
            return;
        }

        leptaController.TryGetHotkey(out _, out _, out var displayText);
        LeptaRunHotkeyText.Text = $"Shortcut: {displayText}";
    }

    private void HandleLeptaThroughputReset()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(HandleLeptaThroughputReset);
            return;
        }

        ResetLeptaThroughputGraph();
        leptaRunStartedUtc = DateTimeOffset.UtcNow;
        leptaLastThroughputSampleUtc = leptaRunStartedUtc;
        leptaThroughputTimer.Start();
    }

    private void HandleLeptaThroughputModelResolved(string modelName)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => HandleLeptaThroughputModelResolved(modelName));
            return;
        }

        leptaResolvedModelName = modelName;
        UpdateLeptaThroughputDetails();
    }

    private void HandleLeptaThroughputTokensObserved(int tokenCount)
    {
        Interlocked.Add(ref leptaTokensSinceLastSample, tokenCount);
        Interlocked.Add(ref leptaTotalObservedTokens, tokenCount);

        if (!hasSeenLeptaFirstToken && tokenCount > 0 && leptaRunStartedUtc.HasValue)
        {
            if (Dispatcher.CheckAccess())
            {
                hasSeenLeptaFirstToken = true;
                leptaFirstTokenReceivedUtc = DateTimeOffset.UtcNow;
                leptaFirstTokenDelaySeconds = Math.Max(0, (DateTimeOffset.UtcNow - leptaRunStartedUtc.Value).TotalSeconds);
                UpdateLeptaThroughputDetails();
            }
            else
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (!hasSeenLeptaFirstToken && leptaRunStartedUtc.HasValue)
                    {
                        hasSeenLeptaFirstToken = true;
                        leptaFirstTokenReceivedUtc = DateTimeOffset.UtcNow;
                        leptaFirstTokenDelaySeconds = Math.Max(0, (DateTimeOffset.UtcNow - leptaRunStartedUtc.Value).TotalSeconds);
                        UpdateLeptaThroughputDetails();
                    }
                });
            }
        }
    }

    private void HandleLeptaThroughputFirstPanelCompleted()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(HandleLeptaThroughputFirstPanelCompleted);
            return;
        }

        if (leptaFirstResponseEndedUtc.HasValue || !leptaRunStartedUtc.HasValue)
        {
            return;
        }

        leptaFirstResponseEndedUtc = DateTimeOffset.UtcNow;
        leptaFirstResponseEndedSeconds = Math.Max(0, (leptaFirstResponseEndedUtc.Value - leptaRunStartedUtc.Value).TotalSeconds);
        leptaTokensAtFirstResponseEnd = Interlocked.Read(ref leptaTotalObservedTokens);
        UpdateLeptaThroughputDetails();
        RenderLeptaThroughputGraph();
    }

    private void HandleLeptaThroughputCompleted()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(HandleLeptaThroughputCompleted);
            return;
        }

        leptaRunCompletedUtc = DateTimeOffset.UtcNow;
        FlushLeptaThroughputSample();
        leptaThroughputTimer.Stop();
        UpdateLeptaThroughputDetails();
        RenderLeptaThroughputGraph();
    }

    private void LeptaThroughputTimer_Tick(object? sender, EventArgs e)
        => FlushLeptaThroughputSample();

    private void FlushLeptaThroughputSample()
    {
        var now = DateTimeOffset.UtcNow;
        var last = leptaLastThroughputSampleUtc ?? now;
        var elapsedSeconds = Math.Max(0.001, (now - last).TotalSeconds);
        leptaLastThroughputSampleUtc = now;

        var tokenCount = Interlocked.Exchange(ref leptaTokensSinceLastSample, 0);
        var secondsSinceStart = leptaRunStartedUtc.HasValue
            ? Math.Max(0, (now - leptaRunStartedUtc.Value).TotalSeconds)
            : leptaThroughputSamples.Count * elapsedSeconds;
        AppendLeptaThroughputSample(secondsSinceStart, tokenCount / elapsedSeconds);
    }

    private void AppendLeptaThroughputSample(double secondsSinceStart, double tokensPerSecond)
    {
        leptaThroughputSamples.Add(new LeptaThroughputSample(secondsSinceStart, Math.Max(0, tokensPerSecond)));
        while (leptaThroughputSamples.Count > 40)
        {
            leptaThroughputSamples.RemoveAt(0);
        }

        LeptaThroughputValueText.Text = $"{tokensPerSecond:F0} tok/s";
        UpdateLeptaThroughputDetails();
        RenderLeptaThroughputGraph();
    }

    private void ResetLeptaThroughputGraph()
    {
        leptaThroughputSamples.Clear();
        Interlocked.Exchange(ref leptaTokensSinceLastSample, 0);
        Interlocked.Exchange(ref leptaTotalObservedTokens, 0);
        leptaLastThroughputSampleUtc = null;
        leptaRunStartedUtc = null;
        leptaFirstTokenReceivedUtc = null;
        leptaFirstResponseEndedUtc = null;
        leptaRunCompletedUtc = null;
        leptaHoveredSeconds = null;
        leptaResolvedModelName = string.Empty;
        hasSeenLeptaFirstToken = false;
        leptaFirstTokenDelaySeconds = null;
        leptaFirstResponseEndedSeconds = null;
        leptaTokensAtFirstResponseEnd = 0;
        LeptaThroughputValueText.Text = "0 tok/s";
        UpdateLeptaThroughputDetails();
        RenderLeptaThroughputGraph();
    }

    private void LeptaThroughputGraphHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => RenderLeptaThroughputGraph();

    private void LeptaThroughputGraphHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (LeptaThroughputCanvas is null || leptaThroughputSamples.Count == 0)
        {
            return;
        }

        var chartBounds = GetLeptaChartBounds();
        if (chartBounds.Width <= 0 || chartBounds.Height <= 0)
        {
            return;
        }

        var position = e.GetPosition(LeptaThroughputCanvas);
        var maxSeconds = Math.Max(1, leptaThroughputSamples.Last().SecondsSinceStart);
        var clampedX = Math.Clamp(position.X, chartBounds.Left, chartBounds.Right);
        var hoveredSeconds = ((clampedX - chartBounds.Left) / chartBounds.Width) * maxSeconds;

        leptaHoveredSeconds = hoveredSeconds;
        RenderLeptaThroughputGraph();
    }

    private void LeptaThroughputGraphHost_MouseLeave(object sender, MouseEventArgs e)
    {
        leptaHoveredSeconds = null;
        RenderLeptaThroughputGraph();
    }

    private void UpdateLeptaThroughputDetails()
    {
        if (LeptaThroughputDetailsText is null)
        {
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        var ttft = leptaFirstTokenDelaySeconds ?? 0;
        var modelName = string.IsNullOrWhiteSpace(leptaResolvedModelName) ? "Model pending..." : leptaResolvedModelName;
        var maxThroughput = leptaThroughputSamples.Count == 0 ? 0d : leptaThroughputSamples.Max(sample => sample.TokensPerSecond);
        var effective = 0d;
        var totalSeconds = 0d;

        if (leptaFirstTokenReceivedUtc.HasValue)
        {
            var effectiveWindowEndUtc = leptaFirstResponseEndedUtc ?? DateTimeOffset.UtcNow;
            var tokensInEffectiveWindow = leptaFirstResponseEndedUtc.HasValue
                ? leptaTokensAtFirstResponseEnd
                : Interlocked.Read(ref leptaTotalObservedTokens);
            var effectiveWindowSeconds = Math.Max(0, (effectiveWindowEndUtc - leptaFirstTokenReceivedUtc.Value).TotalSeconds);
            if (effectiveWindowSeconds > 0)
            {
                effective = tokensInEffectiveWindow / effectiveWindowSeconds;
            }
        }

        if (leptaRunStartedUtc.HasValue)
        {
            var runEndUtc = leptaRunCompletedUtc ?? DateTimeOffset.UtcNow;
            totalSeconds = Math.Max(0, (runEndUtc - leptaRunStartedUtc.Value).TotalSeconds);
        }

        LeptaThroughputDetailsText.Text =
            $"{modelName}{Environment.NewLine}" +
            $"Effective: {effective.ToString("0", culture)} tok/s • Max: {maxThroughput.ToString("0", culture)} tok/s • TTFT: {ttft.ToString("0.00", culture)} s • Total: {totalSeconds.ToString("0.00", culture)} s";
    }

    private void RenderLeptaThroughputGraph()
    {
        if (LeptaThroughputCanvas is null)
        {
            return;
        }

        var width = Math.Max(0, LeptaThroughputCanvas.ActualWidth);
        var height = Math.Max(0, LeptaThroughputCanvas.ActualHeight);
        LeptaThroughputCanvas.Children.Clear();

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var chartBounds = GetLeptaChartBounds();
        if (chartBounds.Width <= 0 || chartBounds.Height <= 0)
        {
            return;
        }

        var maxSeconds = Math.Max(1, leptaThroughputSamples.Count == 0 ? 1 : leptaThroughputSamples.Last().SecondsSinceStart);
        if (leptaFirstTokenDelaySeconds is double ttftSeconds)
        {
            maxSeconds = Math.Max(maxSeconds, ttftSeconds);
        }

        if (leptaFirstResponseEndedSeconds is double firstResponseEndedSeconds)
        {
            maxSeconds = Math.Max(maxSeconds, firstResponseEndedSeconds);
        }

        if (leptaRunStartedUtc.HasValue)
        {
            var runEndUtc = leptaRunCompletedUtc ?? DateTimeOffset.UtcNow;
            maxSeconds = Math.Max(maxSeconds, Math.Max(0, (runEndUtc - leptaRunStartedUtc.Value).TotalSeconds));
        }

        var maxValue = Math.Max(25, leptaThroughputSamples.Count == 0 ? 25 : Math.Ceiling(leptaThroughputSamples.Max(sample => sample.TokensPerSecond) / 25d) * 25d);

        var secondStep = Math.Max(1, (int)Math.Ceiling(maxSeconds / 6d));
        for (var second = 0; second <= Math.Ceiling(maxSeconds); second++)
        {
            var x = chartBounds.Left + (second / maxSeconds) * chartBounds.Width;
            AddLeptaGraphLine(new Point(x, chartBounds.Top), new Point(x, chartBounds.Bottom), "#2D394B", 0.7);
            if (second > 0 && second % secondStep == 0)
            {
                AddLeptaGraphLabel(second.ToString(CultureInfo.InvariantCulture), x, chartBounds.Bottom - 14, centerHorizontally: true);
            }
        }

        for (var tokens = 0; tokens <= maxValue; tokens += 25)
        {
            var y = chartBounds.Bottom - ((tokens / maxValue) * chartBounds.Height);
            AddLeptaGraphLine(new Point(chartBounds.Left, y), new Point(chartBounds.Right, y), "#2D394B", 0.7);
            AddLeptaGraphLabel(tokens.ToString("0", CultureInfo.InvariantCulture), chartBounds.Left - 6, y, rightAlign: true, centerVertically: true);
        }

        AddLeptaGraphLabel("tok/s", chartBounds.Left, Math.Max(0, chartBounds.Top - 16), emphasize: true, centerHorizontally: true);
        AddLeptaGraphLabel("time, s", chartBounds.Right + 8, chartBounds.Bottom, emphasize: true, centerVertically: true);

        AddLeptaGraphLine(new Point(chartBounds.Left, chartBounds.Bottom), new Point(chartBounds.Right, chartBounds.Bottom), "#A8B3C2", 1);
        AddLeptaGraphLine(new Point(chartBounds.Left, chartBounds.Top), new Point(chartBounds.Left, chartBounds.Bottom), "#A8B3C2", 1);

        if (leptaFirstTokenDelaySeconds is double ttftMarkerSeconds && ttftMarkerSeconds > 0)
        {
            var ttftX = chartBounds.Left + (ttftMarkerSeconds / maxSeconds) * chartBounds.Width;
            AddLeptaGraphLine(
                new Point(ttftX, chartBounds.Top),
                new Point(ttftX, chartBounds.Bottom),
                "#7A8798",
                1,
                new DoubleCollection { 3, 3 });
        }

        if (leptaFirstResponseEndedSeconds is double firstResponseMarkerSeconds && firstResponseMarkerSeconds > 0)
        {
            var firstResponseX = chartBounds.Left + (firstResponseMarkerSeconds / maxSeconds) * chartBounds.Width;
            AddLeptaGraphLine(
                new Point(firstResponseX, chartBounds.Top),
                new Point(firstResponseX, chartBounds.Bottom),
                "#7A8798",
                1,
                new DoubleCollection { 3, 3 });
        }

        if (leptaThroughputSamples.Count == 0)
        {
            return;
        }

        var points = new PointCollection(leptaThroughputSamples.Count);
        foreach (var sample in leptaThroughputSamples)
        {
            var x = chartBounds.Left + (sample.SecondsSinceStart / maxSeconds) * chartBounds.Width;
            var y = chartBounds.Bottom - ((sample.TokensPerSecond / maxValue) * chartBounds.Height);
            points.Add(new Point(x, y));
        }

        LeptaThroughputCanvas.Children.Add(new Polyline
        {
            Stroke = (Brush)FindResource("AccentBrush"),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Points = points
        });

        if (leptaHoveredSeconds is double hoveredSeconds && TryInterpolateLeptaThroughput(hoveredSeconds, out var hoveredTokensPerSecond))
        {
            var hoverX = chartBounds.Left + (hoveredSeconds / maxSeconds) * chartBounds.Width;
            var hoverY = chartBounds.Bottom - ((hoveredTokensPerSecond / maxValue) * chartBounds.Height);
            AddLeptaGraphLine(new Point(hoverX, chartBounds.Top), new Point(hoverX, chartBounds.Bottom), "#A8B3C2", 1, new DoubleCollection { 2, 2 });
            AddLeptaGraphLine(new Point(chartBounds.Left, hoverY), new Point(chartBounds.Right, hoverY), "#A8B3C2", 1, new DoubleCollection { 2, 2 });
            LeptaThroughputCanvas.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = (Brush)FindResource("AccentBrush"),
                Stroke = Brushes.White,
                StrokeThickness = 1,
                Margin = new Thickness(hoverX - 4, hoverY - 4, 0, 0)
            });
            AddLeptaGraphLabel(
                $"{hoveredSeconds:F1}s • {hoveredTokensPerSecond:F0} tok/s",
                Math.Min(chartBounds.Right - 12, hoverX + 10),
                Math.Max(chartBounds.Top, hoverY - 22),
                emphasize: true);
        }
    }

    private bool TryInterpolateLeptaThroughput(double secondsSinceStart, out double tokensPerSecond)
    {
        tokensPerSecond = 0;
        if (leptaThroughputSamples.Count == 0)
        {
            return false;
        }

        if (secondsSinceStart <= leptaThroughputSamples[0].SecondsSinceStart)
        {
            tokensPerSecond = leptaThroughputSamples[0].TokensPerSecond;
            return true;
        }

        for (var index = 1; index < leptaThroughputSamples.Count; index++)
        {
            var previous = leptaThroughputSamples[index - 1];
            var current = leptaThroughputSamples[index];
            if (secondsSinceStart > current.SecondsSinceStart)
            {
                continue;
            }

            var span = Math.Max(0.001, current.SecondsSinceStart - previous.SecondsSinceStart);
            var progress = (secondsSinceStart - previous.SecondsSinceStart) / span;
            tokensPerSecond = previous.TokensPerSecond + ((current.TokensPerSecond - previous.TokensPerSecond) * progress);
            return true;
        }

        tokensPerSecond = leptaThroughputSamples[^1].TokensPerSecond;
        return true;
    }

    private Rect GetLeptaChartBounds()
    {
        var width = Math.Max(0, LeptaThroughputCanvas.ActualWidth);
        var height = Math.Max(0, LeptaThroughputCanvas.ActualHeight);
        return new Rect(30, 16, Math.Max(0, width - 82), Math.Max(0, height - 28));
    }

    private void AddLeptaGraphLine(Point start, Point end, string colorHex, double thickness, DoubleCollection? dashArray = null)
    {
        var line = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = (Brush)new BrushConverter().ConvertFromString(colorHex)!,
            StrokeThickness = thickness
        };
        if (dashArray is not null)
        {
            line.StrokeDashArray = dashArray;
        }

        LeptaThroughputCanvas.Children.Add(line);
    }

    private void AddLeptaGraphLabel(
        string text,
        double left,
        double top,
        bool emphasize = false,
        bool centerHorizontally = false,
        bool rightAlign = false,
        bool centerVertically = false)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = emphasize ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)FindResource(emphasize ? "PrimaryTextBrush" : "SecondaryTextBrush"),
            Background = emphasize ? (Brush)FindResource("PanelBackgroundBrush") : Brushes.Transparent,
            Padding = emphasize ? new Thickness(4, 2, 4, 2) : new Thickness(0)
        };

        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desiredSize = label.DesiredSize;
        if (centerHorizontally)
        {
            left -= desiredSize.Width / 2;
        }
        else if (rightAlign)
        {
            left -= desiredSize.Width;
        }

        if (centerVertically)
        {
            top -= desiredSize.Height / 2;
        }

        left = Math.Clamp(left, 0, Math.Max(0, LeptaThroughputCanvas.ActualWidth - desiredSize.Width));
        top = Math.Clamp(top, 0, Math.Max(0, LeptaThroughputCanvas.ActualHeight - desiredSize.Height));
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        LeptaThroughputCanvas.Children.Add(label);
    }

    private static void AnimateLeptaPanelOffset(FrameworkElement panelElement, double targetX, double targetY, int durationMilliseconds)
    {
        var (_, translateTransform) = EnsureLeptaPanelTransforms(panelElement);
        var duration = TimeSpan.FromMilliseconds(durationMilliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        translateTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(targetX, duration) { EasingFunction = easing });
        translateTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(targetY, duration) { EasingFunction = easing });
    }

    private static void SetLeptaPanelOffset(FrameworkElement panelElement, double x, double y)
    {
        var (_, translateTransform) = EnsureLeptaPanelTransforms(panelElement);
        translateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        translateTransform.X = x;
        translateTransform.Y = y;
    }

    private static void AnimateLeptaPanelToRest(FrameworkElement panelElement)
    {
        var (scaleTransform, translateTransform) = EnsureLeptaPanelTransforms(panelElement);
        var shadow = panelElement.Effect as DropShadowEffect ?? new DropShadowEffect
        {
            Color = Colors.Black,
            ShadowDepth = 0,
            BlurRadius = 0,
            Opacity = 0
        };
        panelElement.Effect = shadow;
        Panel.SetZIndex(panelElement, 0);

        var duration = TimeSpan.FromMilliseconds(140);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration) { EasingFunction = easing });
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration) { EasingFunction = easing });
        translateTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
        translateTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
        panelElement.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, duration) { EasingFunction = easing });
        shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
        shadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
    }

    private static void SetLeptaPanelToRest(FrameworkElement panelElement)
    {
        var (scaleTransform, translateTransform) = EnsureLeptaPanelTransforms(panelElement);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        panelElement.BeginAnimation(UIElement.OpacityProperty, null);
        scaleTransform.ScaleX = 1;
        scaleTransform.ScaleY = 1;
        translateTransform.X = 0;
        translateTransform.Y = 0;
        panelElement.Opacity = 1;
        Panel.SetZIndex(panelElement, 0);

        if (panelElement.Effect is DropShadowEffect shadow)
        {
            shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            shadow.BlurRadius = 0;
            shadow.Opacity = 0;
        }
    }

    private static FrameworkElement? FindDraggableLeptaPanel(DependencyObject? current, Guid panelId)
    {
        Border? match = null;
        while (current is not null)
        {
            if (current is Border border && border.Tag is Guid elementPanelId && elementPanelId == panelId)
            {
                match = border;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return match;
    }

    private void StartLeptaPanelDragFeedback(FrameworkElement? panelElement, Point dragStartPoint, UIElement? dragSource)
    {
        StopLeptaPanelDragFeedback();
        if (panelElement is null || dragSource is null || panelElement.ActualWidth <= 0 || panelElement.ActualHeight <= 0)
        {
            return;
        }

        var panelTopLeft = panelElement.TranslatePoint(new Point(0, 0), this);
        leptaDragGhostPointerOffset = new Point(
            Math.Clamp(dragStartPoint.X - panelTopLeft.X, 0, panelElement.ActualWidth),
            Math.Clamp(dragStartPoint.Y - panelTopLeft.Y, 0, panelElement.ActualHeight));

        var sourceBorder = panelElement as Border;
        var ghostBorder = new Border
        {
            Width = panelElement.ActualWidth,
            Height = panelElement.ActualHeight,
            CornerRadius = sourceBorder?.CornerRadius ?? new CornerRadius(12),
            BorderBrush = sourceBorder?.BorderBrush,
            BorderThickness = sourceBorder?.BorderThickness ?? new Thickness(1),
            Background = new VisualBrush(panelElement)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                Opacity = 0.98
            },
            Opacity = 0.88,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 26,
                Direction = 270,
                ShadowDepth = 14,
                Opacity = 0.24
            },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    new ScaleTransform(1.03, 1.03),
                    new TranslateTransform(0, -8)
                }
            }
        };

        leptaDragGhostPopup = new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.Absolute,
            PopupAnimation = PopupAnimation.None,
            StaysOpen = true,
            IsHitTestVisible = false,
            Child = ghostBorder
        };
        leptaActiveDragSource = dragSource;
        dragSource.GiveFeedback += LeptaPanelDragSource_GiveFeedback;
        leptaDragGhostPopup.IsOpen = true;
        UpdateLeptaPanelDragGhostPosition();
    }

    private void StopLeptaPanelDragFeedback()
    {
        if (leptaActiveDragSource is not null)
        {
            leptaActiveDragSource.GiveFeedback -= LeptaPanelDragSource_GiveFeedback;
            leptaActiveDragSource = null;
        }

        if (leptaDragGhostPopup is not null)
        {
            leptaDragGhostPopup.IsOpen = false;
            leptaDragGhostPopup.Child = null;
            leptaDragGhostPopup = null;
        }

        leptaDragGhostPointerOffset = null;
    }

    private void LeptaPanelDragSource_GiveFeedback(object? sender, GiveFeedbackEventArgs e)
    {
        UpdateLeptaPanelDragGhostPosition();
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void UpdateLeptaPanelDragGhostPosition()
    {
        if (leptaDragGhostPopup is null || leptaDragGhostPointerOffset is not Point pointerOffset)
        {
            return;
        }

        var cursorScreenPosition = GetCursorScreenPositionInDips();
        leptaDragGhostPopup.HorizontalOffset = cursorScreenPosition.X - pointerOffset.X;
        leptaDragGhostPopup.VerticalOffset = cursorScreenPosition.Y - pointerOffset.Y - 10;
    }

    private Point GetCursorScreenPositionInDips()
    {
        if (!GetCursorPos(out var nativePoint))
        {
            return PointToScreen(Mouse.GetPosition(this));
        }

        var screenPoint = new Point(nativePoint.X, nativePoint.Y);
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } compositionTarget)
        {
            screenPoint = compositionTarget.TransformFromDevice.Transform(screenPoint);
        }

        return screenPoint;
    }

    private void UpdateLeptaPanelDropPreview(FrameworkElement targetElement, bool insertAfter)
    {
        var direction = insertAfter ? -1 : 1;
        if (ReferenceEquals(leptaDragPreviewElement, targetElement) && leptaDragPreviewDirection == direction)
        {
            return;
        }

        ClearLeptaPanelDropPreview();
        leptaDragPreviewElement = targetElement;
        leptaDragPreviewDirection = direction;
        AnimateLeptaPanelDropPreviewVisual(targetElement, direction * 18);
    }

    private void ClearLeptaPanelDropPreview()
    {
        if (leptaDragPreviewElement is not null)
        {
            AnimateLeptaPanelDropPreviewVisual(leptaDragPreviewElement, 0);
        }

        leptaDragPreviewElement = null;
        leptaDragPreviewDirection = 0;
    }

    private static void AnimateLeptaPanelDragVisual(FrameworkElement? panelElement, bool lifted)
    {
        if (panelElement is null)
        {
            return;
        }

        var (scaleTransform, translateTransform) = EnsureLeptaPanelTransforms(panelElement);

        var shadow = panelElement.Effect as DropShadowEffect ?? new DropShadowEffect
        {
            Color = Colors.Black,
            ShadowDepth = 0,
            BlurRadius = 0,
            Opacity = 0
        };
        panelElement.Effect = shadow;
        Panel.SetZIndex(panelElement, lifted ? 12 : 0);

        var duration = TimeSpan.FromMilliseconds(lifted ? 80 : 110);
        var easing = new QuadraticEase { EasingMode = lifted ? EasingMode.EaseOut : EasingMode.EaseInOut };

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(lifted ? 1.02 : 1.0, duration) { EasingFunction = easing });
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(lifted ? 1.02 : 1.0, duration) { EasingFunction = easing });
        translateTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(lifted ? -3 : 0, duration) { EasingFunction = easing });
        panelElement.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(lifted ? 0.93 : 1.0, duration) { EasingFunction = easing });
        shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(lifted ? 18 : 0, duration) { EasingFunction = easing });
        shadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(lifted ? 0.35 : 0, duration) { EasingFunction = easing });
    }

    private static void AnimateLeptaPanelDropPreviewVisual(FrameworkElement? panelElement, double targetX)
    {
        if (panelElement is null)
        {
            return;
        }

        var (_, translateTransform) = EnsureLeptaPanelTransforms(panelElement);
        translateTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(targetX == 0 ? 120 : 90))
            {
                EasingFunction = new CubicEase { EasingMode = targetX == 0 ? EasingMode.EaseOut : EasingMode.EaseInOut }
            });
        panelElement.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(targetX == 0 ? 1.0 : 0.9, TimeSpan.FromMilliseconds(targetX == 0 ? 120 : 90))
            {
                EasingFunction = new CubicEase { EasingMode = targetX == 0 ? EasingMode.EaseOut : EasingMode.EaseInOut }
            });
    }

    private static (ScaleTransform ScaleTransform, TranslateTransform TranslateTransform) EnsureLeptaPanelTransforms(FrameworkElement panelElement)
    {
        panelElement.RenderTransformOrigin = new Point(0.5, 0.5);

        if (panelElement.RenderTransform is TransformGroup transformGroup
            && !transformGroup.IsFrozen)
        {
            var existingScale = transformGroup.Children.OfType<ScaleTransform>().FirstOrDefault(transform => !transform.IsFrozen);
            var existingTranslate = transformGroup.Children.OfType<TranslateTransform>().FirstOrDefault(transform => !transform.IsFrozen);
            if (existingScale is not null && existingTranslate is not null)
            {
                return (existingScale, existingTranslate);
            }
        }

        ScaleTransform? scaleTransform = null;
        TranslateTransform? translateTransform = null;
        var mutableGroup = new TransformGroup();

        switch (panelElement.RenderTransform)
        {
            case TransformGroup existingGroup:
                foreach (var child in existingGroup.Children)
                {
                    switch (child)
                    {
                        case ScaleTransform existingScale when scaleTransform is null:
                            scaleTransform = new ScaleTransform(existingScale.ScaleX, existingScale.ScaleY);
                            mutableGroup.Children.Add(scaleTransform);
                            break;
                        case TranslateTransform existingTranslate when translateTransform is null:
                            translateTransform = new TranslateTransform(existingTranslate.X, existingTranslate.Y);
                            mutableGroup.Children.Add(translateTransform);
                            break;
                        default:
                            mutableGroup.Children.Add(child.CloneCurrentValue());
                            break;
                    }
                }
                break;
            case ScaleTransform existingScale:
                scaleTransform = new ScaleTransform(existingScale.ScaleX, existingScale.ScaleY);
                mutableGroup.Children.Add(scaleTransform);
                break;
            case TranslateTransform existingTranslate:
                translateTransform = new TranslateTransform(existingTranslate.X, existingTranslate.Y);
                mutableGroup.Children.Add(translateTransform);
                break;
            case Transform existingTransform when existingTransform.Value != Matrix.Identity:
                mutableGroup.Children.Add(existingTransform.CloneCurrentValue());
                break;
        }

        if (scaleTransform is null)
        {
            scaleTransform = new ScaleTransform(1, 1);
            mutableGroup.Children.Insert(0, scaleTransform);
        }

        if (translateTransform is null)
        {
            translateTransform = new TranslateTransform();
            mutableGroup.Children.Add(translateTransform);
        }

        panelElement.RenderTransform = mutableGroup;
        return (scaleTransform, translateTransform);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static Color ParseColor(string? accentColorHex)
        => (Color)ColorConverter.ConvertFromString(LeptaPanelAccentPalette.Normalize(accentColorHex))!;

    private static bool TryParseColor(string? accentColorHex, out Color color)
    {
        try
        {
            color = ParseColor(accentColorHex);
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }

    private static string ColorToHex(Color color)
        => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

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
            CollapseNavigationCheckBox.IsChecked = true;
            VerboseVllmLogsSettingsCheckBox.IsChecked = modelsController.IsVerboseVllmLogsEnabled;
            SelectLeptaDocumentTokenLimit(leptaController.CaptureSettings().DocumentTokenLimit);
            SelectLeptaDocumentTrimMode(leptaController.CaptureSettings().DocumentTrimMode);
            SettingsDefaultDashboardCombo.SelectedItem = SettingsDefaultDashboardCombo.Items
                .OfType<LeptaDashboardReference>()
                .FirstOrDefault(item => string.Equals(item.Id, leptaController.CurrentDashboardId, StringComparison.OrdinalIgnoreCase));
            SettingsDefaultServerCombo.SelectedItem = SettingsDefaultServerCombo.Items
                .OfType<VllmServerConfiguration>()
                .FirstOrDefault(item => string.Equals(item.Id, modelsController.SelectedServerId, StringComparison.OrdinalIgnoreCase));
            SelectUiFontSize(FontSize);
        }
        finally
        {
            suppressSettingsChangeHandlers = false;
        }

        UpdateLeptaDocumentTrimSummary();
    }

    private void ActionLogStream_EntryPublished(object? sender, ActionLogEntry entry)
    {
        if (Dispatcher.CheckAccess())
        {
            AddOverlayEntry(entry);
            return;
        }

        _ = Dispatcher.InvokeAsync(() => AddOverlayEntry(entry));
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
    {
        if (ActionLogOverlayRoot is null || EnableActionLogOverlayCheckBox is null)
        {
            return;
        }

        ActionLogOverlayRoot.Visibility = EnableActionLogOverlayCheckBox.IsChecked == true && actionLogOverlayEntries.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

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
        if (AddClipboardFormatListener(hwndSource.Handle))
        {
            isClipboardListenerRegistered = true;
        }
        else
        {
            logger.Log(nameof(MainWindow), $"Failed to register clipboard listener. errorCode={Marshal.GetLastWin32Error()}.");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == GlobalHotkeyId)
        {
            _ = Dispatcher.InvokeAsync(HandleGlobalHotkeyAsync);
            handled = true;
        }

        if (msg == WmClipboardUpdate)
        {
            _ = Dispatcher.InvokeAsync(HandleClipboardUpdatedAsync);
        }

        return IntPtr.Zero;
    }

    private void QueueClipboardCachePrefillFromCurrentClipboard()
    {
        if (EnableClipboardCachePrefillCheckBox?.IsChecked != true)
        {
            return;
        }

        if (TryGetClipboardTextForCachePrefill(out var clipboardText))
        {
            pendingClipboardCacheText = clipboardText;
            clipboardCachePrefillTimer.Stop();
            clipboardCachePrefillTimer.Start();
        }
    }

    private async void ClipboardCachePrefillTimer_Tick(object? sender, EventArgs e)
    {
        clipboardCachePrefillTimer.Stop();
        var clipboardText = pendingClipboardCacheText;
        pendingClipboardCacheText = null;
        if (string.IsNullOrWhiteSpace(clipboardText) || leptaController is null || EnableClipboardCachePrefillCheckBox?.IsChecked != true)
        {
            return;
        }

        await leptaController.RequestClipboardCachePrefillAsync(clipboardText);
    }

    private Task HandleClipboardUpdatedAsync()
    {
        QueueClipboardCachePrefillFromCurrentClipboard();
        return Task.CompletedTask;
    }

    private bool TryGetClipboardTextForCachePrefill(out string clipboardText)
    {
        clipboardText = string.Empty;
        try
        {
            if (!Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                return false;
            }

            clipboardText = Clipboard.GetText(TextDataFormat.UnicodeText);
            return !string.IsNullOrWhiteSpace(clipboardText);
        }
        catch (Exception exception)
        {
            logger.Log(nameof(MainWindow), $"Clipboard cache prefill skipped because the clipboard could not be read. reason={exception.Message}");
            return false;
        }
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

    private async Task CancelInFlightOperationsAsync()
    {
        var timeout = TimeSpan.FromSeconds(3);
        var shutdownTasks = new List<Task>();

        if (chatController?.IsBusy == true)
        {
            shutdownTasks.Add(chatController.CancelForShutdownAsync(timeout));
        }

        if (leptaController?.IsBusy == true)
        {
            shutdownTasks.Add(leptaController.CancelForShutdownAsync(timeout));
        }

        if (modelsController?.IsBusy == true)
        {
            shutdownTasks.Add(modelsController.CancelForShutdownAsync(timeout));
        }

        if (shutdownTasks.Count == 0)
        {
            return;
        }

        logger.Log(nameof(MainWindow), $"Waiting for {shutdownTasks.Count} in-flight operation(s) to cancel during shutdown.");
        actionLogStream.Publish(nameof(MainWindow), "Cancelling in-flight work before shutdown.", ActionLogLevel.Warning);
        await Task.WhenAll(shutdownTasks);
    }

    private void OpenOverlay(FrameworkElement overlay, Control? focusTarget = null)
    {
        HideLeptaPanelPreview();
        PushOverlayWebViewSuppression();
        lastFocusedElementBeforeOverlay = FocusManager.GetFocusedElement(this);
        overlay.Visibility = Visibility.Visible;
        if (focusTarget is null)
        {
            return;
        }

        focusTarget.Focus();
        if (focusTarget is TextBox textBox)
        {
            textBox.Select(textBox.Text.Length, 0);
        }
    }

    private void CloseOverlay(FrameworkElement overlay)
    {
        if (overlay == LeptaPanelPreviewOverlay)
        {
            HideLeptaPanelPreview();
        }
        else
        {
            overlay.Visibility = Visibility.Collapsed;
            PopOverlayWebViewSuppression();
        }

        if (overlay == AdvancedConfigurationPanel)
        {
            modelsController?.CloseAdvancedConfiguration();
        }
        else if (overlay == LeptaPanelEditorPanel)
        {
            leptaController?.ClearPanelEditor();
        }

        RestoreOverlayFocus();
    }

    private void PushOverlayWebViewSuppression()
    {
        if (overlaySuppressionDepth == 0)
        {
            WebViewAirspaceManager.PushSuppression();
        }

        overlaySuppressionDepth++;
    }

    private void PopOverlayWebViewSuppression()
    {
        if (overlaySuppressionDepth == 0)
        {
            return;
        }

        overlaySuppressionDepth--;
        if (overlaySuppressionDepth == 0)
        {
            WebViewAirspaceManager.PopSuppression();
        }
    }

    private void RestoreOverlayFocus()
    {
        if (lastFocusedElementBeforeOverlay is IInputElement focusTarget)
        {
            Keyboard.Focus(focusTarget);
        }

        lastFocusedElementBeforeOverlay = null;
    }

    private void OverlayBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource) || sender is not FrameworkElement overlay)
        {
            return;
        }

        CloseOverlay(overlay);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (GeneralInstructionPanel.Visibility == Visibility.Visible)
            {
                CloseOverlay(GeneralInstructionPanel);
                e.Handled = true;
                return;
            }

            if (AdvancedConfigurationPanel.Visibility == Visibility.Visible)
            {
                CloseOverlay(AdvancedConfigurationPanel);
                e.Handled = true;
                return;
            }

            if (LeptaPanelEditorPanel.Visibility == Visibility.Visible)
            {
                CloseOverlay(LeptaPanelEditorPanel);
                e.Handled = true;
                return;
            }

            if (LeptaPanelPreviewOverlay.Visibility == Visibility.Visible)
            {
                HideLeptaPanelPreview();
                e.Handled = true;
                return;
            }
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.D1:
                LeptaTabButton.IsChecked = true;
                e.Handled = true;
                break;
            case Key.D2:
                ModelsTabButton.IsChecked = true;
                e.Handled = true;
                break;
            case Key.D3:
                SettingsTabButton.IsChecked = true;
                e.Handled = true;
                break;
            case Key.D4:
                ChatTabButton.IsChecked = true;
                e.Handled = true;
                break;
            case Key.R when LeptaView.Visibility == Visibility.Visible && RunLeptaButton.IsEnabled:
                _ = Dispatcher.InvokeAsync(async () => await leptaController!.RunFromClipboardAsync());
                e.Handled = true;
                break;
            case Key.N when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && ChatViewControl.Visibility == Visibility.Visible:
                chatController?.StartNewChat();
                e.Handled = true;
                break;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    private readonly record struct NativePoint(int X, int Y);

    private sealed record LeptaThroughputSample(double SecondsSinceStart, double TokensPerSecond);

    private sealed record LeptaPanelDragSlot(Guid PanelId, FrameworkElement Element, Point Origin, Size Size);
}