namespace Auto.DebugTools;

using System.Text;
using Auto.Runtime;
using Auto.Utils;

// Kiểm các mốc nhận diện mà engine nhiệm vụ Thám Quân / Bào Thương sẽ phụ thuộc, TRƯỚC khi viết engine.
//
// Lý do: toàn bộ hai máy trạng thái bên AutoFS neo vào tên NPC và chữ trong menu NPC
// (MenuAttribute.cs: "Tạp thương" dòng 16245, "Hoàng Thiên Hóa" dòng 24245, "Thương vụ" dòng 16289,
// "Vận chuyển" dòng 16497, "Thám quân" dòng 24299). AutoFS chạy trên client đời cũ; nếu client 2026-08 đổi
// tên NPC hoặc đổi chữ trong menu thì máy trạng thái phải dựng theo mốc mới, không chép được.
//
// Mục "phù trong ô trang bị nhanh" kiểm thẳng ràng buộc đã chấp nhận ngày 2026-09-09: Auto chỉ dùng được vật
// phẩm nằm trong 4 ô trang bị nhanh (xem Runtime/LowHpEngine.cs), mà Thám Quân cần tới 4 loại phù.
//
// Chỉ ĐỌC bộ nhớ. Không gửi lệnh, không chọn option nào trong menu, không ghi gì vào game.
public static class QuestProbe {
	private const string BuildStamp = "QUEST-20260909-03";
	private const int CaravanMinimumLevel = 25;
	private const int ScoutMinimumLevel = 30;
	private const int MaximumNameLength = 64;
	private const int MaximumPackedItemId = 0x001FFFFF;
	// Bố cục menu NPC của client hiện tại, cùng bộ hằng số DoctorShopSemanticCommand đang dùng.
	private const int MenuTextOffset = 0x7EC;
	private const int MenuOptionStride = 0x69C;
	private const int MenuOptionCount = 8;
	private const int MaximumMenuTextLength = 128;
	private const int MinimumOptionLetters = 2;
	// Object menu tầng 1 đã đọc được mục ở tận +0x7EC + 7*0x69C ≈ +0x3C1C, nên quét 0x4000 mới phủ hết.
	private const int TextDumpLength = 0x4000;
	private const int MaximumDumpedTextLength = 120;
	private const int MinimumDumpedTextLetters = 4;
	private const int MaximumDumpedStrings = 150;
	// Phải khớp DoctorShopSemanticCommand.ExpectedDialogVtableRva.
	private const int DoctorConfirmModalVtableRva = 0x46AF3C;
	private const int RawWindowLeadBytes = 0x80;
	private const int RawWindowLength = 0x300;
	private const int RawWindowBytesPerLine = 32;

	private static readonly string[] CaravanNpcNames = ["Tạp thương"];
	private static readonly string[] ScoutNpcNames = ["Hoàng Thiên Hóa"];
	// Năm loại phù mà hai nhiệm vụ dùng tới, theo đúng chữ trong AutoFS.
	//
	// Quan sát thật trên PID 14236 ngày 2026-09-09: ô trang bị nhanh có "Di Ngoại Phù (siêu cấp)" (ItemId=7) và
	// "Hồi thành phù" (ItemId=58). Tức Di ngoại phù CŨNG có bản siêu cấp — khớp nhãn AutoFS "Di ngoại phù &
	// Phù đặc biệt", nên khớp theo chuỗi con là đúng ý ở đây.
	//
	// Lưu ý khi đọc kết quả: khớp bằng Contains nên một ô tên "Hồi thành phù (Siêu cấp)" sẽ làm CẢ hai dòng
	// "Hồi thành phù" và "Hồi thành phù (Siêu cấp)" cùng báo ĐẠT. Đây là công cụ chẩn đoán nên chấp nhận;
	// engine sau này phải phân biệt rõ hai loại chứ không dùng lại phép khớp này.
	private static readonly string[] TalismanNames = [
		"Di ngoại phù",
		"Khứ lai phù",
		"Hồi thành phù",
		"Hồi thành phù (Siêu cấp)",
		"Bào thương hồi thành phù"
	];

