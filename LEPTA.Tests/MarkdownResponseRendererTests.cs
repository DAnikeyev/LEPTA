using System.Windows.Documents;
using System.Windows.Controls;
using LEPTA.Controls;
using LEPTA.Services;
using LEPTA.Shared.Models;

namespace LEPTA.Tests;

[TestFixture]
[Apartment(System.Threading.ApartmentState.STA)]
public sealed class MarkdownResponseRendererTests
{
    [Test]
    public void MermaidRenderer_WithRawMermaidText_RendersDiagramView()
    {
        var renderer = new PanelResponseRendererRegistry().Resolve(LeptaPanelFormats.Mermaid);

        var document = renderer.BuildDocument("graph TD; A-->B;", 14);

        Assert.That(document.Blocks.FirstBlock, Is.TypeOf<BlockUIContainer>());
        Assert.That(((BlockUIContainer)document.Blocks.FirstBlock!).Child, Is.TypeOf<MermaidDiagramView>());
    }

    [Test]
    public void MermaidRenderer_WithMermaidFence_RendersDiagramView()
    {
        var renderer = new PanelResponseRendererRegistry().Resolve(LeptaPanelFormats.Mermaid);

        var document = renderer.BuildDocument("```mermaid\ngraph TD\n  A-->B\n```", 14);

        Assert.That(document.Blocks.FirstBlock, Is.TypeOf<BlockUIContainer>());
        Assert.That(((BlockUIContainer)document.Blocks.FirstBlock!).Child, Is.TypeOf<MermaidDiagramView>());
    }

    [TestCase(4, 12)]
    [TestCase(14, 20)]
    [TestCase(24, 33)]
    public void MermaidDiagramView_GetEffectiveFontSize_BoostsDiagramFont(double baseFontSize, double expectedFontSize)
    {
        Assert.That(MermaidDiagramView.GetEffectiveFontSize(baseFontSize), Is.EqualTo(expectedFontSize));
    }

    [Test]
    public void CollectMermaidSources_MermaidPanel_ReturnsTrimmedDiagram()
    {
        var sources = MarkdownResponseRenderer.CollectMermaidSources("graph TD; A-->B;", LeptaPanelFormats.Mermaid);

        Assert.That(sources, Has.Count.EqualTo(1));
        Assert.That(sources[0], Does.StartWith("graph TD; A-->B;"));
        Assert.That(sources[0], Does.Not.Contain(MermaidDiagramPalettePostProcessor.AppliedMarker));
    }

    [Test]
    public void MermaidSourceNormalizer_TrimsPerLineIndentation()
    {
        var indented = """
            flowchart TB
                                                                                                      User((User)) --> Router[Blazor Router]
                                                                                                      Router --> AppEntry[App.razor]
            """;

        var normalized = MermaidSourceNormalizer.Normalize(indented);

        Assert.That(normalized, Does.StartWith("flowchart TB"));
        Assert.That(normalized, Does.Not.Contain("                                                                                                      "));
        Assert.That(normalized, Does.Not.Contain(MermaidDiagramPalettePostProcessor.AppliedMarker));
        Assert.That(normalized.Split('\n').Length, Is.EqualTo(3));
    }

    [Test]
    public void MermaidSourceNormalizer_RemovesPaletteMarkerFromNormalizationPath()
    {
        var normalized = MermaidSourceNormalizer.Normalize("graph TD\nA-->B");
        var themed = MermaidDiagramPalettePostProcessor.Apply(normalized, isDarkTheme: true);

        Assert.That(normalized, Does.Not.Contain(MermaidDiagramPalettePostProcessor.AppliedMarker));
        Assert.That(themed, Does.Contain(MermaidDiagramPalettePostProcessor.AppliedMarker));
    }

    [Test]
    public void MermaidPalettePostProcessor_LightTheme_MapsExplicitWhiteTextToReadablePalette()
    {
        var source = """
            flowchart TD
            A[Alpha] --> B{Check}
            style A fill:#fef3c7,color:#ffffff,stroke:#ffffff
            """;

        var normalized = MermaidDiagramPalettePostProcessor.Apply(source, isDarkTheme: false);

        Assert.That(normalized, Does.Contain("style A fill:#FFF0CC,stroke:#CC9A2C,color:#1C2430"));
        Assert.That(normalized, Does.Contain("style B fill:#FFF0CC,stroke:#CC9A2C,color:#1C2430"));
    }

