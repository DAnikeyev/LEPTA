using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LEPTA.Shared.Services;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace LEPTA.Diagnostics;

internal static class LeptaNLogBootstrapper
{
    private const int AttachParentProcess = -1;

    public static void Configure()
    {
        EnsureConsole();

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

        var consoleTarget = new ColoredConsoleTarget("lepta-console")
        {
            Layout = layout,
            DetectConsoleAvailable = true
        };

        config.AddTarget(fileTarget);
        config.AddTarget(consoleTarget);
        config.AddRuleForAllLevels(fileTarget);
        config.AddRuleForAllLevels(consoleTarget);

        LogManager.Configuration = config;
    }

    private static void EnsureConsole()
    {
        if (GetConsoleWindow() == IntPtr.Zero)
        {
            if (!AttachConsole(AttachParentProcess))
            {
                AllocConsole();
            }
        }

        try
        {
            var standardOutput = Console.OpenStandardOutput();
            var writer = new StreamWriter(standardOutput) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);

            var standardInput = Console.OpenStandardInput();
            Console.SetIn(new StreamReader(standardInput));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}


