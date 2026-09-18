namespace Auto.Login;

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Auto.Attack;
using Auto.Utils;

// Mở client rồi đăng nhập, port theo DiskConverter.DisposeNode của AutoFS
// (D:\G\DEV\Resource\AutoSource\AutoProV2\DiskConverter.cs dòng 66-260).
//
// Chuỗi lệnh lấy nguyên của AutoFS, gửi qua ĐÚNG dispatcher mà Auto đang dùng cho lệnh 7/8/38/78/9/321:
//   280 / 0                      -> bước 1
//   281 / 0                      -> bước 2
//   282 / Pack(Partition, Server)  -> chọn cụm máy chủ + máy chủ
//   283 / Pack(0, ký tự)         -> gõ từng ký tự TÀI KHOẢN
//   283 / Pack(1, ký tự)         -> gõ từng ký tự MẬT KHẨU
// Pack lấy nguyên DiskConverter.cs:848-849.
//
// Từ đây trở đi KHÔNG theo AutoFS nữa, vì lệnh 284 của nó gọi thẳng hàm submit trong client và địa chỉ đó đã chết
// trên bản 1.28. Thay bằng đường đi của chính giao diện, đã kiểm chứng đầu-cuối trên client PID 17128 ngày
// 2026-09-16 (ảnh chụp: hai ô hiện đúng chữ, rồi client báo "Tài khoản hoặc mật mã không đúng !" do máy chủ trả về):
//   316 / 0 -> xoá vùng đệm ký tự của native
//   315 / 0 -> ghi cả hai vùng đệm vào hai ô nhập trên giao diện bằng hàm SetText của client
//   314 / 0 -> đọc ô "Đồng ý Điều khoản" (trả 0 = tắt, 1 = bật)
//   309 / 0 -> ĐẢO ô đó; client nhớ lựa chọn giữa các phiên nên phải đọc trước rồi mới đảo
//   308 / 0 -> bấm nút "Bắt đầu trò chơi"; client tự đọc lại hai ô rồi gửi lên máy chủ
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
	// Lệnh 284 của AutoFS gọi thẳng hàm submit trong client và KHÔNG dùng được nữa: địa chỉ của nó
	// (LoginSubmitFunctionRva) đã đo được là trỏ vào giữa thân hàm trên client 1.28. Thay bằng cách ghi chuỗi vào
	// hai ô nhập rồi bấm đúng nút "Bắt đầu trò chơi" để chính client đọc lại và gửi đi.
	private const int StartGameCommand = 308;
	private const int ToggleAgreeTermsCommand = 309;
	private const int ReadAgreeTermsCommand = 314;
	private const int WriteCredentialFieldsCommand = 315;
	private const int ClearPendingCredentialsCommand = 316;
	private const int ReadStatusMessageByteCommand = 317;
	// Lệnh chẩn đoán 294 (DiagnoseSimpleModalDispatch): lParam = RVA ô con trỏ hộp thoại, trả 3 khi đối tượng còn
	// sống và đọc được vtable, trả -2 khi ô con trỏ NULL/không đọc được (hộp chưa dựng hoặc đã đóng).
	// Hai RVA lấy từ Native/SystemUint/GameClientAddresses.h (LoginNoticeDialogRva / LoginVersionDialogRva) — nguồn
	// chuẩn nằm ở đó, sửa bên đó thì phải sửa cả hai chỗ.
	private const int DiagnoseDialogCommand = 294;
	private const int NoticeDialogRva = 0x004FED14;
	private const int VersionDialogRva = 0x004EECD0;
	private const int DialogAliveResult = 3;
	private const int DialogGoneResult = -2;
	private const int StatusMessageMaxLength = 128;
	// Chờ client đổi câu thông báo sau khi bấm đăng nhập rồi mới đọc.
	private const int StatusMessageWaitMilliseconds = 2000;
	// Màn CHỌN NHÂN VẬT sau khi đăng nhập. Mỗi tài khoản của chủ dự án chỉ có một nhân vật nên Enter là vào thẳng
	// game (chủ dự án chốt 2026-09-18). Gửi lại theo nhịp thay vì gửi một phát rồi đoán, vì không biết trước client
	// mất bao lâu mới dựng xong màn chọn — cùng lý do ba hộp thoại đầu phải chờ bằng kết quả lệnh.
	private const int EnterWorldTimeoutMilliseconds = 60000;
	private const int EnterWorldRetryMilliseconds = 2000;
	private const int UserFieldIndex = 0;
	private const int PasswordFieldIndex = 1;
	// AutoFS bỏ cuộc nếu process chết trong 5 giây đầu (DiskConverter.cs, mốc stopwatch 5000).
	private const int ProcessStartTimeoutMilliseconds = 5000;
	private const int WaitForWindowTimeoutMilliseconds = 30000;
	private const int WaitForWindowPollMilliseconds = 200;
	private const int StepRetryMilliseconds = 500;
	// Số lần thử tối đa khi chờ màn đăng nhập hiện ra. Nhân với settings.StepTimeoutMilliseconds (thời gian chờ
	// MỖI lần thử, cấu hình trong Login.json) ra tổng hạn của cả bước — xem TryReachLoginScreen.
	//
	// SỬA 2026-09-18: trước đây thời gian chờ mỗi lần thử là hằng số CỨNG 20000ms, độc lập với tổng hạn đọc từ
	// settings.StepTimeoutMilliseconds (Login.json đặt 10000ms). 20000 > 10000 nên vòng thử lại chỉ chạy được ĐÚNG
	// 1 LẦN rồi thoát ngay khi đang giữa lượt chờ đầu tiên, và log lại in nhầm "trong 10000ms" trong khi thực tế đã
	// chờ 20000ms (đo được: login.log 12:45:52.366 -> 12:46:12.653, lệch 20,287 giây). Sửa StepTimeoutMilliseconds
	// trong Login.json vì vậy KHÔNG hề đổi được thời gian chờ thật của bước này — chủ dự án đổi cấu hình mà kết quả
	// không nhúc nhích là do đây.
	//
	// Giờ mỗi lần thử chờ ĐÚNG settings.StepTimeoutMilliseconds (10000ms mặc định), lặp tối đa
	// LoginScreenMaxAttempts lần — tổng hạn 3 x 10000 = 30000ms, thật sự thử lại được nhiều lần thay vì chỉ 1.
	private const int LoginScreenMaxAttempts = 3;

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
		// Ba hộp thoại chỉ xuất hiện lần lượt, mỗi cái sau khi cái trước đóng. Xác nhận bằng TRẠNG THÁI của chính đối
		// tượng hộp thoại, không bằng giá trị trả về của lệnh.
		if (! TrySendUntilDialogClosed(windowHandle, BeginLoginCommand, NoticeDialogRva, "hộp Khuyến cáo", settings, token, log, index, account)) return false;
		if (! TrySendUntilDialogClosed(windowHandle, PrepareLoginCommand, VersionDialogRva, "hộp Thông tin phiên bản", settings, token, log, index, account)) return false;
		// Lệnh 282 KHÔNG dùng TrySendUntilAccepted được: nó làm client đi kết nối mạng nên cửa sổ bận lâu hơn mọi
		// timeout hợp lý, và SendMessageTimeoutA trả 0 dù lệnh đã chạy xong (đo trên PID 1284 ngày 2026-09-16: trả 0
		// nhưng ảnh chụp cho thấy client đã sang bước sau và hiện popup "Máy chủ đã đầy hoặc đang bảo trì !").
		// Gửi lại lúc đó là gửi vào màn hình đã đổi, hỏng thật rồi huỷ cả luồng dù bước này đã xong.
		//
		// Nên xác nhận bằng TRẠNG THÁI thay vì bằng giá trị trả về: lệnh 314 trả -1 khi hộp thoại đăng nhập chưa tồn
		// tại và trả 0/1 khi đã tồn tại. Đo được cả hai phía: PID 1284 (chưa tới màn đăng nhập) trả -1;
		// PID 17128 và 33808 (đang ở màn đăng nhập) trả 0 rồi 1.
		if (! TryReachLoginScreen(windowHandle, account, settings, token, log, index)) return false;
		token.WaitHandle.WaitOne(stepDelay);

		// Bơm từng ký tự vào hai vùng đệm của native, rồi ghi CẢ HAI vào hai ô nhập trên giao diện bằng đúng hàm
		// SetText của client. SetText ghi đè nên không cần xoá giá trị cũ còn sót trong ô tài khoản.
		if (! TrySend(windowHandle, ClearPendingCredentialsCommand, 0, log, index, account)) return false;
		foreach (char character in account.User) {
			if (! TrySend(windowHandle, TypeCharacterCommand, Pack(UserFieldIndex, character), log, index, account)) return false;
		}
		foreach (char character in account.Pass) {
			if (! TrySend(windowHandle, TypeCharacterCommand, Pack(PasswordFieldIndex, character), log, index, account)) return false;
		}
		if (! TrySend(windowHandle, WriteCredentialFieldsCommand, 0, log, index, account)) return false;
		token.WaitHandle.WaitOne(settings.Speed * 200);

		// Không bấm thẳng vào ô "Đồng ý Điều khoản": lệnh 309 ĐẢO trạng thái, mà client nhớ lựa chọn giữa các phiên
		// nên ô có thể đã bật sẵn. Đọc trước bằng lệnh 314 rồi mới đảo khi đang tắt.
		if (! TrySendLogin(windowHandle, ReadAgreeTermsCommand, 0, out long termsState, log, index, account)) return false;
		if (termsState == 0) {
			if (! TrySend(windowHandle, ToggleAgreeTermsCommand, 0, log, index, account)) return false;
			if (! TrySendLogin(windowHandle, ReadAgreeTermsCommand, 0, out termsState, log, index, account)) return false;
		}
		if (termsState != 1) {
			log($"ĐĂNG NHẬP HỎNG | #{index + 1} | {account.User} | Không bật được ô 'Đồng ý Điều khoản' (cờ đọc ra {termsState}); client sẽ từ chối đăng nhập.");
			return false;
		}
		if (! TrySend(windowHandle, StartGameCommand, 0, log, index, account)) return false;

		token.WaitHandle.WaitOne(StatusMessageWaitMilliseconds);
		string statusMessage = ReadStatusMessage(windowHandle);
		if (statusMessage.Length > 0) {
			log($"ĐĂNG NHẬP THÔNG BÁO | #{index + 1} | {account.User} | {statusMessage}");
		}
		log($"ĐĂNG NHẬP ĐÃ GỬI | #{index + 1} | {account.User} | PID={process.Id} | Partition={account.Partition} | Server={account.Server} | KýTựUser={account.User.Length} | KýTựPass={account.Pass.Length}");
		return TryEnterWorld(process.Id, windowHandle, settings, token, log, index, account);
	}

	// Bấm Enter ở màn chọn nhân vật cho tới khi ĐỌC ĐƯỢC tên nhân vật trong bộ nhớ.
	//
	// Xác nhận bằng trạng thái chứ không bằng giá trị trả về của PostMessageA: bài học lệnh 8 ngày 2026-09-17 là
	// PostMessageA trả thành công trong khi phía nhận bỏ qua hoàn toàn, và 20/20 lần hỏng mà không ai biết.
	// Mốc dùng ở đây là chính mốc AccountEngineCoordinator dùng để phát hiện rớt khỏi game: GameMemory.ReadSnapshot
	// trả NameReadFailed "Character name is empty" khi nhân vật chưa vào thế giới (Utils/GameMemory.cs:148).
	private bool TryEnterWorld(int processId, IntPtr windowHandle, Settings settings, CancellationToken token,
		Action<string> log, int index, LoginAccount account) {
		DateTime deadline = DateTime.UtcNow.AddMilliseconds(EnterWorldTimeoutMilliseconds);
		int attempts = 0;
		string lastFailure = "";
		while (! token.IsCancellationRequested && DateTime.UtcNow < deadline) {
			GameSnapshot snapshot = GameMemory.ReadSnapshot(processId);
			if (snapshot.Success && snapshot.CharacterName.Length > 0) {
				log($"ĐĂNG NHẬP VÀO GAME | #{index + 1} | {account.User} | PID={processId} | NhânVật={snapshot.CharacterName} | SốLầnEnter={attempts} | Cấp={snapshot.Level} | HP={snapshot.Hp}/{snapshot.MaxHp} | ViTri={snapshot.X}/{snapshot.Y}");
				return true;
			}
			lastFailure = $"{snapshot.Status} | {snapshot.FailReason}";
			attempts++;
			if (! transport.TrySendEnterKey(windowHandle, out string enterError)) {
				log($"ĐĂNG NHẬP HỎNG | #{index + 1} | {account.User} | Không gửi được phím Enter ở màn chọn nhân vật | {enterError}");
				return false;
			}
			token.WaitHandle.WaitOne(EnterWorldRetryMilliseconds);
		}
		log($"ĐĂNG NHẬP HỎNG | #{index + 1} | {account.User} | PID={processId} | Đã bấm Enter {attempts} lần trong {EnterWorldTimeoutMilliseconds}ms mà vẫn chưa đọc được tên nhân vật | Lý do cuối: {lastFailure}");
		return false;
	}

	// Gửi lại cho tới khi ĐỐI TƯỢNG hộp thoại biến mất khỏi bộ nhớ, hoặc hết hạn.
	//
	// Bản cũ (TrySendUntilAccepted) coi native trả 1 là xong. Đó là bẫy: 1 chỉ nghĩa là hàm điều phối của client chạy
	// xong, KHÔNG nghĩa là hộp thoại đã đóng. Đo lại trên client 1.30 PID 20736 ngày 2026-09-18, gửi 281 ngay 14ms sau
	// 280 đúng như luồng thật:
	//   lệnh 280 -> 1, lệnh 281 -> 1, nhưng 5 giây sau: hộp Khuyến cáo = -2 (đã đóng), hộp Phiên bản = 3 (VẪN CÒN),
	//   hộp Chọn máy chủ = -2 (chưa bao giờ dựng) — ảnh chụp cho thấy client đứng ở "Thông tin phiên bản".
	// Client chưa dỡ xong hộp trước thì nuốt luôn sự kiện của hộp sau. Cùng khuôn lỗi lệnh 8 ngày 2026-09-17.
	//
	// Phải THẤY hộp thoại tồn tại rồi mới chấp nhận nó biến mất: lúc client chưa dựng xong hộp, ô con trỏ cũng đọc ra
	// -2 y hệt lúc đã đóng, nhận ngay là bỏ qua cả bước.
	private bool TrySendUntilDialogClosed(IntPtr windowHandle, int command, int dialogRva, string description, Settings settings,
		CancellationToken token, Action<string> log, int index, LoginAccount account) {
		DateTime deadline = DateTime.UtcNow.AddMilliseconds(settings.StepTimeoutMilliseconds);
		string lastError = "";
		int attempts = 0;
		bool seenAlive = false;
		while (DateTime.UtcNow < deadline && ! token.IsCancellationRequested) {
			if (transport.TrySendLoginCommand(windowHandle, DiagnoseDialogCommand, dialogRva, out long state, out lastError)) {
				if (state == DialogAliveResult) seenAlive = true;
				if (seenAlive && state == DialogGoneResult) {
					log($"ĐĂNG NHẬP | #{index + 1} | {account.User} | Lệnh {command} ({description}) đã đóng được hộp sau {attempts} lần gửi.");
					return true;
				}
				if (seenAlive) {
					attempts++;
					transport.TrySendLoginCommand(windowHandle, command, 0, out long _, out lastError);
				}
			}
			token.WaitHandle.WaitOne(StepRetryMilliseconds);
		}
		log($"ĐĂNG NHẬP HỎNG | #{index + 1} | {account.User} | Lệnh {command} ({description}) gửi {attempts} lần trong {settings.StepTimeoutMilliseconds}ms mà hộp vẫn chưa đóng | ThấyHộp={seenAlive} | Lỗi cuối: {lastError}");
		return false;
	}

	// Gửi lệnh 282 rồi chờ MÀN HÌNH đăng nhập thật sự xuất hiện, thay vì tin vào giá trị trả về của lệnh. Chỉ gửi lại
	// khi sau cả một lượt chờ mà màn hình vẫn chưa hiện — gửi lại quá sớm là bắn vào màn hình đã đổi.
	private bool TryReachLoginScreen(IntPtr windowHandle, LoginAccount account, Settings settings, CancellationToken token,
		Action<string> log, int index) {
		int attempts = 0;
		for (int attemptIndex = 0; attemptIndex < LoginScreenMaxAttempts && ! token.IsCancellationRequested; attemptIndex++) {
			attempts++;
			// Giá trị trả về của lệnh 282 trước đây bị vứt đi nên log không bao giờ cho biết native từ chối hay
			// không. Từ bản dựng native 99990005, lệnh 282 trả 0 khi dòng cần chọn không tồn tại (danh sách rỗng
			// hoặc chỉ số vượt số dòng) — đúng ca Partition=0 trỏ vào cụm "Máy chủ mới đề cử" có 0 máy chủ.
			transport.TrySendLoginCommand(windowHandle, SelectServerCommand, Pack(account.Partition, account.Server),
				out long selectResult, out string selectError);
			log($"ĐĂNG NHẬP | #{index + 1} | {account.User} | Lệnh {SelectServerCommand} lần {attempts} | Partition={account.Partition} | Server={account.Server} | Result={selectResult}{(selectError.Length > 0 ? " | " + selectError : "")}");
			DateTime settle = DateTime.UtcNow.AddMilliseconds(settings.StepTimeoutMilliseconds);
			while (DateTime.UtcNow < settle && ! token.IsCancellationRequested) {
				if (transport.TrySendLoginCommand(windowHandle, ReadAgreeTermsCommand, 0, out long state, out string _) && state != -1) {
					log($"ĐĂNG NHẬP | #{index + 1} | {account.User} | Đã tới màn đăng nhập sau {attempts} lần gửi lệnh {SelectServerCommand}.");
					return true;
				}
				token.WaitHandle.WaitOne(StepRetryMilliseconds);
			}
		}
		// Đọc luôn câu client đang hiển thị: ca hỏng hay gặp nhất là máy chủ từ chối kết nối, lúc đó client dựng popup
		// thay vì dựng màn đăng nhập. CHƯA kiểm chứng câu cụ thể trong ca đó, nên chỉ ghi nguyên chuỗi đọc được.
		string message = ReadStatusMessage(windowHandle);
		int totalWaitedMilliseconds = attempts * settings.StepTimeoutMilliseconds;
		log($"ĐĂNG NHẬP HỎNG | #{index + 1} | {account.User} | Không tới được màn đăng nhập sau {attempts} lần gửi lệnh {SelectServerCommand} (mỗi lần chờ {settings.StepTimeoutMilliseconds}ms, tổng {totalWaitedMilliseconds}ms) | Client đang hiển thị: {(message.Length > 0 ? message : "(không đọc được)")}");
		return false;
	}

	// Đọc câu thông báo client đang hiện ở màn đăng nhập, từng byte một qua lệnh 317.
	//
	// Chuỗi này mã hoá TCVN3 chứ không phải UTF-8 (đo được ả=0xB6, đ=0xAE, ọ=0xE4, ò=0xDF, ậ=0xCB, ế=0xD5, ố=0xE8,
	// ớ=0xED, á=0xB8, ủ=0xF1 — tất cả khớp bảng TCVN3). CHƯA giải mã sang tiếng Việt vì tôi chưa tra được bảng TCVN3
	// đầy đủ từ nguồn đáng tin, và bịa bảng ra thì log sẽ sai chữ. Tạm ghi nguyên: phần ASCII giữ nguyên, byte có dấu
	// in ra dạng hex, đủ để phân biệt các câu thông báo với nhau.
	private string ReadStatusMessage(IntPtr windowHandle) {
		System.Text.StringBuilder text = new();
		for (int byteIndex = 0; byteIndex < StatusMessageMaxLength; byteIndex++) {
			if (! transport.TrySendLoginCommand(windowHandle, ReadStatusMessageByteCommand, byteIndex, out long value, out string _)) break;
			if (value <= 0) break;
			if (value >= 0x20 && value < 0x7F) text.Append((char)value);
			else text.Append($"<{value:X2}>");
		}
		return text.ToString();
	}

	// Gửi và bắt buộc native phải trả đúng 1. Native trả 1 mới tính là thành công — không có ràng buộc đó thì lệnh
	// rơi vào WndProc gốc vẫn báo thành công, đúng cái bẫy đã làm mất một buổi hôm 2026-09-12.
	private bool TrySend(IntPtr windowHandle, int command, int payload, Action<string> log, int index, LoginAccount account) {
		if (transport.TrySendLoginCommand(windowHandle, command, payload, out long result, out string error) && result == 1) return true;
		if (error.Length == 0) error = $"Native trả về {result} thay vì 1.";
		log($"ĐĂNG NHẬP HỎNG | #{index + 1} | {account.User} | Không gửi được lệnh {command} payload {payload} | {error}");
		return false;
	}

	// Như TrySend nhưng giữ lại mã native trả về, dùng cho lệnh mà giá trị trả về CHÍNH LÀ dữ liệu cần đọc.
	private bool TrySendLogin(IntPtr windowHandle, int command, int payload, out long result, Action<string> log, int index, LoginAccount account) {
		if (transport.TrySendLoginCommand(windowHandle, command, payload, out result, out string error)) return true;
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
