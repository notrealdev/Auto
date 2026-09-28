namespace Auto.Utils;

using Auto.Loot;

// Finds an item by name in the NPC shop that is currently open, and returns its position in that shop row: the
// position is what the buy packet {0xA3, position, count} carries (native command 330).
//
// The shop id is read at run time (Inventory.OpenShopId) instead of hard-coding one, because each Đại Phu can use a
// different row of the catalog (Globals.ShopCatalog).
public static class NpcShopReader {
	private const int MaximumNameLength = 64;
	private const int MaximumRows = 1024;
	private const int MaximumPositions = 256;
	// ShopState value while an NPC shop is open on screen; InventorySaleEngine requires the same value.
	public const uint ShopOpenState = 2;

	public static bool IsShopOpen(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			return moduleBase != IntPtr.Zero && unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ShopState))) == ShopOpenState;
		} catch {
			return false;
		}
	}

	// Name comparison uses the same key as the potion counters (InventoryPotionCounter.ToKey), so "Trung Hồng đơn"
	// matches the TCVN3 name stored in the item record.
	public static bool TryFindItem(int processId, string itemName, out int position, out int unitWeight, out string detail) {
		position = -1;
		unitWeight = 0;
		detail = "";
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return Fail("Game.exe không tồn tại.", out detail);
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			if (inventoryRoot == IntPtr.Zero) return Fail("InventoryRoot bằng 0.", out detail);
			int shopId = reader.ReadInt32(IntPtr.Add(inventoryRoot, GameAddresses.Inventory.OpenShopId));
			IntPtr catalog = IntPtr.Add(moduleBase, GameAddresses.Globals.ShopCatalog);
			IntPtr rows = reader.ReadPointer32(catalog);
			IntPtr records = reader.ReadPointer32(IntPtr.Add(catalog, 4));
			int positionsPerRow = reader.ReadInt32(IntPtr.Add(catalog, 8));
			int rowCount = reader.ReadInt32(IntPtr.Add(catalog, 0xC));
			int recordCount = reader.ReadInt32(IntPtr.Add(catalog, 0x10));
			if (rows == IntPtr.Zero || records == IntPtr.Zero || rowCount <= 0 || rowCount > MaximumRows || positionsPerRow <= 0 || positionsPerRow > MaximumPositions) {
				return Fail($"Bảng cửa hàng không hợp lệ | Rows={rowCount} | PerRow={positionsPerRow}.", out detail);
			}
			if (shopId < 0 || shopId >= rowCount) return Fail($"Chưa mở cửa hàng nào | ShopId={shopId}.", out detail);
			IntPtr row = reader.ReadPointer32(IntPtr.Add(rows, shopId * sizeof(int)));
			if (row == IntPtr.Zero) return Fail($"Cửa hàng {shopId} rỗng.", out detail);
			string key = InventoryPotionCounter.ToKey(itemName);
			List<string> seen = [];
			for (int index = 0; index < positionsPerRow; index++) {
				int recordIndex = reader.ReadInt32(IntPtr.Add(row, index * sizeof(int)));
				if (recordIndex < 0 || recordIndex >= recordCount) continue;
				IntPtr record = new(records.ToInt64() + (long)recordIndex * GameAddresses.Item.InventoryRecordStride);
				string name = InventoryContainer.ReadLegacyString(reader, IntPtr.Add(record, GameAddresses.Item.InventoryName), MaximumNameLength);
				if (name.Length == 0) continue;
				seen.Add($"{index}:{name}");
				if (!InventoryPotionCounter.ToKey(name).Equals(key, StringComparison.Ordinal)) continue;
				position = index;
				unitWeight = reader.ReadInt32(IntPtr.Add(record, GameAddresses.Item.Weight));
				detail = $"ShopId={shopId} | Position={index} | Name={name} | UnitWeight={unitWeight}";
				return true;
			}
			return Fail($"Cửa hàng {shopId} không bán '{itemName}' | Có=[{string.Join("; ", seen)}]", out detail);
		} catch (Exception ex) {
			return Fail($"{ex.GetType().Name}: {ex.Message}", out detail);
		}
	}

	private static bool Fail(string reason, out string detail) {
		detail = reason;
		return false;
	}
}
