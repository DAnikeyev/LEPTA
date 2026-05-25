using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class ThinkingContentFilterTests
{
    [Test]
    public void ExtractVisibleAnswer_RemovesThinkBlocks()
    {
        var open = string.Concat('<', "think", '>');
        var close = string.Concat('<', '/', "think", '>');
        var input = $"{open}Hidden reasoning{close}{Environment.NewLine}Visible answer.";

        var result = ThinkingContentFilter.ExtractVisibleAnswer(input);

        Assert.That(result, Is.EqualTo("Visible answer."));
    }

    [Test]
    public void ExtractVisibleAnswer_RemovesRedactedReasoningBlocks()
    {
        var open = string.Concat('<', "redacted_reasoning", '>');
        var close = string.Concat('<', '/', "redacted_reasoning", '>');
        var input = $"{open}Hidden{close}Final text.";

        var result = ThinkingContentFilter.ExtractVisibleAnswer(input);

        Assert.That(result, Is.EqualTo("Final text."));
    }

    [Test]
    public void StreamFilter_EmitsOnlyVisibleAnswerIncrementally()
    {
        var open = string.Concat('<', "think", '>');
        var close = string.Concat('<', '/', "think", '>');
        var filter = new ThinkingContentStreamFilter();

        Assert.That(filter.Append($"{open}Step"), Is.Empty);
        Assert.That(filter.Append($" one{close}Answer"), Is.EqualTo("Answer"));
        Assert.That(filter.GetVisibleText(), Is.EqualTo("Answer"));
    }
}
