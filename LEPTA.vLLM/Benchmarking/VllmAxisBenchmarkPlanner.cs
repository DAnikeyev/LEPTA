namespace LEPTA.vLLM.Benchmarking;

public static class VllmAxisBenchmarkPlanner
{
    public static IReadOnlyList<VllmAxisBenchmarkPoint> CreateSweep(
        double baselineGpuMemoryUtilization,
        int baselineMaxModelLength,
        int baselineMaxNumSeqs,
        int baselineCpuOffloadGb,
        int stepsPerDirection = 5)
    {
        if (stepsPerDirection < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepsPerDirection));
        }

        var points = new List<VllmAxisBenchmarkPoint>
        {
            new(
                VllmBenchmarkAxis.Baseline,
                0,
                RoundGpu(baselineGpuMemoryUtilization),
                baselineMaxModelLength,
                baselineMaxNumSeqs,
                Math.Max(0, baselineCpuOffloadGb))
        };

        var axes = new Func<int, VllmAxisBenchmarkPoint>[]
        {
            step => new(
                VllmBenchmarkAxis.GpuMemoryUtilization,
                step,
                RoundGpu(Math.Clamp(baselineGpuMemoryUtilization + (step * 0.02d), 0.02d, 0.99d)),
                baselineMaxModelLength,
                baselineMaxNumSeqs,
                Math.Max(0, baselineCpuOffloadGb)),
            step => new(
                VllmBenchmarkAxis.MaxModelLength,
                step,
                RoundGpu(baselineGpuMemoryUtilization),
                Math.Max(1024, baselineMaxModelLength + (step * 1000)),
                baselineMaxNumSeqs,
                Math.Max(0, baselineCpuOffloadGb)),
            step => new(
                VllmBenchmarkAxis.MaxNumSeqs,
                step,
                RoundGpu(baselineGpuMemoryUtilization),
                baselineMaxModelLength,
                Math.Max(1, baselineMaxNumSeqs + step),
                Math.Max(0, baselineCpuOffloadGb)),
            step => new(
                VllmBenchmarkAxis.CpuOffloadGb,
                step,
                RoundGpu(baselineGpuMemoryUtilization),
                baselineMaxModelLength,
                baselineMaxNumSeqs,
                Math.Max(0, baselineCpuOffloadGb + (step * 2)))
        };

        for (var absoluteStep = 1; absoluteStep <= stepsPerDirection; absoluteStep++)
        {
            foreach (var axisFactory in axes)
            {
                AddPointIfUnique(points, axisFactory(absoluteStep));
                AddPointIfUnique(points, axisFactory(-absoluteStep));
            }
        }

        return points;
    }

    private static void AddPointIfUnique(
        ICollection<VllmAxisBenchmarkPoint> points,
        VllmAxisBenchmarkPoint point)
    {
        if (!points.Any(existing => AreEquivalent(existing, point)))
        {
            points.Add(point);
        }
    }

    private static bool AreEquivalent(VllmAxisBenchmarkPoint left, VllmAxisBenchmarkPoint right)
        => left.GpuMemoryUtilization.Equals(right.GpuMemoryUtilization)
           && left.MaxModelLength == right.MaxModelLength
           && left.MaxNumSeqs == right.MaxNumSeqs
           && left.CpuOffloadGb == right.CpuOffloadGb;

    private static double RoundGpu(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}