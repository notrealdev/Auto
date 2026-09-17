namespace Auto.Runtime;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Auto.Attack;
using Auto.Utils;

// Bắt sự cố CLIENT TREO (đóng băng nhưng KHÔNG chết, cửa sổ vẫn còn nên Auto vẫn thấy account).
//
// VÌ SAO BỘ ĐO NÀY TỒN TẠI — chữ ký đã quan sát được, không phải suy đoán.
// Ngày 2026-09-15, PID=28500 treo và chủ dự án xác nhận. Đào ngược log bản cũ thấy:
//   1. heartbeat (GameMemory.ReadSnapshot) báo toạ độ ĐỨNG NGUYÊN 52572/104835 suốt 38 nhịp liên tiếp (~38 phút)
//   2. NHƯNG HP trong CÙNG khối đọc đó vẫn sống: 470/470 -> 459/470 -> 470/470, tức khối không hề cũ
//   3. CÙNG lúc, worker đánh (AutoFsEntityScanner, khác gốc khác offset) đọc ra toạ độ LIÊN TỤC ĐỔI:
//      53810/105037 -> 52891/104673 -> 54151/103941 -> 55260/104634
//   4. anti-afk.log ghi StationaryMilliseconds tới 516812 (8,6 phút)
//   5. KHÔNG có lấy một dòng SendMessageTimeoutA failed hay PostMessageA failed nào
//
// GIẢ THUYẾT (chưa chứng minh được, cần dữ liệu từ bộ đo này): luồng UI của client đứng nên struct player ngừng
// được ghi X/Y và màn hình đóng băng, trong khi luồng mạng vẫn chạy nên HP và bảng entity vẫn cập nhật theo gói
// server. Tôi KHÔNG có cách xác nhận client ghi X/Y từ luồng nào.
//
// Vì sao IsHungAppWindow một mình là KHÔNG ĐỦ: nó chỉ bật khi cửa sổ ngừng bơm message hẳn. Điểm 5 ở trên cho
// thấy lệnh vẫn gửi được bình thường suốt thời gian treo, nên rất có thể message vẫn được bơm và API đó bỏ sót.
// Do đó ở đây đo BỐN tín hiệu độc lập cùng lúc, để lần sau biết cái nào thực sự bắt được.
//
// HOÀN TOÀN CHỈ QUAN SÁT: không đổi một hành vi nào của Auto.
internal static class ClientFreezeWatch {
	// Toạ độ snapshot phải đứng yên liên tục chừng này mới bắt đầu nghi. Nhân vật đứng đánh một con quái tại chỗ
	// vài giây là bình thường; 30 giây thì không.
	private const int SuspectAfterFrozenMilliseconds = 30_000;
	// Đang nghi thì cứ chừng này ghi lại một dòng, để dựng được dòng thời gian thay vì chỉ có mốc đầu và mốc cuối.
	private const int OngoingReportIntervalMilliseconds = 60_000;
	// Toạ độ hai nguồn lệch quá chừng này thì coi là BẤT ĐỒNG. 256 raw = 1 ô trục X.
	private const int SourceDivergenceRaw = 256;
	// Nền lành ĐO ĐƯỢC trên cả 6 client ngày 2026-09-15: WM_NULL quay về trong 11-15ms (đúng một khung hình 60fps).
	private const uint WmNullTimeoutMilliseconds = 1000;
	private const uint WmNull = 0x0000;
	private const uint SmtoAbortIfHung = 0x0002;

	[DllImport("user32.dll")]
	private static extern bool IsHungAppWindow(IntPtr windowHandle);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SendMessageTimeoutA(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMilliseconds, out UIntPtr result);

	private sealed class WatchState {
		public int LastX;
		public int LastY;
		public int LastHp = -1;
		public DateTime FrozenSinceUtc;
		public int HpChangesWhileFrozen;
		// -1 = chưa đo được lần nào (worker Đánh chưa có toạ độ tươi), phân biệt với 0 = đo được và đúng bằng 0.
		public int MaxDivergenceRaw = -1;
		public bool Reported;
		public DateTime NextOngoingReportUtc;
		// Nhịp sống của chính client, đọc từ tiêu đề cửa sổ. Xem SampleWindowTitle.
		public string LastTitle = "";
		public int TitleChangesWhileFrozen;
		public DateTime NextTitleSampleUtc;
		// Mốc hai bộ đếm trên tại lần ghi log gần nhất, để dòng ONGOING xét nhịp sống TRONG CỬA SỔ VỪA RỒI chứ không
		// xét tích luỹ từ đầu đợt. Không có mốc này thì một client sống 30 giây đầu rồi đứng hẳn sẽ mãi mãi bị xếp là
		// còn sống, vì hai bộ đếm tích luỹ không bao giờ tụt về 0.
		public int HpChangesAtLastReport;
		public int TitleChangesAtLastReport;
		// Lần ghi gần nhất xếp loại là đóng băng thật hay chỉ đứng yên, để dòng đóng sổ gọi đúng tên.
		public bool ReportedAsFreeze;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int GetWindowTextW(IntPtr windowHandle, char[] buffer, int maxCount);

