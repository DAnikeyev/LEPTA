namespace LEPTA.vLLM.Models;

public sealed record ModelMemoryEstimate(double EstimatedVramGb, double? EstimatedGpuUsageGb, string Summary);

