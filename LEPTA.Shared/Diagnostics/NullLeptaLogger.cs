namespace LEPTA.Shared.Diagnostics;

public sealed class NullLeptaLogger : ILeptaLogger
{
    public static NullLeptaLogger Instance { get; } = new();

    private NullLeptaLogger()
    {
    }

    public void Log(string source, string message)
    {
    }
}

