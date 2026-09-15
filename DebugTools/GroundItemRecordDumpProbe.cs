namespace Auto.DebugTools;

using System.Text;
using Auto.Utils;

// Đổ NGUYÊN record của từng item đang nằm dưới đất, để tìm chỗ client giữ SỐ THUỘC TÍNH của item.
//
// CHỈ ĐỌC bộ nhớ, không gửi lệnh nào vào game, không nhặt gì.
//
// Vì sao cần: đường nhặt đồ hiện chỉ bóc 2 con số từ record (Loot/ItemGroup.cs) —
//   màu  = QualityCodeA & 0xFF   (0 trắng, 1 xanh lam, 2 xanh lục, 3 vàng, 4 cam)
//   nhóm = (QualityCodeB & 0xFF) >> 4   (1 dược phẩm, 3 mảnh/ngọc)
// Nghĩa là 4 bit thấp của QualityCodeB và toàn bộ 24 bit cao của CẢ HAI trường chưa ai nhìn vào, và 708 byte
// cuối của record (stride 0x3A4, offset đã ánh xạ cao nhất là 0xDC) cũng chưa ai nhìn vào.
//
// Cách dùng: để vài món dưới đất, trong đó có ít nhất HAI món xanh lam cùng tên nhưng KHÁC số dòng thuộc tính,
// rồi bấm chạy. So hai khối "DWORD KHÁC 0" của hai món đó: offset nào lệch đúng theo số thuộc tính là chỗ cần tìm.
internal static class GroundItemRecordDumpProbe {
	private const string BuildStamp = "GROUND-ITEM-RECORD-DUMP-20260911-01";
	private const int FirstIndex = 1;
	private const int LastIndex = 127;
	private const int MaximumNameLength = 64;
	private const int LiveGroundKind = 3;
	// Đủ để so vài món, không làm ngập ô kết quả.
	private const int MaximumDumpedItems = 6;
	private const int DwordsPerLine = 6;
	// Vùng chữ tên nằm ở 0x7C..0x9F, đổ hex ra chỉ thành rác. Bỏ qua, tên đã in ở dòng đầu.
	private const int NameRegionStart = 0x7C;
	private const int NameRegionEnd = 0xA0;

	public static string Run(int processId) {
		StringBuilder output = new();
		output.AppendLine("===== Record item dưới đất (tìm số thuộc tính) =====");
		output.AppendLine($"GROUND_ITEM_RECORD_DUMP | BuildStamp={BuildStamp} | Mode=READ_ONLY | ProcessId={processId}");
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return output.AppendLine("DỪNG | Không thấy module Game.exe.").ToString();
			IntPtr table = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Item.GroundRecordTablePointer));
			if (table == IntPtr.Zero) return output.AppendLine($"DỪNG | Con trỏ bảng item dưới đất = 0 tại 0x{IntPtr.Add(moduleBase, GameAddresses.Item.GroundRecordTablePointer).ToInt64():X8}").ToString();

			int stride = GameAddresses.Item.GroundRecordStride;
			int tableSize = (LastIndex + 1) * stride;
			byte[] records = reader.ReadBytes(table, tableSize);
			if (records.Length != tableSize) return output.AppendLine($"DỪNG | Đọc bảng được {records.Length}/{tableSize} byte tại 0x{table.ToInt64():X8}").ToString();
			output.AppendLine($"BẢNG | Địa chỉ=0x{table.ToInt64():X8} | Stride=0x{stride:X} ({stride} byte) | Quét slot {FirstIndex}..{LastIndex}");

			int live = 0;
			int dumped = 0;
			for (int index = FirstIndex; index <= LastIndex; index++) {
				int recordOffset = index * stride;
				int groundId = ReadInt32(records, recordOffset + GameAddresses.Item.GroundRecordId);
				int groundKind = ReadInt32(records, recordOffset + GameAddresses.Item.GroundRecordKind);
				if (groundId <= 0 || groundKind != LiveGroundKind) continue;
				live++;
				if (dumped >= MaximumDumpedItems) continue;
				dumped++;

				string name = ReadName(records, recordOffset + GameAddresses.Item.GroundName);
				int qualityA = ReadInt32(records, recordOffset + GameAddresses.Item.GroundQualityCodeA);
				int qualityB = ReadInt32(records, recordOffset + GameAddresses.Item.GroundQualityCodeB);
				output.AppendLine();
				output.AppendLine($"--- Slot {index} | Record=0x{IntPtr.Add(table, recordOffset).ToInt64():X8} | Tên={name}");
				output.AppendLine($"    QualityA(0x{GameAddresses.Item.GroundQualityCodeA:X2})=0x{qualityA:X8} | Màu(byte thấp)={DescribeColor(qualityA & 0xFF)} | BitCao=0x{(qualityA >> 8) & 0xFFFFFF:X6}");
				output.AppendLine($"    QualityB(0x{GameAddresses.Item.GroundQualityCodeB:X2})=0x{qualityB:X8} | Nhóm(>>4)={(qualityB & 0xFF) >> 4} | NIBBLE THẤP CHƯA DÙNG={qualityB & 0x0F} | BitCao=0x{(qualityB >> 8) & 0xFFFFFF:X6}");
				output.AppendLine("    DWORD KHÁC 0 trong cả record (bỏ vùng chữ tên 0x7C..0x9F):");
				output.Append(DumpNonZeroDwords(records, recordOffset, stride));
			}

			output.AppendLine();
			output.AppendLine($"TỔNG | {live} item đang nằm dưới đất | đổ chi tiết {dumped} item đầu (trần {MaximumDumpedItems}).");
			if (live == 0) output.AppendLine("     => Không có item nào dưới đất. Đánh rơi vài món rồi bấm lại.");
			else output.AppendLine("     => So khối DWORD của hai món xanh lam khác số thuộc tính: offset nào lệch theo đúng số đó là chỗ cần tìm.");
		} catch (Exception ex) {
			output.AppendLine($"LỖI | {ex.GetType().Name}: {ex.Message}");
		}
		return output.ToString();
	}

	private static string DumpNonZeroDwords(byte[] records, int recordOffset, int stride) {
		StringBuilder block = new();
		List<string> cells = [];
		for (int offset = 0; offset + 4 <= stride; offset += 4) {
			if (offset >= NameRegionStart && offset < NameRegionEnd) continue;
			int value = ReadInt32(records, recordOffset + offset);
			if (value == 0) continue;
			cells.Add($"+0x{offset:X3}=0x{value:X8}");
			if (cells.Count < DwordsPerLine) continue;
			block.AppendLine("      " + string.Join("  ", cells));
			cells.Clear();
		}
		if (cells.Count > 0) block.AppendLine("      " + string.Join("  ", cells));
		if (block.Length == 0) block.AppendLine("      (toàn bộ record bằng 0 — không bình thường)");
		return block.ToString();
	}

	private static string DescribeColor(int code) => code switch {
		0 => "0 Trắng",
		1 => "1 XANH LAM",
		2 => "2 Xanh lục",
		3 => "3 Vàng",
		4 => "4 Cam",
		_ => $"{code} (ngoài bảng)"
	};

	private static string ReadName(byte[] records, int offset) {
		int length = 0;
		while (length < MaximumNameLength && offset + length < records.Length && records[offset + length] != 0) length++;
		return length == 0 ? "(rỗng)" : LegacyVietnameseText.Decode(records.AsSpan(offset, length).ToArray()).Trim();
	}

	private static int ReadInt32(byte[] bytes, int offset) => offset >= 0 && offset + 4 <= bytes.Length ? BitConverter.ToInt32(bytes, offset) : 0;
}
