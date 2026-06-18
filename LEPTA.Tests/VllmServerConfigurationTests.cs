using System.Text.Json;
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
            Runtime = { StatusKind = ServerStatusKind.Ready }
        };

        Assert.That(configuration.IsLeptaManagedDeploymentActive, Is.False);
    }

    [Test]
    public void IsLeptaManagedDeploymentActive_ReturnsFalse_ForLocalServerThatIsNotReady()
    {
        var configuration = new VllmServerConfiguration
        {
            UseExistingHttpServer = false,
            Runtime = { StatusKind = ServerStatusKind.Warning }
        };

        Assert.That(configuration.IsLeptaManagedDeploymentActive, Is.False);
    }

    [Test]
    public void IsLeptaManagedDeploymentActive_ReturnsTrue_ForReadyLocalServer()
    {
        var configuration = new VllmServerConfiguration
        {
            UseExistingHttpServer = false,
            Runtime = { StatusKind = ServerStatusKind.Ready }
        };

        Assert.That(configuration.IsLeptaManagedDeploymentActive, Is.True);
    }

    [Test]
    public void HasEstablishedConnection_ReadsRuntimeStatus()
    {
        var configuration = new VllmServerConfiguration();

        Assert.That(configuration.HasEstablishedConnection, Is.False);

        configuration.Runtime.StatusKind = ServerStatusKind.Ready;
        Assert.That(configuration.HasEstablishedConnection, Is.True);
    }

    [Test]
    public void RuntimeState_DefaultsToNotChecked()
    {
        var configuration = new VllmServerConfiguration();

        Assert.That(configuration.Runtime.StatusKind, Is.EqualTo(ServerStatusKind.Unknown));
        Assert.That(configuration.Runtime.StatusText, Is.EqualTo("Not checked"));
        Assert.That(configuration.Runtime.StatusDetails, Does.Contain("Check server"));
    }

    [Test]
    public void Runtime_IsNotSerialized()
    {
        var configuration = new VllmServerConfiguration
        {
            UseExistingHttpServer = true,
            Runtime = { StatusKind = ServerStatusKind.Ready, StatusText = "ZZZ_RUNTIME_SENTINEL" }
        };

        var json = JsonSerializer.Serialize(configuration);

        Assert.That(json, Does.Not.Contain("\"Runtime\""));
        Assert.That(json, Does.Not.Contain("ZZZ_RUNTIME_SENTINEL"));
    }

    [Test]
    public void EachConfiguration_GetsItsOwnRuntimeInstance()
    {
        var first = new VllmServerConfiguration();
        var second = new VllmServerConfiguration();

        first.Runtime.StatusKind = ServerStatusKind.Ready;

        Assert.That(second.Runtime.StatusKind, Is.EqualTo(ServerStatusKind.Unknown));
        Assert.That(ReferenceEquals(first.Runtime, second.Runtime), Is.False);
    }
}

