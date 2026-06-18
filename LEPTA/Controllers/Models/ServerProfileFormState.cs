namespace LEPTA.Controllers.Models;

/// <summary>
/// Plain, WPF-free snapshot of the editable fields on the Models configuration form.
/// This is the view-model-shaped DTO that decouples <see cref="ModelsController"/> from
/// concrete controls: the controller reads controls into a <see cref="ServerProfileFormState"/>
/// and writes one back out, while all server &harr; form mapping lives in
/// <see cref="ServerProfileFormMapper"/> and is unit-testable without WPF.
/// </summary>
///
/// <remarks>
/// Nullable value types mean &quot;field not supplied / leave the server value unchanged&quot;,
/// mirroring the previous <c>if (TryParse(...)) server.X = ...</c> conditional semantics so the
/// migration is behaviour-preserving.
/// </remarks>
public sealed class ServerProfileFormState
{
    public string Name { get; set; } = string.Empty;

    public bool UseExistingHttpServer { get; set; }

    public string HttpServerAddress { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>Cleared to null when blank, matching the trimmed-on-edit behaviour.</summary>
    public string? ServedModelName { get; set; }

    /// <summary>Empty when blank, trimmed otherwise.</summary>
    public string DockerImage { get; set; } = string.Empty;

    /// <summary>
    /// Populated by <see cref="ServerProfileFormMapper.Build"/> for display, but
    /// <see cref="ServerProfileFormMapper.Apply"/> does not write it back: the controller owns
    /// the local-model-path change (it triggers a metadata rescan side effect).
    /// </summary>
    public string? LocalModelPath { get; set; }

    public int? HostPort { get; set; }

    /// <summary>Combo selection; null means no selection (leave server value unchanged).</summary>
    public string? DType { get; set; }

    public double? GpuMemoryUtilization { get; set; }

    public double? GpuVramGb { get; set; }

    public int? MaxModelLength { get; set; }

    public int? ReadyTimeoutMinutes { get; set; }

    public double? CpuOffloadGb { get; set; }

    public string? WeightQuantization { get; set; }

    public int? TensorParallelSize { get; set; }

    public string? KCacheQuantization { get; set; }

    public string? VCacheQuantization { get; set; }

    /// <summary>Parsed from the &quot;true&quot;/&quot;false&quot; combo; null means no selection.</summary>
    public bool? EnableTokenizersParallelism { get; set; }

    public string AdditionalVllmArguments { get; set; } = string.Empty;

    public int? MaxNumSeqs { get; set; }

    public bool EnableVerboseLogs { get; set; }
}
