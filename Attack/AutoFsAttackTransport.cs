namespace Auto.Attack;

using System.Runtime.InteropServices;

internal sealed class AutoFsAttackTransport {
	private const string SourceLibraryName = "SystemUint.Source.dll";
	private const string HookMessageName = "WM_HOOK_WRITE";
	private const int AttackCommand = 300;
	private const int UseInventoryItemCommand = 310;
	private const int InventoryContainerShift = 6;
	private const int InventoryItemIdShift = 11;
	private const int MaximumInventoryMemoryIndex = 0x3F;
	private const int MaximumInventoryContainer = 0x1F;
	private const int MaximumInventoryItemId = 0x001FFFFF;
	private const int BeginScriptCommand = 311;
	private const int SendChatCommand = 312;
	private const int AppendScriptByteCommand = 34;
	private const int ExecuteScriptCommand = 22;
	private const uint SendMessageTimeoutMilliseconds = 500;
	private const uint SmtoAbortIfHung = 0x0002;
	private const int LeftSkillId = 0x87;

	private static readonly object SyncRoot = new();
	private static readonly HashSet<IntPtr> HookedWindows = new();
	private static GetMsgDelegate? getMsg;
	private static InjectDllDelegate? injectDll;
	private static UnmapDllDelegate? unmapDll;
	private static IntPtr runtimeLibraryHandle;
	private static string runtimeLibraryPath = "";
	private static uint hookMessage;
	private readonly AutoFsActionGate actionGate;

	public AutoFsAttackTransport(AutoFsActionGate actionGate) {
		this.actionGate = actionGate;
	}

	public static bool TryValidateDependency(out string error) {
		error = "";
		string sourcePath = Path.Combine(AppContext.BaseDirectory, SourceLibraryName);
		if (!File.Exists(sourcePath)) {
			error = $"Auto không thể khởi động vì thiếu {SourceLibraryName} cạnh file Auto.exe.";
			return false;
		}

		try {
			EnsureRuntimeLibraryLoaded(sourcePath);
			uint exportedMessage = getMsg!();
			uint registeredMessage = RegisterWindowMessageA(HookMessageName);
			if (exportedMessage == 0 || exportedMessage != registeredMessage) {
				error = $"{SourceLibraryName} không hợp lệ: GetMsg=0x{exportedMessage:X4}, Registered=0x{registeredMessage:X4}.";
				return false;
			}
			return true;
		} catch (Exception ex) {
			error = $"Không thể nạp {SourceLibraryName}: {ex.GetType().Name}: {ex.Message}";
			return false;
		}
	}

	public bool TrySend(IntPtr gameWindow, int entityIndex, out string error) {
		error = "";
		if (gameWindow == IntPtr.Zero || entityIndex < AutoFsClientProfile.FirstEntityIndex || entityIndex > AutoFsClientProfile.LastEntityIndex) {
			error = $"Invalid HWND/index: 0x{gameWindow.ToInt64():X8}/{entityIndex}.";
			return false;
		}

		int payload = (LeftSkillId << 16) | entityIndex;
		return TrySendCommand(gameWindow, AttackCommand, payload, out error);
	}

	public void Stop() {
	}

	public bool TrySendCommand(IntPtr gameWindow, int command, int payload, out string error) {
		if (! actionGate.AutomationEnabled) {
			error = "Master automation switch is disabled.";
			return false;
		}
		string currentError = "";
		bool sent = actionGate.RunCommand(() => TrySendCommandCore(gameWindow, command, payload, out currentError));
		error = currentError;
		return sent;
	}

	// Gửi đúng command 310 của DEV auto với descriptor đóng gói mà native handler đọc lại theo bit.
	public bool TryUseInventoryItem(IntPtr gameWindow, int itemId, int container, int memoryIndex, out string error) {
		if (! actionGate.AutomationEnabled) {
			error = "Master automation switch is disabled.";
			return false;
		}
		if (itemId <= 0 || itemId > MaximumInventoryItemId || container < 0 || container > MaximumInventoryContainer || memoryIndex < 0 || memoryIndex > MaximumInventoryMemoryIndex) {
			error = $"Invalid inventory descriptor. ItemId={itemId}, Container={container}, MemoryIndex={memoryIndex}.";
			return false;
		}
		int packedItem = unchecked((int)(((uint)itemId << InventoryItemIdShift) | ((uint)container << InventoryContainerShift) | (uint)memoryIndex));
		string currentError = "";
		bool sent = actionGate.RunCommand(() => TrySendConfirmedCommandCore(gameWindow, UseInventoryItemCommand, packedItem, out currentError));
		error = sent || currentError.Length > 0 ? currentError : "Automatic command gate rejected the inventory-item sequence.";
		return sent;
	}

