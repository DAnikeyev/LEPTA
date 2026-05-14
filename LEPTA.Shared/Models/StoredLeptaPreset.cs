namespace LEPTA.Shared.Models;

public sealed class StoredLeptaPreset
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Preset";

    public string GeneralInstruction { get; set; } = string.Empty;

    public List<LeptaPanelDefinition> Panels { get; set; } = [];
}

public sealed class LeptaPresetReference
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = "Preset";
}

