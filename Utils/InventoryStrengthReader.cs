namespace Auto.Utils;

// Đọc sức lực mang đồ (ô "x/y" trong túi). Dùng cho điều kiện "Sức lực còn < ngưỡng" của luồng đi bán
// (Sale/InventorySaleEngine.ReadAutomaticTrigger).
//
// Trước 2026-09-23 hàm này là STUB: trả Fail vô điều kiện, không đọc byte nào. Hệ quả là nửa điều kiện đi bán
// chết hẳn — chỉ còn nhánh "số ô > ngưỡng" chạy được, và log REPAIR_SALE_TRIGGER luôn ghi
// RemainingStrength=UNAVAILABLE. Bản DEV cũng là stub y hệt nên đây không phải thụt lùi, mà là chưa từng làm.
//
// Đường đọc và bằng chứng dò ra: xem GameAddresses.Inventory.LoginMaximumStrength.
public static class InventoryStrengthReader {
	private const int MaximumPackedItemId = 0x001FFFFF;
	private const int MaximumTypeLength = 16;

	// The number Auto shows and DECIDES on (sale, stop looting): maximum from Inventory.LoginMaximumStrength, current =
	// carried weight. Globals.StrengthRoot is no longer used here (owner decision 2026-09-27): it stays 0 after login until
	// the bag/shop UI opens, and once set it is a snapshot that stays frozen until the UI opens again (2026-09-27: on 3
	// accounts still training, StrengthRoot read 115/335, 93/515, 221/343 for hours while carried weight rose to 213, 104,
	// 277). The login-time path matched the in-game bag exactly on 3 freshly logged accounts (owner confirmed 2026-09-27).
	public static InventoryStrengthReading Read(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return InventoryStrengthReading.Fail("Game.exe không tồn tại.");
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			if (inventoryRoot == IntPtr.Zero) return InventoryStrengthReading.Fail("Con trỏ túi đồ bằng 0 (chưa vào game?).");
			int maximum = reader.ReadInt32(IntPtr.Add(inventoryRoot, GameAddresses.Inventory.LoginMaximumStrength));
			if (maximum <= 0) return InventoryStrengthReading.Fail($"Sức lực tối đa không hợp lệ: {maximum}.");
			if (! TryReadCarriedWeight(reader, moduleBase, out long carriedWeight)) return InventoryStrengthReading.Fail("Không đọc được trọng lượng túi đồ.");
			return new InventoryStrengthReading(true, (int)carriedWeight, maximum, inventoryRoot, IntPtr.Zero, 0, 0, 0, 0, 0, 0, 0, "");
		} catch (Exception ex) {
			return InventoryStrengthReading.Fail($"{ex.GetType().Name}: {ex.Message}");
		}
	}

	// Số GỐC trong ô nhớ StrengthRoot, không bù. Chỉ còn probe dùng để đối chiếu.
	public static InventoryStrengthReading ReadRaw(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return InventoryStrengthReading.Fail("Game.exe không tồn tại.");
			IntPtr root = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.StrengthRoot));
			// Con trỏ bằng 0 cho tới khi mở túi đồ / cửa hàng lần đầu sau login.
			if (root == IntPtr.Zero) return InventoryStrengthReading.Fail("Con trỏ sức lực bằng 0 (chưa mở túi đồ?).");
			int current = reader.ReadInt32(IntPtr.Add(root, GameAddresses.Inventory.CurrentStrength));
			int maximum = reader.ReadInt32(IntPtr.Add(root, GameAddresses.Inventory.MaximumStrength));
			// current > maximum KHÔNG phải cặp đọc hỏng — đó chính là trạng thái quá tải thật của game (chủ dự án
			// xác nhận 2026-09-24: hiển thị "244/555", vượt quá 555 thì nhân vật không di chuyển được). Trước đây
			// nhánh này coi current > maximum là vô lý và trả Fail, khiến Free luôn null đúng lúc cần bán gấp nhất
			// (đối chiếu repair.log: RemainingStrength=UNAVAILABLE xuất hiện đúng lúc nhân vật đứng im không rõ lý do).
			// Chỉ còn chặn cặp thật sự vô lý: maximum <= 0 hoặc current < 0.
			if (maximum <= 0 || current < 0) return InventoryStrengthReading.Fail($"Cặp sức lực không hợp lệ: {current}/{maximum}.");
			// Ô StrengthRoot+0x27C KHÔNG cập nhật theo từng lần nhặt — game dồn lại rồi cộng một cục sau đó vài giây
			// tới gần 2 phút. Đo trên repair.log PID=22612 ngày 2026-09-24: Free giữ nguyên 57 qua 7 lần nhặt liên tiếp
			// rồi tụt thẳng xuống -15; giữ nguyên 186 qua 8 lần nhặt rồi tụt xuống 88.
			return new InventoryStrengthReading(true, current, maximum, root, IntPtr.Zero, 0, 0, 0, 0, 0, 0, 0, "");
		} catch (Exception ex) {
			return InventoryStrengthReading.Fail($"{ex.GetType().Name}: {ex.Message}");
		}
	}

	// Cùng đường đọc với DebugTools/InventoryInfoProbe (Item.Weight đã đối chiếu game, chủ dự án xác nhận 2026-09-24).
	private static bool TryReadCarriedWeight(MemoryReader reader, IntPtr moduleBase, out long weight) {
		weight = 0;
		try {
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
			if (inventoryRoot == IntPtr.Zero || itemTable == IntPtr.Zero) return false;
			IntPtr inventoryObject = IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object);
			foreach (InventoryContainer container in InventoryContainer.SearchOrder) {
				IntPtr slotList = reader.ReadPointer32(IntPtr.Add(inventoryObject, container.ListPointerOffset));
				if (slotList == IntPtr.Zero) return false;
				byte[] idBytes = reader.ReadBytes(slotList, container.SlotCount * sizeof(int));
				if (idBytes.Length != container.SlotCount * sizeof(int)) return false;
				for (int index = 0; index < container.SlotCount; index++) {
					int itemId = BitConverter.ToInt32(idBytes, index * sizeof(int));
					if (itemId <= 0 || itemId > MaximumPackedItemId) continue;
					IntPtr record = new(itemTable.ToInt64() + (long)itemId * GameAddresses.Item.InventoryRecordStride);
					// Item thật luôn có Type dạng "equip\...", "medecine\..."; đòi dấu '\' để loại id rác.
					if (! HasPathSeparator(reader.ReadBytes(IntPtr.Add(record, GameAddresses.Item.InventoryType), MaximumTypeLength))) continue;
					weight += (long)reader.ReadUInt16(IntPtr.Add(record, GameAddresses.Item.Quantity)) * reader.ReadInt32(IntPtr.Add(record, GameAddresses.Item.Weight));
				}
			}
			return true;
		} catch {
			return false;
		}
	}

	private static bool HasPathSeparator(byte[] bytes) {
		foreach (byte value in bytes) {
			if (value == 0) return false;
			if (value == (byte)'\\') return true;
		}
		return false;
	}
}

public sealed record InventoryStrengthReading(bool Success, int Current, int Maximum, IntPtr InventoryRoot, IntPtr InventoryObject, int First, int Second, int Third, int ItemIndex, int ItemWeight, int ItemQuantity, int ItemContribution, string FailureReason) {
	public int Free => Maximum - Current;
	public static InventoryStrengthReading Fail(string reason) => new(false, 0, 0, IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 0, 0, 0, 0, reason);
}
