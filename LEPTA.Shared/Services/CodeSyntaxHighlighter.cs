using System.Text;
using LEPTA.Shared.Models;

namespace LEPTA.Shared.Services;

public sealed class CodeSyntaxHighlighter
{
    private static readonly HashSet<string> CSharpKeywords =
    [
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit",
        "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator",
        "out", "override", "params", "private", "protected", "public", "readonly", "record", "ref", "required",
        "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
        "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var", "virtual",
        "void", "volatile", "while", "with", "yield"
    ];

    private static readonly HashSet<string> CSharpBuiltInTypes =
    [
        "bool", "byte", "char", "decimal", "double", "dynamic", "float", "int", "long", "nint", "nuint", "object",
        "sbyte", "short", "string", "uint", "ulong", "ushort"
    ];

    private static readonly HashSet<string> PowerShellKeywords =
    [
        "begin", "break", "catch", "class", "continue", "data", "define", "do", "dynamicparam", "else", "elseif",
        "end", "enum", "exit", "filter", "finally", "for", "foreach", "from", "function", "hidden", "if", "in",
        "parallel", "param", "process", "return", "switch", "throw", "trap", "try", "until", "using", "var", "while", "workflow"
    ];

    private static readonly HashSet<string> MarkdownDelimiters = ["#", "-", "*", ">", "```", "~~~"];

