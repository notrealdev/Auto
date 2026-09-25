using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Auto.Utils;

namespace Auto.Runtime;

public static class WindowScanner {
	private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

	public static List<GameWindow> FindGameWindows() {
		long profilerStart = HotPathProfiler.Begin();
		try {
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

			// KHÔNG gọi RuntimeLayoutResolver.Resolve ở đây. Client chưa PlayerReady thì mỗi lần Resolve là dump cả ảnh
			// Game.exe rồi quét chữ ký, mà hàm này chạy mỗi giây cho MỌI cửa sổ kể cả account tắt Auto tổng. Đo trên 21
			// client (6 bật Auto, 2026-09-25): perf.log QuétCửaSổ=563ms/lần; đo riêng 21 lần Resolve = 3.120ms, chỉ 6/21
			// PlayerReady. Kết quả ở đây cũng chỉ dùng cho cửa sổ MỚI (AccountListViewModel.ApplyScanResult bỏ qua cửa sổ
			// đã biết); layout thật do AccountEngineCoordinator.TickOne resolve khi Auto tổng bật.
			games.Add(new GameWindow {
				Handle = hWnd,
				ProcessId = processId,
				Title = title
			});

			return true;
		}, IntPtr.Zero);

		return games.OrderByDescending(GetProcessStartTimeSafe).ToList();
		} finally {
			HotPathProfiler.End(HotPathProfiler.WindowScan, profilerStart);
		}
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
