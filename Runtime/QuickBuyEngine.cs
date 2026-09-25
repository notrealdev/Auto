namespace Auto.Runtime;

using Auto.Attack;
using Auto.Loot;

// Tự mua nhanh thuốc máu/mana khi túi không còn loại thuốc đã chọn (khối "Mua item hồi phục", tab Cơ bản).
//
// Chủ dự án chốt 2026-09-25: luồng này LUÔN chạy khi Auto tổng bật và không luồng nào khác được chen vào. Vì vậy
// AccountEngineCoordinator gọi Tick ngay sau khi đọc được snapshot, TRƯỚC mọi nhánh return của Hồi thành phù, Về thành,
// Sửa đồ, Buff, Đánh... và Tick không trả cờ "giữ quyền điều khiển": nó chỉ gửi một lệnh mua rồi thôi, không đụng vào
// di chuyển hay các engine khác.
//
// Điều kiện mua (giả định, chưa được chủ dự án xác nhận): số thuốc loại đã chọn trong túi = 0. AutoFS còn đòi HP dưới ngưỡng
// và thuốc đã hết; khối UI của Auto không có ngưỡng HP nên chỉ dùng điều kiện "hết thuốc".
//
// Giới hạn 1000 bình/ngày do server giữ (chủ dự án chốt): gửi lệnh mà túi không tăng nghĩa là server không bán, khi đó
// nghỉ NoIncreaseBackoff rồi thử lại thay vì gửi liên tục.
internal sealed class QuickBuyEngine {
	private const int CheckIntervalMilliseconds = 2000;
	private const int VerifyTimeoutMilliseconds = 5000;
	private static readonly TimeSpan NoIncreaseBackoff = TimeSpan.FromMinutes(10);

	private readonly BasicSettings settings;
	private readonly AutoFsAttackTransport transport;
	private readonly InventoryPotionCounter counter = new();
	private readonly PotionState hp = new("HP");
	private readonly PotionState mp = new("MP");
	private DateTime nextCheckUtc = DateTime.MinValue;

	public QuickBuyEngine(BasicSettings settings, AutoFsAttackTransport transport) {
		this.settings = settings;
		this.transport = transport;
	}

	public void Tick(int processId, IntPtr gameWindow, Action<string> log) {
		DateTime now = DateTime.UtcNow;
		if (now < nextCheckUtc) return;
		nextCheckUtc = now.AddMilliseconds(CheckIntervalMilliseconds);
		if (!settings.EnableQuickBuyHp && !settings.EnableQuickBuyMp) {
			hp.Reset();
			mp.Reset();
			return;
		}
		counter.Invalidate();
		if (!counter.TrySnapshot(processId, out Dictionary<string, int> bag)) return;
		Check(hp, settings.EnableQuickBuyHp, QuickBuyPotions.Hp, settings.QuickBuyHpPotionCode, settings.QuickBuyHpQuantity, bag, now, processId, gameWindow, log);
		Check(mp, settings.EnableQuickBuyMp, QuickBuyPotions.Mp, settings.QuickBuyMpPotionCode, settings.QuickBuyMpQuantity, bag, now, processId, gameWindow, log);
	}

	private void Check(PotionState state, bool enabled, IReadOnlyList<(string Name, int Code)> list, int code, int quantity, Dictionary<string, int> bag, DateTime now, int processId, IntPtr gameWindow, Action<string> log) {
		if (!enabled) {
			state.Reset();
			return;
		}
		if (!QuickBuyPotions.TryGetName(list, code, out string name)) {
			if (!state.UnknownLogged) log($"QUICK_BUY_UNKNOWN_POTION | PID={processId} | Kind={state.Kind} | Code={code}");
			state.UnknownLogged = true;
			return;
		}
		state.UnknownLogged = false;
		int count = CountPotion(bag, name);
		if (state.PendingSinceUtc != null) {
			if (count > 0) {
				log($"QUICK_BUY_OK | PID={processId} | Kind={state.Kind} | Potion={name} | Count={count} | ElapsedMs={(int)(now - state.PendingSinceUtc.Value).TotalMilliseconds}");
				state.PendingSinceUtc = null;
			} else if ((now - state.PendingSinceUtc.Value).TotalMilliseconds >= VerifyTimeoutMilliseconds) {
				log($"QUICK_BUY_NO_INCREASE | PID={processId} | Kind={state.Kind} | Potion={name} | Quantity={quantity} | Action=Nghỉ {NoIncreaseBackoff.TotalMinutes:0} phút (nhiều khả năng đã chạm giới hạn 1000 bình/ngày, hoặc hết tiền)");
				state.PendingSinceUtc = null;
				state.BlockedUntilUtc = now + NoIncreaseBackoff;
			}
			return;
		}
		if (count > 0 || now < state.BlockedUntilUtc) return;
		if (transport.TrySendQuickBuy(gameWindow, code, quantity, out string error)) {
			state.PendingSinceUtc = now;
			log($"QUICK_BUY_SENT | PID={processId} | Kind={state.Kind} | Potion={name} | Code={code} | Quantity={quantity}");
		} else {
			// Lệnh bị từ chối (cổng đóng, native cũ, client chưa vào game...): thử lại ở lần kiểm tra kế tiếp, log một lần cho mỗi lý do.
			if (!string.Equals(state.LastError, error, StringComparison.Ordinal)) log($"QUICK_BUY_SEND_FAILED | PID={processId} | Kind={state.Kind} | Potion={name} | {error}");
			state.LastError = error;
		}
	}

	// Cộng cả bản khoá: "Tiểu Hồng đơn (khóa)" dùng được y như bản thường nên không được coi là hết thuốc.
	internal static int CountPotion(Dictionary<string, int> bag, string name) {
		string key = InventoryPotionCounter.ToKey(name);
		int total = 0;
		foreach ((string entryKey, int count) in bag) {
			if (entryKey.Equals(key, StringComparison.Ordinal) || entryKey.StartsWith(key + " ", StringComparison.Ordinal)) total += count;
		}
		return total;
	}

	private sealed class PotionState(string kind) {
		public string Kind { get; } = kind;
		public DateTime? PendingSinceUtc { get; set; }
		public DateTime BlockedUntilUtc { get; set; } = DateTime.MinValue;
		public bool UnknownLogged { get; set; }
		public string LastError { get; set; } = "";

		public void Reset() {
			PendingSinceUtc = null;
			BlockedUntilUtc = DateTime.MinValue;
			LastError = "";
		}
	}
}