	// Sends one explicit debug command without requiring the automation master switch.
	public bool TrySendTestCommand(IntPtr gameWindow, int command, int payload, out string error) {
		return TrySendCommandCore(gameWindow, command, payload, out error);
	}

	// Gửi một command riêng qua SendMessageTimeout và chỉ thành công khi native handler trả về 1.
	public bool TrySendConfirmedCommand(IntPtr gameWindow, int command, int payload, out string error) {
		if (! actionGate.AutomationEnabled) {
			error = "Master automation switch is disabled.";
			return false;
		}
		string currentError = "";
		bool sent = actionGate.RunCommand(() => TrySendConfirmedCommandCore(gameWindow, command, payload, out currentError));
		error = currentError;
		return sent;
	}

	// Gửi trọn chuỗi script theo đúng command 34 -> 22 của AutoFS trong một khóa hành động.
	public bool TrySendScript(IntPtr gameWindow, byte[] script, out string error) {
		if (! actionGate.AutomationEnabled) {
			error = "Master automation switch is disabled.";
			return false;
		}
		if (script.Length == 0 || script.Length > 199) {
			error = $"Invalid script length: {script.Length}.";
			return false;
		}
		string currentError = "";
		bool sent = actionGate.RunCommand(() => TrySendScriptCore(gameWindow, script, out currentError));
		error = currentError;
		return sent;
	}

	// Gửi nội dung chat qua đúng hàm mạng FUN_004b85d0 sau khi nạp đủ byte legacy vào native.
	public bool TrySendChat(IntPtr gameWindow, int channelId, byte[] content, out string error) {
		if (! actionGate.AutomationEnabled) {
			error = "Master automation switch is disabled.";
			return false;
		}
		if (channelId < 0 || content.Length == 0 || content.Length > 199) {
			error = $"Invalid chat channel/content: {channelId}/{content.Length}.";
			return false;
		}
		string currentError = "";
		bool sent = actionGate.RunCommand(() => {
			if (! TrySendConfirmedCommandCore(gameWindow, BeginScriptCommand, 0, out currentError)) return false;
			foreach (byte value in content) {
				if (! TrySendConfirmedCommandCore(gameWindow, AppendScriptByteCommand, value, out currentError)) return false;
			}
			return TrySendConfirmedCommandCore(gameWindow, SendChatCommand, channelId, out currentError);
		});
		error = currentError;
		return sent;
	}

	private bool TrySendCommandCore(IntPtr gameWindow, int command, int payload, out string error) {
		if (! TryEnsureReceiver(gameWindow, out error)) return false;
		if (PostMessageA(gameWindow, hookMessage, (IntPtr)command, (IntPtr)payload)) return true;
		error = $"PostMessageA failed. Command={command}, Payload={payload}, Win32Error={Marshal.GetLastWin32Error()}";
		return false;
	}

	// Gửi đồng bộ từng byte và chỉ báo thành công khi native xác nhận đã thực thi script.
	private bool TrySendScriptCore(IntPtr gameWindow, byte[] script, out string error) {
		if (! TryEnsureReceiver(gameWindow, out error)) return false;
		if (! TrySendConfirmedCommandCore(gameWindow, BeginScriptCommand, 0, out error)) return false;
		foreach (byte value in script) {
			if (! TrySendConfirmedCommandCore(gameWindow, AppendScriptByteCommand, value, out error)) return false;
		}
		return TrySendConfirmedCommandCore(gameWindow, ExecuteScriptCommand, 0, out error);
	}

	// Chờ native handler xử lý command để phân biệt rõ đã nhận với chỉ mới post vào hàng đợi.
	private bool TrySendConfirmedCommandCore(IntPtr gameWindow, int command, int payload, out string error) {
		if (! TryEnsureReceiver(gameWindow, out error)) return false;
		IntPtr sent = SendMessageTimeoutA(gameWindow, hookMessage, (IntPtr)command, (IntPtr)payload, SmtoAbortIfHung, SendMessageTimeoutMilliseconds, out UIntPtr result);
		if (sent == IntPtr.Zero) {
			error = $"SendMessageTimeoutA failed. Command={command}, Payload={payload}, Win32Error={Marshal.GetLastWin32Error()}";
			return false;
		}
		if (result.ToUInt64() == 1) return true;
		error = $"Native handler rejected command. Command={command}, Payload={payload}, Return={result.ToUInt64()}.";
		return false;
	}

