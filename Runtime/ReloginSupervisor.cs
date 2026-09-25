namespace Auto.Runtime;

using System.Collections.Concurrent;
using System.Diagnostics;
using Auto.Login;
using Auto.Utils;

// Tự đăng nhập lại một account đang nằm ngoài game quá lâu: giết client cũ rồi mở client mới qua đúng luồng
// LoginAutomation đã kiểm chứng.
//
// HAI CHẾ ĐỘ, chọn bằng Login.json -> AutoReloginEnabled (mặc định TẮT):
//   - TẮT  (dry-run): chạy đủ chuỗi cổng rồi CHỈ ghi RELOGIN_WOULD_START, không đụng tiến trình nào.
//   - BẬT:            chạy thật. Chỉ được bật sau khi đã đọc log dry-run và thấy nó nhắm đúng account kẹt thật.
//
// Ghi log bằng DebugLog.AddLoginEvent chứ không AddClientEvent: lúc một account rớt khỏi game thì rất có thể mọi
// account đều đang tắt Auto tổng, mà AddClientEvent bị cổng autoLoggingEnabled chặn (DebugLog.cs:67-68) nên sẽ
// câm đúng lúc cần bằng chứng nhất.
internal static class ReloginSupervisor {
	// Ngưỡng rớt khỏi game trước khi tính tới việc đăng nhập lại.
	//
	// SỐ ĐO THẬT từ Release/Diagnostics/client-freeze.log 3 ngày (19->21/09): các lần tự vào lại được nằm trong
	// khoảng 5,0 - 10,5 phút; ca kẹt thật duy nhất là XinLỗiEm 2026-09-19 mất 116,4 phút. 15 phút nằm trên toàn bộ
	// nhóm tự hồi phục và dưới xa ca kẹt thật.
	private const double ReloginAfterMinutes = 15;
	private const int MaximumAttempts = 3;
	private const double RetryGapMinutes = 5;
	// Chờ giữa lúc tắt Auto và lúc giết tiến trình: engine đọc bộ nhớ client mỗi 100ms nên phải cho nó vài nhịp
	// thấy cờ đã tắt. 300ms = 3 nhịp.
	private const int DisableBeforeKillMilliseconds = 300;
	private const int WaitForExitMilliseconds = 10000;
	// Client do Auto mở được đánh dấu trong bấy nhiêu lâu; quá hạn thì coi như client do người mở.
	private const double AutoLaunchWindowMinutes = 10;

	private static readonly ConcurrentDictionary<string, AttemptState> attemptsByCharacter = new(StringComparer.Ordinal);
	private static readonly ConcurrentDictionary<string, DateTime> pendingAutoLaunch = new(StringComparer.Ordinal);
	// Mỗi lúc chỉ một lượt đăng nhập lại: LoginAutomation.Run chặn 60-90 giây, chạy song song hai lượt là hai
	// client cùng nhận chuỗi lệnh 280/281/282.
	private static readonly SemaphoreSlim RunGate = new(1, 1);

	private sealed class AttemptState {
		public int Attempts;
		public DateTime LastAttemptUtc;
		public bool GivenUp;
	}

	public static double ThresholdMinutes => ReloginAfterMinutes;

	// Gọi khi một account đã rớt khỏi game đủ lâu và đúng lý do. Người gọi đã kiểm mốc thời gian và trạng thái.
	public static void Request(GameWindow game, double minutes, SnapshotStatus status) {
		string character = game.CharacterName;
		Decision decision = Evaluate(character);
		string prefix = $"PID={game.ProcessId} | NhânVật={(character.Length > 0 ? character : "(chưa biết)")} " +
			$"| RơiKhỏiGame={minutes:F1} phút | Lý do={status} | User={(decision.User.Length > 0 ? decision.User : "(chưa tra được)")} " +
			$"| SốLầnĐãThử={GetAttempts(character)} | TabLoginĐangChạy={LoginSession.IsBusy}";

		if (! decision.Eligible) {
			DebugLog.AddLoginEvent($"RELOGIN_WOULD_START | {prefix} | KẾT LUẬN={decision.Gate}");
			return;
		}
		if (decision.Settings == null || decision.Account == null) return;
		if (! decision.Settings.AutoReloginEnabled) {
			DebugLog.AddLoginEvent($"RELOGIN_WOULD_START | {prefix} | KẾT LUẬN=ĐỦ ĐIỀU KIỆN nhưng AutoReloginEnabled=false trong Login.json, không thực thi.");
			return;
		}

		AttemptState state = attemptsByCharacter.GetOrAdd(character, _ => new AttemptState());
		state.Attempts++;
		state.LastAttemptUtc = DateTime.UtcNow;
		int attemptNumber = state.Attempts;
		// Đánh dấu TRƯỚC KHI GIẾT, không phải sau khi Run trả về: vòng quét 1 giây của AccountListViewModel có thể
		// đọc được tên nhân vật của client mới TRƯỚC khi Run kịp trả về, và khi đó hồ sơ sẽ khôi phục với
		// TựMở=False rồi Auto tổng không được bật lại — tức cứu xong mà account vẫn nằm im.
		pendingAutoLaunch[character] = DateTime.UtcNow.AddMinutes(AutoLaunchWindowMinutes);

		int processId = game.ProcessId;
		Settings loginSettings = decision.Settings;
		LoginAccount account = decision.Account;
		DebugLog.AddLoginEvent($"RELOGIN_START | {prefix} | LầnThử={attemptNumber}/{MaximumAttempts}");
		lock (game.AutoSync) {
			// Tắt Auto TRƯỚC khi giết: engine đang đọc bộ nhớ tiến trình đó mỗi 100ms, giết ngay giữa chừng là để
			// chúng đọc vào tiến trình đã chết. Cùng khuôn với nút đóng client tay ở UI/Views/MainWindow.xaml.cs:97-99.
			game.Enabled = false;
			game.AttackSettings.Enabled = false;
		}
		_ = Task.Run(() => RunRelogin(character, processId, loginSettings, account, attemptNumber));
	}

