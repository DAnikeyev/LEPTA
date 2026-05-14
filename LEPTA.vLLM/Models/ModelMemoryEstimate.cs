namespace LEPTA.vLLM.Models;

public sealed record ModelMemoryEstimate(double EstimatedVramGb, double EstimatedRamGb, string Summary);

