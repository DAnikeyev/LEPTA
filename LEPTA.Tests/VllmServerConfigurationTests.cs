using LEPTA.vLLM.Models;

namespace LEPTA.Tests;

[TestFixture]
public sealed class VllmServerConfigurationTests
{
    [Test]
    public void IsLeptaManagedDeploymentActive_ReturnsFalse_ForExternalServer()
    {
        var configuration = new VllmServerConfiguration
        {
            UseExistingHttpServer = true,
            UiStatusKind = "Ready"
        };

        Assert.That(configuration.IsLeptaManagedDeploymentActive, Is.False);
    }

    [Test]
    public void IsLeptaManagedDeploymentActive_ReturnsFalse_ForLocalServerThatIsNotReady()
    {
        var configuration = new VllmServerConfiguration
        {
            UseExistingHttpServer = false,
            UiStatusKind = "Warning"
        };

        Assert.That(configuration.IsLeptaManagedDeploymentActive, Is.False);
    }

    [Test]
    public void IsLeptaManagedDeploymentActive_ReturnsTrue_ForReadyLocalServer()
    {
        var configuration = new VllmServerConfiguration
        {
            UseExistingHttpServer = false,
            UiStatusKind = "Ready"
        };

        Assert.That(configuration.IsLeptaManagedDeploymentActive, Is.True);
    }
}

