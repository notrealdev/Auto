namespace Auto.Attack;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

public static class ManualInputTracker {
	private const int WhMouseLl = 14;
	private const int WmLButtonDown = 0x0201;
	private const int WmRButtonDown = 0x0204;
	private const uint LlMouseInjected = 0x00000001;
	private const uint GaRoot = 2;
	private const uint WmQuit = 0x0012;

	private static readonly object syncRoot = new();
	private static readonly HashSet<IntPtr> gameWindowHandles = new();
	private static readonly Dictionary<IntPtr, DateTime> lastLeftClickUtcByWindow = new();
	private static readonly Dictionary<IntPtr, DateTime> lastRightClickUtcByWindow = new();
	private static readonly Dictionary<IntPtr, long> lastLeftClickSequenceByWindow = new();
	private static readonly Dictionary<IntPtr, NativePoint> lastLeftClickScreenPointByWindow = new();
	private static readonly LowLevelMouseProc mouseHookProc = MouseHookCallback;

	private static IntPtr mouseHook = IntPtr.Zero;
	private static Thread? mouseHookThread;
	private static uint mouseHookThreadId;
	private static long nextLeftClickSequence;
	private static string mouseHookError = "";

	// Compatibility only. Right-click is intentionally not a combat-bootstrap input.
	public static bool LastInputRequiresCombatBootstrap(IntPtr gameWindowHandle) {
		return false;
	}

	public static void TrackWindow(IntPtr gameWindowHandle) {
		if (gameWindowHandle == IntPtr.Zero) {
			return;
		}

		lock (syncRoot) {
			if (gameWindowHandles.Add(gameWindowHandle)) {
				lastLeftClickUtcByWindow[gameWindowHandle] = DateTime.MinValue;
				lastRightClickUtcByWindow[gameWindowHandle] = DateTime.MinValue;
				lastLeftClickSequenceByWindow[gameWindowHandle] = 0;
				lastLeftClickScreenPointByWindow[gameWindowHandle] = default;
			}

			EnsureHookNoLock();
		}
	}

	public static void UntrackWindow(IntPtr gameWindowHandle) {
		if (gameWindowHandle == IntPtr.Zero) {
			return;
		}

		lock (syncRoot) {
			gameWindowHandles.Remove(gameWindowHandle);
			lastLeftClickUtcByWindow.Remove(gameWindowHandle);
			lastRightClickUtcByWindow.Remove(gameWindowHandle);
			lastLeftClickSequenceByWindow.Remove(gameWindowHandle);
			lastLeftClickScreenPointByWindow.Remove(gameWindowHandle);

			if (gameWindowHandles.Count == 0) {
				StopHookNoLock();
			}
		}
	}

	public static void ClearInput(IntPtr gameWindowHandle) {
		if (gameWindowHandle == IntPtr.Zero) {
			return;
		}

		lock (syncRoot) {
			if (!gameWindowHandles.Contains(gameWindowHandle)) {
				return;
			}

			lastLeftClickUtcByWindow[gameWindowHandle] = DateTime.MinValue;
			lastRightClickUtcByWindow[gameWindowHandle] = DateTime.MinValue;
			lastLeftClickSequenceByWindow[gameWindowHandle] = 0;
			lastLeftClickScreenPointByWindow[gameWindowHandle] = default;
		}
	}

	public static void ResetAll() {
		lock (syncRoot) {
			gameWindowHandles.Clear();
			lastLeftClickUtcByWindow.Clear();
			lastRightClickUtcByWindow.Clear();
			lastLeftClickSequenceByWindow.Clear();
			lastLeftClickScreenPointByWindow.Clear();
			nextLeftClickSequence = 0;
			StopHookNoLock();
		}
	}

	public static bool TryGetLatestLeftClick(IntPtr gameWindowHandle, out long sequence, out DateTime occurredUtc) {
		return TryGetLatestLeftClick(gameWindowHandle, out sequence, out occurredUtc, out _, out _);
	}

