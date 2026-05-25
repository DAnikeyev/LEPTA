using System.IO;
using LEPTA.Shared.Models;

namespace LEPTA.Shared.Services;

public sealed class LeptaDashboardStore(AppDataPaths appDataPaths, JsonFileStore? fileStore = null)
{
    private const string SearchPattern = "*.dashboard.json";
    private readonly JsonFileStore fileStore = fileStore ?? new JsonFileStore();

    public JsonLoadResult<IReadOnlyList<LeptaDashboardDefinition>> LoadAll()
    {
        var result = fileStore.LoadMany<LeptaDashboardDefinition>(appDataPaths.DashboardsDirectory, SearchPattern);
        if (result.Value.Count > 0)
        {
            return new JsonLoadResult<IReadOnlyList<LeptaDashboardDefinition>>(
                result.Value.Select(CloneDashboard).ToList(),
                result.Warnings);
        }

        return new JsonLoadResult<IReadOnlyList<LeptaDashboardDefinition>>([LeptaDashboardDefinition.CreateDefault()], result.Warnings);
    }

    public void SaveAll(IEnumerable<LeptaDashboardDefinition> dashboards)
    {
        ArgumentNullException.ThrowIfNull(dashboards);

        Directory.CreateDirectory(appDataPaths.DashboardsDirectory);
        var normalizedDashboards = dashboards
            .Where(dashboard => dashboard is not null)
            .Select(CloneDashboard)
            .GroupBy(dashboard => dashboard.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (normalizedDashboards.Count == 0)
        {
            normalizedDashboards.Add(LeptaDashboardDefinition.CreateDefault());
        }

        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dashboard in normalizedDashboards)
        {
            var filePath = GetFilePath(dashboard.Id);
            expectedFiles.Add(filePath);
            fileStore.Save(filePath, dashboard);
        }

        foreach (var existingFile in Directory.EnumerateFiles(appDataPaths.DashboardsDirectory, SearchPattern, SearchOption.TopDirectoryOnly))
        {
            if (!expectedFiles.Contains(existingFile))
            {
                fileStore.Delete(existingFile);
            }
        }
    }

    private string GetFilePath(string dashboardId)
    {
        var normalizedId = string.IsNullOrWhiteSpace(dashboardId)
            ? Guid.NewGuid().ToString("N")
            : dashboardId.Trim();

        return Path.Combine(appDataPaths.DashboardsDirectory, $"{normalizedId}.dashboard.json");
    }

    private static LeptaDashboardDefinition CloneDashboard(LeptaDashboardDefinition dashboard)
    {
        ArgumentNullException.ThrowIfNull(dashboard);

        return new LeptaDashboardDefinition
        {
            SchemaVersion = LeptaDashboardDefinition.CurrentSchemaVersion,
            Id = string.IsNullOrWhiteSpace(dashboard.Id) ? Guid.NewGuid().ToString("N") : dashboard.Id.Trim(),
            Name = string.IsNullOrWhiteSpace(dashboard.Name) ? "Dashboard" : dashboard.Name.Trim(),
            SelectedServerId = string.IsNullOrWhiteSpace(dashboard.SelectedServerId) ? null : dashboard.SelectedServerId.Trim(),
            SelectedPresetId = string.IsNullOrWhiteSpace(dashboard.SelectedPresetId) ? null : dashboard.SelectedPresetId.Trim(),
            GeneralInstruction = dashboard.GeneralInstruction ?? string.Empty,
            EnableThinking = dashboard.EnableThinking,
            Temperature = LeptaSettings.NormalizeTemperature(dashboard.Temperature),
            Panels = dashboard.Panels
                .Where(panel => panel is not null)
                .Select(panel => new LeptaPanelDefinition
                {
                    Name = string.IsNullOrWhiteSpace(panel.Name) ? "Panel" : panel.Name.Trim(),
                    CustomInstruction = panel.CustomInstruction ?? string.Empty,
                    AccentColorHex = string.IsNullOrWhiteSpace(panel.AccentColorHex) ? "#2F6FED" : panel.AccentColorHex.Trim(),
                    Format = LeptaPanelFormats.Normalize(panel.Format)
                })
                .ToList()
        };
    }
}

