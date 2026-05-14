using System.Windows;
using System.Windows.Media;

namespace LEPTA.Theming;

internal abstract class AppThemeBase : IAppTheme
{
    public abstract string Name { get; }
    public abstract bool IsDark { get; }
    protected abstract IReadOnlyDictionary<string, string> BrushColors { get; }

    public ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary();
        foreach (var pair in BrushColors)
        {
            resources[pair.Key] = CreateBrush(pair.Value);
        }

        return resources;
    }

    private static SolidColorBrush CreateBrush(string hexColor)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
        brush.Freeze();
        return brush;
    }
}

