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
        Assert.That(result.Value.EnableVerboseVllmLogs, Is.False);
        Assert.That(result.Value.DefaultDashboardId, Is.EqualTo(LeptaDashboardDefinition.DefaultDashboardId));
        Assert.That(result.Value.DefaultServerId, Is.Null);
        Assert.That(result.Value.Chat.SystemInstruction, Is.Empty);
        Assert.That(result.Value.Hotkey.Ctrl, Is.True);
        Assert.That(result.Value.Hotkey.Shift, Is.True);
        Assert.That(result.Value.Hotkey.Key, Is.EqualTo("F8"));
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
            EnableVerboseVllmLogs = true,
            DefaultDashboardId = "default",
            DefaultServerId = "server-1",
            Chat = new ChatSettings
            {
                SystemInstruction = "Prefer concise answers with markdown lists."
            },
            Hotkey = new HotkeySettings
            {
                Ctrl = true,
                Alt = true,
                Shift = false,
                Win = false,
                Key = "F9"
            }
        };

        store.Save(settings);
        var result = store.Load();

        Assert.That(result.Value.IsDarkTheme, Is.False);
        Assert.That(result.Value.IsNavigationCollapsed, Is.True);
        Assert.That(result.Value.IsActionLogOverlayEnabled, Is.True);
        Assert.That(result.Value.EnableVerboseVllmLogs, Is.True);
        Assert.That(result.Value.DefaultDashboardId, Is.EqualTo("default"));
        Assert.That(result.Value.DefaultServerId, Is.EqualTo("server-1"));
        Assert.That(result.Value.Chat.SystemInstruction, Is.EqualTo("Prefer concise answers with markdown lists."));
        Assert.That(result.Value.Hotkey.Alt, Is.True);
        Assert.That(result.Value.Hotkey.Shift, Is.False);
        Assert.That(result.Value.Hotkey.Key, Is.EqualTo("F9"));
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
                Panels =
                [
                    new LeptaPanelDefinition { Name = "Summary", CustomInstruction = "Return a concise summary." },
                    new LeptaPanelDefinition { Name = "Risks", CustomInstruction = "List risks." }
                ]
            },
            new LeptaDashboardDefinition
            {
                Id = "secondary",
                Name = "Secondary",
                SelectedServerId = "server-2",
                GeneralInstruction = "Check the clipboard for follow-up actions.",
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
        Assert.That(main.Panels.Select(panel => panel.Name), Is.EqualTo(["Summary", "Risks"]));
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
            Panels = [new LeptaPanelDefinition { Name = "Panel 1", CustomInstruction = "Answer." }]
        });
        Directory.CreateDirectory(paths.PresetsDirectory);
        var corruptFile = System.IO.Path.Combine(paths.PresetsDirectory, "broken.lepta.json");
        File.WriteAllText(corruptFile, "{ not valid json }");

        var result = store.LoadAll();

        Assert.That(result.Value.Select(item => item.Name), Is.EqualTo(["Saved preset"]));
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