	// Đo TIÊU ĐỀ CỬA SỔ có còn đổi hay không, dùng làm nhịp sống của client.
	//
	// Vì sao thêm (chủ dự án mô tả 2026-09-17): client đóng băng nhìn như một ảnh chụp, NHƯNG dùng client khác chat
	// và xem trang bị của account đó thì nó vẫn sống trên máy chủ — tức luồng nền vẫn chạy, chỉ phần hiển thị đứng.
	// Ba thứ Auto đang đo (toạ độ, HP, WM_NULL) đều KHÔNG phân biệt được ca này: đêm 2026-09-17 PID=14936 đóng băng
	// 8797 giây mà vẫn ghi IsHungAppWindow=False và WM_NULL=0ms.
	//
	// Tiêu đề thì do chính client ghi và có nhịp thật: đo 2026-09-17 trên 7 client, 6 client đổi giá trị FPS/PING
	// trong vòng 3 giây; client duy nhất đứng im là client chưa đăng nhập (PING:0).
	// Ghi nguyên tiêu đề vào log chứ không tự tách FPS/PING: chưa biết lúc đóng băng nó còn dạng nào.
	//
	// CHỈ đo khi đang đóng băng và tối đa 1 lần/giây — nhánh này hiếm nên không đụng tới chi phí lúc chạy bình thường.
	private static void SampleWindowTitle(IntPtr windowHandle, WatchState state, DateTime now) {
		if (windowHandle == IntPtr.Zero || now < state.NextTitleSampleUtc) return;
		state.NextTitleSampleUtc = now.AddSeconds(1);
		char[] buffer = new char[256];
		int length = GetWindowTextW(windowHandle, buffer, buffer.Length);
		string title = length > 0 ? new string(buffer, 0, length) : "";
		if (state.LastTitle.Length > 0 && ! string.Equals(title, state.LastTitle, StringComparison.Ordinal)) {
			state.TitleChangesWhileFrozen++;
		}
		state.LastTitle = title;
	}

	private static readonly ConcurrentDictionary<int, WatchState> stateByProcessId = new();

