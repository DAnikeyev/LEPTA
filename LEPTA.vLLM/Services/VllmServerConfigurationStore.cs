using LEPTA.Shared.Services;
using LEPTA.vLLM.Configuration;
using LEPTA.vLLM.Models;

namespace LEPTA.vLLM.Services;

public sealed class VllmServerConfigurationStore(AppDataPaths appDataPaths, JsonFileStore? fileStore = null)
{
    private readonly JsonFileStore fileStore = fileStore ?? new JsonFileStore();

    public JsonLoadResult<VllmServerConfigurationsDocument> Load()
        => fileStore.Load(appDataPaths.ModelConfigurationsFilePath, CreateDefaultDocument);

    public void Save(VllmServerConfigurationsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        fileStore.Save(appDataPaths.ModelConfigurationsFilePath, document);
    }

    private static VllmServerConfigurationsDocument CreateDefaultDocument() => new()
    {
        Servers = VllmDefaults.CreateServers().ToList()
    };
}

