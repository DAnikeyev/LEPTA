using System.Windows;
using LEPTA.Models;
using LEPTA.Shared.Diagnostics;
using LEPTA.Shared.Models;
using LEPTA.vLLM.Models;

namespace LEPTA.Controllers;

internal sealed partial class LeptaController
{
    public void LoadDashboards(IEnumerable<LeptaDashboardDefinition> availableDashboards, string? selectedDashboardId)
    {
        ArgumentNullException.ThrowIfNull(availableDashboards);

        dashboards.Clear();
        dashboards.AddRange(
            availableDashboards
                .Where(dashboard => dashboard is not null)
                .Select(CloneDashboard));

        if (dashboards.Count == 0)
        {
            dashboards.Add(LeptaDashboardDefinition.CreateDefault());
        }

        suppressStateChanged = true;
        try
        {
            dashboardEntries.Clear();
            foreach (var dashboard in dashboards)
            {
                dashboardEntries.Add(new LeptaDashboardReference
                {
                    Id = dashboard.Id,
                    Name = dashboard.Name
                });
            }
        }
        finally
        {
            suppressStateChanged = false;
        }

        var selectedDashboard = ResolveDashboard(selectedDashboardId) ?? dashboards[0];
        ApplyDashboardState(selectedDashboard, notifyStateChanged: false);
    }

    public IReadOnlyList<LeptaDashboardDefinition> CaptureDashboards()
    {
        SyncCurrentDashboardIntoCollection();
        return dashboards.Select(CloneDashboard).ToList();
    }

    public void ApplyDashboardState(LeptaDashboardDefinition dashboard, bool notifyStateChanged = true)
    {
        ArgumentNullException.ThrowIfNull(dashboard);

        suppressStateChanged = true;
        try
        {
            currentDashboardId = string.IsNullOrWhiteSpace(dashboard.Id) ? LeptaDashboardDefinition.DefaultDashboardId : dashboard.Id.Trim();
            currentDashboardName = NormalizeDashboardName(dashboard.Name);
            preferredServerId = dashboard.SelectedServerId;
            dashboardsView.NameBox.Text = currentDashboardName;
            SelectDashboard(currentDashboardId);
            instructions.GeneralInstructionBox.Text = dashboard.GeneralInstruction ?? string.Empty;
            run.ThinkingCheckBox.IsChecked = dashboard.EnableThinking;
            currentTemperature = LeptaSettings.NormalizeTemperature(dashboard.Temperature);
            run.TemperatureTextBox.Text = FormatTemperature(currentTemperature);
            ReplacePanels(dashboard.Panels);
            ApplyServerSelection();
        }
        finally
        {
            suppressStateChanged = false;
        }

        HandleServerSelectionChanged();
        currentPresetId = string.IsNullOrWhiteSpace(dashboard.SelectedPresetId) ? null : dashboard.SelectedPresetId.Trim();
        SelectPreset(currentPresetId);
        if (!string.IsNullOrWhiteSpace(currentPresetId))
        {
            var matchingPreset = cachedPresets.FirstOrDefault(p => string.Equals(p.Id, currentPresetId, StringComparison.OrdinalIgnoreCase));
            if (matchingPreset is not null)
            {
                presets.NameBox.Text = matchingPreset.Name;
            }
        }
        else
        {
            presets.NameBox.Text = string.Empty;
        }

        if (notifyStateChanged)
        {
            OnStateChanged();
        }
    }

    public LeptaDashboardDefinition CaptureDashboardState() => new()
    {
        Id = currentDashboardId,
        Name = NormalizeDashboardName(dashboardsView.NameBox.Text),
        SelectedServerId = (run.ServerCombo.SelectedItem as VllmServerConfiguration)?.Id,
        SelectedPresetId = currentPresetId,
        GeneralInstruction = instructions.GeneralInstructionBox.Text.Trim(),
        EnableThinking = run.ThinkingCheckBox.IsChecked == true,
        Temperature = currentTemperature,
        Panels = panels
            .Select(panel => new LeptaPanelDefinition
            {
                Name = panel.Name,
                CustomInstruction = panel.CustomInstruction,
                AccentColorHex = panel.AccentColorHex,
                Format = panel.Format
            })
            .ToList()
    };

    public void HandleDashboardSelectionChanged()
    {
        if (suppressStateChanged || dashboardsView.ListCombo.SelectedItem is not LeptaDashboardReference selectedDashboard)
        {
            return;
        }

        SyncCurrentDashboardIntoCollection();
        var dashboard = ResolveDashboard(selectedDashboard.Id);
        if (dashboard is null)
        {
            return;
        }

        ApplyDashboardState(dashboard);
        logger.Log(nameof(LeptaController), $"Selected dashboard '{dashboard.Name}'. id={dashboard.Id}.");
    }

    public void SaveDashboard()
    {
        SyncCurrentDashboardIntoCollection();
        SetStatusMessage($"Dashboard saved: {currentDashboardName}");
        logger.Log(nameof(LeptaController), $"Saved dashboard '{currentDashboardName}'. id={currentDashboardId}.");
        PublishAction($"Dashboard saved: {currentDashboardName}");
        OnStateChanged();
    }

