using LEPTA.Models;

namespace LEPTA.Tests;

[TestFixture]
public sealed class LeptaPanelAccentPaletteTests
{
    [Test]
    public void Normalize_ReturnsDefault_WhenValueIsBlank()
    {
        Assert.That(LeptaPanelAccentPalette.Normalize("  "), Is.EqualTo(LeptaPanelAccentPalette.DefaultAccentColorHex));
    }

    [Test]
    public void Options_ExposeTwentyDistinctChoices_IncludingNeutralAndYellowOptions()
    {
        var distinctOptions = LeptaPanelAccentPalette.Options
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(LeptaPanelAccentPalette.Options, Has.Count.EqualTo(20));
            Assert.That(distinctOptions, Has.Length.EqualTo(20));
            Assert.That(LeptaPanelAccentPalette.Options, Does.Contain("#FFFFFF"));
            Assert.That(LeptaPanelAccentPalette.Options, Does.Contain("#000000"));
            Assert.That(LeptaPanelAccentPalette.Options, Does.Contain("#FACC15"));
        });
    }

    [TestCase("#2F6FED")]
    [TestCase("#16A34A")]
    [TestCase("#4f46e5")]
    public void GetRandomAccentColor_DoesNotReuseImmediatePreviousColor(string previousAccentColorHex)
    {
        var random = new Random(7);

        var selected = LeptaPanelAccentPalette.GetRandomAccentColor(previousAccentColorHex, random);

        Assert.That(selected, Is.Not.EqualTo(previousAccentColorHex).IgnoreCase);
        Assert.That(LeptaPanelAccentPalette.Options, Does.Contain(selected));
    }

    [Test]
    public void GetRandomAccentColor_UsesPalette_WhenPreviousColorIsMissing()
    {
        var random = new Random(3);

        var selected = LeptaPanelAccentPalette.GetRandomAccentColor(null, random);

        Assert.That(LeptaPanelAccentPalette.Options, Does.Contain(selected));
    }
}

