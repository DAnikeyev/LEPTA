using LEPTA.Shared.Models;

namespace LEPTA.Shared.Services;

public sealed class LeptaPresetStore(AppDataPaths appDataPaths, JsonFileStore? fileStore = null)
{
    private const string SearchPattern = "*.lepta.json";
    private readonly JsonFileStore fileStore = fileStore ?? new JsonFileStore();

    public JsonLoadResult<IReadOnlyList<StoredLeptaPreset>> LoadAll()
        => fileStore.LoadMany<StoredLeptaPreset>(appDataPaths.PresetsDirectory, SearchPattern);

    public void Save(StoredLeptaPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(preset.Id))
        {
            preset.Id = Guid.NewGuid().ToString("N");
        }

        fileStore.Save(GetFilePath(preset.Id), preset);
    }

    public void Delete(string presetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        fileStore.Delete(GetFilePath(presetId));
    }

    private string GetFilePath(string presetId)
        => Path.Combine(appDataPaths.PresetsDirectory, $"{presetId}.lepta.json");
}

