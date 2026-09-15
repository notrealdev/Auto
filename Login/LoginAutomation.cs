namespace Auto.Login;

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Auto.Attack;

// Mở client rồi đăng nhập, port theo DiskConverter.DisposeNode của AutoFS
// (D:\G\DEV\Resource\AutoSource\AutoProV2\DiskConverter.cs dòng 66-260).
//
// Chuỗi lệnh lấy nguyên của AutoFS, gửi qua ĐÚNG dispatcher mà Auto đang dùng cho lệnh 7/8/38/78/9/321:
//   280 / 0                      -> bước 1
//   281 / 0                      -> bước 2
//   282 / Pack(PhânVùng, MáyChủ) -> chọn phân vùng + máy chủ
//   283 / Pack(0, ký tự)         -> gõ từng ký tự TÀI KHOẢN
//   283 / Pack(1, ký tự)         -> gõ từng ký tự MẬT KHẨU
//   284 / 0                      -> bấm đăng nhập
// Pack lấy nguyên DiskConverter.cs:848-849.
//
// CHƯA PORT, và cố ý không bịa: AutoFS chờ giữa các bước bằng cách ĐỌC trạng thái đăng nhập ở địa chỉ tuyệt đối
// 7815592 (0x774FA8), đợi == 2 rồi mới gõ và đợi == 5 là xong, đồng thời đọc chuỗi báo lỗi ở B_Bảng + 6616. Ba địa
// chỉ đó lấy từ bản client CŨ — bằng chứng client đã đổi layout: stride record item dưới đất của AutoFS là 920,
// bản đang chạy là 932 (đo trên PID=32196 ngày 2026-09-11). Nên bản này chỉ chờ theo THỜI GIAN đúng như các
// Thread.Sleep của AutoFS; khi nào đo được địa chỉ thật trên client này thì thay bằng chờ theo trạng thái.
public sealed class LoginAutomation {
	private const int BeginLoginCommand = 280;
	private const int PrepareLoginCommand = 281;
	private const int SelectServerCommand = 282;
	private const int TypeCharacterCommand = 283;
	private const int SubmitLoginCommand = 284;
	private const int UserFieldIndex = 0;
	private const int PasswordFieldIndex = 1;
	// AutoFS bỏ cuộc nếu process chết trong 5 giây đầu (DiskConverter.cs, mốc stopwatch 5000).
	private const int ProcessStartTimeoutMilliseconds = 5000;
	private const int WaitForWindowTimeoutMilliseconds = 30000;
	private const int WaitForWindowPollMilliseconds = 200;
	private const int StepRetryMilliseconds = 500;

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int SetWindowText(IntPtr windowHandle, string text);

	private readonly AutoFsAttackTransport transport = new(new AutoFsActionGate());

	// Chạy lần lượt từng account. Trả về số account đã gửi xong chuỗi lệnh đăng nhập.
	//
	// KHÔNG khẳng định "đăng nhập thành công": chưa đọc được trạng thái đăng nhập của client nên chỉ biết lệnh đã
	// gửi đi, không biết client nhận hay từ chối. Log ghi đúng như vậy.
	public int Run(Settings settings, Action<string> log, CancellationToken token) {
		int dispatched = 0;
		for (int index = 0; index < settings.Accounts.Count; index++) {
			if (token.IsCancellationRequested) break;
			LoginAccount account = settings.Accounts[index];
			if (account.User.Length == 0) {
				log($"ĐĂNG NHẬP BỎ QUA | #{index + 1} | User rỗng.");
				continue;
			}
			if (TryLoginOne(settings, account, index, log, token)) dispatched++;
			// StoreOptions.cs:129 — AutoFS chờ Thread.Sleep(500 + AutoLogin_Model.DelayLogin) giữa hai account.
			// DelayLogin là MILI GIÂY cộng thẳng vào 500, không phải giây. Trước đó tôi viết * 1000 là sai.
			if (index < settings.Accounts.Count - 1) {
				token.WaitHandle.WaitOne(500 + settings.DelayLogin);
			}
		}
		log($"ĐĂNG NHẬP XONG LƯỢT | Đã gửi chuỗi lệnh cho {dispatched}/{settings.Accounts.Count} account.");
		return dispatched;
	}

