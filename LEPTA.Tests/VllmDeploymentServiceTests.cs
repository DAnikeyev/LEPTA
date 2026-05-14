using System.Net;
using System.Net.Sockets;
using System.Text;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class VllmDeploymentServiceTests
{
    [Test]
    public async Task IsAccessibleAsync_LogsModelsRequestAndSuccess()
    {
        var logger = new TestLeptaLogger();
        using var http = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "data": [
                    {
                      "id": "Qwen3.5-9B-local"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        }));

        var service = new VllmDeploymentService(http, logger);
        var accessible = await service.IsAccessibleAsync(new VllmServerConfiguration
        {
            UseExistingHttpServer = true,
            HttpServerAddress = "http://localhost:8512"
        });

        Assert.That(accessible, Is.True);
        Assert.That(logger.Entries.Any(entry => entry.Contains("GET http://localhost:8512/v1/models", StringComparison.Ordinal) || entry.Contains("Checking model accessibility", StringComparison.Ordinal)), Is.True);
        Assert.That(logger.Entries.Any(entry => entry.Contains("accessible=true", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task GetFirstModelNameAsync_LogsResolvedModel()
    {
        var logger = new TestLeptaLogger();
        using var http = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "data": [
                    {
                      "id": "Qwen3.5-9B-local"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        }));

        var service = new VllmDeploymentService(http, logger);
        var model = await service.GetFirstModelNameAsync(new VllmServerConfiguration
        {
            UseExistingHttpServer = true,
            HttpServerAddress = "http://localhost:8512"
        });

        Assert.That(model, Is.EqualTo("Qwen3.5-9B-local"));
        Assert.That(logger.Entries.Any(entry => entry.Contains("Resolving first served model", StringComparison.Ordinal)), Is.True);
        Assert.That(logger.Entries.Any(entry => entry.Contains("Resolved served model 'Qwen3.5-9B-local'", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task ProbeHttpServerAsync_ReturnsEmptyModelList_WhenNoServedModelsAreAvailable()
    {
        using var http = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "data": []
                }
                """,
                Encoding.UTF8,
                "application/json")
        }));

        var service = new VllmDeploymentService(http);
        var probe = await service.ProbeHttpServerAsync(new VllmServerConfiguration
        {
            UseExistingHttpServer = true,
            HttpServerAddress = "http://localhost:8512"
        });

        Assert.That(probe.IsSuccess, Is.False);
        Assert.That(probe.Status, Is.EqualTo(VllmServerProbeStatus.EmptyModelList));
        Assert.That(probe.Message, Does.Contain("empty model list"));
    }

    [Test]
    public async Task ProbeHttpServerAsync_ReturnsInvalidEndpoint_ForMalformedAddress()
    {
        var service = new VllmDeploymentService();
        var probe = await service.ProbeHttpServerAsync(new VllmServerConfiguration
        {
            UseExistingHttpServer = true,
            HttpServerAddress = "http://"
        });

        Assert.That(probe.IsSuccess, Is.False);
        Assert.That(probe.Status, Is.EqualTo(VllmServerProbeStatus.InvalidEndpoint));
        Assert.That(probe.Message, Does.Contain("valid HTTP or HTTPS server address"));
    }

    [Test]
    public async Task ValidateDeploymentAsync_RejectsMissingDockerImageAndContainerNameConflict()
    {
        var service = new VllmDeploymentService(
            dockerCommandRunner: (arguments, _) => Task.FromResult(arguments.StartsWith("ps -a", StringComparison.Ordinal)
                ? new DockerCommandResult(0, "lepta-vllm-conflict\n", string.Empty)
                : new DockerCommandResult(0, "27.0.1", string.Empty)));

        var result = await service.ValidateDeploymentAsync(new VllmServerConfiguration
        {
            Name = "Conflict",
            UseExistingHttpServer = false,
            DockerImage = " ",
            Model = "meta-llama/Llama-3.2-3B-Instruct"
        }, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Contains("Docker image"));
        Assert.That(result.Errors, Has.Some.Contains("already has a container named 'lepta-vllm-conflict'"));
    }

    [Test]
    public async Task ValidateDeploymentAsync_WarnsWhenTheRequestedHostPortIsAlreadyInUse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var service = new VllmDeploymentService(
            dockerCommandRunner: (arguments, _) => Task.FromResult(arguments.StartsWith("ps -a", StringComparison.Ordinal)
                ? new DockerCommandResult(0, string.Empty, string.Empty)
                : new DockerCommandResult(0, "27.0.1", string.Empty)));

        var result = await service.ValidateDeploymentAsync(new VllmServerConfiguration
        {
            Name = "Port Warning",
            UseExistingHttpServer = false,
            DockerImage = "vllm/vllm-openai:latest",
            Model = "meta-llama/Llama-3.2-3B-Instruct",
            HostPort = port
        }, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Warnings, Has.Some.Contains($"Port {port} is already in use"));
    }

    [Test]
    public async Task DeployAsync_WaitsForModelsEndpointBeforeCompleting()
    {
        var composeDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var progressMessages = new List<string>();
        var commands = new List<string>();
        var probeAttempts = 0;

        try
        {
            using var http = new HttpClient(new StubHttpMessageHandler(_ =>
            {
                probeAttempts++;
                if (probeAttempts == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        ReasonPhrase = "warming up"
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "data": [
                            {
                              "id": "llama-3.2-3b-local"
                            }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }));

            var service = new VllmDeploymentService(
                http,
                dockerCommandRunner: (arguments, _) =>
                {
                    commands.Add(arguments);
                    return Task.FromResult(arguments.Contains("compose", StringComparison.Ordinal)
                        ? new DockerCommandResult(0, "started", string.Empty)
                        : new DockerCommandResult(0, "27.0.1", string.Empty));
                });

            await service.DeployAsync(new VllmServerConfiguration
            {
                Name = "Docker Deploy",
                UseExistingHttpServer = false,
                DockerImage = "vllm/vllm-openai:latest",
                Model = "meta-llama/Llama-3.2-3B-Instruct",
                HostPort = 8612,
                EnableVerboseLogs = false
            }, composeDirectory, new Progress<string>(message => progressMessages.Add(message)));

            Assert.That(commands.Any(command => command.StartsWith("info --format", StringComparison.Ordinal)), Is.True);
            Assert.That(commands.Any(command => command.StartsWith("ps -a --format", StringComparison.Ordinal)), Is.True);
            Assert.That(commands.Any(command => command.Contains("compose -f", StringComparison.Ordinal) && command.EndsWith("up -d", StringComparison.Ordinal)), Is.True);
            Assert.That(progressMessages.Any(message => message.Contains("Waiting for http://localhost:8612/v1/models", StringComparison.Ordinal)), Is.True);
            Assert.That(progressMessages.Any(message => message.Contains("Deployment ready. Using 'llama-3.2-3b-local'", StringComparison.Ordinal)), Is.True);
        }
        finally
        {
            if (Directory.Exists(composeDirectory))
            {
                Directory.Delete(composeDirectory, recursive: true);
            }
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}