	// Vào lại được game thì quên sạch bộ đếm — lần rớt sau là một đợt mới.
	public static void NoteBackInWorld(string characterName) {
		if (characterName.Length == 0) return;
		attemptsByCharacter.TryRemove(characterName, out _);
	}

	// Lấy ra và XOÁ dấu "client này do Auto mở". Chỉ có tác dụng đúng một lần, nên client mở tay sau đó không bị
	// nhận nhầm là do Auto mở.
	public static bool ConsumeAutoLaunched(string characterName) {
		if (characterName.Length == 0) return false;
		if (! pendingAutoLaunch.TryRemove(characterName, out DateTime expiryUtc)) return false;
		return DateTime.UtcNow <= expiryUtc;
	}

	private static void RunRelogin(string characterName, int processId, Settings settings, LoginAccount account, int attemptNumber) {
		using IDisposable? lease = LoginSession.TryEnter($"Tự đăng nhập lại {characterName}");
		if (lease == null) {
			DebugLog.AddLoginEvent($"RELOGIN_DEFERRED | NhânVật={characterName} | Luồng đăng nhập khác đang chạy ({LoginSession.CurrentOwner})");
			pendingAutoLaunch.TryRemove(characterName, out _);
			return;
		}
		if (! RunGate.Wait(0)) {
			DebugLog.AddLoginEvent($"RELOGIN_DEFERRED | NhânVật={characterName} | Đang có lượt đăng nhập lại khác chạy.");
			pendingAutoLaunch.TryRemove(characterName, out _);
			return;
		}
		try {
			if (! TryKillClient(characterName, processId)) {
				pendingAutoLaunch.TryRemove(characterName, out _);
				return;
			}
			// Dựng cấu hình chỉ chứa ĐÚNG account đó, chép ĐỦ MỌI TRƯỜNG.
			//
			// Đây là cái bẫy đã có tiền lệ: DebugTools/LoginTestProbe ngày 2026-09-18 quên chép WaitBeforeLogin và
			// StepTimeoutMilliseconds nên chạy bằng giá trị mặc định 60000ms thay vì giá trị thật trong Login.json,
			// gây đúng hiện tượng "debug chạy ngon mà thực tế lỗi".
			Settings single = new() {
				ExeLink = settings.ExeLink,
				Speed = settings.Speed,
				DelayLogin = settings.DelayLogin,
				WaitBeforeLogin = settings.WaitBeforeLogin,
				StepTimeoutMilliseconds = settings.StepTimeoutMilliseconds,
				AutoReloginEnabled = settings.AutoReloginEnabled,
				Accounts = [account]
			};
			int dispatched = new LoginAutomation().Run(single, DebugLog.AddLoginEvent, CancellationToken.None);
			DebugLog.AddLoginEvent($"RELOGIN_RESULT | NhânVật={characterName} | LầnThử={attemptNumber}/{MaximumAttempts} | ĐãGửi={dispatched}/1");
			if (dispatched == 0) {
				pendingAutoLaunch.TryRemove(characterName, out _);
				ReportGiveUpIfExhausted(characterName, account.User);
			}
		} catch (Exception ex) {
			pendingAutoLaunch.TryRemove(characterName, out _);
			DebugLog.AddLoginEvent($"RELOGIN_HỎNG | NhânVật={characterName} | {ex.GetType().Name}: {ex.Message}");
		} finally {
			RunGate.Release();
		}
	}

