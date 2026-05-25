using System.Text.RegularExpressions;

namespace LEPTA.Services;

internal static class MermaidSourceNormalizer
{
    private static readonly Regex LooseFenceRegex = new(
        @"^\s*```(?:\s*mermaid)?\s*\r?\n(?<code>[\s\S]*?)\r?\n```\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Normalize(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var text = source.Replace("\r\n", "\n").Trim();
        var fenced = TryUnwrapFence(text);
        if (!string.IsNullOrWhiteSpace(fenced))
        {
            text = fenced;
        }

        var lines = text.Split('\n').Select(static line => line.Trim()).ToList();
        var normalized = new List<string>(lines.Count);
        var blankStreak = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                blankStreak++;
                if (blankStreak <= 1)
                {
                    normalized.Add(string.Empty);
                }

                continue;
            }

            blankStreak = 0;
            normalized.Add(line);
        }

        while (normalized.Count > 0 && string.IsNullOrEmpty(normalized[0]))
        {
            normalized.RemoveAt(0);
        }

        while (normalized.Count > 0 && string.IsNullOrEmpty(normalized[^1]))
        {
            normalized.RemoveAt(normalized.Count - 1);
        }

        return MermaidDiagramPalettePostProcessor.Apply(string.Join('\n', normalized));
    }

    private static string? TryUnwrapFence(string text)
    {
        var match = LooseFenceRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var code = match.Groups["code"].Value.Trim();
        return string.IsNullOrWhiteSpace(code)
            ? null
            : code;
    }
}