    [Test]
    public void MermaidPalettePostProcessor_DarkTheme_AssignsShapeDefaults()
    {
        var source = """
            flowchart TD
            Start((Start)) --> Step[Step]
            Step --> Choice{Choice}
            """;

        var normalized = MermaidDiagramPalettePostProcessor.Apply(source, isDarkTheme: true);

        Assert.That(normalized, Does.Contain("style Start fill:#1F5E4A,stroke:#5ACB96,color:#F4F6FA"));
        Assert.That(normalized, Does.Contain("style Step fill:#2B4268,stroke:#7EA6FF,color:#F4F6FA"));
        Assert.That(normalized, Does.Contain("style Choice fill:#6B4E16,stroke:#E7B84E,color:#F4F6FA"));
    }

    [Test]
    public void MermaidPalettePostProcessor_IsIdempotent()
    {
        const string source = "flowchart TD\nA[Task] --> B{Choice}";

        var once = MermaidDiagramPalettePostProcessor.Apply(source, isDarkTheme: true);
        var twice = MermaidDiagramPalettePostProcessor.Apply(once, isDarkTheme: true);

        Assert.That(twice, Is.EqualTo(once));
    }

    [Test]
    public void PlainTextRenderer_ResolvesFromRegistry()
    {
        var renderer = new PanelResponseRendererRegistry().Resolve(LeptaPanelFormats.PlainText);

        Assert.That(renderer, Is.TypeOf<PlainTextResponseRenderer>());
        Assert.That(renderer.Format, Is.EqualTo(LeptaPanelFormats.PlainText));
    }

    [Test]
    public void PlainTextRenderer_DoesNotParseMarkdown()
    {
        var renderer = new PlainTextResponseRenderer();
        var text = "# This is not a heading\n**no bold**";

        var document = renderer.BuildDocument(text, 14);

        Assert.That(document.Blocks.Count, Is.EqualTo(1));
        Assert.That(document.Blocks.FirstBlock, Is.TypeOf<Paragraph>());
        var paragraph = (Paragraph)document.Blocks.FirstBlock!;
        var inlineText = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        Assert.That(inlineText, Is.EqualTo(text));
    }

    [Test]
    public void MarkdownRenderer_WithThinkBlock_RendersCollapsedSecondaryExpander()
    {
        var renderer = new PanelResponseRendererRegistry().Resolve(LeptaPanelFormats.Markdown);

        var document = renderer.BuildDocument("<think>Hidden reasoning</think>Visible answer.", 14);

        Assert.That(document.Blocks.Count, Is.EqualTo(2));
        Assert.That(document.Blocks.FirstBlock, Is.TypeOf<BlockUIContainer>());
        var expander = (Expander)((BlockUIContainer)document.Blocks.FirstBlock!).Child;
        Assert.That(expander.IsExpanded, Is.False);
        Assert.That(expander.Header, Is.TypeOf<TextBlock>());
        var header = (TextBlock)expander.Header;
        Assert.That(header.Text, Is.EqualTo("Thinking"));
        Assert.That(header.FontSize, Is.LessThan(14));
        Assert.That(expander.Content, Is.TypeOf<Border>());
        var border = (Border)expander.Content;
        Assert.That(border.Child, Is.TypeOf<RichTextBox>());
        var body = (RichTextBox)border.Child;
        Assert.That(body.FontSize, Is.LessThan(14));
        Assert.That(new TextRange(body.Document.ContentStart, body.Document.ContentEnd).Text.Trim(), Is.EqualTo("Hidden reasoning"));
    }

    [Test]
    public void PlainTextRenderer_WithEmptyText_ReturnsEmptyDocument()
    {
        var renderer = new PlainTextResponseRenderer();

        var document = renderer.BuildDocument("   ", 14);

        Assert.That(document.Blocks.Count, Is.EqualTo(0));
    }
}