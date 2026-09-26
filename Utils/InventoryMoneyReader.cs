namespace Auto.Utils;

// Đọc số tiền trong túi đồ (đơn vị xu). Dùng để HIỂN THỊ trên dòng account, không dùng để quyết định gì.
// Đường đọc và bằng chứng dò ra: xem GameAddresses.Inventory.LoginMoney.
public static class InventoryMoneyReader {
	// 1 vạn = 10.000 xu; dòng account hiển thị theo vạn.
	public const int CoinsPerTenThousand = 10_000;

	// Trả số xu, hoặc -1 khi chưa đọc được (chưa vào game, đọc hỏng).
	// Globals.StrengthRoot + Inventory.Money is no longer used (owner decision 2026-09-27): it is 0 until the bag/shop UI
	// opens, then frozen until it opens again, missing quick-buy and repair costs (TiểuHồngĐơn read 244.405 there for hours
	// while LoginMoney moved to 235.596).
	public static int Read(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return -1;
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			// Con trỏ bằng 0 là chuyện bình thường lúc chưa vào game hẳn.
			if (inventoryRoot == IntPtr.Zero) return -1;
			int coins = reader.ReadInt32(IntPtr.Add(inventoryRoot, GameAddresses.Inventory.LoginMoney));
			return coins < 0 ? -1 : coins;
		} catch {
			return -1;
		}
	}
}