    public void SaveDashboardAsNew()
    {
        SyncCurrentDashboardIntoCollection();
        var dashboard = CaptureDashboardState();
        dashboard.Id = Guid.NewGuid().ToString("N");
        dashboard.Name = EnsureUniqueDashboardName(NormalizeDashboardName(dashboardsView.NameBox.Text));
        dashboards.Add(CloneDashboard(dashboard));
        dashboardEntries.Add(new LeptaDashboardReference
        {
            Id = dashboard.Id,
            Name = dashboard.Name
        });
        ApplyDashboardState(dashboard);
        SetStatusMessage($"Dashboard saved as new: {dashboard.Name}");
        logger.Log(nameof(LeptaController), $"Saved new dashboard '{dashboard.Name}'. id={dashboard.Id}.");
        PublishAction($"Dashboard saved as new: {dashboard.Name}");
    }

    public void DeleteSelectedDashboard()
    {
        if (dashboardsView.ListCombo.SelectedItem is not LeptaDashboardReference selectedDashboard)
        {
            SetStatusMessage("Select a saved dashboard to delete.", ActionLogLevel.Warning, keepVisibleWhenIdle: true);
            return;
        }

        if (MessageBox.Show(
                $"Delete dashboard '{selectedDashboard.Name}'?",
                "Delete dashboard",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var selectedIndex = dashboardEntries.IndexOf(selectedDashboard);
        dashboards.RemoveAll(item => string.Equals(item.Id, selectedDashboard.Id, StringComparison.OrdinalIgnoreCase));
        dashboardEntries.Remove(selectedDashboard);

        if (dashboards.Count == 0)
        {
            var defaultDashboard = LeptaDashboardDefinition.CreateDefault();
            dashboards.Add(defaultDashboard);
            dashboardEntries.Add(new LeptaDashboardReference
            {
                Id = defaultDashboard.Id,
                Name = defaultDashboard.Name
            });
            selectedIndex = 0;
        }

        var nextIndex = Math.Clamp(selectedIndex, 0, dashboardEntries.Count - 1);
        var nextDashboardId = dashboardEntries[nextIndex].Id;
        ApplyDashboardState(ResolveDashboard(nextDashboardId)!, notifyStateChanged: false);
        SetStatusMessage($"Dashboard deleted: {selectedDashboard.Name}");
        logger.Log(nameof(LeptaController), $"Deleted dashboard '{selectedDashboard.Name}'. id={selectedDashboard.Id}.");
        PublishAction($"Dashboard deleted: {selectedDashboard.Name}", ActionLogLevel.Warning);
        OnStateChanged();
    }

    private void SelectDashboard(string? dashboardId)
    {
        dashboardsView.ListCombo.SelectedItem = string.IsNullOrWhiteSpace(dashboardId)
            ? null
            : dashboardEntries.FirstOrDefault(item => string.Equals(item.Id, dashboardId, StringComparison.OrdinalIgnoreCase));
    }

    private LeptaDashboardDefinition? ResolveDashboard(string? dashboardId)
        => string.IsNullOrWhiteSpace(dashboardId)
            ? dashboards.FirstOrDefault()
            : dashboards.FirstOrDefault(item => string.Equals(item.Id, dashboardId, StringComparison.OrdinalIgnoreCase))
              ?? dashboards.FirstOrDefault();

    private void SyncCurrentDashboardIntoCollection()
    {
        var snapshot = CloneDashboard(CaptureDashboardState());
        currentDashboardId = snapshot.Id;
        currentDashboardName = snapshot.Name;

        var existingIndex = dashboards.FindIndex(item => string.Equals(item.Id, snapshot.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            dashboards[existingIndex] = snapshot;
        }
        else
        {
            dashboards.Add(snapshot);
            dashboardEntries.Add(new LeptaDashboardReference
            {
                Id = snapshot.Id,
                Name = snapshot.Name
            });
        }

        UpdateCurrentDashboardReferenceName();
    }

    private void UpdateCurrentDashboardReferenceName()
    {
        var reference = dashboardEntries.FirstOrDefault(item => string.Equals(item.Id, currentDashboardId, StringComparison.OrdinalIgnoreCase));
        if (reference is not null)
        {
            reference.Name = NormalizeDashboardName(dashboardsView.NameBox.Text);
        }

        currentDashboardName = NormalizeDashboardName(dashboardsView.NameBox.Text);
    }

    private string NormalizeDashboardName(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Dashboard" : value.Trim();

    private string EnsureUniqueDashboardName(string baseName)
    {
        var candidate = baseName;
        var suffix = 2;
        while (dashboardEntries.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} ({suffix++})";
        }

        return candidate;
    }

    private static LeptaDashboardDefinition CloneDashboard(LeptaDashboardDefinition dashboard) => new()
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
                AccentColorHex = LeptaPanelAccentPalette.Normalize(panel.AccentColorHex),
                Format = LeptaPanelFormats.Normalize(panel.Format)
            })
            .ToList()
    };
}


