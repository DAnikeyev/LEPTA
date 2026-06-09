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
            ? "Answer format: mermaid ONLY.\n\n"
                + "Mermaid syntax rules:\n"
                + "- Return the raw Mermaid source. Do NOT wrap it in markdown fences (```mermaid).\n"
                + "- In classDiagram, label relationships with ': label' AFTER the arrow. NEVER use '|label|' inside classDiagram.\n"
                + "- In classDiagram, use 'note for NodeId \"text\"' for notes. NEVER use 'note NodeId \"text\"'.\n"
                + "- In classDiagram, node IDs must be alphanumeric (no spaces or dots inside the ID).\n"
                + "- In flowchart, use 'graph TD' or 'flowchart TD' as the header.\n"
                + "- Keep the diagram minimal and valid."
            : "Answer format: markdown.";
}