	public static string Run(GameWindow game) {
		StringBuilder output = new();
		output.AppendLine("===== Nhiệm vụ: kiểm mốc nhận diện =====");
		output.AppendLine($"QUEST_PROBE_START | BuildStamp={BuildStamp} | Mode=READ_ONLY | GameMemoryWrite=NO | ProcessId={game.ProcessId}");

		try {
			GameSnapshot snapshot = GameMemory.ReadSnapshot(game.ProcessId);
			GameMapInfo map = GameMapReader.Read(game.ProcessId);
			if (! snapshot.Success) return output.Append($"QUEST_PROBE_FAIL | Không đọc được nhân vật | {snapshot.FailReason}").ToString();

			output.AppendLine($"Nhân vật: {snapshot.CharacterName} | Level={snapshot.Level} | Hp={snapshot.Hp}/{snapshot.MaxHp} | Raw={snapshot.X}/{snapshot.Y}");
			output.AppendLine($"Bản đồ: MapId={(map.Success ? map.MapId.ToString() : "đọc lỗi")} | LastObservedMapId={game.LastObservedMapId} | {map.FailureReason}");
			output.AppendLine($"Cổng level Bào Thương (≥{CaravanMinimumLevel}) | {Mark(snapshot.Level >= CaravanMinimumLevel)}");
			output.AppendLine($"Cổng level Thám Quân  (≥{ScoutMinimumLevel}) | {Mark(snapshot.Level >= ScoutMinimumLevel)}");
			output.AppendLine();

			output.AppendLine("--- NPC quanh nhân vật ---");
			AppendNpc(output, game.ProcessId, snapshot, "Bào Thương", CaravanNpcNames);
			AppendNpc(output, game.ProcessId, snapshot, "Thám Quân", ScoutNpcNames);
			output.AppendLine();

			AppendQuickSlotTalismans(output, game.ProcessId);
			output.AppendLine();

			AppendNpcMenu(output, game.ProcessId);
			return output.ToString();
		} catch (Exception ex) {
			output.AppendLine($"QUEST_PROBE_FAIL | {ex.GetType().Name}: {ex.Message}");
			return output.ToString();
		}
	}

	// Neo khoảng cách vào chính nhân vật để đọc được "NPC đang đứng cách bao xa", khác luồng Sửa đồ vốn neo vào
	// toạ độ NPC lấy sẵn từ dữ liệu bản đồ (WeaponRepairAutomation.cs:170).
	private static void AppendNpc(StringBuilder output, int processId, GameSnapshot snapshot, string questLabel, string[] names) {
		foreach (string name in names) {
			bool found = RuntimeEntityLocator.TryFindNamedEntity(processId, name, snapshot.X, snapshot.Y, out RuntimeEntityLocation location, out string reason);
			output.AppendLine(found
				? $"{questLabel,-12} | {Mark(true)} | Tên tìm='{name}' | Khớp='{location.Name}' | Index={location.Index} | Raw={location.RawX}/{location.RawY} | KC={location.DistanceToAnchor:F2}"
				: $"{questLabel,-12} | {Mark(false)} | Tên tìm='{name}' | {reason}");
		}
	}

