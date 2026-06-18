namespace LEPTA.vLLM.Models;

/// <summary>
/// Mutable, non-persisted runtime/UI state for a <see cref="VllmServerConfiguration"/>.
/// Holds the transient probe/lifecycle status shown in the UI (status dot, status pill,
/// details). Kept separate from the persisted configuration record so storage is not
/// coupled to UI state; <see cref="VllmServerConfiguration.Runtime"/> is the only
/// <c>[JsonIgnore]</c> bridge onto the record.
/// </summary>
public sealed class VllmServerRuntimeState
{
    public ServerStatusKind StatusKind { get; set; } = ServerStatusKind.Unknown;

    public string StatusText { get; set; } = "Not checked";

    public string StatusDetails { get; set; } = "Select the profile or use Check server to verify it.";
}
