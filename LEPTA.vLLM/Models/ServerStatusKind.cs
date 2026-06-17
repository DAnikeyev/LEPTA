namespace LEPTA.vLLM.Models;

/// <summary>
/// Runtime UI status of a server profile. Not persisted. Replaces the previous
/// magic-string <c>UiStatusKind</c> values to avoid case/typo bugs.
/// </summary>
public enum ServerStatusKind
{
    Unknown,
    Ready,
    Warning,
    Busy,
    Error
}
