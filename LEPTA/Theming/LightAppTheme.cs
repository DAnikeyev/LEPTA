namespace LEPTA.Theming;

internal sealed class LightAppTheme : AppThemeBase
{
    private static readonly IReadOnlyDictionary<string, string> Colors = new Dictionary<string, string>
    {
        [ThemeResourceKeys.WindowBackgroundBrush] = "#EEF2F8",
        [ThemeResourceKeys.PanelBackgroundBrush] = "#FFFFFF",
        [ThemeResourceKeys.PanelBackgroundAltBrush] = "#F7F9FC",
        [ThemeResourceKeys.SurfaceBrush] = "#FFFFFF",
        [ThemeResourceKeys.SurfaceHoverBrush] = "#E7EDF7",
        [ThemeResourceKeys.SurfacePressedBrush] = "#DCE5F3",
        [ThemeResourceKeys.BorderBrushTheme] = "#CAD3E1",
        [ThemeResourceKeys.PrimaryTextBrush] = "#1C2430",
        [ThemeResourceKeys.SecondaryTextBrush] = "#5F6E82",
        [ThemeResourceKeys.AccentBrush] = "#2F6FED",
        [ThemeResourceKeys.AccentHoverBrush] = "#255ED3",
        [ThemeResourceKeys.AccentForegroundBrush] = "#FFFFFF",
        [ThemeResourceKeys.WarningBrush] = "#996A00",
        [ThemeResourceKeys.SuccessBrush] = "#2F8F5B",
        [ThemeResourceKeys.ErrorBrush] = "#C43D3D",
        [ThemeResourceKeys.SelectionBrush] = "#2F6FED",
        [ThemeResourceKeys.SelectionForegroundBrush] = "#FFFFFF",
        [ThemeResourceKeys.MessageSurfaceBrush] = "#E8EEF8",
        [ThemeResourceKeys.LinkBrush] = "#1A5CC8",
        [ThemeResourceKeys.CodeBackgroundBrush] = "#F0F3F8",
        [ThemeResourceKeys.CodeInlineBackgroundBrush] = "#E4EAF2",
        [ThemeResourceKeys.CodeKeywordBrush] = "#7B30D0",
        [ThemeResourceKeys.CodeStringBrush] = "#2E7D32",
        [ThemeResourceKeys.CodeCommentBrush] = "#8090A4",
        [ThemeResourceKeys.CodeNumberBrush] = "#1565C0",
        [ThemeResourceKeys.CodeIdentifierBrush] = "#B8860B",
        [ThemeResourceKeys.OverlayBrush] = "#99000000"
    };

    public override string Name => "Light";
    public override bool IsDark => false;
    protected override IReadOnlyDictionary<string, string> BrushColors => Colors;
}



