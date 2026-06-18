using LEPTA.vLLM.Models;

namespace LEPTA.Tests;

[TestFixture]
public sealed class VllmServerCalculationsTests
{
    [Test]
    public void SanitizeContainerName_LowercasesAndCollapsesNonAlphanumerics()
    {
        Assert.That(VllmServerCalculations.SanitizeContainerName("Qwen 3.5 / AWQ"), Is.EqualTo("qwen-3-5-awq"));
    }

    [Test]
    public void SanitizeContainerName_TrimsLeadingTrailingSeparators()
    {
        Assert.That(VllmServerCalculations.SanitizeContainerName("  --name--  "), Is.EqualTo("name"));
    }

    [Test]
    public void SanitizeContainerName_FallsBackForEmptyInput()
    {
        Assert.That(VllmServerCalculations.SanitizeContainerName("   "), Is.EqualTo("server"));
        Assert.That(VllmServerCalculations.SanitizeContainerName("!!!"), Is.EqualTo("server"));
    }

    [Test]
    public void NormalizeHttpServerAddress_AddsSchemeAndStripsTrailingSlash()
    {
        Assert.That(VllmServerCalculations.NormalizeHttpServerAddress("localhost:8512/", 8512), Is.EqualTo("http://localhost:8512"));
    }

    [Test]
    public void NormalizeHttpServerAddress_PreservesHttpsScheme()
    {
        Assert.That(VllmServerCalculations.NormalizeHttpServerAddress("https://openrouter.ai/api", 443), Is.EqualTo("https://openrouter.ai/api"));
    }

    [Test]
    public void NormalizeHttpServerAddress_FallsBackToHostPortWhenBlank()
    {
        Assert.That(VllmServerCalculations.NormalizeHttpServerAddress(null, 9000), Is.EqualTo("http://localhost:9000"));
        Assert.That(VllmServerCalculations.NormalizeHttpServerAddress("   ", 9000), Is.EqualTo("http://localhost:9000"));
    }

    [Test]
    public void ResolveModelLabel_PrefersLocalFolderName()
    {
        Assert.That(VllmServerCalculations.ResolveModelLabel("My Server", "org/model-id", @"C:\models\My Folder"), Is.EqualTo("My Folder"));
    }

    [Test]
    public void ResolveModelLabel_UsesLastModelIdSegmentOtherwise()
    {
        Assert.That(VllmServerCalculations.ResolveModelLabel("My Server", "cyankiwi/Qwen3.5-9B-AWQ", null), Is.EqualTo("Qwen3.5-9B-AWQ"));
    }

    [Test]
    public void ResolveModelLabel_FallsBackToSanitizedName()
    {
        Assert.That(VllmServerCalculations.ResolveModelLabel("Cool Server", null, null), Is.EqualTo("cool-server"));
    }

    [TestCase("qwen3", ExpectedResult = true)]
    [TestCase("Qwen3.5-9B", ExpectedResult = true)]
    [TestCase("llama-3", ExpectedResult = false)]
    [TestCase("", ExpectedResult = false)]
    public bool LooksLikeQwen_DetectsQwenModels(string value) => VllmServerCalculations.LooksLikeQwen(value);

    [TestCase("deepseek-reasoning", ExpectedResult = true)]
    [TestCase("qwen3", ExpectedResult = true)]
    [TestCase("gpt-oss", ExpectedResult = false)]
    public bool LooksLikeThinkingModel_DetectsReasoningModels(string value) => VllmServerCalculations.LooksLikeThinkingModel(value);

    [Test]
    public void ResolveSuggestedAdditionalVllmArguments_ReturnsQwenArgsForQwenModel()
    {
        var result = VllmServerCalculations.ResolveSuggestedAdditionalVllmArguments(
            "Qwen 3.5", null, null, null, null, "QWEN_ARGS");

        Assert.That(result, Is.EqualTo("QWEN_ARGS"));
    }

    [Test]
    public void ResolveSuggestedAdditionalVllmArguments_ReturnsEmptyForNonQwen()
    {
        var result = VllmServerCalculations.ResolveSuggestedAdditionalVllmArguments(
            "Llama", "llama-3", null, "llama", null, "QWEN_ARGS");

        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void TokenBudgets_RespectFloorAndRatio()
    {
        Assert.That(VllmServerCalculations.ResolveMaxOutputTokens(8192), Is.EqualTo(2048));
        Assert.That(VllmServerCalculations.ResolveMaxContextTokens(8192), Is.EqualTo(4096));
        Assert.That(VllmServerCalculations.ResolveMaxDocumentTokens(8192), Is.EqualTo(6144));
    }

    [Test]
    public void TokenBudgets_FloorSmallMaxModelLength()
    {
        Assert.That(VllmServerCalculations.ResolveMaxOutputTokens(100), Is.EqualTo(256));
        Assert.That(VllmServerCalculations.ResolveMaxContextTokens(100), Is.EqualTo(512));
        Assert.That(VllmServerCalculations.ResolveMaxDocumentTokens(100), Is.EqualTo(512));
    }

    [Test]
    public void ResolveUiTypeLabel_ClassifiesExternalVsLocal()
    {
        Assert.That(VllmServerCalculations.ResolveUiTypeLabel(true), Is.EqualTo("External server"));
        Assert.That(VllmServerCalculations.ResolveUiTypeLabel(false), Is.EqualTo("LEPTA-managed local"));
    }

    [Test]
    public void ResolveUiEndpointLabel_NormalizesExternalAddress()
    {
        Assert.That(
            VllmServerCalculations.ResolveUiEndpointLabel(true, "https://openrouter.ai/api/", 443),
            Is.EqualTo("https://openrouter.ai/api"));
    }

    [Test]
    public void ResolveUiEndpointLabel_ShowsLocalhostForManagedDeployment()
    {
        Assert.That(
            VllmServerCalculations.ResolveUiEndpointLabel(false, "http://localhost:8512", 8512),
            Is.EqualTo("Runs at http://localhost:8512"));
    }
}
