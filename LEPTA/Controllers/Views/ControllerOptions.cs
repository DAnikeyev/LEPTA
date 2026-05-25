using LEPTA.Shared.Diagnostics;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Controllers.Views;

internal sealed class ModelsControllerOptions
{
    public VllmDeploymentService? DeploymentService { get; init; }

    public ILeptaLogger? Logger { get; init; }

    public IActionLogEventStream? ActionLog { get; init; }

    public IEnumerable<VllmServerConfiguration>? InitialServers { get; init; }

    public string? SelectedServerId { get; init; }
}

internal sealed class ChatControllerOptions
{
    public ILeptaLogger? Logger { get; init; }

    public IActionLogEventStream? ActionLog { get; init; }
}

internal sealed class LeptaControllerOptions
{
    public ILeptaLogger? Logger { get; init; }

    public IActionLogEventStream? ActionLog { get; init; }
}
