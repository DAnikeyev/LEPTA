namespace LEPTA.vLLM.Models;

public sealed record VllmRequestOptions
{
    public bool EnableThinking { get; init; }

    public bool OmitReasoningFromOutput { get; init; }

    public string? CacheSalt { get; init; }
}
