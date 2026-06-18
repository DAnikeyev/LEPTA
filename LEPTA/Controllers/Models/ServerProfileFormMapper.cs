using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Controllers.Models;

/// <summary>
/// Pure, WPF-free mapping between <see cref="VllmServerConfiguration"/> and the editable
/// <see cref="ServerProfileFormState"/> shown on the Models screen, plus the request-override
/// text parsing/formatting helpers. Extracted from <see cref="ModelsController"/> so the form
/// round-trip and override parsing are unit-testable without instantiating WPF controls.
/// </summary>
internal static class ServerProfileFormMapper
{
    /// <summary>Writes the editable form fields onto the server (does not touch LocalModelPath).</summary>
    public static void Apply(VllmServerConfiguration server, ServerProfileFormState state)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(state);

        server.Name = state.Name;
        server.UseExistingHttpServer = state.UseExistingHttpServer;
        server.HttpServerAddress = state.HttpServerAddress;
        server.Model = state.Model;
        server.ServedModelName = string.IsNullOrWhiteSpace(state.ServedModelName)
            ? null
            : state.ServedModelName.Trim();
        server.DockerImage = string.IsNullOrWhiteSpace(state.DockerImage)
            ? string.Empty
            : state.DockerImage.Trim();

        if (state.HostPort is { } hostPort) server.HostPort = hostPort;
        if (state.DType is { } dtype) server.DType = dtype;
        if (state.GpuMemoryUtilization is { } gpuMemoryUtilization) server.GpuMemoryUtilization = gpuMemoryUtilization;
        if (state.GpuVramGb is { } gpuVramGb) server.GpuVramGb = gpuVramGb;
        if (state.MaxModelLength is { } maxModelLength) server.MaxModelLength = maxModelLength;
        if (state.ReadyTimeoutMinutes is { } readyTimeoutMinutes) server.ReadyTimeoutMinutes = readyTimeoutMinutes;
        if (state.CpuOffloadGb is { } cpuOffloadGb) server.CpuOffloadGb = cpuOffloadGb;
        if (state.WeightQuantization is { } weightQuantization) server.WeightQuantization = weightQuantization;
        if (state.TensorParallelSize is { } tensorParallelSize) server.TensorParallelSize = tensorParallelSize;
        if (state.KCacheQuantization is { } kCacheQuantization) server.KCacheQuantization = kCacheQuantization;
        if (state.VCacheQuantization is { } vCacheQuantization) server.VCacheQuantization = vCacheQuantization;
        server.KvCacheDType = ResolveKvCacheDType(server.KCacheQuantization, server.VCacheQuantization, server.KvCacheDType);
        if (state.EnableTokenizersParallelism is { } enableTokenizersParallelism)
        {
            server.EnableTokenizersParallelism = enableTokenizersParallelism;
        }
        server.AdditionalVllmArguments = state.AdditionalVllmArguments?.Trim() ?? string.Empty;
        if (state.MaxNumSeqs is { } maxNumSeqs) server.MaxNumSeqs = maxNumSeqs;
        server.EnableVerboseLogs = state.EnableVerboseLogs;
    }

    /// <summary>Builds a form-state snapshot from the server for display editing.</summary>
    public static ServerProfileFormState Build(VllmServerConfiguration server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return new ServerProfileFormState
        {
            Name = server.Name,
            UseExistingHttpServer = server.UseExistingHttpServer,
            HttpServerAddress = server.HttpServerAddress,
            Model = server.Model,
            LocalModelPath = server.LocalModelPath,
            ServedModelName = server.ServedModelName,
            DockerImage = server.EffectiveDockerImage,
            HostPort = server.HostPort,
            DType = server.DType,
            GpuMemoryUtilization = server.GpuMemoryUtilization,
            GpuVramGb = server.GpuVramGb,
            MaxModelLength = server.MaxModelLength,
            ReadyTimeoutMinutes = server.ReadyTimeoutMinutes,
            CpuOffloadGb = server.CpuOffloadGb,
            WeightQuantization = server.WeightQuantization,
            TensorParallelSize = server.TensorParallelSize,
            KCacheQuantization = server.KCacheQuantization,
            VCacheQuantization = server.VCacheQuantization,
            EnableTokenizersParallelism = server.EnableTokenizersParallelism,
            AdditionalVllmArguments = GetAdditionalVllmArgumentsForEditing(server),
            MaxNumSeqs = server.MaxNumSeqs,
            EnableVerboseLogs = server.EnableVerboseLogs,
        };
    }

    public static string ResolveKvCacheDType(string? kCache, string? vCache, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(kCache)
            && string.Equals(kCache, vCache, StringComparison.OrdinalIgnoreCase)
            && kCache is "fp8" or "fp16" or "bf16")
        {
            return kCache;
        }

        return fallback;
    }

    public static string FormatParameterCount(VllmServerConfiguration server)
    {
        var resolved = VllmMemoryEstimator.ResolveParameterCountBillions(server);
        var source = server.ParameterCountBillions > 0
            ? "model metadata"
            : "derived from model ID/name";
        return $"{resolved.ToString("0.###", CultureInfo.InvariantCulture)} B ({source})";
    }

    public static string GetAdditionalVllmArgumentsForEditing(VllmServerConfiguration server)
    {
        if (!string.IsNullOrWhiteSpace(server.AdditionalVllmArguments))
        {
            return server.AdditionalVllmArguments;
        }

        return VllmServerConfiguration.ResolveSuggestedAdditionalVllmArguments(
            server.Name,
            server.Model,
            server.LocalModelPath,
            server.DetectedArchitecture,
            server.ReasoningParser);
    }

    public static Dictionary<string, string> ParseExtraHeaders(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Length > 0)
            {
                result[name] = value;
            }
        }

        return result;
    }

    public static string FormatExtraHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, headers.Select(pair => $"{pair.Key}: {pair.Value}"));
    }

    public static string? TryParseExtraBody(string text, out Dictionary<string, JsonElement>? extraBody)
    {
        extraBody = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "Extra body must be a JSON object (e.g. {\"user\":\"id\"}).";
            }

            var result = new Dictionary<string, JsonElement>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }

            extraBody = result;
            return null;
        }
        catch (JsonException exception)
        {
            return $"Extra body is not valid JSON: {exception.Message}";
        }
    }

    public static string FormatExtraBody(IReadOnlyDictionary<string, JsonElement> extraBody)
    {
        if (extraBody is null || extraBody.Count == 0)
        {
            return string.Empty;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var pair in extraBody)
            {
                writer.WritePropertyName(pair.Key);
                pair.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
