using System.IO;
using System.Windows;

namespace LuminChatWin.App;

public partial class App : Application
{
	public AppRuntime Runtime { get; private set; } = null!;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lumin-chat-win", "config.json");
		var workspaceRoot = Environment.CurrentDirectory;
		Runtime = new AppRuntime(configPath, workspaceRoot);

		var license = LuminChatWin.Core.Services.LicenseGuard.Validate(Runtime.Config);
		if (!license.Ok)
		{
			MessageBox.Show(license.Message, "许可证校验失败", MessageBoxButton.OK, MessageBoxImage.Error);
			Shutdown(-1);
			return;
		}

		var mainWindow = new MainWindow(Runtime);
		MainWindow = mainWindow;
		mainWindow.Show();
	}
}

