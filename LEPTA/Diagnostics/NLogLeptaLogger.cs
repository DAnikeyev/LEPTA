using LEPTA.Shared.Diagnostics;
using NLog;

namespace LEPTA.Diagnostics;

internal sealed class NLogLeptaLogger : ILeptaLogger
{
    public void Log(string source, string message)
        => LogManager.GetLogger(source).Info(message);
}

