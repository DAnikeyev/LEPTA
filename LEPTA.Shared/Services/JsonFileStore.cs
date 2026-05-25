using System.IO;
using System.Text.Json;

namespace LEPTA.Shared.Services;

public sealed class JsonLoadResult<T>(T value, IReadOnlyList<string> warnings)
{
    public T Value { get; } = value;

    public IReadOnlyList<string> Warnings { get; } = warnings;
}

public sealed class JsonFileStore
{
    private readonly JsonSerializerOptions serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public JsonLoadResult<T> Load<T>(string filePath, Func<T> defaultFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(defaultFactory);

        EnsureParentDirectory(filePath);
        if (!File.Exists(filePath))
        {
            return new JsonLoadResult<T>(defaultFactory(), []);
        }

        if (TryLoadFile(filePath, out T? value, out var warning) && value is not null)
        {
            return new JsonLoadResult<T>(value, []);
        }

        return new JsonLoadResult<T>(defaultFactory(), [warning!]);
    }

    public JsonLoadResult<IReadOnlyList<T>> LoadMany<T>(string directoryPath, string searchPattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);

        Directory.CreateDirectory(directoryPath);
        var items = new List<T>();
        var warnings = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(directoryPath, searchPattern, SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (TryLoadFile(filePath, out T? value, out var warning) && value is not null)
            {
                items.Add(value);
                continue;
            }

            warnings.Add(warning!);
        }

        return new JsonLoadResult<IReadOnlyList<T>>(items, warnings);
    }

    public void Save<T>(string filePath, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        EnsureParentDirectory(filePath);
        var json = JsonSerializer.Serialize(value, serializerOptions);
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, json);

        try
        {
            if (File.Exists(filePath))
            {
                File.Replace(temporaryPath, filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, filePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Delete(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private bool TryLoadFile<T>(string filePath, out T? value, out string? warning)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(filePath), serializerOptions);
            if (value is not null)
            {
                warning = null;
                return true;
            }

            var backupPath = MoveToBackup(filePath);
            warning = $"Ignored empty or invalid JSON in '{filePath}'. A backup was written to '{backupPath}'.";
            return false;
        }
        catch (JsonException)
        {
            var backupPath = MoveToBackup(filePath);
            warning = $"Ignored invalid JSON in '{filePath}'. A backup was written to '{backupPath}'.";
            value = default;
            return false;
        }
        catch (NotSupportedException)
        {
            var backupPath = MoveToBackup(filePath);
            warning = $"Ignored unsupported JSON in '{filePath}'. A backup was written to '{backupPath}'.";
            value = default;
            return false;
        }
        catch (IOException exception)
        {
            warning = $"Failed to read '{filePath}': {exception.Message}";
            value = default;
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            warning = $"Failed to read '{filePath}': {exception.Message}";
            value = default;
            return false;
        }
    }

    private static void EnsureParentDirectory(string filePath)
    {
        var directoryPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }

    private static string MoveToBackup(string filePath)
    {
        var backupPath = filePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak";
        File.Move(filePath, backupPath);
        return backupPath;
    }
}

