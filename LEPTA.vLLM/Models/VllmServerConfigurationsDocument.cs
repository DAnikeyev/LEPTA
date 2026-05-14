namespace LEPTA.vLLM.Models;

public sealed class VllmServerConfigurationsDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string? SelectedServerId { get; set; }

    public List<VllmServerConfiguration> Servers { get; set; } = [];
}

