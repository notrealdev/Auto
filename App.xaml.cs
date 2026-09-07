namespace Auto;

using System.IO;
using System.Windows;
using System.Windows.Threading;
using Auto.Attack;
using Auto.UI.Views;

public partial class App : Application {
	public App() {
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
	}

	protected override void OnStartup(StartupEventArgs e) {
		base.OnStartup(e);

		if (!AutoFsAttackTransport.TryValidateDependency(out string dependencyError)) {
			MessageBox.Show(dependencyError, "SystemUint.dll", MessageBoxButton.OK, MessageBoxImage.Error);
			Shutdown();
			return;
		}

		new MainWindow().Show();
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) {
		LogAndShow(e.Exception);
		e.Handled = true;
		Shutdown();
	}

	private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) {
		if (e.ExceptionObject is Exception exception) LogAndShow(exception);
	}

	private static void LogAndShow(Exception exception) {
		try {
			File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), exception.ToString());
		} catch {
			// Ghi log là best-effort, không để lỗi ghi file che mất MessageBox chẩn đoán bên dưới.
		}

		MessageBox.Show(exception.ToString(), "Auto - Lỗi khởi động", MessageBoxButton.OK, MessageBoxImage.Error);
	}
}
