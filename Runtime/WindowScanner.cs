using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Auto.Utils;

namespace Auto.Runtime;

public static class WindowScanner {
	private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

	public static List<GameWindow> FindGameWindows() {
		List<GameWindow> games = new List<GameWindow>();

		EnumWindows((hWnd, lParam) => {
			if (!IsWindowVisible(hWnd)) {
				return true;
			}

			string title = GetWindowTitle(hWnd);

			if (string.IsNullOrWhiteSpace(title)) {
				return true;
			}

			if (!title.Contains("thaptuyettran.vn", StringComparison.OrdinalIgnoreCase)) {
				return true;
			}

			GetWindowThreadProcessId(hWnd, out int processId);

			if (processId <= 0) {
				return true;
			}

			ValidationResult clientIdentity = GameClientIdentity.ValidateProcess(processId);
			if (!clientIdentity.Success) {
				return true;
			}

			games.Add(new GameWindow {
				Handle = hWnd,
				ProcessId = processId,
				Title = title,
				RuntimeLayout = RuntimeLayoutResolver.Resolve(processId)
			});

			return true;
		}, IntPtr.Zero);

		return games.OrderByDescending(GetProcessStartTimeSafe).ToList();
	}

	public static bool IsWindowAlive(IntPtr handle) {
		if (handle == IntPtr.Zero) {
			return false;
		}

		return IsWindow(handle);
	}

	private static DateTime GetProcessStartTimeSafe(GameWindow game) {
		try {
			using Process process = Process.GetProcessById(game.ProcessId);
			return process.StartTime;
		} catch {
			return DateTime.MinValue;
		}
	}

	private static string GetWindowTitle(IntPtr hWnd) {
		int length = GetWindowTextLength(hWnd);

		if (length <= 0) {
			return "";
		}

		StringBuilder builder = new StringBuilder(length + 1);
		GetWindowText(hWnd, builder, builder.Capacity);
		return builder.ToString();
	}

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(IntPtr hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int GetWindowTextLength(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
}
