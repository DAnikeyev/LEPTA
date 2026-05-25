using System.Windows;
using LEPTA.Shared.Diagnostics;
using LEPTA.Shared.Models;
using LEPTA.vLLM.Models;

namespace LEPTA.Controllers;

internal sealed partial class LeptaController
{
    public void SavePreset()
    {
        var presetName = NormalizePresetName(presets.NameBox.Text);
        var selectedPreset = presets.ListCombo.SelectedItem as LeptaPresetReference;
        var matchingPreset = presetEntries.FirstOrDefault(item => string.Equals(item.Name, presetName, StringComparison.OrdinalIgnoreCase));
        var presetId = selectedPreset?.Id ?? matchingPreset?.Id ?? Guid.NewGuid().ToString("N");
        var preset = BuildStoredPreset(presetId, presetName);
        presetStore.Save(preset);
        currentPresetId = preset.Id;
        ReloadPresetEntries(preset.Id);
        presets.NameBox.Text = preset.Name;
        SetStatusMessage($"Preset saved: {preset.Name}");
        logger.Log(nameof(LeptaController), $"Saved preset '{preset.Name}'. id={preset.Id}.");
        PublishAction($"Preset saved: {preset.Name}");
        OnStateChanged();
    }

    public void SavePresetAsNew()
    {
        var presetName = EnsureUniquePresetName(NormalizePresetName(presets.NameBox.Text));
        var preset = BuildStoredPreset(Guid.NewGuid().ToString("N"), presetName);
        presetStore.Save(preset);
        currentPresetId = preset.Id;
        ReloadPresetEntries(preset.Id);
        presets.NameBox.Text = preset.Name;
        SetStatusMessage($"Preset saved as new: {preset.Name}");
        logger.Log(nameof(LeptaController), $"Saved new preset '{preset.Name}'. id={preset.Id}.");
        PublishAction($"Preset saved as new: {preset.Name}");
        OnStateChanged();
    }

    public void LoadPreset()
    {
        if (!TryLoadSelectedPreset(out var preset))
        {
            return;
        }

        suppressStateChanged = true;
        try
        {
            currentPresetId = preset.Id;
            presets.NameBox.Text = preset.Name;
            ApplyDashboardState(new LeptaDashboardDefinition
            {
                Id = currentDashboardId,
                Name = currentDashboardName,
                SelectedServerId = preset.SelectedServerId,
                SelectedPresetId = preset.Id,
                GeneralInstruction = preset.GeneralInstruction,
                EnableThinking = preset.EnableThinking,
                Temperature = preset.Temperature,
                Panels = preset.Panels
            }, notifyStateChanged: false);
            SelectPreset(preset.Id);
        }
        finally
        {
            suppressStateChanged = false;
        }

        HandleServerSelectionChanged();
        var resolvedServer = run.ServerCombo.SelectedItem as VllmServerConfiguration;
        if (resolvedServer is not null
            && !string.IsNullOrWhiteSpace(preset.SelectedServerId)
            && !string.Equals(resolvedServer.Id, preset.SelectedServerId, StringComparison.OrdinalIgnoreCase))
        {
            SetStatusMessage($"Preset loaded: {preset.Name}. Saved model server is unavailable; using '{resolvedServer.Name}'.");
            logger.Log(nameof(LeptaController), $"Preset '{preset.Name}' loaded with fallback server '{resolvedServer.Name}' because saved server '{preset.SelectedServerId}' is unavailable.");
        }
        else
        {
            SetStatusMessage($"Preset loaded: {preset.Name}");
            logger.Log(nameof(LeptaController), $"Loaded preset '{preset.Name}'. panelCount={panels.Count}, serverId={resolvedServer?.Id}.");
        }

        PublishAction($"Preset loaded: {preset.Name}");
        OnStateChanged();
    }

    public void HandlePresetSelectionChanged()
    {
        if (suppressStateChanged || presets.ListCombo.SelectedItem is not LeptaPresetReference selectedPreset)
        {
            return;
        }

        suppressStateChanged = true;
        try
        {
            presets.NameBox.Text = selectedPreset.Name;
        }
        finally
        {
            suppressStateChanged = false;
        }

        OnStateChanged();
    }

