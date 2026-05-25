using System.Text.Json;
using LEPTA.Shared.Models;
using LEPTA.Shared.Services;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class PersistenceStoreTests
{
    [Test]
    public void AppSettingsStore_LoadsDefaultsForMissingOptionalFields()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);
        File.WriteAllText(paths.SettingsFilePath, "{\"SchemaVersion\":1}");
        var store = new AppSettingsStore(paths);

        var result = store.Load();

        Assert.That(result.Warnings, Is.Empty);
        Assert.That(result.Value.IsDarkTheme, Is.True);
        Assert.That(result.Value.IsNavigationCollapsed, Is.False);
        Assert.That(result.Value.IsActionLogOverlayEnabled, Is.False);
        Assert.That(result.Value.EnableClipboardCachePrefill, Is.False);
        Assert.That(result.Value.EnableVerboseVllmLogs, Is.False);
        Assert.That(result.Value.ResponseFontSize, Is.EqualTo(14));
        Assert.That(result.Value.DefaultDashboardId, Is.EqualTo(LeptaDashboardDefinition.DefaultDashboardId));
        Assert.That(result.Value.DefaultServerId, Is.Null);
        Assert.That(result.Value.LeptaSystemInstructions, Is.EqualTo(AppSettings.DefaultLeptaSystemInstructions));
        Assert.That(result.Value.Chat.SystemInstruction, Is.Empty);
        Assert.That(result.Value.Chat.EnableThinking, Is.False);
        Assert.That(result.Value.Lepta.EnableSharedPromptPrefill, Is.False);
        Assert.That(result.Value.Lepta.DocumentTrimMode, Is.EqualTo(LeptaDocumentTrimMode.TrimStart));
        Assert.That(result.Value.Lepta.DocumentTokenLimit, Is.EqualTo(LeptaSettings.DefaultDocumentTokenLimit));
        Assert.That(result.Value.Hotkey.Ctrl, Is.False);
        Assert.That(result.Value.Hotkey.Shift, Is.False);
        Assert.That(result.Value.Hotkey.Key, Is.Empty);
    }

    [Test]
    public void AppSettingsStore_LoadsLegacyActiveDashboardIdIntoDefaultDashboardId()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);
        File.WriteAllText(paths.SettingsFilePath, "{\"ActiveDashboardId\":\"saved-dashboard\"}");
        var store = new AppSettingsStore(paths);

        var result = store.Load();

        Assert.That(result.Value.DefaultDashboardId, Is.EqualTo("saved-dashboard"));
        Assert.That(result.Value.ActiveDashboardId, Is.EqualTo("saved-dashboard"));
    }

    [Test]
    public void AppSettingsStore_RoundTripsThemeAndHotkey()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);
        var store = new AppSettingsStore(paths);
        var settings = new AppSettings
        {
            IsDarkTheme = false,
            IsNavigationCollapsed = true,
            IsActionLogOverlayEnabled = true,
            EnableClipboardCachePrefill = true,
            EnableVerboseVllmLogs = true,
            ResponseFontSize = 18,
            DefaultDashboardId = "default",
            DefaultServerId = "server-1",
            LeptaSystemInstructions = "System rules for LEPTA.",
            Chat = new ChatSettings
            {
                SystemInstruction = "Prefer concise answers with markdown lists.",
                EnableThinking = true
            },
            Lepta = new LeptaSettings
            {
                EnableSharedPromptPrefill = true,
                DocumentTrimMode = LeptaDocumentTrimMode.TrimEnd,
                DocumentTokenLimit = 8192
            },
            Hotkey = new HotkeySettings
            {
                Ctrl = true,
                Alt = true,
                Shift = false,
                Win = false,
                Key = "Delete"
            }
        };

        store.Save(settings);
        var result = store.Load();

        Assert.That(result.Value.IsDarkTheme, Is.False);
        Assert.That(result.Value.IsNavigationCollapsed, Is.True);
        Assert.That(result.Value.IsActionLogOverlayEnabled, Is.True);
        Assert.That(result.Value.EnableClipboardCachePrefill, Is.True);
        Assert.That(result.Value.EnableVerboseVllmLogs, Is.True);
        Assert.That(result.Value.ResponseFontSize, Is.EqualTo(18));
        Assert.That(result.Value.DefaultDashboardId, Is.EqualTo("default"));
        Assert.That(result.Value.DefaultServerId, Is.EqualTo("server-1"));
        Assert.That(result.Value.LeptaSystemInstructions, Is.EqualTo("System rules for LEPTA."));
        Assert.That(result.Value.Chat.SystemInstruction, Is.EqualTo("Prefer concise answers with markdown lists."));
        Assert.That(result.Value.Chat.EnableThinking, Is.True);
        Assert.That(result.Value.Lepta.EnableSharedPromptPrefill, Is.True);
        Assert.That(result.Value.Lepta.DocumentTrimMode, Is.EqualTo(LeptaDocumentTrimMode.TrimEnd));
        Assert.That(result.Value.Lepta.DocumentTokenLimit, Is.EqualTo(8192));
        Assert.That(result.Value.Hotkey.Alt, Is.True);
        Assert.That(result.Value.Hotkey.Shift, Is.False);
        Assert.That(result.Value.Hotkey.Key, Is.EqualTo("Delete"));
    }

    [Test]
    public void AppSettingsStore_Save_ReplacesExistingFileWithoutLeavingTemporaryFile()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);
        var store = new AppSettingsStore(paths);
        File.WriteAllText(paths.SettingsFilePath, "{\"SchemaVersion\":1,\"IsDarkTheme\":false}");

        store.Save(new AppSettings
        {
            DefaultDashboardId = "default",
            LeptaSystemInstructions = AppSettings.DefaultLeptaSystemInstructions
        });

        var persistedJson = File.ReadAllText(paths.SettingsFilePath);

        Assert.That(() => JsonDocument.Parse(persistedJson), Throws.Nothing);
        Assert.That(File.Exists(paths.SettingsFilePath + ".tmp"), Is.False);
    }

    [Test]
    public void LeptaDashboardStore_RoundTripsDashboardsAndPanelOrder()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);
        var store = new LeptaDashboardStore(paths);
        store.SaveAll(
        [
            new LeptaDashboardDefinition
            {
                Id = "default",
                Name = "Main",
                SelectedServerId = "server-1",
                GeneralInstruction = "Summarize the clipboard.",
                EnableThinking = true,
                Temperature = 0.6,
                Panels =
                [
                    new LeptaPanelDefinition { Name = "Summary", CustomInstruction = "Return a concise summary.", Format = LeptaPanelFormats.Markdown },
                    new LeptaPanelDefinition { Name = "Risks", CustomInstruction = "List risks.", Format = LeptaPanelFormats.Mermaid }
                ]
            },
            new LeptaDashboardDefinition
            {
                Id = "secondary",
                Name = "Secondary",
                SelectedServerId = "server-2",
                GeneralInstruction = "Check the clipboard for follow-up actions.",
                EnableThinking = false,
                Temperature = 0.15,
                Panels =
                [
                    new LeptaPanelDefinition { Name = "Actions", CustomInstruction = "List actions." },
                    new LeptaPanelDefinition { Name = "Open questions", CustomInstruction = "List open questions." },
                    new LeptaPanelDefinition { Name = "Dependencies", CustomInstruction = "List dependencies." }
                ]
            }
        ]);

        var result = store.LoadAll();
        var main = result.Value.First(dashboard => dashboard.Id == "default");
        var secondary = result.Value.First(dashboard => dashboard.Id == "secondary");

        Assert.That(result.Value.Select(dashboard => dashboard.Id), Is.EquivalentTo(["default", "secondary"]));
        Assert.That(main.Name, Is.EqualTo("Main"));
        Assert.That(main.SelectedServerId, Is.EqualTo("server-1"));
        Assert.That(main.GeneralInstruction, Is.EqualTo("Summarize the clipboard."));
        Assert.That(main.EnableThinking, Is.True);
        Assert.That(main.Temperature, Is.EqualTo(0.6).Within(0.001));
        Assert.That(main.Panels.Select(panel => panel.Name), Is.EqualTo(["Summary", "Risks"]));
        Assert.That(main.Panels.Select(panel => panel.Format), Is.EqualTo([LeptaPanelFormats.Markdown, LeptaPanelFormats.Mermaid]));
        Assert.That(secondary.Temperature, Is.EqualTo(0.15).Within(0.001));
        Assert.That(secondary.Panels.Select(panel => panel.Name), Is.EqualTo(["Actions", "Open questions", "Dependencies"]));
    }

    [Test]
    public void LeptaDashboardStore_SaveAll_RemovesDeletedDashboardFiles()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);
        var store = new LeptaDashboardStore(paths);

        store.SaveAll(
        [
            new LeptaDashboardDefinition { Id = "default", Name = "Main" },
            new LeptaDashboardDefinition { Id = "secondary", Name = "Secondary" }
        ]);

        store.SaveAll([new LeptaDashboardDefinition { Id = "default", Name = "Main" }]);

        Assert.That(File.Exists(System.IO.Path.Combine(paths.DashboardsDirectory, "default.dashboard.json")), Is.True);
        Assert.That(File.Exists(System.IO.Path.Combine(paths.DashboardsDirectory, "secondary.dashboard.json")), Is.False);
    }

    [Test]
    public void LeptaPresetStore_LoadAll_IncludesBuiltInLearningPresetWhenDirectoryIsEmpty()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);
        var store = new LeptaPresetStore(paths);

        var result = store.LoadAll();

        Assert.That(result.Warnings, Is.Empty);
        Assert.That(result.Value.Select(item => item.Id), Is.EqualTo([StoredLeptaPreset.LearningPresetId]));
        Assert.That(result.Value[0].Name, Is.EqualTo("Learning"));
        Assert.That(result.Value[0].Panels.Select(panel => panel.Name),
            Is.EqualTo(["Terms", "Summary", "Code", "UML", "Knowledge check"]));
    }

    [Test]
    public void LeptaPresetStore_LoadAll_MergesUserPresetsWithBuiltInsAndPrefersUserOverride()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);
        var store = new LeptaPresetStore(paths);
        store.Save(new StoredLeptaPreset
        {
            Id = StoredLeptaPreset.LearningPresetId,
            Name = "Learning (customized)",
            GeneralInstruction = "Custom rules.",
            Panels = [new LeptaPanelDefinition { Name = "Only panel" }]
        });
        store.Save(new StoredLeptaPreset
        {
            Id = "preset-1",
            Name = "Saved preset",
            GeneralInstruction = "Be precise.",
            Panels = [new LeptaPanelDefinition { Name = "Panel 1" }]
        });

        var result = store.LoadAll();

        Assert.That(result.Value.Select(item => item.Id),
            Is.EqualTo([StoredLeptaPreset.LearningPresetId, "preset-1"]));
        Assert.That(result.Value[0].Name, Is.EqualTo("Learning (customized)"));
        Assert.That(result.Value[0].GeneralInstruction, Is.EqualTo("Custom rules."));
        Assert.That(result.Value[1].Name, Is.EqualTo("Saved preset"));
    }

    [Test]
    public void LeptaPresetStore_TryDelete_BlocksBuiltInPresetWithoutUserOverride()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);
        var store = new LeptaPresetStore(paths);

        var deleted = store.TryDelete(StoredLeptaPreset.LearningPresetId, out var failureMessage);

        Assert.That(deleted, Is.False);
        Assert.That(failureMessage, Is.EqualTo("Built-in presets cannot be deleted."));
        Assert.That(store.LoadAll().Value.Select(item => item.Id), Is.EqualTo([StoredLeptaPreset.LearningPresetId]));
    }

    [Test]
    public void LeptaPresetStore_TryDelete_RemovesBuiltInOverrideAndRestoresShippedDefault()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);
        var store = new LeptaPresetStore(paths);
        store.Save(new StoredLeptaPreset
        {
            Id = StoredLeptaPreset.LearningPresetId,
            Name = "Learning (customized)",
            GeneralInstruction = "Custom rules.",
            Panels = [new LeptaPanelDefinition { Name = "Only panel" }]
        });

        var deleted = store.TryDelete(StoredLeptaPreset.LearningPresetId, out var failureMessage);

        Assert.That(deleted, Is.True);
        Assert.That(failureMessage, Is.Null);
        Assert.That(store.HasUserOverride(StoredLeptaPreset.LearningPresetId), Is.False);
        Assert.That(store.LoadAll().Value[0].Name, Is.EqualTo("Learning"));
        Assert.That(store.LoadAll().Value[0].GeneralInstruction, Is.EqualTo("If no programming language is specified, default to C#."));
    }

    [Test]
    public void LeptaPresetStore_LoadAll_BackupsCorruptFilesAndKeepsValidPresets()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);
        var store = new LeptaPresetStore(paths);
        store.Save(new StoredLeptaPreset
        {
            Id = "preset-1",
            Name = "Saved preset",
            GeneralInstruction = "Be precise.",
            EnableThinking = true,
            Temperature = 0.45,
            Panels = [new LeptaPanelDefinition { Name = "Panel 1", CustomInstruction = "Answer.", Format = LeptaPanelFormats.Mermaid }]
        });
        Directory.CreateDirectory(paths.PresetsDirectory);
        var corruptFile = System.IO.Path.Combine(paths.PresetsDirectory, "broken.lepta.json");
        File.WriteAllText(corruptFile, "{ not valid json }");

        var result = store.LoadAll();

        Assert.That(result.Value.Select(item => item.Name), Is.EqualTo(["Learning", "Saved preset"]));
        var savedPreset = result.Value.First(item => item.Id == "preset-1");
        Assert.That(savedPreset.EnableThinking, Is.True);
        Assert.That(savedPreset.Temperature, Is.EqualTo(0.45).Within(0.001));
        Assert.That(savedPreset.Panels.Select(panel => panel.Format), Is.EqualTo([LeptaPanelFormats.Mermaid]));
        Assert.That(result.Warnings, Has.Count.EqualTo(1));
        Assert.That(File.Exists(corruptFile), Is.False);
        Assert.That(Directory.EnumerateFiles(paths.PresetsDirectory, "broken.lepta.json.corrupt-*.bak"), Is.Not.Empty);
    }

    [Test]
    public void VllmServerConfigurationStore_PersistsSelectedServerAndProfiles()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);
        var store = new VllmServerConfigurationStore(paths);
        var document = new VllmServerConfigurationsDocument
        {
            SelectedServerId = "server-2",
            Servers =
            [
                new VllmServerConfiguration
                {
                    Id = "server-1",
                    Name = "Primary",
                    UseExistingHttpServer = true,
                    HttpServerAddress = "http://localhost:8512"
                },
                new VllmServerConfiguration
                {
                    Id = "server-2",
                    Name = "Secondary",
                    UseExistingHttpServer = true,
                    HttpServerAddress = "http://localhost:8612"
                }
            ]
        };

        store.Save(document);
        var result = store.Load();

        Assert.That(result.Value.SelectedServerId, Is.EqualTo("server-2"));
        Assert.That(result.Value.Servers.Select(server => server.Name), Is.EqualTo(["Primary", "Secondary"]));
        Assert.That(result.Value.Servers[1].Endpoint, Is.EqualTo("http://localhost:8612"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LEPTA.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