	private bool TryLoginOne(Settings settings, LoginAccount account, int index, Action<string> log, CancellationToken token) {
		int stepDelay = 100 + settings.Speed * 200;
		Process process = new();
		// Giữ nguyên hai thứ của AutoFS: truyền TÊN FILE exe làm tham số dòng lệnh, và UseShellExecute = true.
		process.StartInfo.Arguments = Path.GetFileName(settings.ExeLink);
		process.StartInfo.Verb = "OPEN";
		process.StartInfo.FileName = Path.GetFullPath(settings.ExeLink);
		process.StartInfo.UseShellExecute = true;
		// LỆCH CÓ CHỦ Ý so với AutoFS: nó đặt WorkingDirectory = ExeLink, tức đường dẫn tới FILE chứ không phải thư
		// mục. Đó là chỗ sai của AutoFS, chép lại chỉ tạo lỗi; client đọc data\*.pak theo thư mục chứa exe nên phải
		// là thư mục thật.
		process.StartInfo.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(settings.ExeLink)) ?? "";
		try {
			log($"ĐĂNG NHẬP | #{index + 1} | {account.User} | Đang khởi động {process.StartInfo.FileName}...");
			process.Start();
		} catch (Exception ex) {
			log($"ĐĂNG NHẬP HỎNG | #{index + 1} | {account.User} | Không khởi động được client | {ex.GetType().Name}: {ex.Message}");
			return false;
		}

		if (! TryWaitForMainWindow(process, token, out IntPtr windowHandle, out string waitFailure)) {
			log($"ĐĂNG NHẬP HỎNG | #{index + 1} | {account.User} | {waitFailure}");
			return false;
		}
		// AutoFS đổi tiêu đề cửa sổ để phân biệt từng client (SetWindowText, DiskConverter.cs:192). Nó đặt theo tên
		// file exe, nhưng khi KHÔNG copy exe thì mọi cửa sổ trùng tên nên vô dụng — đặt theo tài khoản để còn nhận ra.
		SetWindowText(windowHandle, account.User);
		log($"ĐĂNG NHẬP | #{index + 1} | {account.User} | PID={process.Id} | HWND=0x{windowHandle.ToInt64():X8} | Đã đổi tiêu đề cửa sổ.");

		if (settings.WaitBeforeLogin > 0) {
			log($"ĐĂNG NHẬP | #{index + 1} | {account.User} | Chờ {settings.WaitBeforeLogin}ms cho client dựng xong màn hình rồi mới gửi lệnh.");
			token.WaitHandle.WaitOne(settings.WaitBeforeLogin);
		}
		// Ba hộp thoại chỉ xuất hiện lần lượt, mỗi cái sau khi cái trước đóng. Native trả 0 khi hộp thoại tương ứng
		// chưa dựng xong, nên chờ bằng chính kết quả của lệnh thay vì đoán thời gian.
		if (! TrySendUntilAccepted(windowHandle, BeginLoginCommand, 0, "hộp Khuyến cáo", settings, token, log, index, account)) return false;
		if (! TrySendUntilAccepted(windowHandle, PrepareLoginCommand, 0, "hộp Thông tin phiên bản", settings, token, log, index, account)) return false;
		if (! TrySendUntilAccepted(windowHandle, SelectServerCommand, Pack(account.Partition, account.Server), "hộp Chọn máy chủ", settings, token, log, index, account)) return false;
		token.WaitHandle.WaitOne(stepDelay);

		foreach (char character in account.User) {
			if (! TrySend(windowHandle, TypeCharacterCommand, Pack(UserFieldIndex, character), log, index, account)) return false;
		}
		foreach (char character in account.Pass) {
			if (! TrySend(windowHandle, TypeCharacterCommand, Pack(PasswordFieldIndex, character), log, index, account)) return false;
		}
		token.WaitHandle.WaitOne(settings.Speed * 200);
		if (! TrySend(windowHandle, SubmitLoginCommand, 0, log, index, account)) return false;

