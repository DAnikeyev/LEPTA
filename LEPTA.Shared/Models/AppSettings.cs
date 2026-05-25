using System.Text.Json.Serialization;

namespace LEPTA.Shared.Models;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 12;
    public const string DefaultLeptaSystemInstructions = """
        You are an information distillation and learning augmentation engine. This request for one panel consists of system instructions, global instructions, text for analysis, and panel instructions.
        Rules:
        - High information density
        - Minimal verbosity
        - No repetition between panels
        - Optimize for scanability
        - Preserve technical precision
        - Use concise structured formatting
        - Prefer mechanisms, causality, architecture, and tradeoffs
        - Keep outputs compact enough for small dashboard cards, preferably under 3 paragraphs
        """;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool IsDarkTheme { get; set; } = true;

    public bool IsNavigationCollapsed { get; set; }

    public bool IsActionLogOverlayEnabled { get; set; }

    public bool EnableVerboseVllmLogs { get; set; }

    public bool EnableClipboardCachePrefill { get; set; }

    public double UiFontSize { get; set; } = 14;

    public double ResponseFontSize { get; set; } = 14;

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

    public LeptaSettings Lepta { get; set; } = LeptaSettings.CreateDefault();

    public string LeptaSystemInstructions { get; set; } = DefaultLeptaSystemInstructions;
}

public sealed class HotkeySettings
{
    public bool Ctrl { get; set; }

    public bool Alt { get; set; }

    public bool Shift { get; set; }

    public bool Win { get; set; }

    public string Key { get; set; } = string.Empty;

    public static HotkeySettings CreateDefault() => new();
}

public sealed class ChatSettings
{
    public string SystemInstruction { get; set; } = string.Empty;

    public bool EnableThinking { get; set; }

    public static ChatSettings CreateDefault() => new();
}

