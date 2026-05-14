using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Configuration;

public static class VllmDefaults
{
    public static IReadOnlyList<VllmServerConfiguration> CreateServers() =>
    [
        new VllmServerConfiguration
        {
            Name = "Localhost vLLM (8512)",
            UseExistingHttpServer = true,
            HttpServerAddress = "http://localhost:8512",
            HostPort = 8512,
            EnableVerboseLogs = false
        }
    ];

    public const string VllmModelNote = "Use either an already deployed HTTP server or a LEPTA-managed Docker deployment. The default profile still points to http://localhost:8512 for quick testing, while Docker profiles validate compose settings, save compose files under %LOCALAPPDATA%\\Lepta\\vllm, and become available to Chat and LEPTA after /v1/models responds.";
}

