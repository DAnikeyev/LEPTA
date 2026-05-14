namespace LEPTA.Theming;

internal sealed class DarkAppTheme : AppThemeBase
{
    private static readonly IReadOnlyDictionary<string, string> Colors = new Dictionary<string, string>
    {
        [ThemeResourceKeys.WindowBackgroundBrush] = "#101419",
        [ThemeResourceKeys.PanelBackgroundBrush] = "#171D25",
        [ThemeResourceKeys.PanelBackgroundAltBrush] = "#1D2531",
        [ThemeResourceKeys.SurfaceBrush] = "#232C3A",
        [ThemeResourceKeys.SurfaceHoverBrush] = "#304056",
        [ThemeResourceKeys.SurfacePressedBrush] = "#1B2635",
        [ThemeResourceKeys.BorderBrushTheme] = "#2D394B",
        [ThemeResourceKeys.PrimaryTextBrush] = "#F4F6FA",
        [ThemeResourceKeys.SecondaryTextBrush] = "#A8B3C2",
        [ThemeResourceKeys.AccentBrush] = "#2F6FED",
        [ThemeResourceKeys.AccentHoverBrush] = "#3C7AFF",
        [ThemeResourceKeys.AccentForegroundBrush] = "#FFFFFF",
        [ThemeResourceKeys.WarningBrush] = "#F5C76B",
        [ThemeResourceKeys.SuccessBrush] = "#3BB273",
        [ThemeResourceKeys.ErrorBrush] = "#F26D6D",
        [ThemeResourceKeys.SelectionBrush] = "#2F6FED",
        [ThemeResourceKeys.SelectionForegroundBrush] = "#FFFFFF",
        [ThemeResourceKeys.MessageSurfaceBrush] = "#232C3A",
        [ThemeResourceKeys.LinkBrush] = "#7DB6FF",
        [ThemeResourceKeys.CodeBackgroundBrush] = "#11161F",
        [ThemeResourceKeys.CodeInlineBackgroundBrush] = "#273142",
        [ThemeResourceKeys.CodeKeywordBrush] = "#C792EA",
        [ThemeResourceKeys.CodeStringBrush] = "#C3E88D",
        [ThemeResourceKeys.CodeCommentBrush] = "#7F8C9D",
        [ThemeResourceKeys.CodeNumberBrush] = "#82AAFF",
        [ThemeResourceKeys.CodeIdentifierBrush] = "#F6BD60",
        [ThemeResourceKeys.OverlayBrush] = "#B2000000"
    };

    public override string Name => "Dark";
    public override bool IsDark => true;
    protected override IReadOnlyDictionary<string, string> BrushColors => Colors;
}



