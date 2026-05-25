using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LEPTA.Shared.Models;

public sealed class LeptaDashboardDefinition
{
    public const int CurrentSchemaVersion = 6;
    public const string DefaultDashboardId = "default";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Id { get; set; } = DefaultDashboardId;

    public string Name { get; set; } = "Default Dashboard";

    public string? SelectedServerId { get; set; }

    public string? SelectedPresetId { get; set; }

    public string GeneralInstruction { get; set; } = string.Empty;

    public bool EnableThinking { get; set; }

    public double Temperature { get; set; } = LeptaSettings.DefaultTemperature;

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

    public string AccentColorHex { get; set; } = "#2F6FED";

    public string Format { get; set; } = LeptaPanelFormats.Markdown;
}

public static class LeptaPanelFormats
{
    public const string Markdown = "Markdown";
    public const string Mermaid = "Mermaid";
    public const string PlainText = "Plain text";

    public static IReadOnlyList<string> All { get; } = [Markdown, Mermaid, PlainText];

    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, Mermaid, StringComparison.OrdinalIgnoreCase))
            return Mermaid;
        if (string.Equals(trimmed, PlainText, StringComparison.OrdinalIgnoreCase))
            return PlainText;
        return Markdown;
    }
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

