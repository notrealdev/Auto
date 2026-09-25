namespace Auto.Attack;

using System.IO;
using System.Runtime.InteropServices;

internal sealed class AutoFsAttackTransport {
	private const string SourceLibraryName = "SystemUint.Source.dll";
	private const string HookMessageName = "WM_HOOK_WRITE";
	private const int AttackCommand = 300;
	private const int NativeBuildStampCommand = 322;
	public const int QuickSlotContainer = 11;
	public const int QuickSlotCount = 4;
	private const uint WmKeyDown = 0x0100;
	private const uint WmKeyUp = 0x0101;
	private const int VirtualKeyOne = 0x31;
	private const int ScanCodeOne = 0x02;
	private const int BeginScriptCommand = 311;
	private const int AppendScriptByteCommand = 34;
	private const int SendChatCommand = 312;
	private const int PassiveBuffCommand = 313;
	private const int AuditAddressCommand = 320;
	// Phải khớp SystemUint.cpp. Xem TryProbeSelectEntity.
	//
	// Giá trị cũ là 8, chép từ AutoFS (WindowQueue.cs:24293-24318 gửi "8, k" sau khi tìm NPC theo tên). Số 8 đó là
	// số lệnh của DLL AutoFS, KHÔNG phải của DLL Auto: grep toàn bộ SystemUint.cpp không có nhánh "wParam == 8" nào,
	// nên message bị bỏ im lặng trong khi PostMessageA vẫn trả thành công. Đó là lý do 20/20 lần gửi lệnh 8 không
	// mở được cửa hàng (repair.log 2026-09-17 22:37-22:38) mà phía C# vẫn tưởng đã click xong.
	private const int SelectEntityCommand = 323;
	private const int ReadCurrentTargetCommand = 324;
	private const int MaximumPassiveBuffSkillId = 0x7CF;
	// Phải khớp SystemUint.cpp (QuickBuyCommand, QuickBuyMaximumQuantity).
	private const int QuickBuyCommand = 327;
	private const int MaximumQuickBuyQuantity = 100;
	private const uint SendMessageTimeoutMilliseconds = 500;
	// Các bước đăng nhập dựng lại cả màn hình nên lâu hơn hẳn một lần gửi gói; xem TrySendLoginCommand.
	private const uint LoginSendTimeoutMilliseconds = 10000;
	// Buff bị động bắn lại mỗi 350 ms (BuffEngine.SkillIntervalMilliseconds) nên KHÔNG được chờ tới 500 ms:
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

	// Lộ trạng thái cổng ra ngoài để DebugTools báo trước được. Mọi hàm gửi lệnh dưới đây đều bị chặn khi cổng
	// đóng, mà thông báo chặn chỉ hiện ra SAU khi đã bấm gửi — kiểm trước thì đỡ tốn một vòng thử.
	public bool MasterEnabled => actionGate.MasterEnabled;

	public bool AutomationEnabled => actionGate.AutomationEnabled;

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

