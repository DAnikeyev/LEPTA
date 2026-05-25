using LEPTA.vLLM.Benchmarking;

namespace LEPTA.Tests;

[TestFixture]
public sealed class VllmAxisBenchmarkTests
{
    [Test]
    public void CreateSweep_ProducesBaselineAndAxisOnlyVariations()
    {
        var points = VllmAxisBenchmarkPlanner.CreateSweep(0.92d, 8192, 5, 0);

        Assert.That(points, Has.Count.EqualTo(34));
        Assert.That(points.Count(item => item.Axis == VllmBenchmarkAxis.Baseline), Is.EqualTo(1));
        Assert.That(points.Count(item => item.Axis == VllmBenchmarkAxis.GpuMemoryUtilization), Is.EqualTo(9));
        Assert.That(points.Count(item => item.Axis == VllmBenchmarkAxis.MaxModelLength), Is.EqualTo(10));
        Assert.That(points.Count(item => item.Axis == VllmBenchmarkAxis.MaxNumSeqs), Is.EqualTo(9));
        Assert.That(points.Count(item => item.Axis == VllmBenchmarkAxis.CpuOffloadGb), Is.EqualTo(5));

        var gpuPoint = points.Single(item => item.Axis == VllmBenchmarkAxis.GpuMemoryUtilization && item.Step == 1);
        Assert.That(gpuPoint.GpuMemoryUtilization, Is.EqualTo(0.94d));
        Assert.That(gpuPoint.MaxModelLength, Is.EqualTo(8192));
        Assert.That(gpuPoint.MaxNumSeqs, Is.EqualTo(5));
        Assert.That(gpuPoint.CpuOffloadGb, Is.EqualTo(0));

        var maxLenPoint = points.Single(item => item.Axis == VllmBenchmarkAxis.MaxModelLength && item.Step == -5);
        Assert.That(maxLenPoint.MaxModelLength, Is.EqualTo(3192));

        var maxSeqsPoint = points.Single(item => item.Axis == VllmBenchmarkAxis.MaxNumSeqs && item.MaxNumSeqs == 1);
        Assert.That(maxSeqsPoint.MaxNumSeqs, Is.EqualTo(1));
        Assert.That(maxSeqsPoint.Step, Is.LessThan(0));

        var offloadPoint = points.Single(item => item.Axis == VllmBenchmarkAxis.CpuOffloadGb && item.Step == 5);
        Assert.That(offloadPoint.CpuOffloadGb, Is.EqualTo(10));
    }

    [Test]
    public void CreateSweep_ClampsInvalidLowerBounds()
    {
        var points = VllmAxisBenchmarkPlanner.CreateSweep(0.04d, 1500, 2, 0);

        Assert.That(points.Any(item => item.Axis == VllmBenchmarkAxis.GpuMemoryUtilization && item.GpuMemoryUtilization < 0.02d), Is.False);
        Assert.That(points.Any(item => item.Axis == VllmBenchmarkAxis.MaxModelLength && item.MaxModelLength < 1024), Is.False);
        Assert.That(points.Any(item => item.Axis == VllmBenchmarkAxis.MaxNumSeqs && item.MaxNumSeqs < 1), Is.False);
        Assert.That(points.Any(item => item.Axis == VllmBenchmarkAxis.CpuOffloadGb && item.CpuOffloadGb < 0), Is.False);
    }

    [Test]
    public void CreateSweep_OrdersPointsInSpiralByDistanceAcrossAxes()
    {
        var points = VllmAxisBenchmarkPlanner.CreateSweep(0.92d, 8192, 5, 0);

        var labels = points.Take(12).Select(item => item.Label).ToArray();

        Assert.That(labels, Is.EqualTo(new[]
        {
            "baseline",
            "gpu-memory-utilization+1",
            "gpu-memory-utilization-1",
            "max-model-length+1",
            "max-model-length-1",
            "max-num-seqs+1",
            "max-num-seqs-1",
            "cpu-offload-gb+1",
            "gpu-memory-utilization+2",
            "gpu-memory-utilization-2",
            "max-model-length+2",
            "max-model-length-2"
        }));
    }