	private static bool TryKillClient(string characterName, int processId) {
		try {
			// Cho engine vài nhịp thấy cờ Auto đã tắt trước khi tiến trình biến mất.
			Thread.Sleep(DisableBeforeKillMilliseconds);
			using Process process = Process.GetProcessById(processId);
			// Kill chứ không WM_CLOSE: client rớt khỏi game hay treo cứng, mà cửa sổ treo thì không bơm message nên
			// WM_CLOSE rơi vào hư không. Lý do đầy đủ đã ghi ở UI/Views/MainWindow.xaml.cs:81-83.
			process.Kill();
			process.WaitForExit(WaitForExitMilliseconds);
			DebugLog.AddLoginEvent($"RELOGIN_KILLED | NhânVật={characterName} | PID={processId} | ĐãThoát={process.HasExited}");
			return process.HasExited;
		} catch (Exception ex) {
			DebugLog.AddLoginEvent($"RELOGIN_KILL_HỎNG | NhânVật={characterName} | PID={processId} | {ex.GetType().Name}: {ex.Message}");
			return false;
		}
	}

	private static void ReportGiveUpIfExhausted(string characterName, string user) {
		if (! attemptsByCharacter.TryGetValue(characterName, out AttemptState? state)) return;
		if (state.Attempts < MaximumAttempts || state.GivenUp) return;
		state.GivenUp = true;
		string line = $"RELOGIN_GIVE_UP | NhânVật={characterName} | User={user} | Đã thử {state.Attempts} lần mà không vào lại được | " +
			"Auto SẼ KHÔNG đụng tới account này nữa cho tới khi nó tự vào game lại hoặc khởi động lại Auto.";
		DebugLog.AddLoginEvent(line);
		// Ghi cả vào client-freeze.log: chủ dự án đang đọc file đó cho mọi sự cố vòng đời client.
		DebugLog.AddClientEvent(line);
	}

	private static int GetAttempts(string characterName) =>
		characterName.Length > 0 && attemptsByCharacter.TryGetValue(characterName, out AttemptState? state) ? state.Attempts : 0;

	private readonly record struct Decision(bool Eligible, string Gate, string User, Settings? Settings, LoginAccount? Account);

	// Chuỗi cổng, thoát ngay ở cổng đầu tiên trượt. Thứ tự cố ý: cổng rẻ và chặn chắc nhất đứng trước.
	private static Decision Evaluate(string characterName) {
		// Cổng 1. Client chưa từng vào game thì không biết nó là ai — TUYỆT ĐỐI không đụng tới.
		if (characterName.Length == 0) return new Decision(false, "BỎ QUA | Chưa đọc được tên nhân vật bao giờ", "", null, null);

		// Cổng 2. Phải tra được tài khoản từ tên nhân vật.
		if (! AccountCharacterMap.TryResolveUser(characterName, out string user)) {
			return new Decision(false, "BỎ QUA | Không có trong Profiles/AccountCharacters.json", "", null, null);
		}

		// Cổng 3. Tài khoản đó phải nằm trong Login.json. Đây là lời bảo đảm "chỉ đụng tới account của chính
		// chủ dự án", đúng yêu cầu đã chốt.
		if (! Settings.TryLoad(Settings.DefaultPath, out Settings settings, out string failure)) {
			return new Decision(false, $"BỎ QUA | Không đọc được Login.json | {failure}", user, null, null);
		}
		LoginAccount? account = settings.Accounts.FirstOrDefault(candidate => string.Equals(candidate.User, user, StringComparison.Ordinal));
		if (account == null) return new Decision(false, "BỎ QUA | Tài khoản không có trong Login.json", user, null, null);

		// Cổng 4. Không chen vào lúc tab Login đang chạy tay.
		if (LoginSession.IsBusy) return new Decision(false, $"HOÃN | Luồng đăng nhập khác đang chạy ({LoginSession.CurrentOwner})", user, null, null);

		// Cổng 5. Chính sách thử lại.
		if (attemptsByCharacter.TryGetValue(characterName, out AttemptState? state)) {
			if (state.GivenUp) return new Decision(false, $"BỎ QUA | Đã bỏ cuộc sau {state.Attempts} lần thử", user, null, null);
			if (state.Attempts >= MaximumAttempts) return new Decision(false, $"BỎ QUA | Đã đủ {MaximumAttempts} lần thử", user, null, null);
			double sinceLast = (DateTime.UtcNow - state.LastAttemptUtc).TotalMinutes;
			if (sinceLast < RetryGapMinutes) return new Decision(false, $"HOÃN | Mới thử cách đây {sinceLast:F1} phút, chờ đủ {RetryGapMinutes} phút", user, null, null);
		}
		return new Decision(true, "ĐỦ ĐIỀU KIỆN", user, settings, account);
	}
}
