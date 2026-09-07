namespace Auto.DebugTools;

using System.Runtime.InteropServices;

public static class BackgroundEscapeCommand {
	public const string BuildStamp = "BACKGROUND-ESC-20260716-01";
	private const int WmKeyDown = 0x0100;
	private const int WmKeyUp = 0x0101;
	private const int VkEscape = 0x1B;
	private static readonly IntPtr KeyDownLParam = new(0x00010001);
	private static readonly IntPtr KeyUpLParam = new(unchecked((int)0xC0010001));

	public static string Run(int processId, IntPtr gameWindowHandle) {
		if (processId <= 0 || gameWindowHandle == IntPtr.Zero) return BuildResult(processId, gameWindowHandle, false, false, "PID hoặc HWND không hợp lệ.");
		GetWindowThreadProcessId(gameWindowHandle, out int windowProcessId);
		if (windowProcessId != processId) return BuildResult(processId, gameWindowHandle, false, false, $"HWND thuộc PID khác | WindowPID={windowProcessId}.");

		bool keyDown = PostMessage(gameWindowHandle, WmKeyDown, new IntPtr(VkEscape), KeyDownLParam);
		bool keyUp = PostMessage(gameWindowHandle, WmKeyUp, new IntPtr(VkEscape), KeyUpLParam);
		string reason = keyDown && keyUp ? "Đã gửi ESC riêng tới cửa sổ game đã chọn." : $"PostMessage thất bại | Win32Error={Marshal.GetLastWin32Error()}.";
		return BuildResult(processId, gameWindowHandle, keyDown, keyUp, reason);
	}

	private static string BuildResult(int processId, IntPtr gameWindowHandle, bool keyDown, bool keyUp, string reason) {
		return
			"===== Background ESC Probe =====\r\n" +
			$"BuildStamp = {BuildStamp}\r\n" +
			$"ProcessId = {processId}\r\n" +
			$"Window = 0x{gameWindowHandle.ToInt64():X8}\r\n" +
			$"Status = {(keyDown && keyUp ? "SENT" : "FAIL")}\r\n" +
			$"KeyDown = {keyDown}\r\n" +
			$"KeyUp = {keyUp}\r\n" +
			reason;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int GetWindowThreadProcessId(IntPtr windowHandle, out int processId);
}
