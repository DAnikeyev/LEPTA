using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class VllmServerProfileValidatorTests
{
    private readonly VllmServerProfileValidator validator = new();

    [Test]
    public void ValidateExternalEndpoint_RejectsEmptyEndpoint()
    {
        var result = validator.ValidateExternalEndpoint("   ");

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Message, Does.Contain("already deployed HTTP server address"));
    }

    [Test]
    public void ValidateExternalEndpoint_RejectsInvalidUri()
    {
        var result = validator.ValidateExternalEndpoint("http://");

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Message, Does.Contain("valid HTTP or HTTPS server address"));
    }

    [Test]
    public void ValidateExternalEndpoint_NormalizesMissingSchemeAndTrailingSlash()
    {
        var result = validator.ValidateExternalEndpoint("localhost:8512/");

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.NormalizedEndpoint, Is.EqualTo("http://localhost:8512"));
    }

    [Test]
    public void ValidateExternalEndpoint_DoesNotInjectLocalPortOntoCloudHttpsHost()
    {
        // OpenRouter and other cloud servers live on the https default port (443). Forcing the
        // local Docker host port onto them makes every probe time out.
        var result = validator.ValidateExternalEndpoint("https://openrouter.ai/api");

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.NormalizedEndpoint, Is.EqualTo("https://openrouter.ai/api"));
    }

    [Test]
    public void ValidateExternalEndpoint_PreservesExplicitPort()
    {
        var result = validator.ValidateExternalEndpoint("http://localhost:8512/api");

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.NormalizedEndpoint, Is.EqualTo("http://localhost:8512/api"));
    }
}
