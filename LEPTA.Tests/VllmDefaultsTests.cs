using LEPTA.vLLM.Configuration;
using LEPTA.vLLM.Models;

namespace LEPTA.Tests;

[TestFixture]
public sealed class VllmDefaultsTests
{
    [Test]
    public void CreateServers_OpenRouterProfile_ShipsRecommendedHeadersAndBearerAuth()
    {
        var openRouter = VllmDefaults.CreateServers().Single(server => server.UseExistingHttpServer);

        Assert.Multiple(() =>
        {
            Assert.That(openRouter.HttpServerAddress, Is.EqualTo(VllmDefaults.OpenRouterEndpoint));
            Assert.That(openRouter.RequestOverrides.AuthHeaderName, Is.EqualTo("Authorization"));
            Assert.That(openRouter.RequestOverrides.AuthHeaderScheme, Is.EqualTo("Bearer"));
            Assert.That(openRouter.RequestOverrides.Headers, Contains.Key("HTTP-Referer"));
            Assert.That(openRouter.RequestOverrides.Headers, Contains.Key("X-Title"));
            Assert.That(openRouter.RequestOverrides.ExtraBody, Is.Empty);
        });
    }

    [Test]
    public void OpenRouterRecommendedHeaders_AreStableAndNonEmpty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VllmDefaults.OpenRouterRecommendedHeaders["HTTP-Referer"], Is.Not.Empty);
            Assert.That(VllmDefaults.OpenRouterRecommendedHeaders["X-Title"], Is.Not.Empty);
            Assert.That(VllmDefaults.OpenRouterRecommendedHeaders.Count, Is.EqualTo(2));
        });
    }

    [Test]
    public void BuildOpenRouterOverrides_ReturnsBearerAuthAndHeadersWithoutKeyOrExtraBody()
    {
        var overrides = VllmDefaults.BuildOpenRouterOverrides();

        Assert.Multiple(() =>
        {
            Assert.That(overrides.AuthHeaderName, Is.EqualTo("Authorization"));
            Assert.That(overrides.AuthHeaderScheme, Is.EqualTo("Bearer"));
            Assert.That(overrides.Headers, Contains.Key("HTTP-Referer"));
            Assert.That(overrides.Headers, Contains.Key("X-Title"));
            Assert.That(overrides.ExtraBody, Is.Empty);
            Assert.That(overrides.ApiKey, Is.Null);
        });
    }

    [Test]
    public void BuildOpenRouterOverrides_ReturnsIndependentInstances()
    {
        // Mutating one returned override must not bleed into the shared recommended-headers table.
        var first = VllmDefaults.BuildOpenRouterOverrides();
        var second = VllmDefaults.BuildOpenRouterOverrides();

        first.Headers["HTTP-Referer"] = "mutated";

        Assert.Multiple(() =>
        {
            Assert.That(second.Headers["HTTP-Referer"], Is.Not.EqualTo("mutated"));
            Assert.That(VllmDefaults.OpenRouterRecommendedHeaders["HTTP-Referer"], Is.Not.EqualTo("mutated"));
        });
    }
}