	// Liệt kê 4 ô trang bị nhanh và đối chiếu với 5 loại phù hai nhiệm vụ cần. Đây là chỗ kiểm ràng buộc
	// "vật phẩm phải nằm trong ô trang bị nhanh mới dùng được".
	private static void AppendQuickSlotTalismans(StringBuilder output, int processId) {
		InventoryContainer quickSlot = InventoryContainer.SearchOrder[0];
		output.AppendLine($"--- Phù trong {quickSlot.DisplayName} (C{quickSlot.Number}, {quickSlot.SlotCount} ô) ---");
		List<string> present = [];
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
			if (inventoryRoot == IntPtr.Zero || itemTable == IntPtr.Zero) {
				output.AppendLine("    KHÔNG ĐỌC ĐƯỢC | InventoryRoot hoặc ItemTable bằng 0.");
				return;
			}
			IntPtr slotList = reader.ReadPointer32(IntPtr.Add(IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object), quickSlot.ListPointerOffset));
			if (slotList == IntPtr.Zero) {
				output.AppendLine("    KHÔNG ĐỌC ĐƯỢC | Danh sách ô bằng 0.");
				return;
			}
			byte[] idBytes = reader.ReadBytes(slotList, quickSlot.SlotCount * sizeof(int));
			if (idBytes.Length != quickSlot.SlotCount * sizeof(int)) {
				output.AppendLine("    KHÔNG ĐỌC ĐƯỢC | Đọc hụt danh sách ô.");
				return;
			}
			for (int index = 0; index < quickSlot.SlotCount; index++) {
				int itemId = BitConverter.ToInt32(idBytes, index * sizeof(int));
				if (itemId <= 0 || itemId > MaximumPackedItemId) {
					output.AppendLine($"    Ô {index + 1} (slot {index}) | trống");
					continue;
				}
				long record = itemTable.ToInt64() + (long)itemId * GameAddresses.Item.InventoryRecordStride;
				if (record <= 0 || record > uint.MaxValue) {
					output.AppendLine($"    Ô {index + 1} (slot {index}) | ItemId={itemId} | bản ghi ngoài vùng đọc");
					continue;
				}
				string name = InventoryContainer.ReadLegacyString(reader, new IntPtr(record + GameAddresses.Item.InventoryName), MaximumNameLength);
				int quantity = reader.ReadUInt16(new IntPtr(record + GameAddresses.Item.Quantity));
				output.AppendLine($"    Ô {index + 1} (slot {index}) | ItemId={itemId} | {name} | SL={quantity}");
				foreach (string talisman in TalismanNames) {
					if (name.Contains(talisman, StringComparison.OrdinalIgnoreCase)) present.Add(talisman);
				}
			}
		} catch (Exception ex) {
			output.AppendLine($"    KHÔNG ĐỌC ĐƯỢC | {ex.GetType().Name}: {ex.Message}");
			return;
		}
		foreach (string talisman in TalismanNames) {
			output.AppendLine($"    {talisman,-26} | {Mark(present.Contains(talisman))}");
		}
	}

	// Chỉ đọc chữ của menu NPC đang mở, không chọn option nào. Cùng bố cục mà DoctorShopSemanticCommand dùng.
	//
	// Quan sát thật ngày 2026-09-09 trên PID 14236: menu NPC Hoàng Thiên Hóa có NHIỀU TẦNG. Khi chủ dự án tự tay
	// bấm "Thám quân" để mở tầng 2, mục này VẪN in ra tầng 1 (đúng ba mục "Thám quân/Kế Ly Gián/Sơ Hiện Đoan Nghê",
	// MenuState không đổi). Kết luận: con trỏ ModalState chỉ giữ tầng 1, tầng 2 nằm ở object khác chưa biết ở đâu.
	//
	// Nên mục này quét thêm: DialogPointer (địa chỉ toàn cục đã biết nhưng chưa dùng ở luồng nào), và nếu vẫn không
	// ra thì dò các object con trỏ tới từ chính object tầng 1. KHÔNG suy đoán tầng 2 nằm ở đâu — in hết ra để xem.
	private static void AppendNpcMenu(StringBuilder output, int processId) {
		output.AppendLine("--- Menu NPC đang mở ---");
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr modal = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState));
			IntPtr dialog = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.DialogPointer));
			output.AppendLine($"    ModalState=0x{modal.ToInt64():X8} | DialogPointer=0x{dialog.ToInt64():X8}");
			// Vtable phân biệt popup xác nhận (khớp DoctorShopSemanticCommand.ExpectedDialogVtableRva) với menu chọn.
			if (modal != IntPtr.Zero) {
				long modalVtable = unchecked((uint)reader.ReadInt32(modal));
				bool isConfirmModal = modalVtable == moduleBase.ToInt64() + DoctorConfirmModalVtableRva;
				output.AppendLine($"    Vtable=0x{modalVtable:X8} | VtableRva=0x{modalVtable - moduleBase.ToInt64():X6} | KhớpPopupXácNhận={(isConfirmModal ? "CÓ" : "KHÔNG")}");
			}
			if (modal == IntPtr.Zero && dialog == IntPtr.Zero) {
				output.AppendLine("    Không có menu nào đang mở. Mở hội thoại NPC rồi chạy lại để đọc chữ trong menu.");
				return;
			}
			HashSet<long> seen = [];
			AppendMenuObject(output, reader, "ModalState", modal, seen);
			AppendMenuObject(output, reader, "DialogPointer", dialog, seen);
			AppendTextDump(output, reader, modal);
		} catch (Exception ex) {
			output.AppendLine($"    KHÔNG ĐỌC ĐƯỢC | {ex.GetType().Name}: {ex.Message}");
		}
	}

	// In các mục giải mã được của MỘT object theo bố cục menu hiện hành. Trả về true nếu có ít nhất một mục.
	private static bool AppendMenuObject(StringBuilder output, MemoryReader reader, string source, IntPtr menu, HashSet<long> seen) {
		if (menu == IntPtr.Zero || ! seen.Add(menu.ToInt64())) return false;
		List<string> decodedOptions = [];
		for (int index = 0; index < MenuOptionCount; index++) {
			string decoded = DecodeMenuOption(reader, menu, index);
			if (decoded.Length > 0) decodedOptions.Add($"#{index} | {decoded}");
		}
		if (decodedOptions.Count == 0) {
			output.AppendLine($"    [{source}=0x{menu.ToInt64():X8}] không giải mã được mục nào theo bố cục hiện tại.");
			return false;
		}
		output.AppendLine($"    [{source}=0x{menu.ToInt64():X8}]");
		foreach (string option in decodedOptions) output.AppendLine("      " + option);
		return true;
	}

	// In MỌI chuỗi kết thúc bằng null đọc được trong object menu, kèm offset — KHÔNG áp bất kỳ bố cục nào.
	//
	// Lý do đổi cách: hai lần trước áp bố cục cố định (+0x7EC, bước 0x69C) đều không ra tầng 2, mà lại cắt cụt đầu
	// chuỗi thật ("uyền tại Triều Ca..." thiếu chữ đầu) vì offset rơi vào giữa chuỗi. Đoán bố cục là sai hướng.
	//
	// Cách dùng: chạy HAI lần, một lần đang mở tầng 1 và một lần đang mở tầng 2, rồi so hai bản. Chuỗi nào chỉ
	// xuất hiện ở bản tầng 2 chính là chữ của tầng 2, và offset in kèm là chỗ để đọc nó.
	private static void AppendTextDump(StringBuilder output, MemoryReader reader, IntPtr menu) {
		if (menu == IntPtr.Zero) return;
		output.AppendLine($"    [Chuỗi trong object 0x{menu.ToInt64():X8} | quét {TextDumpLength} byte, không áp bố cục]");
		byte[] block = reader.ReadBytes(menu, TextDumpLength);
		if (block.Length == 0) {
			output.AppendLine("      KHÔNG ĐỌC ĐƯỢC object.");
			return;
		}
		int printed = 0;
		int firstTextOffset = -1;
		int start = 0;
		while (start < block.Length) {
			while (start < block.Length && ! IsTextByte(block[start])) start++;
			int end = start;
			while (end < block.Length && IsTextByte(block[end]) && end - start < MaximumDumpedTextLength) end++;
			if (end > start && end < block.Length && block[end] == 0) {
				byte[] bytes = block[start..end];
				string decoded = LegacyVietnameseText.Decode(bytes);
				// Bỏ đường dẫn sprite: object giao diện nào cũng đầy ".spr", chiếm hết chỗ và che mất chữ thật.
				// Lượt dò 2026-09-09 in ra hơn 100 dòng thì gần hết là đường dẫn.
				if (decoded.Count(char.IsLetter) >= MinimumDumpedTextLetters && ! decoded.Contains(".spr", StringComparison.OrdinalIgnoreCase)) {
					output.AppendLine($"      +0x{start:X4} | {decoded}");
					if (firstTextOffset < 0) firstTextOffset = start;
					printed++;
					if (printed >= MaximumDumpedStrings) {
						output.AppendLine($"      (dừng ở {MaximumDumpedStrings} chuỗi để log không quá dài)");
						break;
					}
				}
			}
			start = end > start ? end + 1 : start + 1;
		}
		if (printed == 0) output.AppendLine("      Không có chuỗi nào đạt điều kiện trong vùng đã quét.");
		AppendRawWindow(output, block, firstTextOffset);
	}

	// In nguyên vùng quanh chỗ có chữ, dạng hex + ký tự, KHÔNG cắt tại byte điều khiển.
	//
	// Lý do: phép quét chuỗi ở trên cắt tại mọi byte < 0x20, mà văn bản trong popup có byte điều khiển xen giữa
	// (bằng chứng 2026-09-09: in ra "hiên Hóa." — đuôi của "...Thiên Hóa." đã mất chữ đầu, và "ho Hoàng Thiên Hóa."
	// mất chữ "c" của "cho"). Tên map nhiệm vụ rơi đúng vào mấy khúc bị bỏ đó. Vùng thô này giữ nguyên mọi byte
	// để đọc được trọn câu và biết chính xác byte nào đang chen vào.
	private static void AppendRawWindow(StringBuilder output, byte[] block, int firstTextOffset) {
		if (firstTextOffset < 0) return;
		int start = Math.Max(0, firstTextOffset - RawWindowLeadBytes);
		int end = Math.Min(block.Length, start + RawWindowLength);
		output.AppendLine($"    [Vùng thô +0x{start:X4}..+0x{end:X4} | hex kèm ký tự, không cắt]");
		for (int offset = start; offset < end; offset += RawWindowBytesPerLine) {
			int count = Math.Min(RawWindowBytesPerLine, end - offset);
			byte[] line = block[offset..(offset + count)];
			string text = LegacyVietnameseText.Decode(line.Select(value => value < 0x20 ? (byte)'.' : value).ToArray());
			output.AppendLine($"      +0x{offset:X4} | {Convert.ToHexString(line)} | {text}");
		}
	}

	private static bool IsTextByte(byte value) => value is >= 0x20 and <= 0x7E || value >= 0x80;

	private static string DecodeMenuOption(MemoryReader reader, IntPtr menu, int index) {
		byte[] bytes = reader.ReadBytes(IntPtr.Add(menu, MenuTextOffset + index * MenuOptionStride), MaximumMenuTextLength);
		if (bytes.Length == 0) return "";
		int terminator = Array.IndexOf(bytes, (byte)0);
		if (terminator >= 0) bytes = bytes[..terminator];
		if (bytes.Length == 0) return "";
		string decoded = LegacyVietnameseText.Decode(bytes).Trim();
		return decoded.Count(char.IsLetterOrDigit) >= MinimumOptionLetters ? decoded : "";
	}

	private static string Mark(bool ok) => ok ? "ĐẠT " : "HỎNG";
}
