namespace LEPTA.vLLM.Benchmarking;

public enum VllmBenchmarkAxis
{
    Baseline,
    GpuMemoryUtilization,
    MaxModelLength,
    MaxNumSeqs,
    CpuOffloadGb
}

public sealed record VllmAxisBenchmarkPoint(
    VllmBenchmarkAxis Axis,
    int Step,
    double GpuMemoryUtilization,
    int MaxModelLength,
    int MaxNumSeqs,
    int CpuOffloadGb)
{
    public string AxisName => Axis switch
    {
        VllmBenchmarkAxis.Baseline => "baseline",
        VllmBenchmarkAxis.GpuMemoryUtilization => "gpu-memory-utilization",
        VllmBenchmarkAxis.MaxModelLength => "max-model-length",
        VllmBenchmarkAxis.MaxNumSeqs => "max-num-seqs",
        VllmBenchmarkAxis.CpuOffloadGb => "cpu-offload-gb",
        _ => Axis.ToString()
    };

    public string Label => Step == 0 ? AxisName : $"{AxisName}{(Step > 0 ? "+" : string.Empty)}{Step}";
}

public sealed record VllmBenchmarkRequestTrace(
    string RequestName,
    DateTimeOffset RequestStartedAt,
    DateTimeOffset? FirstTokenAt,
    DateTimeOffset CompletedAt,
    int PromptTokens,
    int CompletionTokens,
    IReadOnlyList<VllmBenchmarkStreamPiece> StreamPieces);

public sealed record VllmBenchmarkStreamPiece(DateTimeOffset Timestamp, string Text, int CumulativeCompletionTokens);

public sealed record VllmBenchmarkTryMetrics(
    string ScenarioId,
    VllmBenchmarkAxis Axis,
    int Step,
    int TryIndex,
    double GpuMemoryUtilization,
    int MaxModelLength,
    int MaxNumSeqs,
    int CpuOffloadGb,
    int DocumentTokens,
    int SharedRequestCount,
    double AvgTimeToFirstTokenSeconds,
    double PeakTokensPerSecond,
    double AvgTokensPerSecond,
    double? PeakGpuMemoryUsedMb,
    double? PeakContainerMemoryUsedMb,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<VllmBenchmarkRequestTrace> Traces);

public sealed record VllmBenchmarkSummary(
    string AxisName,
    int Step,
    double GpuMemoryUtilization,
    int MaxModelLength,
    int MaxNumSeqs,
    int CpuOffloadGb,
    int TryCount,
    double AvgTimeToFirstTokenSeconds,
    double PeakTokensPerSecond,
    double AvgTokensPerSecond,
    double? PeakGpuMemoryUsedMb,
    double? PeakContainerMemoryUsedMb);