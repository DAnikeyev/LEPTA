namespace LEPTA.vLLM.Models;

public readonly record struct DockerCommandResult(int ExitCode, string Output, string Error);
