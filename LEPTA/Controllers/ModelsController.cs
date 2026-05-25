using System.Collections.ObjectModel;
using LEPTA.Controllers.Views;
using LEPTA.Shared.Diagnostics;
using LEPTA.vLLM.Configuration;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Controllers;

internal sealed partial class ModelsController
{
    private readonly ModelsSelectionViews selection;
    private readonly ModelsConfigurationViews config;
    private readonly ModelsDeploymentViews deploy;
    private readonly ObservableCollection<VllmServerConfiguration> servers;
    private readonly ObservableCollection<VllmServerConfiguration> connectedServers = [];
    private readonly VllmDeploymentService deploymentService;
    private readonly ILeptaLogger logger;
    private readonly IActionLogEventStream actionLog;
    private readonly string composeDirectory;

    private bool isLoadingConfiguration;
    private bool isSynchronizingSelection;
    private VllmServerConfiguration? activeServer;
    private VllmServerConfiguration? activeActionServer;
    private bool activeActionCanStopServer;
    private string? activeActionMessage;
    private CancellationTokenSource? currentActionCancellation;
    private TaskCompletionSource<bool>? activeActionCompletion;

    public event Action? StateChanged;

    public ModelsController(
        ModelsControllerViews views,
        string composeDirectory,
        ModelsControllerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(views);
        options ??= new ModelsControllerOptions();

        selection = views.Selection;
        config = views.Configuration;
        deploy = views.Deployment;
        this.composeDirectory = composeDirectory;
        logger = options.Logger ?? NullLeptaLogger.Instance;
        actionLog = options.ActionLog ?? NullActionLogEventStream.Instance;
        deploymentService = options.DeploymentService ?? new VllmDeploymentService(logger: logger);

        var seededServers = options.InitialServers?.ToList() ?? VllmDefaults.CreateServers().ToList();
        if (seededServers.Count == 0)
        {
            seededServers = VllmDefaults.CreateServers().ToList();
        }

        servers = new ObservableCollection<VllmServerConfiguration>(seededServers);
        selection.ModelsList.ItemsSource = servers;
        selection.ChatServerCombo.ItemsSource = connectedServers;
        selection.ModelNoteText.Text = VllmDefaults.VllmModelNote;
        InitializeServerStatuses();
        RefreshConnectedServers();
        SelectServer(options.SelectedServerId);
        SetDockerStatusState("Not checked yet", "Refresh to verify Docker Desktop and the active engine.", Theming.ThemeResourceKeys.SecondaryTextBrush);
        UpdateActionButtons();
    }

    public VllmServerConfiguration? SelectedServer => activeServer
        ?? selection.ModelsList.SelectedItem as VllmServerConfiguration
        ?? selection.ChatServerCombo.SelectedItem as VllmServerConfiguration
        ?? servers.FirstOrDefault();

    public string? SelectedServerId => SelectedServer?.Id;

    public IEnumerable<VllmServerConfiguration> Servers => servers;

    public IEnumerable<VllmServerConfiguration> ConnectedServers => connectedServers;

    public bool IsBusy => currentActionCancellation is not null;

    public bool IsVerboseVllmLogsEnabled => servers.Any(server => server.EnableVerboseLogs);

    public void ApplyVerboseLogsSetting(bool enabled, bool publishAction = true)
    {
        isLoadingConfiguration = true;
        try
        {
            foreach (var server in servers)
            {
                server.EnableVerboseLogs = enabled;
            }

            config.VerboseLogsCheckBox.IsChecked = enabled;
            selection.ModelsList.Items.Refresh();
            selection.ChatServerCombo.Items.Refresh();
            RefreshConnectedServers();
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

    private void OnStateChanged() => StateChanged?.Invoke();
}
