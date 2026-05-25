using LEPTA.Shared.Models;

namespace LEPTA.Shared.Services;

public sealed class LeptaPresetStore(AppDataPaths appDataPaths, JsonFileStore? fileStore = null)
{
    private const string SearchPattern = "*.lepta.json";
    private readonly JsonFileStore fileStore = fileStore ?? new JsonFileStore();

    public JsonLoadResult<IReadOnlyList<StoredLeptaPreset>> LoadAll()
    {
        var userResult = fileStore.LoadMany<StoredLeptaPreset>(appDataPaths.PresetsDirectory, SearchPattern);
        var userPresetsById = userResult.Value
            .Where(preset => !string.IsNullOrWhiteSpace(preset.Id))
            .GroupBy(preset => preset.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var mergedPresets = new List<StoredLeptaPreset>();
        foreach (var builtInPreset in StoredLeptaPreset.GetBuiltInPresets())
        {
            mergedPresets.Add(userPresetsById.TryGetValue(builtInPreset.Id, out var userOverride)
                ? userOverride
                : builtInPreset);
            userPresetsById.Remove(builtInPreset.Id);
        }

        mergedPresets.AddRange(userPresetsById.Values.OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase));
        return new JsonLoadResult<IReadOnlyList<StoredLeptaPreset>>(mergedPresets, userResult.Warnings);
    }

    public void Save(StoredLeptaPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(preset.Id))
        {
            preset.Id = Guid.NewGuid().ToString("N");
        }

        fileStore.Save(GetFilePath(preset.Id), preset);
    }

    public bool HasUserOverride(string presetId)
        => !string.IsNullOrWhiteSpace(presetId) && File.Exists(GetFilePath(presetId));

    public bool TryDelete(string presetId, out string? failureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);

        if (StoredLeptaPreset.IsBuiltInPresetId(presetId))
        {
            if (!HasUserOverride(presetId))
            {
                failureMessage = "Built-in presets cannot be deleted.";
                return false;
            }

            fileStore.Delete(GetFilePath(presetId));
            failureMessage = null;
            return true;
        }

        fileStore.Delete(GetFilePath(presetId));
        failureMessage = null;
        return true;
    }

    public void Delete(string presetId)
    {
        if (!TryDelete(presetId, out var failureMessage))
        {
            throw new InvalidOperationException(failureMessage);
        }
    }

    private string GetFilePath(string presetId)
        => Path.Combine(appDataPaths.PresetsDirectory, $"{presetId.Trim()}.lepta.json");
}
