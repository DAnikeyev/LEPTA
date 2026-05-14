namespace LEPTA.vLLM.Models;

public sealed record VllmDeploymentValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;

    public string BuildDisplayMessage()
    {
        if (IsValid && Warnings.Count == 0)
        {
            return "Deployment settings look ready.";
        }

        var lines = new List<string>();
        if (Errors.Count > 0)
        {
            lines.Add("Fix these deployment issues before continuing:");
            lines.AddRange(Errors.Select(error => $"- {error}"));
        }

        if (Warnings.Count > 0)
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("Warnings:");
            lines.AddRange(Warnings.Select(warning => $"- {warning}"));
        }

        return string.Join(Environment.NewLine, lines);
    }
}

