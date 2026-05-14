using System.Globalization;
using System.Text;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class VllmDockerComposeBuilder
{
    public string Build(VllmServerConfiguration configuration)
    {
        var modelArgument = string.IsNullOrWhiteSpace(configuration.LocalModelPath)
            ? configuration.Model
            : "/models/active";
        var containerPort = 8000;

        var lines = new List<string>
        {
            "services:",
            "  vllm:",
            $"    image: {YamlQuote(configuration.DockerImage)}",
            $"    container_name: {YamlQuote(configuration.ContainerName)}",
            "    ports:",
            $"      - {YamlQuote($"{configuration.HostPort}:{containerPort}")}",
            "    deploy:",
            "      resources:",
            "        reservations:",
            "          devices:",
            "            - driver: nvidia",
            "              count: all",
            "              capabilities: [gpu]",
            "    volumes:",
            $"      - {YamlQuote("hf_cache:/root/.cache/huggingface")}",
        };

        if (!string.IsNullOrWhiteSpace(configuration.LocalModelPath))
        {
            lines.Add($"      - {YamlQuote($"{EscapePath(configuration.LocalModelPath)}:/models/active:ro")}");
        }

        lines.Add("    command:");
        AppendCommandArgument(lines, "--host");
        AppendCommandArgument(lines, "0.0.0.0");
        AppendCommandArgument(lines, "--port");
        AppendCommandArgument(lines, containerPort.ToString(CultureInfo.InvariantCulture));
        AppendCommandArgument(lines, "--model");
        AppendCommandArgument(lines, modelArgument);
        AppendCommandArgument(lines, "--served-model-name");
        AppendCommandArgument(lines, configuration.EffectiveServedModelName);
        if (!string.IsNullOrWhiteSpace(configuration.DType))
        {
            AppendCommandArgument(lines, "--dtype");
            AppendCommandArgument(lines, configuration.DType);
        }

        AppendCommandArgument(lines, "--gpu-memory-utilization");
        AppendCommandArgument(lines, configuration.GpuMemoryUtilization.ToString(CultureInfo.InvariantCulture));
        AppendCommandArgument(lines, "--max-model-len");
        AppendCommandArgument(lines, configuration.MaxModelLength.ToString(CultureInfo.InvariantCulture));
        AppendCommandArgument(lines, "--kv-cache-dtype");
        AppendCommandArgument(lines, configuration.KvCacheDType);
        AppendCommandArgument(lines, "--swap-space");
        AppendCommandArgument(lines, configuration.SwapSpaceGb.ToString(CultureInfo.InvariantCulture));
        if (ShouldEmitQuantization(configuration.WeightQuantization))
        {
            AppendCommandArgument(lines, "--quantization");
            AppendCommandArgument(lines, configuration.WeightQuantization.ToLowerInvariant());
        }

        if (configuration.EnforceEager)
        {
            AppendCommandArgument(lines, "--enforce-eager");
        }

        AppendCommandArgument(lines, "--cpu-offload-gb");
        AppendCommandArgument(lines, configuration.CpuOffloadGb.ToString(CultureInfo.InvariantCulture));
        AppendCommandArgument(lines, "--max-num-seqs");
        AppendCommandArgument(lines, configuration.MaxNumSeqs.ToString(CultureInfo.InvariantCulture));
        AppendCommandArgument(lines, "--tensor-parallel-size");
        AppendCommandArgument(lines, configuration.TensorParallelSize.ToString(CultureInfo.InvariantCulture));
        if (configuration.EnablePrefixCaching)
        {
            AppendCommandArgument(lines, "--enable-prefix-caching");
        }

        if (configuration.LanguageModelOnly)
        {
            AppendCommandArgument(lines, "--language-model-only");
        }

        if (!string.IsNullOrWhiteSpace(configuration.ReasoningParser))
        {
            AppendCommandArgument(lines, "--reasoning-parser");
            AppendCommandArgument(lines, configuration.ReasoningParser);
        }

        BuildVerboseCommandBlock(lines, configuration);

        lines.Add(string.Empty);
        lines.Add("volumes:");
        lines.Add("  hf_cache:");
        return string.Join(Environment.NewLine, lines);
    }

    private static void BuildVerboseCommandBlock(ICollection<string> lines, VllmServerConfiguration configuration)
    {
        if (!configuration.EnableVerboseLogs)
        {
            return;
        }

        AppendCommandArgument(lines, "--uvicorn-log-level");
        AppendCommandArgument(lines, "debug");
        AppendCommandArgument(lines, "--disable-log-requests");
        AppendCommandArgument(lines, "false");
    }

    private static void AppendCommandArgument(ICollection<string> lines, string argument)
        => lines.Add($"      - {YamlQuote(argument)}");

    private static bool ShouldEmitQuantization(string? quantization)
        => quantization?.Trim().ToLowerInvariant() switch
        {
            null or "" or "none" or "auto" or "fp16" or "float16" or "half" or "bf16" or "bfloat16" => false,
            _ => true
        };

    private static string EscapePath(string path) => path.Replace("\\", "/");

    private static string YamlQuote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('\'');
        builder.Append(value.Replace("'", "''"));
        builder.Append('\'');
        return builder.ToString();
    }
}