		log($"ĐĂNG NHẬP ĐÃ GỬI | #{index + 1} | {account.User} | PID={process.Id} | PhânVùng={account.Partition} | MáyChủ={account.Server} | KýTựUser={account.User.Length} | KýTựPass={account.Pass.Length} | CHƯA đọc được trạng thái client nên KHÔNG kết luận đăng nhập thành công.");
		return true;
	}

	// Gửi lại cho tới khi native xác nhận đã bấm được, hoặc hết hạn. Mỗi lần thất bại ghi lại lý do native trả về để
	// còn phân biệt "màn hình chưa hiện" với "địa chỉ sai".
	private bool TrySendUntilAccepted(IntPtr windowHandle, int command, int payload, string description, Settings settings,
		CancellationToken token, Action<string> log, int index, LoginAccount account) {
		DateTime deadline = DateTime.UtcNow.AddMilliseconds(settings.StepTimeoutMilliseconds);
		string lastError = "";
		int attempts = 0;
		while (DateTime.UtcNow < deadline && ! token.IsCancellationRequested) {
			attempts++;
			if (transport.TrySendConfirmedCommandForDebug(windowHandle, command, payload, out lastError)) {
				log($"ĐĂNG NHẬP | #{index + 1} | {account.User} | Lệnh {command} ({description}) được nhận sau {attempts} lần thử.");
				return true;
			}
			token.WaitHandle.WaitOne(StepRetryMilliseconds);
		}
		log($"ĐĂNG NHẬP HỎNG | #{index + 1} | {account.User} | Lệnh {command} ({description}) không được nhận sau {attempts} lần thử trong {settings.StepTimeoutMilliseconds}ms | Lỗi cuối: {lastError}");
		return false;
	}

	private bool TrySend(IntPtr windowHandle, int command, int payload, Action<string> log, int index, LoginAccount account) {
		// Dùng bản ForDebug: nó bỏ qua công tắc Auto tổng nhưng giữ khoá chống gửi trùng. Đăng nhập luôn chạy khi
		// chưa có account nào được bật, nên đường thường sẽ bị "Master automation switch is disabled" chặn sạch.
		//
		// Bản CÓ XÁC NHẬN chứ không phải PostMessage: native trả 1 mới tính là thành công. Không có nó thì lệnh rơi
		// vào WndProc gốc vẫn báo thành công — đúng cái bẫy đã làm mất một buổi hôm 2026-09-12.
		if (transport.TrySendConfirmedCommandForDebug(windowHandle, command, payload, out string error)) return true;
		log($"ĐĂNG NHẬP HỎNG | #{index + 1} | {account.User} | Không gửi được lệnh {command} payload {payload} | {error}");
		return false;
	}

	// DiskConverter.cs:848-849 — ghép hai giá trị 16 bit vào một payload 32 bit.
	private static int Pack(int low, int high) => (low & 0xFFFF) + ((high & 0xFFFF) << 16);

	private static bool TryWaitForMainWindow(Process process, CancellationToken token, out IntPtr windowHandle, out string failure) {
		windowHandle = IntPtr.Zero;
		failure = "";
		DateTime deadline = DateTime.UtcNow.AddMilliseconds(WaitForWindowTimeoutMilliseconds);
		DateTime startDeadline = DateTime.UtcNow.AddMilliseconds(ProcessStartTimeoutMilliseconds);
		while (DateTime.UtcNow < deadline && ! token.IsCancellationRequested) {
			process.Refresh();
			if (process.HasExited) {
				// AutoFS cũng coi process chết sớm là hỏng rồi thử lại vòng ngoài.
				failure = $"Client thoát ngay sau khi mở (ExitCode={process.ExitCode}).";
				return false;
			}
			if (process.MainWindowHandle != IntPtr.Zero) {
				windowHandle = process.MainWindowHandle;
				return true;
			}
			if (DateTime.UtcNow > startDeadline && process.MainWindowHandle == IntPtr.Zero && process.Threads.Count == 0) {
				failure = "Client không tạo được luồng nào.";
				return false;
			}
			token.WaitHandle.WaitOne(WaitForWindowPollMilliseconds);
		}
		failure = token.IsCancellationRequested ? "Đã huỷ." : $"Không thấy cửa sổ chính sau {WaitForWindowTimeoutMilliseconds / 1000}s.";
		return false;
	}
}
