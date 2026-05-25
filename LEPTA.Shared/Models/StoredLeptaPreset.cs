namespace LEPTA.Shared.Models;

public sealed class StoredLeptaPreset
{
    public const int CurrentSchemaVersion = 4;
    public const string LearningPresetId = "builtin-learning";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Preset";

    public string GeneralInstruction { get; set; } = string.Empty;

    public string? SelectedServerId { get; set; }

    public bool EnableThinking { get; set; }

    public double Temperature { get; set; } = LeptaSettings.DefaultTemperature;

    public List<LeptaPanelDefinition> Panels { get; set; } = [];

    public static bool IsBuiltInPresetId(string? presetId)
        => !string.IsNullOrWhiteSpace(presetId)
           && BuiltInPresets.Any(preset => string.Equals(preset.Id, presetId.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<StoredLeptaPreset> GetBuiltInPresets()
        => BuiltInPresets.Select(ClonePreset).ToList();

    private static IReadOnlyList<StoredLeptaPreset> BuiltInPresets { get; } =
    [
        CreateLearningPreset(),
    ];

    public static StoredLeptaPreset CreateLearningPreset() => new()
    {
        Id = LearningPresetId,
        Name = "Learning",
        GeneralInstruction = "If no programming language is specified, default to C#.",
        EnableThinking = false,
        Temperature = 0.2,
        Panels =
        [
            new LeptaPanelDefinition
            {
                Name = "Terms",
                CustomInstruction = """
                    Extract 5–8 important technical terms.

                    Format:
                     - **Term**: concise definition

                    Rules:
                    - Only high-signal concepts
                    - One sentence per term
                    - Avoid generic vocabulary
                    """,
                AccentColorHex = "#F23535",
                Format = LeptaPanelFormats.Markdown
            },
            new LeptaPanelDefinition
            {
                Name = "Summary",
                CustomInstruction = """
                    Summarize the document’s:
                    - main idea
                    - key mechanism
                    - important implication

                    Rules:
                    - Maximum 2 short paragraphs
                    - High compression
                    - No filler
                    """,
                AccentColorHex = "#60F235",
                Format = LeptaPanelFormats.Markdown
            },
            new LeptaPanelDefinition
            {
                Name = "Code",
                CustomInstruction = """
                    Generate a minimal code example demonstrating the core mechanism.

                    Rules:
                    - Default language: C#
                    - Keep code compact
                    - Avoid boilerplate
                    - Prefer executable examples
                    - Lines should me no longer then 30 characters

                    Respond with one code block only, No explanation besides code comment.
                    """,
                AccentColorHex = "#2F6FED",
                Format = LeptaPanelFormats.Markdown
            },
            new LeptaPanelDefinition
            {
                Name = "UML",
                CustomInstruction = """
                    Generate a compact UML or architecture-style diagram.

                    Rules:
                    - Focus on major components and interactions
                    - Keep readable and minimal
                    - Mermaid-compatible
                    """,
                AccentColorHex = "#6BF235",
                Format = LeptaPanelFormats.Mermaid
            },
            new LeptaPanelDefinition
            {
                Name = "Knowledge check",
                CustomInstruction = """
                    Generate exactly 2 conceptual questions with concise answers.

                    Focus on:
                    - mechanisms
                    - tradeoffs
                    - causality
                    - failure cases

                    Avoid trivial factual questions. Each answer should be 1 paragraph max,
                    """,
                AccentColorHex = "#2F6FED",
                Format = LeptaPanelFormats.Markdown
            }
        ]
    };

    private static StoredLeptaPreset ClonePreset(StoredLeptaPreset preset) => new()
    {
        SchemaVersion = preset.SchemaVersion,
        Id = preset.Id,
        Name = preset.Name,
        GeneralInstruction = preset.GeneralInstruction,
        SelectedServerId = preset.SelectedServerId,
        EnableThinking = preset.EnableThinking,
        Temperature = preset.Temperature,
        Panels = preset.Panels
            .Select(panel => new LeptaPanelDefinition
            {
                Name = panel.Name,
                CustomInstruction = panel.CustomInstruction,
                AccentColorHex = panel.AccentColorHex,
                Format = panel.Format
            })
            .ToList()
    };
}

public sealed class LeptaPresetReference
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = "Preset";

    public int PanelCount { get; set; }

    public bool IsBuiltIn { get; set; }

    public string DisplayName
    {
        get
        {
            var panelSuffix = PanelCount <= 0
                ? string.Empty
                : $" ({PanelCount} panel{(PanelCount == 1 ? string.Empty : "s")})";
            return IsBuiltIn
                ? $"{Name}{panelSuffix} (built-in)"
                : $"{Name}{panelSuffix}";
        }
    }
}

