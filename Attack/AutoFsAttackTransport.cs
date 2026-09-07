namespace Auto.Attack;

using System.IO;
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
	private const int AppendScriptByteCommand = 34;
	private const int SendChatCommand = 312;
	private const int PassiveBuffCommand = 313;
	private const int AuditAddressCommand = 320;
	private const int MaximumPassiveBuffSkillId = 0x7CF;
	private const uint SendMessageTimeoutMilliseconds = 500;
	// Buff bị động bắn lại mỗi 350 ms (PassiveBuffEngine.SkillIntervalMilliseconds) nên KHÔNG được chờ tới 500 ms:
	// mọi lệnh gửi đồng bộ đều nằm trong khoá syncRoot của AutoFsActionGate, dùng chung với TryRunAttack/RunLoot/
	// RunMovement, nên một lần chờ quá hạn khoá luôn cả vòng đánh của account đó.
	// Runtime beta 2026-09-06 22:40-22:44: 597/5095 dòng BUFF_PASSIVE_FAILED với Win32Error=1460 (ERROR_TIMEOUT),
	// dồn thành 15 cụm mỗi cụm ~14 giây, và attack.log có đúng một khoảng trống 22:40:48->22:41:04 (16,5 giây) trùng
	// khít cụm đầu tiên. Chờ lâu hơn nhịp lặp của chính nó là tự chặn mình.
	private const uint PassiveBuffSendTimeoutMilliseconds = 250;
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
	// Gửi nội dung chat vào kênh theo chỉ số kênh runtime mà client đang nạp.
	public bool TrySendChat(IntPtr gameWindow, byte[] message, int channelIndex, out string error) {
		if (! actionGate.AutomationEnabled) {
			error = "Master automation switch is disabled.";
			return false;
		}
		if (message.Length == 0 || message.Length > 199) {
			error = $"Invalid message length: {message.Length}.";
			return false;
		}
		if (channelIndex < 0) {
			error = $"Invalid channel index: {channelIndex}.";
			return false;
		}
		string currentError = "";
		bool sent = actionGate.RunCommand(() => TrySendChatCore(gameWindow, message, channelIndex, out currentError));
		error = currentError;
		return sent;
	}


	// Bật một skill hỗ trợ bị động của hệ Dị Nhân. Chờ native xác nhận để phân biệt đã gọi được với chỉ mới post lệnh;
	// mã trả về khác 1 của native cho biết đúng bước bị từ chối (20 id sai, 21 image lạ, 22 địa chỉ không thực thi,
	// 23 chữ ký hàm lệch, 24 entity table bằng 0).
	public bool TrySendPassiveBuff(IntPtr gameWindow, int skillId, out string error) {
		if (! actionGate.AutomationEnabled) {
			error = "Master automation switch is disabled.";
			return false;
		}
		if (skillId <= 0 || skillId > MaximumPassiveBuffSkillId) {
			error = $"Invalid passive buff skill id: {skillId}.";
			return false;
		}
		string currentError = "";
		bool sent = actionGate.RunCommand(() => TrySendConfirmedCommandCore(gameWindow, PassiveBuffCommand, skillId, PassiveBuffSendTimeoutMilliseconds, out currentError));
		error = currentError;
		return sent;
	}

	// Đọc kết quả kiểm tra một địa chỉ client từ native và trả nguyên mã, khác các lệnh khác vốn chỉ coi 1 là thành công.
	// Không đi qua AutoFsActionGate vì lệnh này chỉ đọc bộ nhớ và phải chạy được cả khi Auto tổng đang tắt.
	public bool TryQueryAddressAudit(IntPtr gameWindow, int index, out ulong result, out string error) {
		result = 0;
		if (! TryEnsureReceiver(gameWindow, out error)) return false;
		IntPtr sent = SendMessageTimeoutA(gameWindow, hookMessage, (IntPtr)AuditAddressCommand, (IntPtr)index, SmtoAbortIfHung, SendMessageTimeoutMilliseconds, out UIntPtr value);
		if (sent == IntPtr.Zero) {
			error = $"SendMessageTimeoutA failed. Command={AuditAddressCommand}, Index={index}, Win32Error={Marshal.GetLastWin32Error()}";
			return false;
		}
		result = value.ToUInt64();
		return true;
	}

	private bool TrySendCommandCore(IntPtr gameWindow, int command, int payload, out string error) {
		if (! TryEnsureReceiver(gameWindow, out error)) return false;
		if (PostMessageA(gameWindow, hookMessage, (IntPtr)command, (IntPtr)payload)) return true;
		error = $"PostMessageA failed. Command={command}, Payload={payload}, Win32Error={Marshal.GetLastWin32Error()}";
		return false;
	}

	// Gửi đồng bộ từng byte nội dung và chỉ báo thành công khi native xác nhận đã chạy hết chuỗi gửi chat của client.
	private bool TrySendChatCore(IntPtr gameWindow, byte[] message, int channelIndex, out string error) {
		if (! TryEnsureReceiver(gameWindow, out error)) return false;
		if (! TrySendConfirmedCommandCore(gameWindow, BeginScriptCommand, 0, out error)) return false;
		foreach (byte value in message) {
			if (! TrySendConfirmedCommandCore(gameWindow, AppendScriptByteCommand, value, out error)) return false;
		}
		return TrySendConfirmedCommandCore(gameWindow, SendChatCommand, channelIndex, out error);
	}

	// Chờ native handler xử lý command để phân biệt rõ đã nhận với chỉ mới post vào hàng đợi.
	private bool TrySendConfirmedCommandCore(IntPtr gameWindow, int command, int payload, out string error) {
		return TrySendConfirmedCommandCore(gameWindow, command, payload, SendMessageTimeoutMilliseconds, out error);
	}

	private bool TrySendConfirmedCommandCore(IntPtr gameWindow, int command, int payload, uint timeoutMilliseconds, out string error) {
		if (! TryEnsureReceiver(gameWindow, out error)) return false;
		IntPtr sent = SendMessageTimeoutA(gameWindow, hookMessage, (IntPtr)command, (IntPtr)payload, SmtoAbortIfHung, timeoutMilliseconds, out UIntPtr result);
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