	// Gọi mỗi nhịp coordinator (100ms). Phần chạy thường chỉ là vài phép so sánh số nguyên; nhánh đo WM_NULL chỉ
	// chạy lúc ghi log, tức hiếm.
	public static void Observe(GameWindow game, GameSnapshot snapshot) {
		if (snapshot == null || ! snapshot.Success) return;
		if (snapshot.X <= 0 || snapshot.Y <= 0) return;

		WatchState state = stateByProcessId.GetOrAdd(game.ProcessId, _ => new WatchState());
		DateTime now = DateTime.UtcNow;

		lock (state) {
			bool positionMoved = snapshot.X != state.LastX || snapshot.Y != state.LastY;
			if (state.LastHp >= 0 && snapshot.Hp != state.LastHp && state.FrozenSinceUtc != DateTime.MinValue) state.HpChangesWhileFrozen++;
			state.LastHp = snapshot.Hp;

			if (positionMoved) {
				// Vừa nhúc nhích trở lại: nếu trước đó đã ghi nghi ngờ thì đóng sổ bằng một dòng hồi phục.
				if (state.Reported) {
					string clearedDivergence = state.MaxDivergenceRaw < 0 ? "LệchNguồnLớnNhất=CHƯA_ĐO_ĐƯỢC" : $"LệchNguồnLớnNhất={state.MaxDivergenceRaw} raw";
					DebugLog.AddClientEvent($"{(state.ReportedAsFreeze ? "CLIENT_FREEZE_CLEARED" : "CLIENT_STATIONARY_CLEARED")} | PID={game.ProcessId} | {game.CharacterName} | ĐứngYênTrong={(int)(now - state.FrozenSinceUtc).TotalSeconds}s | SốLầnHPĐổi={state.HpChangesWhileFrozen} | SốLầnĐổiTiêuĐề={state.TitleChangesWhileFrozen} | {clearedDivergence} | ViTriMới={snapshot.X}/{snapshot.Y}");
				}
				state.LastX = snapshot.X;
				state.LastY = snapshot.Y;
				state.FrozenSinceUtc = DateTime.MinValue;
				state.HpChangesWhileFrozen = 0;
				state.MaxDivergenceRaw = -1;
				state.Reported = false;
				state.LastTitle = "";
				state.TitleChangesWhileFrozen = 0;
				state.NextTitleSampleUtc = DateTime.MinValue;
				state.HpChangesAtLastReport = 0;
				state.TitleChangesAtLastReport = 0;
				state.ReportedAsFreeze = false;
				return;
			}

			if (state.FrozenSinceUtc == DateTime.MinValue) {
				state.FrozenSinceUtc = now;
				SampleWindowTitle(game.Handle, state, now);
				return;
			}
			SampleWindowTitle(game.Handle, state, now);

			// Đo chênh lệch với nguồn toạ độ của worker đánh — đây là tín hiệu ĐÃ quan sát được trên PID=28500.
			(int scannedX, int scannedY, DateTime scannedUtc) = game.AttackEngine.LastScannedPlayerPosition;
			bool scannedFresh = scannedX > 0 && scannedY > 0 && (now - scannedUtc).TotalSeconds <= 5;
			int divergence = scannedFresh ? (int)EliteAvoidance.Distance(snapshot.X, snapshot.Y, scannedX, scannedY) : -1;
			if (divergence > state.MaxDivergenceRaw) state.MaxDivergenceRaw = divergence;

			double frozenMilliseconds = (now - state.FrozenSinceUtc).TotalMilliseconds;
			if (frozenMilliseconds < SuspectAfterFrozenMilliseconds) return;
			if (state.Reported && now < state.NextOngoingReportUtc) return;

			// TOẠ ĐỘ ĐỨNG YÊN MỘT MÌNH KHÔNG PHẢI LÀ ĐÓNG BĂNG.
			//
			// Nhân vật đứng tại chỗ đánh một con quái thì toạ độ không đổi phút này qua phút khác, mà client hoàn toàn
			// bình thường. Bản cũ chỉ xét toạ độ nên gọi hết thảy là CLIENT_FREEZE_SUSPECTED.
			// Đo trên Release/Diagnostics/client-freeze.log phiên ngày 2026-09-17: 21 dòng SUSPECTED, 20 dòng tự hết
			// sau 39-42 giây. Dòng 14:54:23 PID=1604 ghi HP=524/661, SốLầnHPĐổi=7, SốLầnĐổiTiêuĐề=19; sáu mươi giây
			// sau cùng đợt đó lên SốLầnHPĐổi=50 và SốLầnĐổiTiêuĐề=58 — client đang mất máu và tiêu đề vẫn đập, tức nó
			// sống hẳn hoi, chỉ là nhân vật không nhúc nhích.
			//
			// KHÔNG chặn ghi log ở ca này mà TÁCH TÊN. Chưa từng bắt được đợt đóng băng THẬT nào kể từ khi thêm phép
			// đo tiêu đề (2026-09-17), nên chưa biết lúc đóng băng thật thì tiêu đề có còn đổi hay không — lấy tiêu đề
			// làm cổng chặn là tự bịt mắt chính mình. Tách tên thì giữ nguyên mọi số đo mà vẫn dọn sạch được nhóm
			// CLIENT_FREEZE để sau này nối cơ chế kill + mở lại client không giết nhầm 20 client đang khoẻ.
			//
			// Xét theo CỬA SỔ VỪA RỒI: lần đầu là 30 giây kể từ lúc đứng yên, các lần sau là 60 giây kể từ dòng trước.
			bool hpAlive = state.HpChangesWhileFrozen > state.HpChangesAtLastReport;
			bool titleAlive = state.TitleChangesWhileFrozen > state.TitleChangesAtLastReport;
			bool clientAlive = hpAlive || titleAlive;
			string marker = clientAlive
				? (state.Reported ? "CLIENT_STATIONARY_ONGOING" : "CLIENT_STATIONARY")
				: (state.Reported ? "CLIENT_FREEZE_ONGOING" : "CLIENT_FREEZE_SUSPECTED");
			state.ReportedAsFreeze = ! clientAlive;
			state.HpChangesAtLastReport = state.HpChangesWhileFrozen;
			state.TitleChangesAtLastReport = state.TitleChangesWhileFrozen;
			state.Reported = true;
			state.NextOngoingReportUtc = now.AddMilliseconds(OngoingReportIntervalMilliseconds);
			// Không có số đo thì phải ghi là KHÔNG ĐO ĐƯỢC, tuyệt đối không ghi "khớp".
			// divergence = -1 nghĩa là worker Đánh không có toạ độ tươi (nó đã bị dừng, hoặc chưa kịp quét lần nào).
			// Bản cũ gán 0 cho ca đó rồi in "LệchHaiNguồn=0 raw (khớp)" — đọc y như hai nguồn đang đồng ý, trong khi
			// sự thật là chỉ có một nguồn. Dòng log sai kiểu này làm chính việc chẩn đoán đi lệch hướng.
			string divergenceText = divergence < 0
				? "LệchHaiNguồn=KHÔNG_ĐO_ĐƯỢC (worker Đánh không có toạ độ tươi)"
				: $"LệchHaiNguồn={divergence} raw ({(divergence > SourceDivergenceRaw ? "BẤT ĐỒNG" : "khớp")})";
			string maxDivergenceText = state.MaxDivergenceRaw < 0 ? "LệchLớnNhất=CHƯA_ĐO_ĐƯỢC" : $"LệchLớnNhất={state.MaxDivergenceRaw} raw";
			string lifeText = $"NhịpSốngCửaSổNày={(clientAlive ? "CÒN" : "TẮT")} (HPĐổi={(hpAlive ? "có" : "không")}, TiêuĐềĐổi={(titleAlive ? "có" : "không")})";
			DebugLog.AddClientEvent($"{marker} | PID={game.ProcessId} | {game.CharacterName} | ĐứngYên={(int)(frozenMilliseconds / 1000)}s | {lifeText} | ViTriSnapshot={snapshot.X}/{snapshot.Y} | HP={snapshot.Hp}/{snapshot.MaxHp} | SốLầnHPĐổi={state.HpChangesWhileFrozen} | ViTriWorkerĐánh={(scannedFresh ? $"{scannedX}/{scannedY}" : "KHÔNG_TƯƠI")} | {divergenceText} | {maxDivergenceText} | {ProbeCoordinateSources(game.ProcessId)} | Map={game.LastObservedMapId} | SốLầnĐổiTiêuĐề={state.TitleChangesWhileFrozen} | TiêuĐề=\"{state.LastTitle}\" | {ProbeWindow(game.Handle)}");
		}
	}

