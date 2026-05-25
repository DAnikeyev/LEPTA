namespace LEPTA.Services;

internal sealed class MermaidDiagramViewCache
{
    private readonly Dictionary<string, MermaidRenderResult> entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> usedKeys = new(StringComparer.Ordinal);

    public void BeginBuild()
        => usedKeys.Clear();

    public bool TryGet(string source, double fontSize, out MermaidRenderResult? result)
    {
        var key = CreateKey(source, fontSize);
        return entries.TryGetValue(key, out result);
    }

    public void Store(string source, double fontSize, MermaidRenderResult result)
    {
        var key = CreateKey(source, fontSize);
        entries[key] = result;
        usedKeys.Add(key);
    }

    public void Track(string source, double fontSize)
        => usedKeys.Add(CreateKey(source, fontSize));

    public void Prefetch(string source, double fontSize, double renderWidth)
        => MermaidRenderService.Shared.Prefetch(source, fontSize, renderWidth);

    public void EndBuild()
    {
        if (usedKeys.Count == 0)
        {
            entries.Clear();
            return;
        }

        var staleKeys = entries.Keys.Where(key => !usedKeys.Contains(key)).ToList();
        foreach (var staleKey in staleKeys)
        {
            entries.Remove(staleKey);
        }

        usedKeys.Clear();
    }

    internal static string CreateKey(string source, double fontSize)
        => $"{fontSize:0.##}|{source}";
}
