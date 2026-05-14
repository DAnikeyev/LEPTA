using System.Collections;
using System.Windows;
using LEPTA.Theming;

namespace LEPTA.Controllers;

internal sealed class ThemeController
{
    private static readonly IAppTheme DarkTheme = new DarkAppTheme();
    private static readonly IAppTheme LightTheme = new LightAppTheme();

    public bool IsDarkTheme { get; private set; } = true;

    public void ApplyTheme(bool dark)
        => ApplyTheme(dark ? DarkTheme : LightTheme);

    public void ApplyTheme(IAppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        IsDarkTheme = theme.IsDark;
        var resources = Application.Current.Resources;
        foreach (DictionaryEntry entry in theme.CreateResources())
        {
            resources[entry.Key] = entry.Value;
        }

        ApplySystemBrushMappings(resources);
    }

    private static void ApplySystemBrushMappings(ResourceDictionary resources)
    {
        MapBrush(resources, SystemColors.WindowBrushKey, ThemeResourceKeys.SurfaceBrush);
        MapBrush(resources, SystemColors.WindowTextBrushKey, ThemeResourceKeys.PrimaryTextBrush);
        MapBrush(resources, SystemColors.ControlBrushKey, ThemeResourceKeys.SurfaceBrush);
        MapBrush(resources, SystemColors.ControlTextBrushKey, ThemeResourceKeys.PrimaryTextBrush);
        MapBrush(resources, SystemColors.GrayTextBrushKey, ThemeResourceKeys.SecondaryTextBrush);
        MapBrush(resources, SystemColors.HighlightBrushKey, ThemeResourceKeys.SelectionBrush);
        MapBrush(resources, SystemColors.HighlightTextBrushKey, ThemeResourceKeys.SelectionForegroundBrush);
        MapBrush(resources, SystemColors.InactiveSelectionHighlightBrushKey, ThemeResourceKeys.SurfaceHoverBrush);
        MapBrush(resources, SystemColors.InactiveSelectionHighlightTextBrushKey, ThemeResourceKeys.PrimaryTextBrush);
        MapBrush(resources, SystemColors.MenuBrushKey, ThemeResourceKeys.SurfaceBrush);
        MapBrush(resources, SystemColors.MenuTextBrushKey, ThemeResourceKeys.PrimaryTextBrush);
        MapBrush(resources, SystemColors.InfoBrushKey, ThemeResourceKeys.PanelBackgroundAltBrush);
        MapBrush(resources, SystemColors.InfoTextBrushKey, ThemeResourceKeys.PrimaryTextBrush);
    }

    private static void MapBrush(ResourceDictionary resources, object systemKey, string themeBrushKey)
    {
        if (resources[themeBrushKey] is object brush)
        {
            resources[systemKey] = brush;
        }
    }
}