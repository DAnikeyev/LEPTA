using System.Globalization;
using System.Text;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class VllmDockerComposeBuilder
{
    private const string NewLine = "\n";

    public VllmDockerDeploymentAssets BuildAssets(DockerComposeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var server = configuration.Server;
        var modelMountTarget = ResolveModelMountTarget(server);
        var kvCacheDType = ResolveEffectiveKvCacheDType(server);
        var entrypointFileName = Path.GetFileName(configuration.EntrypointScriptPath);
        var dockerfileFileName = Path.GetFileName(configuration.DockerfilePath);
        var composeLines = new List<string>
        {
            "services:",
            "  vllm-dev:",
            "    build:",
            "      context: .",
            $"      dockerfile: {YamlQuote(dockerfileFileName)}",
            $"    image: {YamlQuote($"{server.ContainerName}:latest")}",
            $"    container_name: {YamlQuote(server.ContainerName)}",
            "    environment:",
            $"      VLLM_PORT: {YamlQuote($"${{VLLM_PORT:-{server.HostPort.ToString(CultureInfo.InvariantCulture)}}}")}",
            $"      VLLM_GPU_MEMORY_UTILIZATION: {YamlQuote($"${{VLLM_GPU_MEMORY_UTILIZATION:-{server.GpuMemoryUtilization.ToString(CultureInfo.InvariantCulture)}}}")}",
            $"      VLLM_MAX_MODEL_LEN: {YamlQuote($"${{VLLM_MAX_MODEL_LEN:-{server.MaxModelLength.ToString(CultureInfo.InvariantCulture)}}}")}",
            $"      VLLM_DTYPE: {YamlQuote($"${{VLLM_DTYPE:-{server.DType}}}")}",
            $"      VLLM_KV_CACHE_DTYPE: {YamlQuote($"${{VLLM_KV_CACHE_DTYPE:-{kvCacheDType}}}")}",
            $"      VLLM_MAX_NUM_SEQS: {YamlQuote($"${{VLLM_MAX_NUM_SEQS:-{server.MaxNumSeqs.ToString(CultureInfo.InvariantCulture)}}}")}",
            $"      TOKENIZERS_PARALLELISM: {YamlQuote($"${{TOKENIZERS_PARALLELISM:-{ToLowerInvariant(server.EnableTokenizersParallelism)}}}")}",
            "    ports:",
            $"      - {YamlQuote($"${{VLLM_PORT:-{server.HostPort.ToString(CultureInfo.InvariantCulture)}}}:${{VLLM_PORT:-{server.HostPort.ToString(CultureInfo.InvariantCulture)}}}")}",
            "    deploy:",
            "      resources:",
            "        reservations:",
            "          devices:",
            "            - driver: nvidia",
            "              count: all",
            "              capabilities: [gpu]",
            "    volumes:",
        };

        if (ShouldEmitCpuOffload(configuration.Server))
        {
            composeLines.Insert(15, $"      VLLM_CPU_OFFLOAD_GB: {YamlQuote($"${{VLLM_CPU_OFFLOAD_GB:-{server.CpuOffloadGb.ToString(CultureInfo.InvariantCulture)}}}")}");
        }

        if (!string.IsNullOrWhiteSpace(server.LocalModelPath) && modelMountTarget is not null)
        {
            composeLines.Add("      - type: bind");
            composeLines.Add($"        source: {YamlQuote(EscapePath(server.LocalModelPath))}");
            composeLines.Add($"        target: {YamlQuote(modelMountTarget)}");
            composeLines.Add("        read_only: true");
        }

        composeLines.Add($"      - {YamlQuote("hf_cache:/root/.cache/huggingface")}");
        composeLines.Add(string.Empty);
        composeLines.Add("volumes:");
        composeLines.Add("  hf_cache:");

        return new VllmDockerDeploymentAssets(
            string.Join(NewLine, composeLines),
            BuildDockerfile(server, entrypointFileName, kvCacheDType),
            BuildEntrypointScript(server, modelMountTarget));
    }

    public string Build(DockerComposeConfiguration configuration)
        => BuildAssets(configuration).ComposeText;

    private static string BuildDockerfile(VllmServerConfiguration configuration, string entrypointFileName, string kvCacheDType)
    {
        var lines = new List<string>
        {
            $"FROM {configuration.EffectiveDockerImage}",
            string.Empty,
            $"ARG VLLM_PORT={configuration.HostPort.ToString(CultureInfo.InvariantCulture)}",
            $"ARG VLLM_GPU_MEMORY_UTILIZATION={configuration.GpuMemoryUtilization.ToString(CultureInfo.InvariantCulture)}",
            $"ARG VLLM_MAX_MODEL_LEN={configuration.MaxModelLength.ToString(CultureInfo.InvariantCulture)}",
            $"ARG VLLM_DTYPE={configuration.DType}",
            $"ARG VLLM_KV_CACHE_DTYPE={kvCacheDType}",
            $"ARG VLLM_MAX_NUM_SEQS={configuration.MaxNumSeqs.ToString(CultureInfo.InvariantCulture)}",
            $"ARG TOKENIZERS_PARALLELISM={ToLowerInvariant(configuration.EnableTokenizersParallelism)}",
            string.Empty,
            "ENV NVIDIA_DISABLE_REQUIRE=true \\",
            "    VLLM_PORT=${VLLM_PORT} \\",
            "    VLLM_GPU_MEMORY_UTILIZATION=${VLLM_GPU_MEMORY_UTILIZATION} \\",
            "    VLLM_MAX_MODEL_LEN=${VLLM_MAX_MODEL_LEN} \\",
            "    VLLM_DTYPE=${VLLM_DTYPE} \\",
            "    VLLM_KV_CACHE_DTYPE=${VLLM_KV_CACHE_DTYPE} \\",
            "    VLLM_MAX_NUM_SEQS=${VLLM_MAX_NUM_SEQS} \\",
            "    TOKENIZERS_PARALLELISM=${TOKENIZERS_PARALLELISM}",
            string.Empty,
            $"COPY {entrypointFileName} /opt/lepta/{entrypointFileName}",
            string.Empty,
            $"RUN chmod +x /opt/lepta/{entrypointFileName}",
            string.Empty,
            $"EXPOSE {configuration.HostPort.ToString(CultureInfo.InvariantCulture)}",
            string.Empty,
            $"ENTRYPOINT [\"/opt/lepta/{entrypointFileName}\"]"
        };

        return string.Join(NewLine, lines);
    }

    private static string BuildEntrypointScript(VllmServerConfiguration configuration, string? modelMountTarget)
    {
        var modelArgument = string.IsNullOrWhiteSpace(configuration.LocalModelPath)
            ? configuration.Model.Trim()
            : modelMountTarget ?? "/models/active";
        var lines = new List<string>
        {
            "#!/bin/sh",
            "set -eu",
            string.Empty,
            "exec python3 -m vllm.entrypoints.openai.api_server \\",
            $"  --model {ShellQuote(modelArgument)} \\",
            $"  --served-model-name {ShellQuote(configuration.EffectiveServedModelName)} \\",
            "  --host 0.0.0.0 \\",
            "  --port \"${VLLM_PORT}\" \\",
            "  --dtype \"${VLLM_DTYPE}\" \\",
        };

        if (ShouldEmitQuantization(configuration.WeightQuantization))
        {
            lines.Add($"  --quantization {ShellQuote(configuration.WeightQuantization.ToLowerInvariant())} \\");
        }

        lines.Add("  --gpu-memory-utilization \"${VLLM_GPU_MEMORY_UTILIZATION}\" \\");
        lines.Add("  --max-model-len \"${VLLM_MAX_MODEL_LEN}\" \\");
        lines.Add("  --kv-cache-dtype \"${VLLM_KV_CACHE_DTYPE}\" \\");
        if (ShouldEmitCpuOffload(configuration))
        {
            lines.Add("  --cpu-offload-gb \"${VLLM_CPU_OFFLOAD_GB}\" \\");
        }

        if (configuration.EnablePrefixCaching)
        {
            lines.Add("  --enable-prefix-caching \\");
        }

        lines.Add("  --max-num-seqs \"${VLLM_MAX_NUM_SEQS}\" \\");
        lines.Add($"  --tensor-parallel-size {ShellQuote(configuration.TensorParallelSize.ToString(CultureInfo.InvariantCulture))} \\");
        if (configuration.EnforceEager)
        {
            lines.Add("  --enforce-eager \\");
        }

        if (configuration.LanguageModelOnly)
        {
            lines.Add("  --language-model-only \\");
        }

        if (!string.IsNullOrWhiteSpace(configuration.ReasoningParser))
        {
            lines.Add($"  --reasoning-parser {ShellQuote(configuration.ReasoningParser)} \\");
        }

        BuildVerboseCommandBlock(lines, configuration);
        BuildAdditionalArgumentsBlock(lines, configuration);
        lines.Add("  \"$@\"");
        return string.Join(NewLine, lines);
    }

    private static string? ResolveModelMountTarget(VllmServerConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.LocalModelPath))
        {
            return null;
        }

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(configuration.LocalModelPath));
        var sanitizedFolderName = string.IsNullOrWhiteSpace(folderName)
            ? string.Empty
            : SanitizePathSegment(folderName);
        return string.IsNullOrWhiteSpace(folderName)
            ? "/models/active"
            : string.IsNullOrWhiteSpace(sanitizedFolderName)
                ? "/models/active"
                : $"/models/{sanitizedFolderName}";
    }

    private static string ResolveEffectiveKvCacheDType(VllmServerConfiguration configuration)
    {
        var kCache = NormalizeKvCacheDType(configuration.KCacheQuantization);
        var vCache = NormalizeKvCacheDType(configuration.VCacheQuantization);
        if (!string.IsNullOrWhiteSpace(kCache)
            && string.Equals(kCache, vCache, StringComparison.OrdinalIgnoreCase))
        {
            return kCache;
        }

        return NormalizeKvCacheDType(configuration.KvCacheDType) ?? "fp8";
    }

    private static string? NormalizeKvCacheDType(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "fp8" or "fp16" or "bf16" => value.Trim().ToLowerInvariant(),
            _ => null
        };

    private static void BuildVerboseCommandBlock(ICollection<string> lines, VllmServerConfiguration configuration)
    {
        if (!configuration.EnableVerboseLogs)
        {
            return;
        }

        lines.Add("  --uvicorn-log-level debug \\");
        lines.Add("  --enable-log-requests \\");
    }

    private static void BuildAdditionalArgumentsBlock(ICollection<string> lines, VllmServerConfiguration configuration)
    {
        foreach (var argument in VllmAdditionalArgumentsSanitizer.Normalize(configuration.AdditionalVllmArguments).Arguments)
        {
            lines.Add($"  {ShellQuote(argument)} \\");
        }
    }

    private static bool ShouldEmitQuantization(string? quantization)
        => quantization?.Trim().ToLowerInvariant() switch
        {
            null or "" or "none" or "auto" or "fp16" or "float16" or "half" or "bf16" or "bfloat16" => false,
            _ => true
        };

    private static bool ShouldEmitCpuOffload(VllmServerConfiguration configuration)
        => configuration.CpuOffloadGb > 0;


    private static string EscapePath(string path) => path.Replace("\\", "/");

    private static string SanitizePathSegment(string value)
        => string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-')).Trim('-');

    private static string ShellQuote(string value)
        => $"'{value.Replace("'", "'\"'\"'")}'";

    private static string ToLowerInvariant(bool value)
        => value ? "true" : "false";

    private static string YamlQuote(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 2);
        builder.Append('\'');
        builder.Append(value.Replace("'", "''"));
        builder.Append('\'');
        return builder.ToString();
    }
}

