using LEPTA.Models;
using LEPTA.Shared.Models;

namespace LEPTA.Tests;

[TestFixture]
public sealed class LeptaPanelStateTests
{
    [Test]
    public void StatusBadge_IsNone_ByDefault()
    {
        var panel = new MarkdownLeptaPanelState();

        Assert.That(panel.StatusBadgeState, Is.EqualTo(LeptaPanelStateBase.StatusBadgeStateNone));
        Assert.That(panel.StatusBadgeGlyph, Is.Empty);
        Assert.That(panel.StatusBadgeToolTip, Is.Null);
    }

    [Test]
    public void ApplyGenerationOutcome_SetsSuccessBadgeAndStatisticsTooltip()
    {
        var panel = new MermaidLeptaPanelState();

        panel.ApplyGenerationOutcome(estimatedTokenCount: 120, elapsed: TimeSpan.FromSeconds(2.5));

        Assert.That(panel.StatusBadgeState, Is.EqualTo(LeptaPanelStateBase.StatusBadgeStateSuccess));
        Assert.That(panel.StatusBadgeGlyph, Is.EqualTo("✓"));
        Assert.That(panel.StatusBadgeToolTip, Does.Contain("Generation time: 2.50 s"));
        Assert.That(panel.StatusBadgeToolTip, Does.Contain("Throughput: 48 tok/s"));
        Assert.That(panel.StatusBadgeToolTip, Does.Contain("Estimated output tokens: 120"));
    }

    [Test]
    public void RenderError_OverridesSuccessBadgeWithErrorDetails()
    {
        var panel = new MermaidLeptaPanelState();
        panel.ApplyGenerationOutcome(estimatedTokenCount: 40, elapsed: TimeSpan.FromSeconds(2));

        panel.SetRenderError("Mermaid syntax error near node A");

        Assert.That(panel.StatusBadgeState, Is.EqualTo(LeptaPanelStateBase.StatusBadgeStateError));
        Assert.That(panel.StatusBadgeGlyph, Is.EqualTo("✕"));
        Assert.That(panel.StatusBadgeToolTip, Does.Contain("Render failed: Mermaid syntax error near node A"));
        Assert.That(panel.StatusBadgeToolTip, Does.Contain("Generation time: 2.00 s"));
    }

    [Test]
    public void RenderRepairInProgress_ShowsRunningBadgeWithAttemptMessage()
    {
        var panel = new MermaidLeptaPanelState();

        panel.SetRenderRepairStatus(isInProgress: true, message: "Attempt 1 of 3: repairing Mermaid diagram...");

        Assert.That(panel.StatusBadgeState, Is.EqualTo(LeptaPanelStateBase.StatusBadgeStateRunning));
        Assert.That(panel.StatusBadgeGlyph, Is.EqualTo("…"));
        Assert.That(panel.StatusBadgeToolTip, Is.EqualTo("Attempt 1 of 3: repairing Mermaid diagram..."));
    }

    [Test]
    public void RenderRepairSuccess_AddsRepairMessageToSuccessTooltip()
    {
        var panel = new MermaidLeptaPanelState();
        panel.ApplyGenerationOutcome(estimatedTokenCount: 64, elapsed: TimeSpan.FromSeconds(2));

        panel.SetRenderRepairStatus(isInProgress: false, message: "Mermaid auto-repair succeeded on attempt 2.");

        Assert.That(panel.StatusBadgeState, Is.EqualTo(LeptaPanelStateBase.StatusBadgeStateSuccess));
        Assert.That(panel.StatusBadgeToolTip, Does.Contain("Mermaid auto-repair succeeded on attempt 2."));
        Assert.That(panel.StatusBadgeToolTip, Does.Contain("Throughput: 32 tok/s"));
    }

    [Test]
    public void ResetRunState_ClearsBadgeAndTooltip()
    {
        var panel = new MarkdownLeptaPanelState();
        panel.ApplyGenerationOutcome(estimatedTokenCount: 10, elapsed: TimeSpan.FromSeconds(1), errorMessage: "HTTP 500");
        panel.SetRenderError("Broken preview");

        panel.ResetRunState();

        Assert.That(panel.StatusBadgeState, Is.EqualTo(LeptaPanelStateBase.StatusBadgeStateNone));
        Assert.That(panel.StatusBadgeGlyph, Is.Empty);
        Assert.That(panel.StatusBadgeToolTip, Is.Null);
        Assert.That(panel.Status, Is.Empty);
    }

    [Test]
    public void PlainTextPanelState_Format_IsPlainText()
    {
        var panel = new PlainTextLeptaPanelState();

        Assert.That(panel.Format, Is.EqualTo(LeptaPanelFormats.PlainText));
    }

    [Test]
    public void Factory_Create_WithPlainTextFormat_ReturnsPlainTextPanelState()
    {
        var panel = LeptaPanelStateFactory.Create(LeptaPanelFormats.PlainText, "Notes", "Be concise", "#FF0000");

        Assert.That(panel, Is.TypeOf<PlainTextLeptaPanelState>());
        Assert.That(panel.Format, Is.EqualTo(LeptaPanelFormats.PlainText));
        Assert.That(panel.Name, Is.EqualTo("Notes"));
        Assert.That(panel.CustomInstruction, Is.EqualTo("Be concise"));
    }

    [Test]
    public void Factory_Convert_ToPlainText_PreservesRuntimeState()
    {
        var source = new MarkdownLeptaPanelState();
        source.ApplyGenerationOutcome(estimatedTokenCount: 50, elapsed: TimeSpan.FromSeconds(1));
        source.SetRenderError("some error");

        var converted = LeptaPanelStateFactory.Convert(source, LeptaPanelFormats.PlainText);

        Assert.That(converted, Is.TypeOf<PlainTextLeptaPanelState>());
        Assert.That(converted.StatusBadgeState, Is.EqualTo(LeptaPanelStateBase.StatusBadgeStateError));
        Assert.That(((LeptaPanelStateBase)converted).RenderErrorMessage, Is.EqualTo("some error"));
    }
}

