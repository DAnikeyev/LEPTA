namespace LEPTA.vLLM.Benchmarking;

public static class VllmAxisBenchmarkMetrics
{
    public static VllmBenchmarkSummary Summarize(IReadOnlyList<VllmBenchmarkTryMetrics> tries)
    {
        ArgumentNullException.ThrowIfNull(tries);
        if (tries.Count == 0)
        {
            throw new ArgumentException("At least one try is required.", nameof(tries));
        }

        var first = tries[0];
        return new VllmBenchmarkSummary(
            first.Axis switch
            {
                VllmBenchmarkAxis.Baseline => "baseline",
                VllmBenchmarkAxis.GpuMemoryUtilization => "gpu-memory-utilization",
                VllmBenchmarkAxis.MaxModelLength => "max-model-length",
                VllmBenchmarkAxis.MaxNumSeqs => "max-num-seqs",
                VllmBenchmarkAxis.CpuOffloadGb => "cpu-offload-gb",
                _ => first.Axis.ToString()
            },
            first.Step,
            first.GpuMemoryUtilization,
            first.MaxModelLength,
            first.MaxNumSeqs,
            first.CpuOffloadGb,
            tries.Count,
            Math.Round(tries.Average(item => item.AvgTimeToFirstTokenSeconds), 4, MidpointRounding.AwayFromZero),
            Math.Round(tries.Average(item => item.PeakTokensPerSecond), 4, MidpointRounding.AwayFromZero),
            Math.Round(tries.Average(item => item.AvgTokensPerSecond), 4, MidpointRounding.AwayFromZero),
            AverageNullable(tries.Select(item => item.PeakGpuMemoryUsedMb)),
            AverageNullable(tries.Select(item => item.PeakContainerMemoryUsedMb)));
    }

    public static VllmBenchmarkTryMetrics CreateTryMetrics(
        string scenarioId,
        VllmBenchmarkAxis axis,
        int step,
        int tryIndex,
        double gpuMemoryUtilization,
        int maxModelLength,
        int maxNumSeqs,
        int cpuOffloadGb,
        int documentTokens,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        IReadOnlyList<VllmBenchmarkRequestTrace> traces,
        double? peakGpuMemoryUsedMb,
        double? peakContainerMemoryUsedMb)
    {
        ArgumentNullException.ThrowIfNull(traces);
        if (traces.Count == 0)
        {
            throw new ArgumentException("At least one request trace is required.", nameof(traces));
        }

        var traceList = traces.OrderBy(item => item.RequestStartedAt).ToArray();
        var ttfts = traceList
            .Where(item => item.FirstTokenAt is not null)
            .Select(item => (item.FirstTokenAt!.Value - item.RequestStartedAt).TotalSeconds)
            .ToArray();
        if (ttfts.Length == 0)
        {
            throw new InvalidOperationException("No first-token timestamps were captured.");
        }

        var batchFirstTokenAt = traceList
            .Where(item => item.FirstTokenAt is not null)
            .Min(item => item.FirstTokenAt!.Value);
        var firstFinishedAt = traceList.Min(item => item.CompletedAt);
        var tokensUntilFirstFinished = traceList.Sum(item => TokensUntil(item, firstFinishedAt));
        var peakWindowSeconds = Math.Max((firstFinishedAt - batchFirstTokenAt).TotalSeconds, 0.001d);
        var scenarioSeconds = Math.Max((firstFinishedAt - startedAt).TotalSeconds, 0.001d);

        return new VllmBenchmarkTryMetrics(
            scenarioId,
            axis,
            step,
            tryIndex,
            gpuMemoryUtilization,
            maxModelLength,
            maxNumSeqs,
            cpuOffloadGb,
            documentTokens,
            traceList.Length,
            Math.Round(ttfts.Average(), 4, MidpointRounding.AwayFromZero),
            Math.Round(tokensUntilFirstFinished / peakWindowSeconds, 4, MidpointRounding.AwayFromZero),
            Math.Round(tokensUntilFirstFinished / scenarioSeconds, 4, MidpointRounding.AwayFromZero),
            peakGpuMemoryUsedMb,
            peakContainerMemoryUsedMb,
            startedAt,
            completedAt,
            traceList);
    }

    private static int TokensUntil(VllmBenchmarkRequestTrace trace, DateTimeOffset cutoff)
    {
        var piece = trace.StreamPieces
            .Where(item => item.Timestamp <= cutoff)
            .OrderBy(item => item.Timestamp)
            .LastOrDefault();

        return piece?.CumulativeCompletionTokens
               ?? (trace.CompletedAt <= cutoff ? trace.CompletionTokens : 0);
    }

    private static double? AverageNullable(IEnumerable<double?> values)
    {
        var defined = values.Where(item => item.HasValue).Select(item => item!.Value).ToArray();
        return defined.Length == 0
            ? null
            : Math.Round(defined.Average(), 4, MidpointRounding.AwayFromZero);
    }
}