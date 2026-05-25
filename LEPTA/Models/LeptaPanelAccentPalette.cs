namespace LEPTA.Models;

internal static class LeptaPanelAccentPalette
{
    public const string DefaultAccentColorHex = "#2F6FED";

    public static IReadOnlyList<string> Options { get; } =
    [
        DefaultAccentColorHex,
        "#7C3AED",
        "#C026D3",
        "#DB2777",
        "#DC2626",
        "#EA580C",
        "#FACC15",
        "#84CC16",
        "#16A34A",
        "#5EEAD4",
        "#0F766E",
        "#06B6D4",
        "#0284C7",
        "#4F46E5",
        "#7F1D1D",
        "#92400E",
        "#475569",
        "#A1A1AA",
        "#FFFFFF",
        "#000000"
    ];

    public static string Normalize(string? accentColorHex)
        => string.IsNullOrWhiteSpace(accentColorHex) ? DefaultAccentColorHex : accentColorHex.Trim();

    public static string GetRandomAccentColor(string? previousAccentColorHex, Random? random = null)
    {
        var candidates = string.IsNullOrWhiteSpace(previousAccentColorHex)
            ? Options.ToArray()
            : Options.Where(option => !string.Equals(option, previousAccentColorHex.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

        if (candidates.Length == 0)
        {
            return DefaultAccentColorHex;
        }

        var source = random ?? Random.Shared;
        return candidates[source.Next(candidates.Length)];
    }
}
