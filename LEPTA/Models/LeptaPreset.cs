namespace LEPTA.Models;

public sealed class LeptaPreset
{
    public string Name { get; set; } = "Preset";

    public string GeneralInstruction { get; set; } = string.Empty;

    public List<LeptaPresetPanel> Panels { get; set; } = [];
}

public sealed class LeptaPresetPanel
{
    public string Name { get; set; } = "Panel";

    public string CustomInstruction { get; set; } = string.Empty;
}
