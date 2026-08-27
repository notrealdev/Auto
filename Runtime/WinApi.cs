using System.Runtime.InteropServices;

namespace Auto.Runtime;

public static class WinApi {
	[DllImport("user32.dll")]
	public static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	public static extern bool IsIconic(IntPtr hWnd);
}