	// Bảo đảm DLL nhận private message đã được gắn vào đúng game window trước khi gửi lệnh.
	private static bool TryEnsureReceiver(IntPtr gameWindow, out string error) {
		error = "";
		if (gameWindow == IntPtr.Zero || ! IsWindow(gameWindow)) {
			error = $"Invalid HWND: 0x{gameWindow.ToInt64():X8}.";
			return false;
		}
		lock (SyncRoot) {
			GetMsgDelegate? currentGetMsg = getMsg;
			InjectDllDelegate? currentInjectDll = injectDll;
			UnmapDllDelegate? currentUnmapDll = unmapDll;
			if (currentInjectDll == null || currentGetMsg == null || currentUnmapDll == null) {
				error = $"{SourceLibraryName} runtime chưa được khởi tạo.";
				return false;
			}
			if (! HookedWindows.Contains(gameWindow)) {
				int result = currentInjectDll(gameWindow);
				if (result != 1) {
					error = $"InjectDll failed. HWND=0x{gameWindow.ToInt64():X8}, Return={result}.";
					return false;
				}
				HookedWindows.Add(gameWindow);
			}
			if (hookMessage != 0) return true;
			uint exportedMessage = currentGetMsg();
			uint registeredMessage = RegisterWindowMessageA(HookMessageName);
			if (exportedMessage == 0 || exportedMessage != registeredMessage) {
				HookedWindows.Remove(gameWindow);
				currentUnmapDll(gameWindow);
				error = $"Hook message mismatch: Exported=0x{exportedMessage:X4}, Registered=0x{registeredMessage:X4}.";
				return false;
			}
			hookMessage = exportedMessage;
			return true;
		}
	}

	private static void EnsureRuntimeLibraryLoaded(string sourcePath) {
		lock (SyncRoot) {
			if (getMsg != null && injectDll != null && unmapDll != null) {
				return;
			}

			string runtimeDirectory = Path.Combine(Path.GetTempPath(), "DEV", "Native");
			Directory.CreateDirectory(runtimeDirectory);
			DeleteUnusedRuntimeLibraries(runtimeDirectory);
			runtimeLibraryPath = Path.Combine(runtimeDirectory, $"SystemUint-{Environment.ProcessId}-{Guid.NewGuid():N}.dll");
			File.Copy(sourcePath, runtimeLibraryPath, false);

			try {
				runtimeLibraryHandle = NativeLibrary.Load(runtimeLibraryPath);
				getMsg = Marshal.GetDelegateForFunctionPointer<GetMsgDelegate>(NativeLibrary.GetExport(runtimeLibraryHandle, "GetMsg"));
				injectDll = Marshal.GetDelegateForFunctionPointer<InjectDllDelegate>(NativeLibrary.GetExport(runtimeLibraryHandle, "InjectDll"));
				unmapDll = Marshal.GetDelegateForFunctionPointer<UnmapDllDelegate>(NativeLibrary.GetExport(runtimeLibraryHandle, "UnmapDll"));
			} catch {
				if (runtimeLibraryHandle != IntPtr.Zero) {
					NativeLibrary.Free(runtimeLibraryHandle);
					runtimeLibraryHandle = IntPtr.Zero;
				}
				TryDeleteRuntimeLibrary(runtimeLibraryPath);
				runtimeLibraryPath = "";
				throw;
			}
		}
	}

	private static void DeleteUnusedRuntimeLibraries(string runtimeDirectory) {
		foreach (string path in Directory.EnumerateFiles(runtimeDirectory, "SystemUint-*.dll", SearchOption.TopDirectoryOnly)) {
			TryDeleteRuntimeLibrary(path);
		}
	}

	private static void TryDeleteRuntimeLibrary(string path) {
		try {
			File.Delete(path);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate uint GetMsgDelegate();

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int InjectDllDelegate(IntPtr gameWindowHandle);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int UnmapDllDelegate(IntPtr gameWindowHandle);

	[DllImport("user32.dll", CharSet = CharSet.Ansi)]
	private static extern uint RegisterWindowMessageA(string messageName);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostMessageA(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SendMessageTimeoutA(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMilliseconds, out UIntPtr result);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(IntPtr windowHandle);
}
