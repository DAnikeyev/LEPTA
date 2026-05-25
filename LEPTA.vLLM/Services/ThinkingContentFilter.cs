using System.Text;
using System.Text.RegularExpressions;

namespace LEPTA.vLLM.Services;

internal static class ThinkingContentFilter
{
    private static readonly string OpenThink = string.Concat('<', "think", '>');
    private static readonly string CloseThink = string.Concat('<', '/', "think", '>');
    private static readonly string OpenReasoning = string.Concat('<', "reasoning", '>');
    private static readonly string CloseReasoning = string.Concat('<', '/', "reasoning", '>');
    private static readonly string OpenRedacted = string.Concat('<', "redacted_reasoning", '>');
    private static readonly string CloseRedacted = string.Concat('<', '/', "redacted_reasoning", '>');

    private static readonly Regex RedactedReasoningBlockRegex = CreateBlockRegex(OpenRedacted, CloseRedacted);
    private static readonly Regex ThinkBlockRegex = CreateBlockRegex(OpenThink, CloseThink);
    private static readonly Regex ReasoningBlockRegex = CreateBlockRegex(OpenReasoning, CloseReasoning);
    private static readonly Regex TrailingIncompleteThinkingOpenerRegex = new(
        $@"(?:{Regex.Escape(OpenRedacted)}|{Regex.Escape(OpenThink)}|{Regex.Escape(OpenReasoning)})[^\r\n]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string ExtractVisibleAnswer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = RedactedReasoningBlockRegex.Replace(text, string.Empty);
        result = ThinkBlockRegex.Replace(result, string.Empty);
        result = ReasoningBlockRegex.Replace(result, string.Empty);
        result = TrailingIncompleteThinkingOpenerRegex.Replace(result, string.Empty);
        return result.Trim();
    }

    private static Regex CreateBlockRegex(string openTag, string closeTag)
    {
        var pattern = $"{Regex.Escape(openTag)}[\\s\\S]*?{Regex.Escape(closeTag)}";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

internal sealed class ThinkingContentStreamFilter
{
    private readonly StringBuilder raw = new();
    private int lastVisibleLength;

    public string Append(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return string.Empty;
        }

        raw.Append(chunk);
        var visible = ThinkingContentFilter.ExtractVisibleAnswer(raw.ToString());
        if (visible.Length <= lastVisibleLength)
        {
            return string.Empty;
        }

        var delta = visible[lastVisibleLength..];
        lastVisibleLength = visible.Length;
        return delta;
    }

    public string GetVisibleText()
        => ThinkingContentFilter.ExtractVisibleAnswer(raw.ToString());
}
