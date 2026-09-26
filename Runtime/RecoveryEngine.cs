namespace Auto.Runtime;

using Auto.Attack;
using Auto.Loot;
using Auto.Utils;

// Khối "Hồi phục" (tab Cơ bản, dựa luồng THồiPhục của AutoFS): HP/MP dưới ngưỡng thì dùng thuốc đã chọn ở khối mua nhanh.
//
// AutoFS (FormLoader.SaveDevice, đọc IL): mỗi 500 ms, HP < Max*PhầnTrăm/100 thì WindowQueue.NavigateSelection(tên thuốc) dùng
// thuốc theo tên; không có thuốc thì mới mua nhanh. Auto khác hai chỗ:
//   - Ngưỡng tính theo ĐIỂM, không theo phần trăm (chủ dự án chốt 2026-09-26).
//   - Dùng thuốc bằng hàm dùng vật phẩm của client (lệnh native 329), vì đường 4 lệnh 0/1/2/10 của AutoFS gọi hàm client cũ
//     không còn tồn tại. Hàm đó dùng được thuốc ở cả ô nhanh, túi chính và rương 2.
// Hết thuốc thì QuickBuyEngine lo mua; engine này chỉ ghi log một lần cho mỗi đợt hết.
internal sealed class RecoveryEngine {
	// Khớp Stopwatch 500 ms giữa hai lần dùng cùng một loại của AutoFS.
	private const int UseIntervalMilliseconds = 500;
	private const int MaximumNameLength = 64;
	private const int MaximumItemId = 0x001FFFFF;

	private readonly BasicSettings settings;
	private readonly AutoFsAttackTransport transport;
	private readonly KindState hp = new("HP");
	private readonly KindState mp = new("MP");

	public RecoveryEngine(BasicSettings settings, AutoFsAttackTransport transport) {
		this.settings = settings;
		this.transport = transport;
	}

	public void Tick(int processId, IntPtr gameWindow, GameSnapshot snapshot, Action<string> log) {
		// HP <= 0 là nhân vật đã chết: dùng thuốc vô ích, để luồng Về thành xử lý.
		if (snapshot.Hp <= 0 || snapshot.MaxHp <= 0) return;
		DateTime now = DateTime.UtcNow;
		Check(hp, settings.EnableRecoverHp, snapshot.Hp, snapshot.MaxHp, settings.RecoverHpThreshold, QuickBuyPotions.Hp, settings.QuickBuyHpPotionCode, now, processId, gameWindow, log);
		Check(mp, settings.EnableRecoverMp, snapshot.Mp, snapshot.MaxMp, settings.RecoverMpThreshold, QuickBuyPotions.Mp, settings.QuickBuyMpPotionCode, now, processId, gameWindow, log);
	}

	private void Check(KindState state, bool enabled, int value, int maximum, int threshold, IReadOnlyList<(string Name, int Code)> list, int code, DateTime now, int processId, IntPtr gameWindow, Action<string> log) {
		if (!enabled || value < 0 || maximum <= 0 || value >= threshold || now < state.NextUseUtc) return;
		if (!QuickBuyPotions.TryGetName(list, code, out string name)) return;
		state.NextUseUtc = now.AddMilliseconds(UseIntervalMilliseconds);
		if (!TryFindPotion(processId, name, out int itemId, out string location)) {
			if (!state.NoPotionLogged) log($"RECOVERY_NO_POTION | PID={processId} | Kind={state.Kind} | Potion={name} | Value={value}/{maximum} | Threshold={threshold} | Action=Chờ mua nhanh");
			state.NoPotionLogged = true;
			return;
		}
		state.NoPotionLogged = false;
		if (transport.TryUseItemById(gameWindow, itemId, out string error)) {
			state.LastError = "";
			log($"RECOVERY_USE | PID={processId} | Kind={state.Kind} | Potion={name} | ItemId={itemId} | Location={location} | Value={value}/{maximum} | Threshold={threshold}");
		} else {
			if (!string.Equals(state.LastError, error, StringComparison.Ordinal)) log($"RECOVERY_USE_FAILED | PID={processId} | Kind={state.Kind} | Potion={name} | ItemId={itemId} | {error}");
			state.LastError = error;
		}
	}

	// Tìm ô chứa thuốc theo thứ tự quét container của AutoFS (ô nhanh, túi chính, rương 2). Tính cả bản "(khóa)" như QuickBuyEngine.
	private static bool TryFindPotion(int processId, string potionName, out int itemId, out string location) {
		itemId = 0;
		location = "";
		string key = InventoryPotionCounter.ToKey(potionName);
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return false;
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
			if (inventoryRoot == IntPtr.Zero || itemTable == IntPtr.Zero) return false;
			IntPtr inventoryObject = IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object);
			foreach (InventoryContainer container in InventoryContainer.SearchOrder) {
				IntPtr slotList = reader.ReadPointer32(IntPtr.Add(inventoryObject, container.ListPointerOffset));
				if (slotList == IntPtr.Zero) continue;
				byte[] idBytes = reader.ReadBytes(slotList, container.SlotCount * sizeof(int));
				if (idBytes.Length != container.SlotCount * sizeof(int)) continue;
				for (int index = 0; index < container.SlotCount; index++) {
					int candidate = BitConverter.ToInt32(idBytes, index * sizeof(int));
					if (candidate <= 0 || candidate > MaximumItemId) continue;
					long nameAddress = itemTable.ToInt64() + (long)candidate * GameAddresses.Item.InventoryRecordStride + GameAddresses.Item.InventoryName;
					if (nameAddress <= 0 || nameAddress > uint.MaxValue) continue;
					string entryKey = InventoryPotionCounter.ToKey(InventoryContainer.ReadLegacyString(reader, new IntPtr(nameAddress), MaximumNameLength));
					if (!entryKey.Equals(key, StringComparison.Ordinal) && !entryKey.StartsWith(key + " ", StringComparison.Ordinal)) continue;
					itemId = candidate;
					location = $"{container.DisplayName}#{index}";
					return true;
				}
			}
			return false;
		} catch {
			return false;
		}
	}

	private sealed class KindState(string kind) {
		public string Kind { get; } = kind;
		public DateTime NextUseUtc { get; set; } = DateTime.MinValue;
		public bool NoPotionLogged { get; set; }
		public string LastError { get; set; } = "";
	}
}
