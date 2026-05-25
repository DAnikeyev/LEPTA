using System.IO;

namespace LEPTA.Shared.Services;

public sealed class AppDataPaths
{
    public AppDataPaths(string? rootDirectory = null)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lepta")
            : rootDirectory;
    }

    public string RootDirectory { get; }

    public string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    public string ModelsDirectory => Path.Combine(RootDirectory, "models");

    public string ModelConfigurationsFilePath => Path.Combine(ModelsDirectory, "model-configs.json");

    public string PresetsDirectory => Path.Combine(RootDirectory, "presets");

    public string DashboardsDirectory => Path.Combine(RootDirectory, "dashboards");

    public string DefaultDashboardFilePath => Path.Combine(DashboardsDirectory, "default.dashboard.json");

    public string VllmDirectory => Path.Combine(RootDirectory, "vllm");

    public string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public string ChatHistoryFilePath => Path.Combine(RootDirectory, "chat-history.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(PresetsDirectory);
        Directory.CreateDirectory(DashboardsDirectory);
        Directory.CreateDirectory(VllmDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}