	public static void Forget(int processId) => stateByProcessId.TryRemove(processId, out _);

	// BIỂU QUYẾT BA NGUỒN — mục đích duy nhất là trả lời một câu hỏi chưa ai trả lời được:
	// khi toạ độ đứng im, cặp nào đang nói thật?
	//
	// Struct nhân vật có BA cặp toạ độ, cùng một bản ghi, ba offset khác nhau:
	//   Raw       0x434C/0x4350 — cặp mọi nơi khác trong Auto dùng (EntityFinder, EntitySnapshot, MonsterFinder,
	//                             AutoFsEntityScanner), và từ 2026-09-16 cả GameMemory.ReadSnapshot.
	//   Mirror    0x71EC/0x71F0 — đã khai báo trong GameAddresses từ trước, chưa nơi nào dùng.
	//   Legacy    0x75F4/0x75F8 — cặp ReadSnapshot dùng CHO TỚI 2026-09-16.
	//
	// Đêm 2026-09-15 -> 16 hai cặp Raw và Legacy bất đồng tới 3403 raw (~13 ô) mà KHÔNG có cách nào biết cặp nào
	// đúng: log hợp với cả "nhân vật đang chạy, Legacy chết" lẫn "nhân vật kẹt thật, Raw nhảy loạn". Cặp thứ ba
	// phá thế hoà: hai cặp trùng nhau thì cặp còn lại là cặp hỏng.
	//
	// Lưu ý khi đọc log: từ 2026-09-16 ViTriSnapshot và ViTriWorkerĐánh CÙNG đọc cặp Raw, nên LệchHaiNguồn không
	// còn là phép so hai nguồn độc lập nữa. Ba con số dưới đây mới là phép so thật.
	//
	// HOÀN TOÀN CHỈ QUAN SÁT. Chỉ chạy lúc sắp ghi log, tức hiếm.
	private static string ProbeCoordinateSources(int processId) {
		try {
			RuntimeLayout layout = RuntimeLayoutResolver.Resolve(processId);
			if (! layout.PlayerReady) return "BaNguồn=KHÔNG_ĐỌC_ĐƯỢC (layout chưa sẵn sàng)";
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return "BaNguồn=KHÔNG_ĐỌC_ĐƯỢC (ModuleBase=0)";
			IntPtr entityTable = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			if (entityTable == IntPtr.Zero) return "BaNguồn=KHÔNG_ĐỌC_ĐƯỢC (EntityTable=0)";
			IntPtr player = IntPtr.Add(entityTable, layout.PlayerRecordOffset);

			int rawX = reader.ReadInt32(IntPtr.Add(player, GameAddresses.Entity.RawX));
			int rawY = reader.ReadInt32(IntPtr.Add(player, GameAddresses.Entity.RawY));
			int mirrorX = reader.ReadInt32(IntPtr.Add(player, GameAddresses.Entity.RawXMirror));
			int mirrorY = reader.ReadInt32(IntPtr.Add(player, GameAddresses.Entity.RawYMirror));
			int legacyX = reader.ReadInt32(IntPtr.Add(player, GameAddresses.Entity.RawXLegacy));
			int legacyY = reader.ReadInt32(IntPtr.Add(player, GameAddresses.Entity.RawYLegacy));

			int rawToMirror = (int)EliteAvoidance.Distance(rawX, rawY, mirrorX, mirrorY);
			int rawToLegacy = (int)EliteAvoidance.Distance(rawX, rawY, legacyX, legacyY);
			int mirrorToLegacy = (int)EliteAvoidance.Distance(mirrorX, mirrorY, legacyX, legacyY);

			// Cặp nào bị CẢ HAI cặp kia bỏ lại thì cặp đó là cặp lạc. Ngưỡng dùng chung SourceDivergenceRaw (1 ô).
			string verdict;
			if (rawToMirror <= SourceDivergenceRaw && rawToLegacy <= SourceDivergenceRaw && mirrorToLegacy <= SourceDivergenceRaw) verdict = "BaNguồnĐỒNG_Ý";
			else if (rawToMirror <= SourceDivergenceRaw) verdict = "LẠC=Legacy(0x75F4) — Raw và Mirror đồng ý";
			else if (rawToLegacy <= SourceDivergenceRaw) verdict = "LẠC=Mirror(0x71EC) — Raw và Legacy đồng ý";
			else if (mirrorToLegacy <= SourceDivergenceRaw) verdict = "LẠC=Raw(0x434C) — Mirror và Legacy đồng ý";
			else verdict = "BA_NGUỒN_LỆCH_NHAU_HẾT";

			return $"BaNguồn Raw={rawX}/{rawY} Mirror={mirrorX}/{mirrorY} Legacy={legacyX}/{legacyY} | Raw-Mirror={rawToMirror} Raw-Legacy={rawToLegacy} Mirror-Legacy={mirrorToLegacy} | {verdict}";
		} catch (Exception ex) {
			return $"BaNguồn=KHÔNG_ĐỌC_ĐƯỢC ({ex.GetType().Name})";
		}
	}

	// Chỉ gọi lúc sắp ghi log: mỗi lần là một chuyến đồng bộ sang luồng UI của client.
	private static string ProbeWindow(IntPtr windowHandle) {
		if (windowHandle == IntPtr.Zero) return "CửaSổ=KHÔNG_CÓ";
		bool hung = IsHungAppWindow(windowHandle);
		Stopwatch stopwatch = Stopwatch.StartNew();
		IntPtr answered = SendMessageTimeoutA(windowHandle, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, WmNullTimeoutMilliseconds, out _);
		stopwatch.Stop();
		string roundTrip = answered == IntPtr.Zero
			? $"WM_NULL=KHÔNG_TRẢ_LỜI sau {stopwatch.ElapsedMilliseconds}ms (Win32Error={Marshal.GetLastWin32Error()})"
			: $"WM_NULL={stopwatch.ElapsedMilliseconds}ms (nền lành 11-15ms)";
		return $"IsHungAppWindow={hung} | {roundTrip}";
	}
}
