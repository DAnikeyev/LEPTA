using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class VllmAdditionalArgumentsSanitizerTests
{
    [Test]
    public void Normalize_RemovesDeprecatedSwapSpaceAndLegacyDisableLogRequestsFalse()
    {
        var result = VllmAdditionalArgumentsSanitizer.Normalize(
            "--swap-space 8 --disable-log-requests false --speculative-config '{\"method\":\"qwen3_next_mtp\"}'");

        Assert.That(result.Arguments, Is.EqualTo(["--cpu-offload-gb", "8", "--speculative-config", "{\"method\":\"qwen3_next_mtp\"}"]));
        Assert.That(result.Warnings, Has.Some.Contains("--swap-space"));
        Assert.That(result.Warnings, Has.Some.Contains("--disable-log-requests false"));
    }

    [Test]
    public void Normalize_DropsLegacySwapSpace_WhenModernCpuOffloadIsAlreadyPresent()
    {
        var result = VllmAdditionalArgumentsSanitizer.Normalize("--swap-space 8 --cpu-offload-gb 4");

        Assert.That(result.Arguments, Is.EqualTo(["--cpu-offload-gb", "4"]));
        Assert.That(result.Warnings, Has.Some.Contains("Ignored deprecated additional vLLM flag '--swap-space'"));
    }

    [Test]
    public void Normalize_TranslatesLegacyDisableLogRequestsToModernFlag()
    {
        var result = VllmAdditionalArgumentsSanitizer.Normalize("--disable-log-requests true");

        Assert.That(result.Arguments, Is.EqualTo(["--no-enable-log-requests"]));
        Assert.That(result.Warnings, Has.Some.Contains("--no-enable-log-requests"));
    }

    [Test]
    public void Normalize_ConvertsBooleanEnableLogRequestsSyntax()
    {
        var result = VllmAdditionalArgumentsSanitizer.Normalize("--enable-log-requests=false");

        Assert.That(result.Arguments, Is.EqualTo(["--no-enable-log-requests"]));
        Assert.That(result.Warnings, Has.Some.Contains("boolean syntax expected by current vLLM versions"));
    }

    [Test]
    public void Parse_ThrowsForMalformedQuotedArguments()
    {
        Assert.That(
            () => VllmAdditionalArgumentsSanitizer.Parse("--speculative-config 'broken"),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("unfinished quote or escape sequence"));
    }
}

