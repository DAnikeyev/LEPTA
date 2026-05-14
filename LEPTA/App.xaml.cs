using System.Windows;
using LEPTA.Diagnostics;
using LEPTA.Shared.Diagnostics;
using NLog;

namespace LEPTA;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
	public ILeptaLogger RuntimeLogger { get; private set; } = NullLeptaLogger.Instance;

	protected override void OnStartup(StartupEventArgs e)
	{
		LeptaNLogBootstrapper.Configure();
		RuntimeLogger = new NLogLeptaLogger();
		RuntimeLogger.Log(nameof(App), "Application startup initialized NLog logging.");
		base.OnStartup(e);
	}

	protected override void OnExit(ExitEventArgs e)
	{
		RuntimeLogger.Log(nameof(App), $"Application exit. code={e.ApplicationExitCode}.");
		LogManager.Shutdown();
		base.OnExit(e);
	}
}

