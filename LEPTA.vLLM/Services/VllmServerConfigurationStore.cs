using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LEPTA.Shared.Services;
using LEPTA.vLLM.Configuration;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class VllmServerConfigurationStore(AppDataPaths appDataPaths, JsonFileStore? fileStore = null)
{
    private readonly JsonFileStore fileStore = fileStore ?? new JsonFileStore();
    private static readonly JsonSerializerOptions MigrationOptions = new() { WriteIndented = true };

    public JsonLoadResult<VllmServerConfigurationsDocument> Load()
    {
        MigrateLegacyApiKey();
        return fileStore.Load(appDataPaths.ModelConfigurationsFilePath, CreateDefaultDocument);
    }

    public void Save(VllmServerConfigurationsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        fileStore.Save(appDataPaths.ModelConfigurationsFilePath, document);
    }

    private static VllmServerConfigurationsDocument CreateDefaultDocument() => new()
    {
        Servers = VllmDefaults.CreateServers().ToList()
    };

    /// <summary>
    /// Folds any legacy top-level <c>ApiKey</c> field into <c>RequestOverrides.ApiKey</c> so older
    /// stores load cleanly into the canonical overrides model. Idempotent; no-op for new files.
    /// </summary>
    private void MigrateLegacyApiKey()
    {
        var path = appDataPaths.ModelConfigurationsFilePath;
        if (!File.Exists(path))
        {
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        if (root is not JsonObject document
            || !TryGetArray(document, "Servers", out var servers))
        {
            return;
        }

        var changed = false;
        foreach (var node in servers)
        {
            if (node is not JsonObject server)
            {
                continue;
            }

            if (!TryGetProperty(server, "ApiKey", out var legacyKey) || legacyKey is null)
            {
                continue;
            }

            var overrides = TryGetProperty(server, "RequestOverrides", out var existing) && existing is JsonObject
                ? (JsonObject)existing
                : new JsonObject();
            if (!TryGetProperty(overrides, "ApiKey", out _) )
            {
                overrides["ApiKey"] = legacyKey.DeepClone();
            }
            server["RequestOverrides"] = overrides;
            server.Remove("ApiKey");
            server.Remove("apiKey");
            changed = true;
        }

        if (changed)
        {
            try
            {
                File.WriteAllText(path, root.ToJsonString(MigrationOptions));
            }
            catch (IOException)
            {
                // Best-effort migration; the canonical load path still tolerates a missing key.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool TryGetArray(JsonObject obj, string name, out JsonArray array)
    {
        array = null!;
        if (obj.TryGetPropertyValue(name, out var node) && node is JsonArray arr)
        {
            array = arr;
            return true;
        }
        // camelCase fallback for files written by other tooling.
        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        if (obj.TryGetPropertyValue(camel, out var camelNode) && camelNode is JsonArray camelArr)
        {
            array = camelArr;
            return true;
        }
        return false;
    }

    private static bool TryGetProperty(JsonObject obj, string name, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(name, out value))
        {
            return true;
        }
        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        if (obj.TryGetPropertyValue(camel, out value))
        {
            return true;
        }
        value = null;
        return false;
    }
}

