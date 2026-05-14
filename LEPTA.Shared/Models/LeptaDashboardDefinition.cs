using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LEPTA.Shared.Models;

public sealed class LeptaDashboardDefinition
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultDashboardId = "default";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Id { get; set; } = DefaultDashboardId;

    public string Name { get; set; } = "Default Dashboard";

    public string? SelectedServerId { get; set; }

    public string GeneralInstruction { get; set; } = string.Empty;

    public List<LeptaPanelDefinition> Panels { get; set; } = [];

    public static LeptaDashboardDefinition CreateDefault() => new()
    {
        Panels =
        [
            new LeptaPanelDefinition
            {
                Name = "Panel 1",
                CustomInstruction = "Answer with the perspective for this panel."
            }
        ]
    };
}

public sealed class LeptaPanelDefinition
{
    public string Name { get; set; } = "Panel";

    public string CustomInstruction { get; set; } = string.Empty;
}

public sealed class LeptaDashboardReference : INotifyPropertyChanged
{
    private string name = "Dashboard";

    public string Id { get; set; } = LeptaDashboardDefinition.DefaultDashboardId;

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