	public static bool TryGetLatestLeftClick(IntPtr gameWindowHandle, out long sequence, out DateTime occurredUtc, out int screenX, out int screenY) {
		sequence = 0;
		occurredUtc = DateTime.MinValue;
		screenX = 0;
		screenY = 0;

		if (gameWindowHandle == IntPtr.Zero) {
			return false;
		}

		lock (syncRoot) {
			if (!lastLeftClickSequenceByWindow.TryGetValue(gameWindowHandle, out sequence) || !lastLeftClickUtcByWindow.TryGetValue(gameWindowHandle, out occurredUtc) || !lastLeftClickScreenPointByWindow.TryGetValue(gameWindowHandle, out NativePoint screenPoint)) {
				sequence = 0;
				occurredUtc = DateTime.MinValue;
				return false;
			}

			screenX = screenPoint.X;
			screenY = screenPoint.Y;
			return sequence > 0 && occurredUtc != DateTime.MinValue;
		}
	}

	public static string GetLastInputDescription(IntPtr gameWindowHandle) {
		lock (syncRoot) {
			lastLeftClickUtcByWindow.TryGetValue(gameWindowHandle, out DateTime lastLeftClickUtc);
			lastRightClickUtcByWindow.TryGetValue(gameWindowHandle, out DateTime lastRightClickUtc);
			DateTime latestUtc = lastRightClickUtc > lastLeftClickUtc ? lastRightClickUtc : lastLeftClickUtc;

			if (latestUtc == DateTime.MinValue) {
				return "Unknown";
			}

			string inputName = lastRightClickUtc > lastLeftClickUtc ? "RightMouse" : "LeftMouse";
			return $"{inputName} | {(DateTime.UtcNow - latestUtc).TotalMilliseconds:F0}ms";
		}
	}

	public static string GetHookStatus() {
		lock (syncRoot) {
			string mouseStatus = mouseHook == IntPtr.Zero ? "FAIL" : "OK";
			return $"ManualInputTracker | MouseHook={mouseStatus} | KeyboardHook=DISABLED | MouseError={mouseHookError}";
		}
	}

	private static void EnsureHookNoLock() {
		if (mouseHook != IntPtr.Zero || mouseHookThread != null) {
			return;
		}

		ManualResetEvent started = new(false);
		mouseHookThread = new Thread(() => RunMouseHookThread(started)) { IsBackground = true, Name = "DEV Manual Mouse Hook" };
		mouseHookThread.Start();
		bool initialized = started.WaitOne(2000);
		if (initialized) started.Dispose();
		else mouseHookError = "Timeout khi khởi tạo mouse hook thread.";
		if (mouseHook == IntPtr.Zero) {
			mouseHookThread = null;
			mouseHookThreadId = 0;
		}
	}

	private static void StopHookNoLock() {
		IntPtr hook = mouseHook;
		uint threadId = mouseHookThreadId;
		mouseHook = IntPtr.Zero;
		mouseHookThread = null;
		mouseHookThreadId = 0;
		if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
		if (threadId != 0) PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
	}

