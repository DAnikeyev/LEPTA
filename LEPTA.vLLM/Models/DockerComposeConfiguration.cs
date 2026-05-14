namespace LEPTA.vLLM.Models;

public sealed record DockerComposeConfiguration
{
    public required VllmServerConfiguration Server { get; init; }

    public required string ComposeDirectory { get; init; }

    public string ComposeFilePath => Path.Combine(ComposeDirectory, $"{Server.ContainerName}.compose.yml");
}
