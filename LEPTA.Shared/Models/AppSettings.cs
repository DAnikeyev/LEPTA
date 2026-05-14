using System.Text.Json.Serialization;

namespace LEPTA.Shared.Models;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool IsDarkTheme { get; set; } = true;

    public bool IsNavigationCollapsed { get; set; }

    public bool IsActionLogOverlayEnabled { get; set; }

    public bool EnableVerboseVllmLogs { get; set; }

    public string DefaultDashboardId { get; set; } = LeptaDashboardDefinition.DefaultDashboardId;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("ActiveDashboardId")]
    public string? LegacyActiveDashboardId
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                DefaultDashboardId = value.Trim();
            }
        }
    }

    [JsonIgnore]
    public string ActiveDashboardId
    {
        get => DefaultDashboardId;
        set => DefaultDashboardId = string.IsNullOrWhiteSpace(value)
            ? LeptaDashboardDefinition.DefaultDashboardId
            : value.Trim();
    }

    public string? DefaultServerId { get; set; }

    public HotkeySettings Hotkey { get; set; } = HotkeySettings.CreateDefault();

    public ChatSettings Chat { get; set; } = ChatSettings.CreateDefault();
}

public sealed class HotkeySettings
{
    public bool Ctrl { get; set; } = true;

    public bool Alt { get; set; }

    public bool Shift { get; set; } = true;

    public bool Win { get; set; }

    public string Key { get; set; } = "F8";

    public static HotkeySettings CreateDefault() => new();
}

public sealed class ChatSettings
{
    public string SystemInstruction { get; set; } = string.Empty;

    public static ChatSettings CreateDefault() => new();
}

