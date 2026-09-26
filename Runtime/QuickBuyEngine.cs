namespace Auto.Runtime;

using Auto.Attack;
using Auto.Loot;
using Auto.Utils;

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
// Xác nhận đã mua: thuốc xuất hiện trong túi HOẶC bộ đếm bình-mua-nhanh-trong-ngày của client tăng lên. Chỉ xét thuốc là
// sai: PID=20260 18:43:41 và 18:43:57 (2026-09-25) bị ghi QUICK_BUY_NO_INCREASE sau 6 giây rồi khoá 10 phút, trong khi
// client đang giật (CLIENT_STATIONARY 40 giây cùng lúc) và sau đó túi có đúng 1 Trung Hồng + 1 Tiểu Hoàn. Đo trực tiếp
// cùng ngày trên PID 20260 cũng thấy thuốc mua về biến mất ngay (MP tăng từng 50, số Trung Hoàn giảm) nên số thuốc trong túi
// không phản ánh được việc mua. Bộ đếm ngày của client (InventoryRoot+0x39CCC) là cách xác nhận độc lập với việc thuốc bị dùng.
//
// Không tự đếm/chặn theo giới hạn 1000 bình/ngày (chủ dự án chốt 2026-09-25: đếm rất thiếu chính xác): cứ hết thuốc là gửi lệnh,
// server từ chối thì thôi. Bộ đếm ngày chỉ dùng làm bằng chứng xác nhận đã mua.
internal sealed class QuickBuyEngine {
	private const int CheckIntervalMilliseconds = 2000;
	// Client giật thì phản hồi mua có thể tới trễ hơn 6 giây (PID=20260 18:43).
	private const int VerifyTimeoutMilliseconds = 15000;
	private static readonly TimeSpan NoIncreaseBackoff = TimeSpan.FromSeconds(30);

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
		int? dailyCount = TryReadDailyCount(processId);
		// Không gửi loại này khi loại kia còn chờ xác nhận, để bộ đếm ngày tăng thì biết chắc là của lệnh nào.
		bool anyPending = hp.PendingSinceUtc != null || mp.PendingSinceUtc != null;
		Check(hp, settings.EnableQuickBuyHp, QuickBuyPotions.Hp, settings.QuickBuyHpPotionCode, settings.QuickBuyHpQuantity, bag, dailyCount, anyPending, now, processId, gameWindow, log);
		anyPending = hp.PendingSinceUtc != null || mp.PendingSinceUtc != null;
		Check(mp, settings.EnableQuickBuyMp, QuickBuyPotions.Mp, settings.QuickBuyMpPotionCode, settings.QuickBuyMpQuantity, bag, dailyCount, anyPending, now, processId, gameWindow, log);
	}

	private void Check(PotionState state, bool enabled, IReadOnlyList<(string Name, int Code)> list, int code, int quantity, Dictionary<string, int> bag, int? dailyCount, bool anyPending, DateTime now, int processId, IntPtr gameWindow, Action<string> log) {
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
		string dailyText = dailyCount?.ToString() ?? "?";
		if (state.PendingSinceUtc != null) {
			bool counterRose = dailyCount != null && state.DailyCountAtSend != null && dailyCount.Value > state.DailyCountAtSend.Value;
			if (count > 0 || counterRose) {
				log($"QUICK_BUY_OK | PID={processId} | Kind={state.Kind} | Potion={name} | Count={count} | DailyCount={dailyText} | Evidence={(count > 0 ? "BAG_COUNT" : "DAILY_COUNTER")} | ElapsedMs={(int)(now - state.PendingSinceUtc.Value).TotalMilliseconds}");
				state.PendingSinceUtc = null;
			} else if ((now - state.PendingSinceUtc.Value).TotalMilliseconds >= VerifyTimeoutMilliseconds) {
				log($"QUICK_BUY_NO_INCREASE | PID={processId} | Kind={state.Kind} | Potion={name} | Quantity={quantity} | DailyCount={dailyText} | Action=Nghỉ {NoIncreaseBackoff.TotalSeconds:0} giây rồi thử lại (túi và bộ đếm ngày đều không tăng)");
				state.PendingSinceUtc = null;
				state.BlockedUntilUtc = now + NoIncreaseBackoff;
			}
			return;
		}
		if (count > 0 || now < state.BlockedUntilUtc || anyPending) return;
		if (!TryFindFreeMainBagSlot(processId, out int slotX, out int slotY)) {
			if (!string.Equals(state.LastError, "NO_FREE_SLOT", StringComparison.Ordinal)) log($"QUICK_BUY_NO_FREE_SLOT | PID={processId} | Kind={state.Kind} | Potion={name} | Action=Túi chính không còn ô trống, không gửi lệnh mua");
			state.LastError = "NO_FREE_SLOT";
			return;
		}
		if (transport.TrySendQuickBuy(gameWindow, code, quantity, GameAddresses.Inventory.MainBagRoom, slotX, slotY, out string error)) {
			state.PendingSinceUtc = now;
			state.DailyCountAtSend = dailyCount;
			state.LastError = "";
			log($"QUICK_BUY_SENT | PID={processId} | Kind={state.Kind} | Potion={name} | Code={code} | Quantity={quantity} | Slot=({slotX},{slotY}) | DailyCount={dailyText}");
		} else {
			// Lệnh bị từ chối (cổng đóng, native cũ, client chưa vào game...): thử lại ở lần kiểm tra kế tiếp, log một lần cho mỗi lý do.
			if (!string.Equals(state.LastError, error, StringComparison.Ordinal)) log($"QUICK_BUY_SEND_FAILED | PID={processId} | Kind={state.Kind} | Potion={name} | {error}");
			state.LastError = error;
		}
	}

	// Cờ chặn của hàm mua client: hàm thoát ngay khi [InventoryRoot+0x4B79C] != 0 (đọc từ disassembly 0x746082). Ý nghĩa cờ chưa biết.
	internal static int? TryReadBlockFlag(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return null;
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			if (inventoryRoot == IntPtr.Zero) return null;
			return reader.ReadInt32(IntPtr.Add(inventoryRoot, 0x4B79C));
		} catch {
			return null;
		}
	}

	// Đọc bộ đếm bình mua nhanh trong ngày của client. Null khi không đọc được: khi đó chỉ còn bằng chứng là số thuốc trong túi.
	internal static int? TryReadDailyCount(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return null;
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			if (inventoryRoot == IntPtr.Zero) return null;
			return reader.ReadUInt16(IntPtr.Add(inventoryRoot, GameAddresses.Inventory.QuickBuyDailyCount));
		} catch {
			return null;
		}
	}

	// Ô trống đầu tiên của túi chính, làm ô đích cho thuốc mua về. Gói mua mang 3 byte (room, x, y) của ô đích: game tự mua luôn
	// điền thuốc vào đúng ô vừa trống, còn gửi 0,0,0 thì server trừ tiền mà thuốc không xuất hiện ở ô nào (PID 21740, 2026-09-26).
	internal static bool TryFindFreeMainBagSlot(int processId, out int slotX, out int slotY) {
		slotX = 0;
		slotY = 0;
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return false;
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			if (inventoryRoot == IntPtr.Zero) return false;
			IntPtr cells = reader.ReadPointer32(IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object + GameAddresses.Inventory.SaleSlotListPointer));
			if (cells == IntPtr.Zero) return false;
			for (int cell = 0; cell < GameAddresses.Inventory.SaleSlotCount; cell++) {
				if (reader.ReadInt32(IntPtr.Add(cells, cell * 4)) > 0) continue;
				slotX = cell % GameAddresses.Inventory.MainBagColumns;
				slotY = cell / GameAddresses.Inventory.MainBagColumns;
				return true;
			}
			return false;
		} catch {
			return false;
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
		public int? DailyCountAtSend { get; set; }
		public DateTime BlockedUntilUtc { get; set; } = DateTime.MinValue;
		public bool UnknownLogged { get; set; }
		public string LastError { get; set; } = "";

		public void Reset() {
			PendingSinceUtc = null;
			DailyCountAtSend = null;
			BlockedUntilUtc = DateTime.MinValue;
			LastError = "";
		}
	}
}
