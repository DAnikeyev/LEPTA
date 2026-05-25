using System.Text.Json.Serialization;

namespace LEPTA.Shared.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LeptaDocumentTrimMode
{
    TrimStart,
    TrimEnd
}

public sealed class LeptaSettings
{
    public const int DefaultDocumentTokenLimit = 6_000;
    public const int MinDocumentTokenLimit = 512;
    public const int MaxDocumentTokenLimit = 128_000;
    public const double DefaultTemperature = 0.2;
    public const double MinTemperature = 0.0;
    public const double MaxTemperature = 2.0;

    public bool EnableSharedPromptPrefill { get; set; }

    public LeptaDocumentTrimMode DocumentTrimMode { get; set; } = LeptaDocumentTrimMode.TrimStart;

    public int DocumentTokenLimit { get; set; } = DefaultDocumentTokenLimit;

    public static int NormalizeDocumentTokenLimit(int value)
        => value <= 0
            ? DefaultDocumentTokenLimit
            : Math.Clamp(value, MinDocumentTokenLimit, MaxDocumentTokenLimit);

    public static double NormalizeTemperature(double value)
        => !double.IsFinite(value)
            ? DefaultTemperature
            : Math.Clamp(Math.Round(value, 2), MinTemperature, MaxTemperature);

    public static LeptaSettings CreateDefault() => new();
}

