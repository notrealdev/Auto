using System.Drawing;
using System.Runtime.InteropServices;

namespace Auto.Runtime;

public static class WindowCapture {
	public static Bitmap Capture(IntPtr hwnd) {
		GetWindowRect(hwnd, out RECT r);

		int width = r.Right - r.Left;
		int height = r.Bottom - r.Top;
		Bitmap bmp = new Bitmap(width, height);

		using Graphics g = Graphics.FromImage(bmp);
		IntPtr hdc = g.GetHdc();

		PrintWindow(hwnd, hdc, 0);
		g.ReleaseHdc(hdc);

		return bmp;
	}

	private struct RECT {
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	[DllImport("user32.dll")]
	private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, int flags);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
