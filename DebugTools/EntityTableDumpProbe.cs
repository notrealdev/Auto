namespace Auto.DebugTools;

using System.Text;
using Auto.Attack;
using Auto.Runtime;
using Auto.Utils;

// Đổ NGUYÊN bảng entity index 2..256, KHÔNG lọc gì: không lọc EntityType, không lọc LifecycleStatus, không lọc toạ độ.
//
// Vì sao cần: mọi probe cũ đều lọc mất NPC. EliteMonsterProbe bỏ qua entity có EntityType != MonsterType
// (EliteMonsterProbe.cs:120) nên NPC không bao giờ hiện ra, và AutoFsEntityScanner cũng chỉ giữ quái. Khi luồng Sửa đồ
// báo "Chưa thấy entity có tên 'Đại phu'" thì không có công cụ nào trả lời được câu hỏi kế tiếp: NPC đó có nằm trong
// bảng hay không, và nếu có thì mang EntityType/tên/byte gì.
//
// Sự cố dẫn tới probe này (Release/Diagnostics/repair.log 2026-09-11): PID=22824 quay 469 chuyến sửa đồ trong 6 giờ 39
// phút, 5119 dòng đều "TênKhớp=0". Cùng lúc đó RuntimeEntityLocator vẫn đọc ra tên sạch của NGƯỜI CHƠI đứng ngay chỗ
// Đại Phu ('QuanCafeNo7'@0,0 — 'Thiên Vũ tế'@0,2), tức bảng đọc đúng và client vẫn nhận cập nhật, chỉ riêng NPC vắng.
// Chưa đủ dữ liệu để kết luận vì sao, nên probe này đi lấy đúng phần còn thiếu.
//
// In kèm cả EntityTableRva đã giải theo client LẪN hằng số GameAddresses.Globals.EntityTable, vì AutoFsEntityScanner
// giải layout theo từng client còn RuntimeEntityLocator dùng hằng số cứng — hai giá trị lệch nhau thì phải thấy ngay.
//
// Chỉ đọc bộ nhớ, không gửi lệnh nào, không ghi gì vào game.
public static class EntityTableDumpProbe {
	private const string BuildStamp = "ENTITY-TABLE-DUMP-20260911-02";
	// Quét VƯỢT trần 256 mà cả Auto lẫn AutoFS đang dùng, để trả lời câu hỏi bảng entity có entity nào nằm trên đó không.
	// Lý do nghi ngờ (2026-09-11): PID=22824 lúc kẹt trên map 37 có 5 entity gần nhất đều ở #223..#255, và trên map 21
	// tìm thấy Đại Phu ở index 214 trong khi 22000/32196 ra 81/87. Client này nhất quán cấp index cao, nên NPC rơi
	// lên trên 256 là khả năng chưa loại trừ được. Chỉ đọc, sai địa chỉ thì try/catch nuốt và đếm vào ĐọcLỗi.
	private const int ExtendedLastIndex = 1023;
	private const int NameColumnWidth = 22;
	private const int MaximumHexBytes = 32;

	public static string Run(int processId) {
		StringBuilder output = new();
		output.AppendLine("===== Đổ nguyên bảng entity (không lọc) =====");
		output.AppendLine($"ENTITY_DUMP_START | BuildStamp={BuildStamp} | Mode=READ_ONLY | GameMemoryWrite=NO | ProcessId={processId}");

		try {
			using MemoryReader reader = new(processId);
			RuntimeLayout layout = RuntimeLayoutResolver.Resolve(processId);
			if (! layout.Get(RuntimeSubsystem.Entity).Available) return output.Append("ENTITY_DUMP_FAIL | Reason=ENTITY_SUBSYSTEM_UNAVAILABLE").ToString();

			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return output.Append("ENTITY_DUMP_FAIL | Reason=MODULE_NOT_FOUND").ToString();

			// Hai con đường đọc bảng đang tồn tại song song trong Auto. In cả hai để loại trừ giả thuyết lệch RVA.
			IntPtr resolvedTable = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			IntPtr constantTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.EntityTable));
			output.AppendLine($"EntityTableRva: ĐãGiải=0x{layout.EntityTableRva:X} -> Bảng=0x{resolvedTable.ToInt64():X8} | HằngSố=0x{GameAddresses.Globals.EntityTable:X} -> Bảng=0x{constantTable.ToInt64():X8} | Khớp={(resolvedTable == constantTable ? "CÓ" : "KHÔNG")}");
			if (resolvedTable == IntPtr.Zero) return output.Append("ENTITY_DUMP_FAIL | Reason=ENTITY_TABLE_NULL").ToString();

