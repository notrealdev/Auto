namespace Auto.Attack;

using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using Auto.Runtime;

// Observes Ctrl+A without suppressing or synthesizing keyboard input.
public static class AttackHotkeyTracker {
	private const int WhKeyboardLl = 13;
	private const int WmKeyDown = 0x0100;
	private const int WmKeyUp = 0x0101;
	private const int WmSysKeyDown = 0x0104;
	private const int WmSysKeyUp = 0x0105;
	private const int VkA = 0x41;
	private const int VkControl = 0x11;
	private const uint LlKeyboardInjected = 0x00000010;
	private const uint GaRoot = 2;
	private const uint WmQuit = 0x0012;

	private static readonly object syncRoot = new();
	private static readonly object diagnosticSync = new();
	private static readonly ConcurrentQueue<string> diagnosticEntries = new();
	private static int diagnosticWriterScheduled;
	private static string DiagnosticPath => Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "ctrl-a.log");
	private static readonly HashSet<IntPtr> gameWindowHandles = new();
	private static readonly ConcurrentQueue<IntPtr> pendingWindows = new();
	private static readonly LowLevelKeyboardProc keyboardHookProc = KeyboardHookCallback;
	private static IntPtr keyboardHook = IntPtr.Zero;
	private static Thread? keyboardHookThread;
	private static uint keyboardHookThreadId;
	private static bool ctrlAIsDown;

	// Xếp log theo đúng thứ tự phát sinh mà không chặn callback bàn phím bằng I/O.
	public static void LogDiagnostic(string message) {
		if (!DebugFileLogging.Enabled) return;
		string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | TID={Environment.CurrentManagedThreadId} | {message}{Environment.NewLine}";
		diagnosticEntries.Enqueue(entry);
		if (Interlocked.Exchange(ref diagnosticWriterScheduled, 1) == 0) ThreadPool.QueueUserWorkItem(_ => DrainDiagnosticQueue());
	}

	// Xả tuần tự hàng đợi log và tự xử lý trường hợp log mới đến đúng lúc kết thúc.
	private static void DrainDiagnosticQueue() {
		while (true) {
			while (diagnosticEntries.TryDequeue(out string? entry)) WriteDiagnostic(entry);
			Interlocked.Exchange(ref diagnosticWriterScheduled, 0);
			if (diagnosticEntries.IsEmpty || Interlocked.Exchange(ref diagnosticWriterScheduled, 1) != 0) return;
		}
	}

	// Ghi một dòng chẩn đoán vào đúng thư mục của bản đang chạy.
	private static void WriteDiagnostic(string entry) {
		if (!DebugFileLogging.Enabled) return;
		try {
			string diagnosticPath = DiagnosticPath;
			lock (diagnosticSync) {
				Directory.CreateDirectory(Path.GetDirectoryName(diagnosticPath)!);
				File.AppendAllText(diagnosticPath, entry);
			}
		} catch { }
	}

	// Theo dõi một cửa sổ game và bảo đảm hook bàn phím đã được cài đặt.
	public static void TrackWindow(IntPtr gameWindowHandle) {
		if (gameWindowHandle == IntPtr.Zero) return;
		bool added;
		lock (syncRoot) {
			added = gameWindowHandles.Add(gameWindowHandle);
			EnsureHookNoLock();
		}
		GetWindowThreadProcessId(gameWindowHandle, out uint gameProcessId);
		LogDiagnostic($"TRACK_WINDOW | HWND=0x{gameWindowHandle.ToInt64():X8} | PID={gameProcessId} | Added={added} | Hook=0x{keyboardHook.ToInt64():X8} | HookThreadId={keyboardHookThreadId}");
	}

	// Dừng theo dõi bàn phím và ghi lại trạng thái bị xóa để chẩn đoán vòng đời hook.
	public static void ResetAll() {
		int trackedCount;
		int queuedCount;
		lock (syncRoot) {
			trackedCount = gameWindowHandles.Count;
			queuedCount  = pendingWindows.Count;
			gameWindowHandles.Clear();
			ctrlAIsDown = false;
			while (pendingWindows.TryDequeue(out _)) {
			}
			StopHookNoLock();
		}
		LogDiagnostic($"RESET | Tracked={trackedCount} | Queued={queuedCount}");
	}

	// Lấy một yêu cầu toggle đã xếp hàng và ghi lại số yêu cầu còn chờ.
	public static bool TryTakeToggle(out IntPtr gameWindowHandle) {
		bool dequeued = pendingWindows.TryDequeue(out gameWindowHandle);
		if (dequeued) LogDiagnostic($"DEQUEUE | HWND=0x{gameWindowHandle.ToInt64():X8} | Remaining={pendingWindows.Count}");
		return dequeued;
	}

	// Khởi động hook bàn phím trên luồng thông điệp riêng và ghi kết quả chờ khởi tạo.
	private static void EnsureHookNoLock() {
		if (keyboardHook != IntPtr.Zero || keyboardHookThread != null) return;
		ManualResetEvent started = new(false);
		keyboardHookThread = new Thread(() => RunKeyboardHookThread(started)) { IsBackground = true, Name = "DEV Attack Hotkey Hook" };
		keyboardHookThread.Start();
		bool initialized = started.WaitOne(2000);
		if (initialized) started.Dispose();
		LogDiagnostic($"HOOK_START_RESULT | Initialized={initialized} | Hook=0x{keyboardHook.ToInt64():X8} | HookThreadId={keyboardHookThreadId}");
		if (keyboardHook == IntPtr.Zero) {
			keyboardHookThread = null;
			keyboardHookThreadId = 0;
		}
	}

	// Gỡ hook, kết thúc vòng lặp thông điệp và ghi kết quả từng thao tác native.
	private static void StopHookNoLock() {
		IntPtr hook = keyboardHook;
		uint threadId = keyboardHookThreadId;
		keyboardHook = IntPtr.Zero;
		keyboardHookThread = null;
		keyboardHookThreadId = 0;
		bool unhooked = hook == IntPtr.Zero || UnhookWindowsHookEx(hook);
		bool quitPosted = threadId == 0 || PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
		LogDiagnostic($"HOOK_STOP | Hook=0x{hook.ToInt64():X8} | HookThreadId={threadId} | Unhooked={unhooked} | QuitPosted={quitPosted}");
	}

	// Sở hữu hook native, ghi lỗi cài đặt và vận hành vòng lặp thông điệp.
	private static void RunKeyboardHookThread(ManualResetEvent started) {
		keyboardHookThreadId = GetCurrentThreadId();
		keyboardHook = SetWindowsHookEx(WhKeyboardLl, keyboardHookProc, GetModuleHandle(null), 0);
		int installError = Marshal.GetLastWin32Error();
		LogDiagnostic($"HOOK_INSTALLED | Hook=0x{keyboardHook.ToInt64():X8} | HookThreadId={keyboardHookThreadId} | Success={keyboardHook != IntPtr.Zero} | Win32Error={installError}");
		started.Set();
		if (keyboardHook == IntPtr.Zero) return;
		int messageResult;
		while ((messageResult = GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0)) > 0) {
			TranslateMessage(ref message);
			DispatchMessage(ref message);
		}
		int messageError = messageResult < 0 ? Marshal.GetLastWin32Error() : 0;
		LogDiagnostic($"HOOK_THREAD_EXIT | HookThreadId={GetCurrentThreadId()} | GetMessageResult={messageResult} | Win32Error={messageError}");
	}

	// Quan sát phím A vật lý, ghi trạng thái Ctrl và chỉ xếp toggle khi foreground/root thuộc cửa sổ game.
	private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
		try {
			if (nCode >= 0) {
				int message = unchecked((int)wParam.ToInt64());
				KeyboardHookStruct hookData = Marshal.PtrToStructure<KeyboardHookStruct>(lParam);
				if (hookData.VirtualKey == VkA) {
					bool injected = (hookData.Flags & LlKeyboardInjected) != 0;
					if (message == WmKeyUp || message == WmSysKeyUp) {
						if (!injected) ctrlAIsDown = false;
						LogDiagnostic($"A_KEYUP | Message=0x{message:X4} | ScanCode={hookData.ScanCode} | Flags=0x{hookData.Flags:X8} | Injected={injected}");
					} else if (message == WmKeyDown || message == WmSysKeyDown) {
						short controlState = GetAsyncKeyState(VkControl);
						bool controlDown = (controlState & 0x8000) != 0;
						if (injected || !controlDown) {
							LogDiagnostic($"A_KEYDOWN_IGNORED | Message=0x{message:X4} | ScanCode={hookData.ScanCode} | Flags=0x{hookData.Flags:X8} | Injected={injected} | ControlDown={controlDown} | ControlState=0x{unchecked((ushort)controlState):X4}");
						} else {
							bool repeated = ctrlAIsDown;
							ctrlAIsDown = true;
							IntPtr foregroundWindow = GetForegroundWindow();
							IntPtr rootWindow = foregroundWindow == IntPtr.Zero ? IntPtr.Zero : GetAncestor(foregroundWindow, GaRoot);
							GetWindowThreadProcessId(foregroundWindow, out uint foregroundProcessId);
							GetWindowThreadProcessId(rootWindow, out uint rootProcessId);
							IntPtr selectedWindow = IntPtr.Zero;
							bool foregroundTracked;
							bool rootTracked;
							int trackedCount;
							int queuedCount;
							lock (syncRoot) {
								foregroundTracked = gameWindowHandles.Contains(foregroundWindow);
								rootTracked       = rootWindow != IntPtr.Zero && gameWindowHandles.Contains(rootWindow);
								trackedCount      = gameWindowHandles.Count;
								if (!repeated) {
									if (foregroundTracked) selectedWindow = foregroundWindow;
									else if (rootTracked) selectedWindow = rootWindow;
									if (selectedWindow != IntPtr.Zero) pendingWindows.Enqueue(selectedWindow);
								}
								queuedCount = pendingWindows.Count;
							}
							LogDiagnostic($"CTRL_A_KEYDOWN | Message=0x{message:X4} | ScanCode={hookData.ScanCode} | Flags=0x{hookData.Flags:X8} | ControlState=0x{unchecked((ushort)controlState):X4} | Repeated={repeated} | Foreground=0x{foregroundWindow.ToInt64():X8} | ForegroundPID={foregroundProcessId} | Root=0x{rootWindow.ToInt64():X8} | RootPID={rootProcessId} | ForegroundTracked={foregroundTracked} | RootTracked={rootTracked} | Selected=0x{selectedWindow.ToInt64():X8} | Enqueued={selectedWindow != IntPtr.Zero} | Tracked={trackedCount} | Queue={queuedCount}");
						}
					}
				}
			}
		} catch (Exception exception) {
			LogDiagnostic($"HOOK_CALLBACK_EXCEPTION | Type={exception.GetType().FullName} | Message={exception.Message}");
		}
		return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
	}

	private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

	[StructLayout(LayoutKind.Sequential)]
	private struct KeyboardHookStruct {
		public int VirtualKey;
		public int ScanCode;
		public uint Flags;
		public uint Time;
		public IntPtr ExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint {
		public int X;
		public int Y;
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
	private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr moduleHandle, uint threadId);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnhookWindowsHookEx(IntPtr hook);

	[DllImport("user32.dll")]
	private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int virtualKey);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int GetMessage(out NativeMessage message, IntPtr windowHandle, uint messageFilterMin, uint messageFilterMax);

	[DllImport("user32.dll")]
	private static extern bool TranslateMessage(ref NativeMessage message);

	[DllImport("user32.dll")]
	private static extern IntPtr DispatchMessage(ref NativeMessage message);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr GetModuleHandle(string? moduleName);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();
}
