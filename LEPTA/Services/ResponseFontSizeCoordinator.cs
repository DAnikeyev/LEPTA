namespace LEPTA.Services;

internal static class ResponseFontSizeCoordinator
{
    public static event Action<int>? StepRequested;

    public static void RequestStep(int direction)
    {
        if (direction == 0)
        {
            return;
        }

        StepRequested?.Invoke(direction);
    }
}

