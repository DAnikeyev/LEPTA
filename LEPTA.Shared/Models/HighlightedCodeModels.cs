namespace LEPTA.Shared.Models;

public enum CodeTokenKind
{
    PlainText,
    Keyword,
    String,
    Comment,
    Number,
    TypeName,
    PropertyName,
    TagName,
    AttributeName,
    AttributeValue,
    Variable,
    Parameter,
    MarkdownDelimiter
}

public sealed record HighlightedCodeToken(string Text, CodeTokenKind Kind);

public sealed record HighlightedCodeLine(IReadOnlyList<HighlightedCodeToken> Tokens);