    [Test]
    public void CreateTryMetrics_ComputesRequestedThroughputWindows()
    {
        var startedAt = new DateTimeOffset(2026, 05, 17, 10, 0, 0, TimeSpan.Zero);
        var traces = new[]
        {
            new VllmBenchmarkRequestTrace(
                "req-1",
                startedAt,
                startedAt.AddSeconds(2),
                startedAt.AddSeconds(6),
                2000,
                120,
                new[]
                {
                    new VllmBenchmarkStreamPiece(startedAt.AddSeconds(2), "a", 30),
                    new VllmBenchmarkStreamPiece(startedAt.AddSeconds(4), "b", 90),
                    new VllmBenchmarkStreamPiece(startedAt.AddSeconds(6), "c", 120)
                }),
            new VllmBenchmarkRequestTrace(
                "req-2",
                startedAt,
                startedAt.AddSeconds(3),
                startedAt.AddSeconds(5),
                2000,
                80,
                new[]
                {
                    new VllmBenchmarkStreamPiece(startedAt.AddSeconds(3), "a", 20),
                    new VllmBenchmarkStreamPiece(startedAt.AddSeconds(5), "b", 80)
                })
        };

        var metrics = VllmAxisBenchmarkMetrics.CreateTryMetrics(
            "scenario-1",
            VllmBenchmarkAxis.GpuMemoryUtilization,
            1,
            3,
            0.94d,
            8192,
            5,
            0,
            2001,
            startedAt,
            startedAt.AddSeconds(6),
            traces,
            1234,
            4567);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.AvgTimeToFirstTokenSeconds, Is.EqualTo(2.5d));
            Assert.That(metrics.PeakTokensPerSecond, Is.EqualTo(56.6667d).Within(0.0001d));
            Assert.That(metrics.AvgTokensPerSecond, Is.EqualTo(34d));
            Assert.That(metrics.SharedRequestCount, Is.EqualTo(2));
            Assert.That(metrics.PeakGpuMemoryUsedMb, Is.EqualTo(1234d));
            Assert.That(metrics.PeakContainerMemoryUsedMb, Is.EqualTo(4567d));
        });
    }

    [Test]
    public void Summarize_AveragesTryMetricsAndOptionalMemory()
    {
        var startedAt = new DateTimeOffset(2026, 05, 17, 10, 0, 0, TimeSpan.Zero);
        var tries = new[]
        {
            new VllmBenchmarkTryMetrics("a", VllmBenchmarkAxis.CpuOffloadGb, 2, 1, 0.92d, 8192, 5, 4, 1999, 6, 1.1d, 100d, 80d, 8000d, null, startedAt, startedAt.AddSeconds(3), Array.Empty<VllmBenchmarkRequestTrace>()),
            new VllmBenchmarkTryMetrics("b", VllmBenchmarkAxis.CpuOffloadGb, 2, 2, 0.92d, 8192, 5, 4, 2003, 6, 1.3d, 120d, 90d, 8200d, 4096d, startedAt, startedAt.AddSeconds(4), Array.Empty<VllmBenchmarkRequestTrace>())
        };

        var summary = VllmAxisBenchmarkMetrics.Summarize(tries);

        Assert.Multiple(() =>
        {
            Assert.That(summary.AxisName, Is.EqualTo("cpu-offload-gb"));
            Assert.That(summary.TryCount, Is.EqualTo(2));
            Assert.That(summary.AvgTimeToFirstTokenSeconds, Is.EqualTo(1.2d));
            Assert.That(summary.PeakTokensPerSecond, Is.EqualTo(110d));
            Assert.That(summary.AvgTokensPerSecond, Is.EqualTo(85d));
            Assert.That(summary.PeakGpuMemoryUsedMb, Is.EqualTo(8100d));
            Assert.That(summary.PeakContainerMemoryUsedMb, Is.EqualTo(4096d));
        });
    }
}