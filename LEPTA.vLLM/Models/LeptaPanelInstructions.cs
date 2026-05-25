using LEPTA.Shared.Models;

namespace LEPTA.vLLM.Models;

public sealed class LeptaPanelInstructions
{
    public LeptaPanelInstructions(string requestInstructions, string panelInstructions)
    {
        RequestInstructions = requestInstructions.Trim();
        PanelInstructions = panelInstructions.Trim();
    }

    public string RequestInstructions { get; }

    public string PanelInstructions { get; }

    public static LeptaPanelInstructions Create(string? requestInstructions, string? format)
        => new(requestInstructions ?? string.Empty, BuildHiddenPanelInstructions(format));

    private static string BuildHiddenPanelInstructions(string? format)
        => string.Equals(LeptaPanelFormats.Normalize(format), LeptaPanelFormats.Mermaid, StringComparison.Ordinal)
            ? "Answer format: mermaid ONLY."
            : "Answer format: markdown.";
}


