namespace LEPTA.Services;

internal static class WebViewAirspaceManager
{
    private static int suppressionDepth;

    public static bool IsSuppressed => suppressionDepth > 0;

    public static event Action<bool>? SuppressionChanged;

    public static void PushSuppression()
    {
        suppressionDepth++;
        if (suppressionDepth == 1)
        {
            SuppressionChanged?.Invoke(true);
        }
    }

    public static void PopSuppression()
    {
        if (suppressionDepth == 0)
        {
            return;
        }

        suppressionDepth--;
        if (suppressionDepth == 0)
        {
            SuppressionChanged?.Invoke(false);
        }
    }
}
