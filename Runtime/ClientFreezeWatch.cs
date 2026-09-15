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
		public int MaxDivergenceRaw;
		public bool Reported;
		public DateTime NextOngoingReportUtc;
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
					DebugLog.AddClientEvent($"CLIENT_FREEZE_CLEARED | PID={game.ProcessId} | {game.CharacterName} | ĐóngBăngTrong={(int)(now - state.FrozenSinceUtc).TotalSeconds}s | SốLầnHPĐổiLúcĐóngBăng={state.HpChangesWhileFrozen} | LệchNguồnLớnNhất={state.MaxDivergenceRaw} raw | ViTriMới={snapshot.X}/{snapshot.Y}");
				}
				state.LastX = snapshot.X;
				state.LastY = snapshot.Y;
				state.FrozenSinceUtc = DateTime.MinValue;
				state.HpChangesWhileFrozen = 0;
				state.MaxDivergenceRaw = 0;
				state.Reported = false;
				return;
			}

			if (state.FrozenSinceUtc == DateTime.MinValue) {
				state.FrozenSinceUtc = now;
				return;
			}

			// Đo chênh lệch với nguồn toạ độ của worker đánh — đây là tín hiệu ĐÃ quan sát được trên PID=28500.
			(int scannedX, int scannedY, DateTime scannedUtc) = game.AttackEngine.LastScannedPlayerPosition;
			bool scannedFresh = scannedX > 0 && scannedY > 0 && (now - scannedUtc).TotalSeconds <= 5;
			int divergence = scannedFresh ? (int)EliteAvoidance.Distance(snapshot.X, snapshot.Y, scannedX, scannedY) : 0;
			if (divergence > state.MaxDivergenceRaw) state.MaxDivergenceRaw = divergence;

			double frozenMilliseconds = (now - state.FrozenSinceUtc).TotalMilliseconds;
			if (frozenMilliseconds < SuspectAfterFrozenMilliseconds) return;
			if (state.Reported && now < state.NextOngoingReportUtc) return;

			string marker = state.Reported ? "CLIENT_FREEZE_ONGOING" : "CLIENT_FREEZE_SUSPECTED";
			state.Reported = true;
			state.NextOngoingReportUtc = now.AddMilliseconds(OngoingReportIntervalMilliseconds);
			DebugLog.AddClientEvent($"{marker} | PID={game.ProcessId} | {game.CharacterName} | ĐóngBăng={(int)(frozenMilliseconds / 1000)}s | ViTriSnapshot={snapshot.X}/{snapshot.Y} | HP={snapshot.Hp}/{snapshot.MaxHp} | SốLầnHPĐổi={state.HpChangesWhileFrozen} | ViTriWorkerĐánh={(scannedFresh ? $"{scannedX}/{scannedY}" : "KHÔNG_TƯƠI")} | LệchHaiNguồn={divergence} raw ({(divergence > SourceDivergenceRaw ? "BẤT ĐỒNG" : "khớp")}) | LệchLớnNhất={state.MaxDivergenceRaw} raw | Map={game.LastObservedMapId} | {ProbeWindow(game.Handle)}");
		}
	}

	public static void Forget(int processId) => stateByProcessId.TryRemove(processId, out _);

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
