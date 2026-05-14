namespace LEPTA.vLLM.Models;

public enum VllmServerProbeStatus
{
    Success,
    EmptyEndpoint,
    InvalidEndpoint,
    Unreachable,
    HttpError,
    InvalidResponse,
    EmptyModelList
}

public sealed record VllmServerValidationResult(
    bool IsValid,
    string? NormalizedEndpoint,
    string Message);

public sealed record VllmServerProbeResult(
    VllmServerProbeStatus Status,
    string Message,
    string? NormalizedEndpoint,
    IReadOnlyList<string> ModelNames)
{
    public bool IsSuccess => Status == VllmServerProbeStatus.Success;

    public string? FirstModelName => ModelNames.FirstOrDefault();

    public static VllmServerProbeResult Success(string normalizedEndpoint, IReadOnlyList<string> modelNames, string message)
        => new(VllmServerProbeStatus.Success, message, normalizedEndpoint, modelNames);

    public static VllmServerProbeResult Failure(VllmServerProbeStatus status, string message, string? normalizedEndpoint = null)
        => new(status, message, normalizedEndpoint, Array.Empty<string>());
}