    public string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "plain";
        }

        var candidate = language.Trim();
        var separatorIndex = candidate.IndexOfAny([' ', '\t', ',']);
        if (separatorIndex >= 0)
        {
            candidate = candidate[..separatorIndex];
        }

        candidate = candidate.Trim().ToLowerInvariant();
        return candidate switch
        {
            "c#" or "cs" or "csharp" or "dotnet" => "csharp",
            "json" or "jsonc" => "json",
            "xml" or "xaml" or "html" or "svg" => "xml",
            "powershell" or "pwsh" or "ps1" or "psm1" => "powershell",
            "md" or "markdown" => "markdown",
            _ => "plain"
        };
    }

    public IReadOnlyList<HighlightedCodeLine> Highlight(string code, string? language)
    {
        var normalizedLanguage = NormalizeLanguage(language);
        var normalizedCode = code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalizedCode.Split('\n');

        return normalizedLanguage switch
        {
            "csharp" => HighlightCSharp(lines),
            "json" => HighlightJson(lines),
            "xml" => HighlightXml(lines),
            "powershell" => HighlightPowerShell(lines),
            "markdown" => HighlightMarkdown(lines),
            _ => HighlightPlain(lines)
        };
    }

    private static IReadOnlyList<HighlightedCodeLine> HighlightPlain(IEnumerable<string> lines)
        => lines.Select(line => new HighlightedCodeLine([new HighlightedCodeToken(line, CodeTokenKind.PlainText)])).ToList();

    private static IReadOnlyList<HighlightedCodeLine> HighlightMarkdown(IEnumerable<string> lines)
    {
        var result = new List<HighlightedCodeLine>();
        foreach (var line in lines)
        {
            var tokens = new List<HighlightedCodeToken>();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                tokens.Add(new HighlightedCodeToken(string.Empty, CodeTokenKind.PlainText));
                result.Add(new HighlightedCodeLine(tokens));
                continue;
            }

            var leadingWhitespaceLength = line.Length - trimmed.Length;
            if (leadingWhitespaceLength > 0)
            {
                tokens.Add(new HighlightedCodeToken(line[..leadingWhitespaceLength], CodeTokenKind.PlainText));
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                tokens.Add(new HighlightedCodeToken(trimmed, CodeTokenKind.MarkdownDelimiter));
            }
            else if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                var markerLength = trimmed.TakeWhile(character => character == '#').Count();
                tokens.Add(new HighlightedCodeToken(trimmed[..markerLength], CodeTokenKind.MarkdownDelimiter));
                tokens.Add(new HighlightedCodeToken(trimmed[markerLength..], CodeTokenKind.PlainText));
            }
            else if (MarkdownDelimiters.Any(delimiter => trimmed.StartsWith(delimiter + " ", StringComparison.Ordinal)))
            {
                tokens.Add(new HighlightedCodeToken(trimmed[..1], CodeTokenKind.MarkdownDelimiter));
                tokens.Add(new HighlightedCodeToken(trimmed[1..], CodeTokenKind.PlainText));
            }
            else
            {
                tokens.Add(new HighlightedCodeToken(trimmed, CodeTokenKind.PlainText));
            }

            result.Add(new HighlightedCodeLine(tokens));
        }

        return result;
    }

    private static IReadOnlyList<HighlightedCodeLine> HighlightJson(IEnumerable<string> lines)
    {
        var result = new List<HighlightedCodeLine>();
        foreach (var line in lines)
        {
            var tokens = new List<HighlightedCodeToken>();
            var index = 0;
            while (index < line.Length)
            {
                var current = line[index];
                if (char.IsWhiteSpace(current))
                {
                    var start = index;
                    while (index < line.Length && char.IsWhiteSpace(line[index]))
                    {
                        index++;
                    }

                    tokens.Add(new HighlightedCodeToken(line[start..index], CodeTokenKind.PlainText));
                    continue;
                }

                if (current == '"')
                {
                    var tokenText = ReadQuotedToken(line, ref index, '"');
                    var probeIndex = index;
                    while (probeIndex < line.Length && char.IsWhiteSpace(line[probeIndex]))
                    {
                        probeIndex++;
                    }

                    var kind = probeIndex < line.Length && line[probeIndex] == ':'
                        ? CodeTokenKind.PropertyName
                        : CodeTokenKind.String;
                    tokens.Add(new HighlightedCodeToken(tokenText, kind));
                    continue;
                }

                if (char.IsDigit(current) || (current == '-' && index + 1 < line.Length && char.IsDigit(line[index + 1])))
                {
                    var start = index;
                    index++;
                    while (index < line.Length && (char.IsDigit(line[index]) || ".eE+-".Contains(line[index], StringComparison.Ordinal)))
                    {
                        index++;
                    }

                    tokens.Add(new HighlightedCodeToken(line[start..index], CodeTokenKind.Number));
                    continue;
                }

                if (char.IsLetter(current))
                {
                    var start = index;
                    index++;
                    while (index < line.Length && char.IsLetter(line[index]))
                    {
                        index++;
                    }

                    tokens.Add(new HighlightedCodeToken(line[start..index], CodeTokenKind.Keyword));
                    continue;
                }

                tokens.Add(new HighlightedCodeToken(current.ToString(), CodeTokenKind.PlainText));
                index++;
            }

            result.Add(new HighlightedCodeLine(tokens));
        }

        return result;
    }

    private static IReadOnlyList<HighlightedCodeLine> HighlightXml(IEnumerable<string> lines)
    {
        var result = new List<HighlightedCodeLine>();
        var inComment = false;
        foreach (var line in lines)
        {
            var tokens = new List<HighlightedCodeToken>();
            var index = 0;
            while (index < line.Length)
            {
                if (inComment)
                {
                    var endIndex = line.IndexOf("-->", index, StringComparison.Ordinal);
                    if (endIndex < 0)
                    {
                        tokens.Add(new HighlightedCodeToken(line[index..], CodeTokenKind.Comment));
                        index = line.Length;
                        continue;
                    }

                    tokens.Add(new HighlightedCodeToken(line[index..(endIndex + 3)], CodeTokenKind.Comment));
                    index = endIndex + 3;
                    inComment = false;
                    continue;
                }

                if (line.AsSpan(index).StartsWith("<!--".AsSpan(), StringComparison.Ordinal))
                {
                    var endIndex = line.IndexOf("-->", index + 4, StringComparison.Ordinal);
                    if (endIndex < 0)
                    {
                        tokens.Add(new HighlightedCodeToken(line[index..], CodeTokenKind.Comment));
                        inComment = true;
                        index = line.Length;
                        continue;
                    }

                    tokens.Add(new HighlightedCodeToken(line[index..(endIndex + 3)], CodeTokenKind.Comment));
                    index = endIndex + 3;
                    continue;
                }

                if (line[index] == '<')
                {
                    var tagStart = index;
                    tokens.Add(new HighlightedCodeToken("<", CodeTokenKind.PlainText));
                    index++;
                    if (index < line.Length && (line[index] == '/' || line[index] == '?' || line[index] == '!'))
                    {
                        tokens.Add(new HighlightedCodeToken(line[index].ToString(), CodeTokenKind.PlainText));
                        index++;
                    }

                    var nameStart = index;
                    while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] is ':' or '_' or '-' or '.'))
                    {
                        index++;
                    }

                    if (index > nameStart)
                    {
                        tokens.Add(new HighlightedCodeToken(line[nameStart..index], CodeTokenKind.TagName));
                    }

                    while (index < line.Length && line[index] != '>')
                    {
                        if (char.IsWhiteSpace(line[index]))
                        {
                            var whitespaceStart = index;
                            while (index < line.Length && char.IsWhiteSpace(line[index]))
                            {
                                index++;
                            }

                            tokens.Add(new HighlightedCodeToken(line[whitespaceStart..index], CodeTokenKind.PlainText));
                            continue;
                        }

                        if (line[index] is '/' or '=')
                        {
                            tokens.Add(new HighlightedCodeToken(line[index].ToString(), CodeTokenKind.PlainText));
                            index++;
                            continue;
                        }

                        var attributeStart = index;
                        while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] is ':' or '_' or '-' or '.'))
                        {
                            index++;
                        }

                        if (index > attributeStart)
                        {
                            tokens.Add(new HighlightedCodeToken(line[attributeStart..index], CodeTokenKind.AttributeName));
                            continue;
                        }

                        if (line[index] == '"' || line[index] == '\'')
                        {
                            var delimiter = line[index];
                            var tokenText = ReadQuotedToken(line, ref index, delimiter);
                            tokens.Add(new HighlightedCodeToken(tokenText, CodeTokenKind.AttributeValue));
                            continue;
                        }

                        tokens.Add(new HighlightedCodeToken(line[index].ToString(), CodeTokenKind.PlainText));
                        index++;
                    }

                    if (index < line.Length && line[index] == '>')
                    {
                        tokens.Add(new HighlightedCodeToken(">", CodeTokenKind.PlainText));
                        index++;
                    }

                    continue;
                }

                var textStart = index;
                while (index < line.Length && line[index] != '<')
                {
                    index++;
                }

                tokens.Add(new HighlightedCodeToken(line[textStart..index], CodeTokenKind.PlainText));
            }

            result.Add(new HighlightedCodeLine(tokens));
        }

        return result;
    }

    private static IReadOnlyList<HighlightedCodeLine> HighlightPowerShell(IEnumerable<string> lines)
    {
        var result = new List<HighlightedCodeLine>();
        foreach (var line in lines)
        {
            var tokens = new List<HighlightedCodeToken>();
            var index = 0;
            while (index < line.Length)
            {
                var current = line[index];
                if (char.IsWhiteSpace(current))
                {
                    var start = index;
                    while (index < line.Length && char.IsWhiteSpace(line[index]))
                    {
                        index++;
                    }

                    tokens.Add(new HighlightedCodeToken(line[start..index], CodeTokenKind.PlainText));
                    continue;
                }

                if (current == '#')
                {
                    tokens.Add(new HighlightedCodeToken(line[index..], CodeTokenKind.Comment));
                    break;
                }

                if (current == '"' || current == '\'')
                {
                    var tokenText = ReadQuotedToken(line, ref index, current);
                    tokens.Add(new HighlightedCodeToken(tokenText, CodeTokenKind.String));
                    continue;
                }

                if (current == '$')
                {
                    var start = index;
                    index++;
                    while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] == '_' || line[index] == ':'))
                    {
                        index++;
                    }

                    tokens.Add(new HighlightedCodeToken(line[start..index], CodeTokenKind.Variable));
                    continue;
                }

                if (current == '-' && index + 1 < line.Length && char.IsLetter(line[index + 1]))
                {
                    var start = index;
                    index++;
                    while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] == '-'))
                    {
                        index++;
                    }

                    tokens.Add(new HighlightedCodeToken(line[start..index], CodeTokenKind.Parameter));
                    continue;
                }

                if (char.IsDigit(current))
                {
                    var start = index;
                    index++;
                    while (index < line.Length && (char.IsDigit(line[index]) || line[index] == '.'))
                    {
                        index++;
                    }

                    tokens.Add(new HighlightedCodeToken(line[start..index], CodeTokenKind.Number));
                    continue;
                }

                if (char.IsLetter(current))
                {
                    var start = index;
                    index++;
                    while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] == '-'))
                    {
                        index++;
                    }

                    var tokenText = line[start..index];
                    tokens.Add(new HighlightedCodeToken(
                        tokenText,
                        PowerShellKeywords.Contains(tokenText.ToLowerInvariant()) ? CodeTokenKind.Keyword : CodeTokenKind.PlainText));
                    continue;
                }

                tokens.Add(new HighlightedCodeToken(current.ToString(), CodeTokenKind.PlainText));
                index++;
            }

            result.Add(new HighlightedCodeLine(tokens));
        }

        return result;
    }

    private static IReadOnlyList<HighlightedCodeLine> HighlightCSharp(IEnumerable<string> lines)
    {
        var result = new List<HighlightedCodeLine>();
        var inBlockComment = false;
        var inVerbatimString = false;
        foreach (var line in lines)
        {
            var tokens = new List<HighlightedCodeToken>();
            var index = 0;
            while (index < line.Length)
            {
                if (inBlockComment)
                {
                    var endIndex = line.IndexOf("*/", index, StringComparison.Ordinal);
                    if (endIndex < 0)
                    {
                        tokens.Add(new HighlightedCodeToken(line[index..], CodeTokenKind.Comment));
                        index = line.Length;
                        continue;
                    }

                    tokens.Add(new HighlightedCodeToken(line[index..(endIndex + 2)], CodeTokenKind.Comment));
                    index = endIndex + 2;
                    inBlockComment = false;
                    continue;
                }

                if (inVerbatimString)
                {
                    var text = ReadVerbatimStringContinuation(line, ref index, ref inVerbatimString);
                    tokens.Add(new HighlightedCodeToken(text, CodeTokenKind.String));
                    continue;
                }

                var current = line[index];
                if (char.IsWhiteSpace(current))
                {
                    var start = index;
                    while (index < line.Length && char.IsWhiteSpace(line[index]))
                    {
                        index++;
                    }

                    tokens.Add(new HighlightedCodeToken(line[start..index], CodeTokenKind.PlainText));
                    continue;
                }

                if (line.AsSpan(index).StartsWith("//".AsSpan(), StringComparison.Ordinal))
                {
                    tokens.Add(new HighlightedCodeToken(line[index..], CodeTokenKind.Comment));
                    break;
                }

                if (line.AsSpan(index).StartsWith("/*".AsSpan(), StringComparison.Ordinal))
                {
                    var endIndex = line.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    if (endIndex < 0)
                    {
                        tokens.Add(new HighlightedCodeToken(line[index..], CodeTokenKind.Comment));
                        inBlockComment = true;
                        index = line.Length;
                        continue;
                    }

                    tokens.Add(new HighlightedCodeToken(line[index..(endIndex + 2)], CodeTokenKind.Comment));
                    index = endIndex + 2;
                    continue;
                }

                if (line.AsSpan(index).StartsWith("@\"".AsSpan(), StringComparison.Ordinal))
                {
                    var text = ReadVerbatimStringStart(line, ref index, ref inVerbatimString);
                    tokens.Add(new HighlightedCodeToken(text, CodeTokenKind.String));
                    continue;
                }

                if (current == '"' || current == '\'')
                {
                    var tokenText = ReadEscapedString(line, ref index, current);
                    tokens.Add(new HighlightedCodeToken(tokenText, CodeTokenKind.String));
                    continue;
                }

                if (char.IsDigit(current))
                {
                    var start = index;
                    index++;
                    while (index < line.Length && (char.IsLetterOrDigit(line[index]) || ".xX_".Contains(line[index], StringComparison.Ordinal)))
                    {
                        index++;
                    }

                    tokens.Add(new HighlightedCodeToken(line[start..index], CodeTokenKind.Number));
                    continue;
                }

                if (current == '#')
                {
                    tokens.Add(new HighlightedCodeToken(line[index..], CodeTokenKind.Keyword));
                    break;
                }

                if (char.IsLetter(current) || current == '_')
                {
                    var start = index;
                    index++;
                    while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] == '_'))
                    {
                        index++;
                    }

                    var tokenText = line[start..index];
                    var kind = CSharpKeywords.Contains(tokenText)
                        ? CodeTokenKind.Keyword
                        : CSharpBuiltInTypes.Contains(tokenText) || char.IsUpper(tokenText[0])
                            ? CodeTokenKind.TypeName
                            : CodeTokenKind.PlainText;
                    tokens.Add(new HighlightedCodeToken(tokenText, kind));
                    continue;
                }

                tokens.Add(new HighlightedCodeToken(current.ToString(), CodeTokenKind.PlainText));
                index++;
            }

            result.Add(new HighlightedCodeLine(tokens));
        }

        return result;
    }

    private static string ReadQuotedToken(string line, ref int index, char delimiter)
    {
        var start = index;
        index++;
        while (index < line.Length)
        {
            if (line[index] == '\\')
            {
                index = Math.Min(line.Length, index + 2);
                continue;
            }

            if (line[index] == delimiter)
            {
                index++;
                break;
            }

            index++;
        }

        return line[start..index];
    }

    private static string ReadEscapedString(string line, ref int index, char delimiter)
        => ReadQuotedToken(line, ref index, delimiter);

    private static string ReadVerbatimStringStart(string line, ref int index, ref bool inVerbatimString)
    {
        var start = index;
        index += 2;
        while (index < line.Length)
        {
            if (line[index] == '"')
            {
                if (index + 1 < line.Length && line[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                index++;
                inVerbatimString = false;
                return line[start..index];
            }

            index++;
        }

        inVerbatimString = true;
        return line[start..index];
    }

    private static string ReadVerbatimStringContinuation(string line, ref int index, ref bool inVerbatimString)
    {
        var start = index;
        while (index < line.Length)
        {
            if (line[index] == '"')
            {
                if (index + 1 < line.Length && line[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                index++;
                inVerbatimString = false;
                return line[start..index];
            }

            index++;
        }

        return line[start..index];
    }
}

