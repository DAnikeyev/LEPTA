using System.IO;
using System.Text;
using LEPTA.Shared.Services;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace LEPTA.Diagnostics;

internal static class LeptaNLogBootstrapper
{
    public static void Configure()
    {
        var appDataPaths = new AppDataPaths();
        appDataPaths.EnsureCreated();

        var layout = "${longdate}|${level:uppercase=true}|${logger}|${message}${onexception:${newline}${exception:format=tostring}}";
        var config = new LoggingConfiguration();

        var fileTarget = new FileTarget("lepta-file")
        {
            FileName = Path.Combine(appDataPaths.LogsDirectory, "lepta.log"),
            Layout = layout,
            ArchiveEvery = FileArchivePeriod.Day,
            MaxArchiveFiles = 7,
            KeepFileOpen = false,
            Encoding = Encoding.UTF8
        };

        config.AddTarget(fileTarget);
        config.AddRuleForAllLevels(fileTarget);

        // A console window is only attached in development. Ship builds (WinExe, no debugger,
        // no LEPTA_CONSOLE_LOGS env var) must stay quiet and never pop a console.
        var enableConsole = System.Diagnostics.Debugger.IsAttached
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEPTA_CONSOLE_LOGS"));
        if (enableConsole)
        {
            var consoleTarget = new ColoredConsoleTarget("lepta-console")
            {
                Layout = layout,
                DetectConsoleAvailable = true
            };
            config.AddTarget(consoleTarget);
            config.AddRuleForAllLevels(consoleTarget);
        }

        LogManager.Configuration = config;
    }

}


