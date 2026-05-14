using LEPTA.Shared.Diagnostics;

namespace LEPTA.Tests;

internal sealed class TestLeptaLogger : ILeptaLogger
{
    private readonly List<string> entries = [];
    private readonly object sync = new();

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (sync)
            {
                return entries.ToArray();
            }
        }
    }

    public void Log(string source, string message)
    {
        var entry = $"[{source}] {message}";
        lock (sync)
        {
            entries.Add(entry);
        }

        TestContext.Progress.WriteLine(entry);
    }
}