			IntPtr playerBase = IntPtr.Add(resolvedTable, layout.PlayerRecordOffset);
			int playerX = reader.ReadInt32(IntPtr.Add(playerBase, AutoFsClientProfile.RawX));
			int playerY = reader.ReadInt32(IntPtr.Add(playerBase, AutoFsClientProfile.RawY));
			output.AppendLine($"Nhân vật: Raw={playerX}/{playerY} | Ô={playerX / 256}/{playerY / 512}");
			output.AppendLine($"{"#",-4}{"Tên",-NameColumnWidth} | {"Type",4} | {"St",2} | {"Hp",8} | {"Lv",3} | {"Raw",13} | {"KC ô",6} | Byte thô");

			List<(double Distance, string Line)> rows = [];
			int emptyNameCount = 0;
			int zeroPositionCount = 0;
			int readFailCount = 0;
			Dictionary<int, int> typeCounts = [];

			int aboveLimitCount = 0;
			for (int index = AutoFsClientProfile.FirstEntityIndex; index <= ExtendedLastIndex; index++) {
				try {
					IntPtr entityBase = IntPtr.Add(resolvedTable, index * layout.EntityStride);
					byte[] nameBytes = ReadName(reader, entityBase);
					if (nameBytes.Length == 0) {
						emptyNameCount++;
						continue;
					}
					int rawX = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.RawX));
					int rawY = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.RawY));
					// KHÔNG bỏ qua entity toạ độ 0: chính chỗ này là điểm nghi vấn. RuntimeEntityLocator loại chúng
					// (RuntimeEntityLocator.cs:57) nên nếu NPC nằm trong nhóm này thì nó vô hình với luồng Sửa đồ.
					if (rawX <= 0 || rawY <= 0) zeroPositionCount++;

					int type = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.EntityType));
					int status = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.LifecycleStatus));
					int hp = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.Hp));
					int level = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.Level));
					typeCounts[type] = typeCounts.GetValueOrDefault(type) + 1;

					string name = LegacyVietnameseText.Decode(nameBytes);
					double distance = rawX > 0 && rawY > 0 ? Distance(playerX, playerY, rawX, rawY) : double.MaxValue;
					string distanceText = distance == double.MaxValue ? "KHÔNG" : distance.ToString("F2");
					// Đánh dấu rõ entity nằm ngoài dải mà luồng Sửa đồ/Đánh thật sự quét: đó chính là nhóm cần nhìn.
					bool aboveLimit = index > AutoFsClientProfile.LastEntityIndex;
					if (aboveLimit) aboveLimitCount++;
					string marker = aboveLimit ? "!" : " ";
					rows.Add((distance, $"{marker}{index,-4}{name.PadRight(NameColumnWidth)} | {type,4} | {status,2} | {hp,8} | {level,3} | {rawX,6}/{rawY,-6} | {distanceText,6} | {ToHex(nameBytes)}"));
				} catch {
					readFailCount++;
				}
			}

			rows.Sort((left, right) => left.Distance.CompareTo(right.Distance));
			foreach ((_, string line) in rows) output.AppendLine(line);
			if (rows.Count == 0) output.AppendLine("(không entity nào có tên)");

			string typeSummary = string.Join(", ", typeCounts.OrderBy(entry => entry.Key).Select(entry => $"Type{entry.Key}={entry.Value}"));
			output.AppendLine($"ENTITY_DUMP_END | CóTên={rows.Count} | TênRỗng={emptyNameCount} | ToạĐộ0={zeroPositionCount} | ĐọcLỗi={readFailCount}");
			output.AppendLine($"Quét {AutoFsClientProfile.FirstEntityIndex}..{ExtendedLastIndex} | TrongDải(<={AutoFsClientProfile.LastEntityIndex})={rows.Count - aboveLimitCount} | NGOÀIDẢI(>{AutoFsClientProfile.LastEntityIndex})={aboveLimitCount} <- dòng đánh dấu '!'");
			output.AppendLine(aboveLimitCount > 0
				? "=> CÓ entity nằm ngoài dải quét của Auto/AutoFS: trần 256 là thiếu, NPC rơi lên trên đó sẽ vô hình."
				: "=> Không entity nào nằm ngoài dải quét: trần 256 đủ, loại được giả thuyết chỉ số vượt trần.");
			output.AppendLine($"Phân bố EntityType: {typeSummary}");
			output.AppendLine($"Type 0=quái, 1=người chơi, 6=đệ (theo AutoFsClientProfile). Type nào khác là loại Auto chưa từng phân loại.");
			output.AppendLine("St=LifecycleStatus (6=đã kết thúc) | KC ô=khoảng cách tới nhân vật, X chia 256 Y chia 512 | KHÔNG=entity không có toạ độ");
			return output.ToString();
		} catch (Exception ex) {
			output.AppendLine($"ENTITY_DUMP_FAIL | {ex.GetType().Name}: {ex.Message}");
			return output.ToString();
		}
	}

	private static double Distance(int firstX, int firstY, int secondX, int secondY) {
		double deltaX = (firstX - (double)secondX) / 256.0;
		double deltaY = (firstY - (double)secondY) / 512.0;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	private static byte[] ReadName(MemoryReader reader, IntPtr entityBase) {
		byte[] bytes = reader.ReadBytes(IntPtr.Add(entityBase, AutoFsClientProfile.Name), AutoFsClientProfile.MaximumNameLength);
		int length = Array.IndexOf(bytes, (byte)0);
		if (length < 0) length = bytes.Length;
		return length == 0 ? Array.Empty<byte>() : bytes.AsSpan(0, length).ToArray();
	}

	private static string ToHex(byte[] bytes) {
		int count = Math.Min(bytes.Length, MaximumHexBytes);
		return Convert.ToHexString(bytes, 0, count) + (bytes.Length > count ? "..." : "");
	}
}