    public void DeleteSelectedPreset()
    {
        if (presets.ListCombo.SelectedItem is not LeptaPresetReference selectedPreset)
        {
            SetStatusMessage("Select a saved preset to delete.", ActionLogLevel.Warning, keepVisibleWhenIdle: true);
            return;
        }

        if (selectedPreset.IsBuiltIn && !presetStore.HasUserOverride(selectedPreset.Id))
        {
            SetStatusMessage("Built-in presets cannot be deleted.", ActionLogLevel.Warning, keepVisibleWhenIdle: true);
            return;
        }

        var deletePrompt = selectedPreset.IsBuiltIn
            ? $"Reset built-in preset '{selectedPreset.Name}' to the shipped default?"
            : $"Delete preset '{selectedPreset.Name}'?";
        if (MessageBox.Show(
                deletePrompt,
                selectedPreset.IsBuiltIn ? "Reset preset" : "Delete preset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!presetStore.TryDelete(selectedPreset.Id, out var failureMessage))
        {
            SetStatusMessage(failureMessage ?? "Preset could not be deleted.", ActionLogLevel.Warning, keepVisibleWhenIdle: true);
            return;
        }

        if (string.Equals(currentPresetId, selectedPreset.Id, StringComparison.OrdinalIgnoreCase))
        {
            currentPresetId = null;
        }

        ReloadPresetEntries();
        if (selectedPreset.IsBuiltIn)
        {
            SetStatusMessage($"Preset reset to default: {selectedPreset.Name}");
            logger.Log(nameof(LeptaController), $"Reset built-in preset '{selectedPreset.Name}' to default. id={selectedPreset.Id}.");
            PublishAction($"Preset reset to default: {selectedPreset.Name}", ActionLogLevel.Warning);
        }
        else
        {
            SetStatusMessage($"Preset deleted: {selectedPreset.Name}");
            logger.Log(nameof(LeptaController), $"Deleted preset '{selectedPreset.Name}'. id={selectedPreset.Id}.");
            PublishAction($"Preset deleted: {selectedPreset.Name}", ActionLogLevel.Warning);
        }

        OnStateChanged();
    }

    private StoredLeptaPreset BuildStoredPreset(string presetId, string presetName) => new()
    {
        Id = presetId,
        Name = presetName,
        GeneralInstruction = instructions.GeneralInstructionBox.Text.Trim(),
        SelectedServerId = (run.ServerCombo.SelectedItem as VllmServerConfiguration)?.Id,
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

    private bool TryLoadSelectedPreset(out StoredLeptaPreset preset)
    {
        if (presets.ListCombo.SelectedItem is not LeptaPresetReference selectedPreset)
        {
            SetStatusMessage("Select a saved preset to load.", ActionLogLevel.Warning, keepVisibleWhenIdle: true);
            preset = null!;
            return false;
        }

        var result = presetStore.LoadAll();
        foreach (var warning in result.Warnings)
        {
            logger.Log(nameof(LeptaController), warning);
        }

        preset = result.Value.FirstOrDefault(item => string.Equals(item.Id, selectedPreset.Id, StringComparison.OrdinalIgnoreCase))!;
        if (preset is null)
        {
            SetStatusMessage($"Preset '{selectedPreset.Name}' is no longer available.", ActionLogLevel.Warning, keepVisibleWhenIdle: true);
            ReloadPresetEntries();
            return false;
        }

        return true;
    }

    private IReadOnlyList<string> ReloadPresetEntries(string? selectedPresetId = null)
    {
        var selectedId = selectedPresetId ?? (presets.ListCombo.SelectedItem as LeptaPresetReference)?.Id;
        var result = presetStore.LoadAll();
        cachedPresets = result.Value.ToList();
        suppressStateChanged = true;
        try
        {
            presetEntries.Clear();
            foreach (var preset in result.Value
                         .OrderByDescending(item => StoredLeptaPreset.IsBuiltInPresetId(item.Id))
                         .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                presetEntries.Add(new LeptaPresetReference
                {
                    Id = preset.Id,
                    Name = preset.Name,
                    PanelCount = preset.Panels?.Count ?? 0,
                    IsBuiltIn = StoredLeptaPreset.IsBuiltInPresetId(preset.Id)
                });
            }

            SelectPreset(selectedId);
        }
        finally
        {
            suppressStateChanged = false;
        }

        foreach (var warning in result.Warnings)
        {
            logger.Log(nameof(LeptaController), warning);
        }

        return result.Warnings;
    }

    private void SelectPreset(string? presetId)
    {
        presets.ListCombo.SelectedItem = string.IsNullOrWhiteSpace(presetId)
            ? null
            : presetEntries.FirstOrDefault(item => string.Equals(item.Id, presetId, StringComparison.OrdinalIgnoreCase));
    }

    private string NormalizePresetName(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Preset" : value.Trim();

    private string EnsureUniquePresetName(string baseName)
    {
        var candidate = baseName;
        var suffix = 2;
        while (presetEntries.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} ({suffix++})";
        }

        return candidate;
    }
}


