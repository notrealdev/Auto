namespace Auto.Utils;

// Bố cục 3 container túi của client theo đúng thứ tự quét của AutoFS (11, 3, 16).
// Tách ra khỏi LowHpReturnTalismanEngine để luồng đếm dược phẩm dùng lại đúng một bản mô tả layout,
// tránh hai nơi cùng khai báo offset rồi lệch nhau khi client đổi.
public sealed record InventoryContainer(int Number, string DisplayName, int ListPointerOffset, int SlotCount) {
	public static InventoryContainer[] SearchOrder { get; } = [
		new(11, "Ô trang bị nhanh", GameAddresses.Inventory.QuickSlotListPointer, GameAddresses.Inventory.QuickSlotCount),
		new(3, "Túi chính", GameAddresses.Inventory.SaleSlotListPointer, GameAddresses.Inventory.SaleSlotCount),
		new(16, "Rương 2", GameAddresses.Inventory.ExtendedSlotListPointer, GameAddresses.Inventory.ExtendedSlotCount)
	];

	public static string ReadLegacyString(MemoryReader reader, IntPtr address, int maximumLength) {
		byte[] bytes = reader.ReadBytes(address, maximumLength);
		int length = Array.IndexOf(bytes, (byte)0);
		if (length >= 0) bytes = bytes[..length];
		return LegacyVietnameseText.Decode(bytes).Trim();
	}
}
