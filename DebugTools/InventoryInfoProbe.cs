namespace Auto.DebugTools;

using System.Text;
using Auto.Utils;

// Liệt kê TÊN, SỐ LƯỢNG và TRỌNG LƯỢNG của từng vật phẩm trong cả 3 container: túi chính, ô trang bị nhanh, rương 2.
// Không in thêm gì khác.
//
// Ba trường đọc từ record vật phẩm, tất cả đã đối chiếu với giao diện game ngày 2026-09-07:
//   Tên       = +0x704 (Item.InventoryName), chuỗi tiếng Việt bảng mã cũ
//   Số lượng  = +0x754 (Item.Quantity) đọc theo UInt16 — đọc Int32 thì byte cao là rác
//   Trọng lượng = +0x6EC (Item.Weight), Int32, là trọng lượng MỘT đơn vị
// Chủ dự án xác nhận tổng trọng lượng của một ô = Số lượng × Trọng lượng, nên cột Tổng in sẵn tích đó.
//
// Offset danh sách ô của 3 container lấy từ InventoryContainer.SearchOrder (đã sửa lại 2026-09-07).
// Chỉ đọc bộ nhớ, không gửi lệnh nào, không ghi gì vào game.
public static class InventoryInfoProbe {
	private const string BuildStamp = "INVENTORY-INFO-20260907-01";
	private const int MaximumNameLength = 64;
	private const int MaximumTypeLength = 16;
	private const int MaximumPackedItemId = 0x001FFFFF;
	private const int NameColumnWidth = 26;

	public static string Run(int processId) {
		StringBuilder output = new();
		output.AppendLine("===== Túi đồ: tên / số lượng / trọng lượng =====");
		output.AppendLine($"INV_LIST_START | BuildStamp={BuildStamp} | Mode=READ_ONLY | GameMemoryWrite=NO | ProcessId={processId}");

		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return output.Append("INV_LIST_FAIL | Reason=MODULE_NOT_FOUND").ToString();

			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
			if (inventoryRoot == IntPtr.Zero) return output.Append("INV_LIST_FAIL | Reason=INVENTORY_ROOT_NULL").ToString();
			if (itemTable == IntPtr.Zero) return output.Append("INV_LIST_FAIL | Reason=ITEM_TABLE_NULL").ToString();
			IntPtr inventoryObject = IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object);

			List<string> summary = [];
			long grandTotalWeight = 0;

			foreach (InventoryContainer container in InventoryContainer.SearchOrder) {
				output.AppendLine($"--- C{container.Number} {container.DisplayName} | Offset=+0x{container.ListPointerOffset:X4} | {container.SlotCount} ô ---");
				IntPtr slotList = reader.ReadPointer32(IntPtr.Add(inventoryObject, container.ListPointerOffset));
				if (slotList == IntPtr.Zero) {
					output.AppendLine("    SLOT_LIST_NULL");
					summary.Add($"C{container.Number}=NULL");
					continue;
				}
				byte[] idBytes = reader.ReadBytes(slotList, container.SlotCount * sizeof(int));
				if (idBytes.Length != container.SlotCount * sizeof(int)) {
					output.AppendLine("    SLOT_LIST_READ_FAILED");
					summary.Add($"C{container.Number}=READ_FAILED");
					continue;
				}

				int occupied = 0;
				long containerWeight = 0;
				for (int index = 0; index < container.SlotCount; index++) {
					try {
						int itemId = BitConverter.ToInt32(idBytes, index * sizeof(int));
						if (itemId <= 0 || itemId > MaximumPackedItemId) continue;
						long record = checked(itemTable.ToInt64() + checked((long)itemId * GameAddresses.Item.InventoryRecordStride));
						if (record <= 0 || record > uint.MaxValue) continue;
						// Item thật luôn có Type dạng "medecine\...", "equip\...", "weapen\..."; đòi dấu '\' để loại id rác.
						if (! ReadAsciiString(reader, new IntPtr(record + GameAddresses.Item.InventoryType), MaximumTypeLength).Contains('\\')) continue;

						string name = ReadLegacyString(reader, new IntPtr(record + GameAddresses.Item.InventoryName), MaximumNameLength);
						int quantity = reader.ReadUInt16(new IntPtr(record + GameAddresses.Item.Quantity));
						int weight = reader.ReadInt32(new IntPtr(record + GameAddresses.Item.Weight));
						long totalWeight = (long)quantity * weight;
						occupied++;
						containerWeight += totalWeight;
						output.AppendLine($"    #{index:D2} | {name.PadRight(NameColumnWidth)} | SL={quantity,4} | TL={weight,4} | Tổng={totalWeight,6}");
					} catch (Exception ex) {
						output.AppendLine($"    #{index:D2} | READ_FAIL | {ex.GetType().Name}");
					}
				}
				if (occupied == 0) output.AppendLine("    (trống)");
				output.AppendLine($"    Cộng: {occupied}/{container.SlotCount} ô | Trọng lượng={containerWeight}");
				summary.Add($"C{container.Number}={occupied}/{container.SlotCount}");
				grandTotalWeight += containerWeight;
			}

			output.AppendLine($"INV_LIST_END | {string.Join(" | ", summary)} | TotalWeight={grandTotalWeight}");
			output.AppendLine("SL=số lượng | TL=trọng lượng một đơn vị | Tổng=SL×TL");
			return output.ToString();
		} catch (Exception ex) {
			output.AppendLine($"INV_LIST_FAIL | {ex.GetType().Name}: {ex.Message}");
			return output.ToString();
		}
	}

	private static string ReadLegacyString(MemoryReader reader, IntPtr address, int maximumLength) {
		byte[] bytes = reader.ReadBytes(address, maximumLength);
		int length = Array.IndexOf(bytes, (byte)0);
		if (length >= 0) bytes = bytes[..length];
		return LegacyVietnameseText.Decode(bytes).Trim();
	}

	private static string ReadAsciiString(MemoryReader reader, IntPtr address, int maximumLength) {
		byte[] bytes = reader.ReadBytes(address, maximumLength);
		int length = Array.IndexOf(bytes, (byte)0);
		if (length >= 0) bytes = bytes[..length];
		return Encoding.ASCII.GetString(bytes);
	}
}
