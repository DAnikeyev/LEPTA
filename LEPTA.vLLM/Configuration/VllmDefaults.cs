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

    public const string VllmModelNote = "For Docker-managed local deployments, choose a vLLM-compatible Hugging Face Transformers-style folder with config.json, tokenizer files, and .safetensors/.bin weights. It does not need to come directly from huggingface.co, but GGUF or llama.cpp/Ollama-style folders are not suitable for this workflow. You can also leave Local folder empty and enter a Hugging Face model ID instead.";
}

