using LEPTA.Shared.Services;

namespace LEPTA.Tests;

[TestFixture]
public sealed class AppDataPathsTests
{
    [Test]
    public void Constructor_UsesCanonicalLeptaFolderNames()
    {
        var paths = new AppDataPaths("C:\\Temp\\LeptaRoot");

        Assert.That(paths.RootDirectory, Is.EqualTo("C:\\Temp\\LeptaRoot"));
        Assert.That(paths.SettingsFilePath, Is.EqualTo("C:\\Temp\\LeptaRoot\\settings.json"));
        Assert.That(paths.ModelConfigurationsFilePath, Is.EqualTo("C:\\Temp\\LeptaRoot\\models\\model-configs.json"));
        Assert.That(paths.PresetsDirectory, Is.EqualTo("C:\\Temp\\LeptaRoot\\presets"));
        Assert.That(paths.DefaultDashboardFilePath, Is.EqualTo("C:\\Temp\\LeptaRoot\\dashboards\\default.dashboard.json"));
        Assert.That(paths.LogsDirectory, Is.EqualTo("C:\\Temp\\LeptaRoot\\logs"));
        Assert.That(paths.VllmDirectory, Is.EqualTo("C:\\Temp\\LeptaRoot\\vllm"));
    }

    [Test]
    public void EnsureCreated_CreatesAllExpectedDirectories()
    {
        using var sandbox = new TemporaryDirectory();
        var paths = new AppDataPaths(sandbox.Path);

        paths.EnsureCreated();

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(paths.RootDirectory), Is.True);
            Assert.That(Directory.Exists(paths.ModelsDirectory), Is.True);
            Assert.That(Directory.Exists(paths.PresetsDirectory), Is.True);
            Assert.That(Directory.Exists(paths.DashboardsDirectory), Is.True);
            Assert.That(Directory.Exists(paths.LogsDirectory), Is.True);
            Assert.That(Directory.Exists(paths.VllmDirectory), Is.True);
        });
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

