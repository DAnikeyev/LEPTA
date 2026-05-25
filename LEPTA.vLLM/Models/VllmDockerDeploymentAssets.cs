namespace LEPTA.vLLM.Models;

public sealed record VllmDockerDeploymentAssets(
    string ComposeText,
    string DockerfileText,
    string EntrypointScriptText);
