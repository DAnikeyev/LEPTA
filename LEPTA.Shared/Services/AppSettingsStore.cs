using LEPTA.Shared.Models;

namespace LEPTA.Shared.Services;

public sealed class AppSettingsStore(AppDataPaths appDataPaths, JsonFileStore? fileStore = null)
{
    private readonly JsonFileStore fileStore = fileStore ?? new JsonFileStore();

    public JsonLoadResult<AppSettings> Load()
        => fileStore.Load(appDataPaths.SettingsFilePath, () => new AppSettings());

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        fileStore.Save(appDataPaths.SettingsFilePath, settings);
    }
}