	private static void RunMouseHookThread(ManualResetEvent started) {
		mouseHookThreadId = GetCurrentThreadId();
		mouseHook = SetWindowsHookEx(WhMouseLl, mouseHookProc, GetModuleHandle(null), 0);
		mouseHookError = mouseHook == IntPtr.Zero ? "Win32Error=" + Marshal.GetLastWin32Error() : "";
		started.Set();
		if (mouseHook == IntPtr.Zero) return;
		while (GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0) > 0) {
			TranslateMessage(ref message);
			DispatchMessage(ref message);
		}
	}

	private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
		try {
			int message = unchecked((int)wParam.ToInt64());

			if (nCode >= 0 && (message == WmLButtonDown || message == WmRButtonDown)) {
				MsllHookStruct hookData = Marshal.PtrToStructure<MsllHookStruct>(lParam);

				if ((hookData.Flags & LlMouseInjected) == 0) {
					IntPtr gameWindowHandle = FindTrackedWindowAtScreenPoint(hookData.Point);

					if (gameWindowHandle != IntPtr.Zero && !IsIconic(gameWindowHandle)) {
						if (message == WmLButtonDown) {
							MarkLeftClickForWindow(gameWindowHandle, hookData.Point);
						} else {
							MarkRightClickForWindow(gameWindowHandle);
						}
					}
				}
			}
		} catch {
		}

		return CallNextHookEx(mouseHook, nCode, wParam, lParam);
	}

	private static IntPtr FindTrackedWindowAtScreenPoint(NativePoint screenPoint) {
		IntPtr hitWindowHandle = WindowFromPoint(screenPoint);
		IntPtr trackedWindowHandle = FindTrackedWindowFromWindow(hitWindowHandle);

		if (trackedWindowHandle == IntPtr.Zero || IsIconic(trackedWindowHandle)) {
			return IntPtr.Zero;
		}

		return IsPointInsideClientArea(trackedWindowHandle, screenPoint) ? trackedWindowHandle : IntPtr.Zero;
	}

	private static IntPtr FindTrackedWindowFromWindow(IntPtr windowHandle) {
		if (windowHandle == IntPtr.Zero) {
			return IntPtr.Zero;
		}

		lock (syncRoot) {
			if (gameWindowHandles.Contains(windowHandle)) {
				return windowHandle;
			}

			IntPtr rootWindowHandle = GetAncestor(windowHandle, GaRoot);

			if (rootWindowHandle != IntPtr.Zero && gameWindowHandles.Contains(rootWindowHandle)) {
				return rootWindowHandle;
			}

			foreach (IntPtr gameWindowHandle in gameWindowHandles) {
				if (IsChild(gameWindowHandle, windowHandle)) {
					return gameWindowHandle;
				}
			}
		}

		return IntPtr.Zero;
	}

	private static bool IsPointInsideClientArea(IntPtr gameWindowHandle, NativePoint screenPoint) {
		NativePoint clientPoint = screenPoint;

		if (!GetClientRect(gameWindowHandle, out NativeRect clientRect) || !ScreenToClient(gameWindowHandle, ref clientPoint)) {
			return false;
		}

		int clientWidth = clientRect.Right - clientRect.Left;
		int clientHeight = clientRect.Bottom - clientRect.Top;

		return clientPoint.X >= 0 && clientPoint.Y >= 0 && clientPoint.X < clientWidth && clientPoint.Y < clientHeight;
	}

	private static void MarkLeftClickForWindow(IntPtr gameWindowHandle, NativePoint screenPoint) {
		if (gameWindowHandle == IntPtr.Zero) {
			return;
		}

		lock (syncRoot) {
			if (!gameWindowHandles.Contains(gameWindowHandle)) {
				return;
			}

			nextLeftClickSequence++;
			lastLeftClickSequenceByWindow[gameWindowHandle] = nextLeftClickSequence;
			lastLeftClickUtcByWindow[gameWindowHandle] = DateTime.UtcNow;
			lastLeftClickScreenPointByWindow[gameWindowHandle] = screenPoint;
		}
	}

	private static void MarkRightClickForWindow(IntPtr gameWindowHandle) {
		if (gameWindowHandle == IntPtr.Zero) {
			return;
		}

		lock (syncRoot) {
			if (gameWindowHandles.Contains(gameWindowHandle)) {
				lastRightClickUtcByWindow[gameWindowHandle] = DateTime.UtcNow;
			}
		}
	}

	private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint {
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRect {
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MsllHookStruct {
		public NativePoint Point;
		public uint MouseData;
		public uint Flags;
		public uint Time;
		public IntPtr DwExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeMessage {
		public IntPtr Window;
		public uint Message;
		public IntPtr WParam;
		public IntPtr LParam;
		public uint Time;
		public NativePoint Point;
		public uint Private;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr moduleHandle, uint threadId);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int GetMessage(out NativeMessage message, IntPtr windowHandle, uint messageFilterMin, uint messageFilterMax);

	[DllImport("user32.dll")]
	private static extern bool TranslateMessage(ref NativeMessage message);

	[DllImport("user32.dll")]
	private static extern IntPtr DispatchMessage(ref NativeMessage message);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnhookWindowsHookEx(IntPtr hook);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(IntPtr hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetClientRect(IntPtr gameWindowHandle, out NativeRect clientRect);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool ScreenToClient(IntPtr gameWindowHandle, ref NativePoint screenPoint);

	[DllImport("user32.dll")]
	private static extern IntPtr WindowFromPoint(NativePoint point);

	[DllImport("user32.dll")]
	private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

	[DllImport("user32.dll")]
	private static extern bool IsChild(IntPtr parentWindowHandle, IntPtr childWindowHandle);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr GetModuleHandle(string? moduleName);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();
}