	// Click entity theo index đúng cách AutoFS làm với NPC tìm theo tên. Dùng khi entity không có toạ độ nên đường
	// click theo toạ độ màn hình không áp dụng được.
	public bool TrySelectEntity(IntPtr gameWindow, int entityIndex, out string error) {
		error = "";
		if (gameWindow == IntPtr.Zero || entityIndex < AutoFsClientProfile.FirstEntityIndex || entityIndex > AutoFsClientProfile.LastEntityIndex) {
			error = $"Invalid HWND/index: 0x{gameWindow.ToInt64():X8}/{entityIndex}.";
			return false;
		}
		return TrySendCommand(gameWindow, SelectEntityCommand, entityIndex, out error);
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

	// Dùng vật phẩm ở ô trang bị nhanh bằng PHÍM TẮT của chính client.
	//
	// Đây là đường DUY NHẤT để dùng vật phẩm. Lệnh 310 đã bị gỡ ngày 2026-09-09: đo trên PID 22056 nó chạy trọn
	// native tới return 1 nhưng game KHÔNG trừ vật phẩm (SốLượng 1 -> 1) và không đổi map, trong khi đường phím tắt
	// cho SốLượng 1 -> 0 và MapId 37 -> 21. Đợt dò tìm hàm dùng vật phẩm thật của client sau đó cũng không ra kết
	// quả (chi tiết ở LowHpEngine), nên hệ quả đã chấp nhận: vật phẩm phải nằm ở ô trang bị nhanh.
	//
	// Ô trang bị nhanh hiện trên màn hình đánh số 1..4, slot trong bộ nhớ đếm từ 0.
	public bool TryUseQuickSlotHotkey(IntPtr gameWindow, int slotIndex, out string error) {
		if (! actionGate.AutomationEnabled) {
			error = "Master automation switch is disabled.";
			return false;
		}
		return TryUseQuickSlotHotkeyCore(gameWindow, slotIndex, false, out error);
	}

	// Bản chẩn đoán của TrySendCommand: bỏ qua công tắc Auto tổng nhưng GIỮ khoá chống gửi trùng, đúng lý do đã
	// ghi ở AutoFsActionGate.RunDebugCommand. Cần cho luồng Bán chạy tay — nó phải chạy lúc Auto tổng đang TẮT,
	// nếu không các engine khác cùng chen lệnh vào và làm bẩn phép đo.
	public bool TrySendCommandForDebug(IntPtr gameWindow, int command, int payload, out string error) {
		string currentError = "";
		bool sent = actionGate.RunDebugCommand(() => TrySendCommandCore(gameWindow, command, payload, out currentError));
		error = currentError;
		return sent;
	}

	// Bản CÓ XÁC NHẬN của TrySendCommandForDebug: chờ native trả kết quả thay vì chỉ post vào hàng đợi. Cần cho việc
	// dò số thứ tự — không có nó thì không phân biệt được "native từ chối" với "client nhận nhưng bỏ qua".
	public bool TrySendConfirmedCommandForDebug(IntPtr gameWindow, int command, int payload, out string error) {
		string currentError = "";
		bool sent = actionGate.RunDebugCommand(() => TrySendConfirmedCommandCore(gameWindow, command, payload, out currentError));
		error = currentError;
		return sent;
	}

	// Mua nhanh thuốc bằng hàm mua của chức năng "Tự động mua thuốc" trong client, payload dạng lệnh 95 của AutoFS:
	// mã chi tiết của thuốc (16 bit thấp) | số lượng << 16. Bản chẩn đoán: bỏ qua công tắc Auto tổng, giữ khoá chống gửi trùng.
	// Native trả 1 chỉ chứng minh hàm client đã được gọi, KHÔNG chứng minh server đã bán — phải đọc lại số thuốc trong túi.
	public bool TrySendQuickBuyForDebug(IntPtr gameWindow, int potionCode, int quantity, out string error) {
		if (potionCode < 0 || potionCode > 0xFFFF || quantity < 1 || quantity > MaximumQuickBuyQuantity) {
			error = $"Invalid quick-buy arguments. PotionCode={potionCode}, Quantity={quantity}.";
			return false;
		}
		return TrySendConfirmedCommandForDebug(gameWindow, QuickBuyCommand, potionCode | (quantity << 16), out error);
	}

	// Bản tự động của TrySendQuickBuyForDebug: chỉ chạy khi Auto tổng bật, đi qua cùng khoá hành động với các lệnh khác.
	public bool TrySendQuickBuy(IntPtr gameWindow, int potionCode, int quantity, out string error) {
		if (! actionGate.AutomationEnabled) {
			error = "Master automation switch is disabled.";
			return false;
		}
		if (potionCode < 0 || potionCode > 0xFFFF || quantity < 1 || quantity > MaximumQuickBuyQuantity) {
			error = $"Invalid quick-buy arguments. PotionCode={potionCode}, Quantity={quantity}.";
			return false;
		}
		string currentError = "";
		bool sent = actionGate.RunCommand(() => TrySendConfirmedCommandCore(gameWindow, QuickBuyCommand, potionCode | (quantity << 16), out currentError));
		error = currentError;
		return sent;
	}

	// Gửi phím Enter vào cửa sổ client. Dùng cho màn CHỌN NHÂN VẬT sau khi đăng nhập: mỗi tài khoản của chủ dự án
	// chỉ có một nhân vật nên Enter là vào thẳng game (chủ dự án chốt 2026-09-18).
	//
	// Dùng lại đúng khuôn PostMessageA WM_KEYDOWN/WM_KEYUP của TryUseQuickSlotHotkeyCore — đường gửi phím DUY NHẤT
	// đã chạy được trong dự án này. KHÔNG đi qua AutoFsActionGate vì lúc đăng nhập chưa có account nào được bật,
	// cùng lý do đã ghi ở TryQueryAddressAudit.
	//
	// CHƯA VERIFY: chưa từng chạy thật trên màn chọn nhân vật. PostMessageA trả true chỉ chứng minh message đã vào
	// hàng đợi, KHÔNG chứng minh client đã vào game — LoginAutomation phải tự xác nhận bằng trạng thái.
	public bool TrySendEnterKey(IntPtr gameWindow, out string error) {
		error = "";
		if (gameWindow == IntPtr.Zero) {
			error = "Invalid HWND.";
			return false;
		}
		const int virtualKeyReturn = 0x0D;
		const int scanCodeReturn = 0x1C;
		IntPtr downLParam = new((scanCodeReturn << 16) | 1);
		IntPtr upLParam = new(unchecked((int)(0xC0000000u | (uint)(scanCodeReturn << 16) | 1u)));
		if (! PostMessageA(gameWindow, WmKeyDown, new IntPtr(virtualKeyReturn), downLParam)
			|| ! PostMessageA(gameWindow, WmKeyUp, new IntPtr(virtualKeyReturn), upLParam)) {
			error = $"PostMessageA failed. VirtualKey=0x0D, Win32Error={Marshal.GetLastWin32Error()}.";
			return false;
		}
		return true;
	}

	private bool TryUseQuickSlotHotkeyCore(IntPtr gameWindow, int slotIndex, bool debugRun, out string error) {
		if (gameWindow == IntPtr.Zero || slotIndex < 0 || slotIndex >= QuickSlotCount) {
			error = $"Invalid quick-slot hotkey. Window=0x{gameWindow.ToInt64():X}, SlotIndex={slotIndex}.";
			return false;
		}
		int virtualKey = VirtualKeyOne + slotIndex;
		int scanCode = ScanCodeOne + slotIndex;
		IntPtr downLParam = new((scanCode << 16) | 1);
		IntPtr upLParam = new(unchecked((int)(0xC0000000u | (uint)(scanCode << 16) | 1u)));
		string currentError = "";
		bool Send() {
			if (! PostMessageA(gameWindow, WmKeyDown, new IntPtr(virtualKey), downLParam) || ! PostMessageA(gameWindow, WmKeyUp, new IntPtr(virtualKey), upLParam)) {
				currentError = $"PostMessageA failed. VirtualKey=0x{virtualKey:X2}, Win32Error={Marshal.GetLastWin32Error()}.";
				return false;
			}
			return true;
		}
		bool sent = debugRun ? actionGate.RunDebugCommand(Send) : actionGate.RunCommand(Send);
		error = sent || currentError.Length > 0 ? currentError : "Automatic command gate rejected the quick-slot hotkey.";
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

	// Dò chức năng "chọn entity theo chỉ số" của client 1.28 — bước đang thiếu để bỏ hẳn chuột giả lập khi click NPC.
	//
	// Trả NGUYÊN giá trị native: đó là chỉ số mục tiêu ĐỌC LẠI từ 0x4CE688 ngay sau khi gọi, không phải cờ 0/1.
	// Cần vậy vì bài học lệnh 8: PostMessageA thành công nhưng DLL không có handler nào khớp, phía C# vẫn báo
	// thành công và 20/20 chuyến Sửa đồ hỏng mà không ai biết (repair.log 2026-09-17 22:37-22:38).
	// int.MinValue = native từ chối (sai chỉ số, manager/vtable không qua kiểm tra, hoặc con trỏ không thực thi được).
	//
	// Đặt ở đây cùng TryQueryAddressAudit vì cùng tính chất: không qua AutoFsActionGate, phải chạy được cả khi
	// công tắc Auto tổng đang tắt — lúc dò thì không ai bật Auto lên cả.
	public bool TryProbeSelectEntity(IntPtr gameWindow, int entityIndex, out int targetIndexAfter, out string error) {
		targetIndexAfter = int.MinValue;
		if (! TryEnsureReceiver(gameWindow, out error)) return false;
		IntPtr sent = SendMessageTimeoutA(gameWindow, hookMessage, (IntPtr)SelectEntityCommand, (IntPtr)entityIndex, SmtoAbortIfHung, SendMessageTimeoutMilliseconds, out UIntPtr value);
		if (sent == IntPtr.Zero) {
			error = $"SendMessageTimeoutA failed. Command={SelectEntityCommand}, Index={entityIndex}, Win32Error={Marshal.GetLastWin32Error()}";
			return false;
		}
		targetIndexAfter = unchecked((int)value.ToUInt32());
		return true;
	}

	// Chỉ đọc chỉ số mục tiêu đang chọn, không ghi gì. Dùng để lấy mốc TRƯỚC khi gọi TryProbeSelectEntity.
	public bool TryProbeCurrentTarget(IntPtr gameWindow, out int targetIndex, out string error) {
		targetIndex = int.MinValue;
		if (! TryEnsureReceiver(gameWindow, out error)) return false;
		IntPtr sent = SendMessageTimeoutA(gameWindow, hookMessage, (IntPtr)ReadCurrentTargetCommand, IntPtr.Zero, SmtoAbortIfHung, SendMessageTimeoutMilliseconds, out UIntPtr value);
		if (sent == IntPtr.Zero) {
			error = $"SendMessageTimeoutA failed. Command={ReadCurrentTargetCommand}, Win32Error={Marshal.GetLastWin32Error()}";
			return false;
		}
		targetIndex = unchecked((int)value.ToUInt32());
		return true;
	}

	// Gửi một lệnh của luồng đăng nhập và trả về NGUYÊN mã native, vì có lệnh dùng chính giá trị trả về làm dữ liệu
	// (lệnh 314 trả 0/1 là trạng thái ô "Đồng ý Điều khoản") chứ không chỉ 0/1 là hỏng/được.
	//
	// Khác hai điểm so với các lệnh còn lại, cả hai đều có lý do đo được:
	//   1. KHÔNG đặt SMTO_ABORTIFHUNG. Lệnh 282 dựng hẳn một màn hình mới nên cửa sổ ngừng bơm message trong lúc chạy;
	//      có cờ đó thì SendMessageTimeoutA trả 0 dù lệnh đã chạy xong.
	//      NHƯNG bỏ cờ đi VẪN CHƯA ĐỦ, đã đo được: trên PID 1284 (2026-09-16) lệnh 282 hết giờ sau 8s và trả 0, mà
	//      ảnh chụp cho thấy nó đã chạy xong và client còn kịp hiện popup "Máy chủ đã đầy hoặc đang bảo trì !".
	//      Nguyên nhân là client đi kết nối mạng nên bận lâu hơn mọi timeout hợp lý. Vì vậy KHÔNG được coi kết quả
	//      của lệnh 282 là căn cứ; LoginAutomation phải xác nhận bằng trạng thái màn hình (xem ở đó).
	//   2. Chờ lâu hơn nhiều so với 500 ms mặc định, vì các bước này dựng lại giao diện chứ không phải gửi gói.
	// Không đi qua AutoFsActionGate: đăng nhập luôn chạy lúc chưa có account nào được bật.
	public bool TrySendLoginCommand(IntPtr gameWindow, int command, int payload, out long result, out string error) {
		result = 0;
		if (! TryEnsureReceiver(gameWindow, out error)) return false;
		IntPtr sent = SendMessageTimeoutA(gameWindow, hookMessage, (IntPtr)command, (IntPtr)payload, 0, LoginSendTimeoutMilliseconds, out UIntPtr value);
		if (sent == IntPtr.Zero) {
			error = $"SendMessageTimeoutA failed. Command={command}, Payload={payload}, Win32Error={Marshal.GetLastWin32Error()}";
			return false;
		}
		result = unchecked((int)value.ToUInt64());
		return true;
	}

	// Đọc dấu phiên bản của DLL native ĐANG SỐNG trong tiến trình game.
	//
	// DLL được inject vào game và tồn tại lâu hơn một phiên chạy Auto, nên sau khi build lại native không có
	// cách nào biết tiến trình game đang chạy bản cũ hay mới. Đo trên bản cũ thì mọi kết luận đều vô nghĩa —
	// đúng cái bẫy đã suýt làm hỏng kết luận về lệnh 310 ngày 2026-09-09.
	// Không qua AutoFsActionGate vì đây là lệnh chỉ đọc và phải chạy được cả khi Auto đang tắt.
	public bool TryQueryNativeBuildStamp(IntPtr gameWindow, out ulong stamp, out string error) {
		stamp = 0;
		if (! TryEnsureReceiver(gameWindow, out error)) return false;
		IntPtr sent = SendMessageTimeoutA(gameWindow, hookMessage, (IntPtr)NativeBuildStampCommand, IntPtr.Zero, SmtoAbortIfHung, SendMessageTimeoutMilliseconds, out UIntPtr value);
		if (sent == IntPtr.Zero) {
			error = $"SendMessageTimeoutA failed. Command={NativeBuildStampCommand}, Win32Error={Marshal.GetLastWin32Error()}";
			return false;
		}
		stamp = value.ToUInt64();
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
