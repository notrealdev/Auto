namespace Auto.DebugTools;

using System.Text;
using Auto.Runtime;
using Auto.Utils;

// Chẩn đoán tính năng "Hồi thành phù khi HP <=". Chủ dự án báo tính năng chưa hoạt động nhưng chưa xác định
// được hỏng ở khâu nào, mà log runtime chỉ hiện lỗi khi HP đã tụt dưới ngưỡng — rất khó dựng lại theo ý muốn.
//
// Probe đi qua ĐÚNG thứ tự các cổng mà LowHpReturnTalismanEngine.Tick kiểm, và gọi thẳng
// LowHpReturnTalismanEngine.TryFindTalisman thay vì chép lại, để kết quả nói về code đang ship.
//
// Kiểm tra: chỉ đọc bộ nhớ, không gửi lệnh nào.
// Dùng thật: GỬI LỆNH VÀO GAME, nhân vật sẽ dịch chuyển về thành và mất một Hồi thành phù. Chỉ chạy khi
// chủ dự án chủ động bấm.
public static class ReturnTalismanProbe {
	private const string BuildStamp = "RETURN-TALISMAN-20260909-10";
	private const int MaximumPackedItemId = 0x001FFFFF;
	// Phải khớp NativeBuildStamp trong Native/SystemUint/SystemUint.cpp.
	private const ulong ExpectedNativeBuildStamp = 20260909;
	private const int UseConfirmTimeoutMilliseconds = 8000;
	private const int UsePollMilliseconds = 250;
	private const int QuickSlotContainer = 11;
	private const int QuickSlotCount = 4;

	// Đọc trạng thái từng cổng, không đụng gì vào game.
	public static string Inspect(GameWindow game) {
		StringBuilder output = new();
		output.AppendLine("===== Hồi thành phù: kiểm tra =====");
		output.AppendLine($"TALISMAN_PROBE_START | BuildStamp={BuildStamp} | Mode=READ_ONLY | GameMemoryWrite=NO | ProcessId={game.ProcessId}");

		try {
			BasicSettings settings = game.BasicSettings;
			GameSnapshot snapshot = GameMemory.ReadSnapshot(game.ProcessId);
			GameMapInfo map = GameMapReader.Read(game.ProcessId);
			bool snapshotUsable = snapshot.Success && snapshot.Hp > 0 && snapshot.MaxHp > 0;
			bool belowThreshold = snapshotUsable && snapshot.Hp * 100 <= settings.LowHpReturnTalismanThreshold * snapshot.MaxHp;
			bool found = LowHpReturnTalismanEngine.TryFindTalisman(game.ProcessId, out int memoryIndex, out int container, out int itemId, out string itemName, out string findError, out List<string> candidateSummaries);

			// Cổng 0 chặn TRƯỚC mọi thứ khác và trước đây probe không hề kiểm: lần chạy 2026-09-09 trên PID 22056
			// bị "Master automation switch is disabled." đúng ở đây, sau khi đã báo mọi cổng khác đều ĐẠT.
			// Cổng đầu tiên phải là "bản native nào đang chạy": mọi kết luận bên dưới đều vô nghĩa nếu tiến trình
			// game vẫn giữ DLL của phiên Auto trước.
			bool stampRead = game.AutoFsTransport.TryQueryNativeBuildStamp(game.Handle, out ulong nativeStamp, out string stampError);
			output.AppendLine($"Native đang sống      | {Mark(stampRead && nativeStamp == ExpectedNativeBuildStamp)} | BuildStamp={(stampRead ? nativeStamp.ToString() : "đọc lỗi: " + stampError)} | Mong đợi={ExpectedNativeBuildStamp} | (lệch = tiến trình game còn giữ DLL cũ, phải khởi động lại game)");
			output.AppendLine($"Cổng 0 Công tắc tổng    | {Mark(game.AutoFsTransport.AutomationEnabled)} | MasterEnabled={game.AutoFsTransport.MasterEnabled} | AutomationEnabled={game.AutoFsTransport.AutomationEnabled} (False khi tắt công tắc hoặc đang bị tạm khoá)");
			output.AppendLine($"Cổng 1 Bật tính năng    | {Mark(settings.EnableLowHpReturnTalisman)} | EnableLowHpReturnTalisman={settings.EnableLowHpReturnTalisman} | Ngưỡng={settings.LowHpReturnTalismanThreshold}%");
			output.AppendLine($"Cổng 2 Đọc được HP      | {Mark(snapshotUsable)} | Hp={snapshot.Hp}/{snapshot.MaxHp} | Success={snapshot.Success} | {snapshot.FailReason}");
			output.AppendLine($"Cổng 3 HP dưới ngưỡng   | {Mark(belowThreshold)} | {(snapshotUsable ? $"{snapshot.Hp * 100 / snapshot.MaxHp}% so với {settings.LowHpReturnTalismanThreshold}%" : "chưa đọc được HP")} | (bình thường KHÔNG đạt khi đang đầy máu)");
			// Engine nhận game.LastObservedMapId chứ KHÔNG tự đọc (AccountEngineCoordinator.cs:78), nên phải kiểm
			// đúng con số đó. Đọc tươi bằng GameMapReader chỉ để đối chiếu: hai bên lệch nhau tức là vòng lặp tài
			// khoản chưa cập nhật map, và bước xác nhận đổi map của bùa sẽ so trên số cũ.
			bool observedUsable = game.LastObservedMapId > 0;
			output.AppendLine($"Cổng 4 Map ID engine dùng | {Mark(observedUsable)} | LastObservedMapId={game.LastObservedMapId} | ĐọcTươi={map.MapId} | {(observedUsable && map.MapId > 0 && game.LastObservedMapId != map.MapId ? "LỆCH so với đọc tươi" : "")}{map.FailureReason}");
			output.AppendLine($"Cổng 5 Có bùa trong túi | {Mark(found)} | {(found ? $"Tên={itemName} | ItemId={itemId} | Container={container} | Slot={memoryIndex}" : findError)}");
			output.AppendLine($"Cổng 6 ItemId hợp lệ    | {Mark(found && itemId is > 0 and <= MaximumPackedItemId)} | ItemId={itemId} | Trần={MaximumPackedItemId}");
			output.AppendLine($"Cổng 7 Kênh gửi lệnh    | {Mark(game.RuntimeLayout.Get(RuntimeSubsystem.AttackTransport).Available)} | {game.RuntimeLayout.Get(RuntimeSubsystem.AttackTransport).Evidence}{game.RuntimeLayout.Get(RuntimeSubsystem.AttackTransport).FailureReason}");
			output.AppendLine($"Cổng 8 Đọc được túi đồ  | {Mark(game.RuntimeLayout.Get(RuntimeSubsystem.Inventory).Available)} | {game.RuntimeLayout.Get(RuntimeSubsystem.Inventory).Evidence}{game.RuntimeLayout.Get(RuntimeSubsystem.Inventory).FailureReason}");
			// In HẾT bùa tìm được, không chỉ con được chọn: đây là cách duy nhất kiểm thứ tự ưu tiên
			// (ô trang bị nhanh trước, rồi Siêu cấp) mà không phải dùng thử mất bùa.
			if (candidateSummaries.Count > 0) {
				output.AppendLine($"       Tất cả bùa tìm được ({candidateSummaries.Count}):");
				foreach (string summary in candidateSummaries) output.AppendLine($"         {summary}");
			}
			output.AppendLine();
			output.AppendLine(InspectVerdict(settings, snapshotUsable, map, found, itemId, game));
			// Cổng 3 và cổng 9 KHÔNG kiểm được ở chế độ đọc: một cái đòi HP thật sự thấp, một cái đòi gửi lệnh thật.
			output.AppendLine("Cổng 3 không đạt khi đang đầy máu là BÌNH THƯỜNG, không phải lỗi.");
			output.AppendLine("Việc bấm phím tắt có thật sự dịch chuyển được hay không thì chế độ đọc KHÔNG kiểm được — dùng \"Dùng Hồi thành phù bằng phím tắt\".");
			return output.ToString();
		} catch (Exception ex) {
			output.AppendLine($"TALISMAN_PROBE_FAIL | {ex.GetType().Name}: {ex.Message}");
			return output.ToString();
		}
	}

	private static string InspectVerdict(BasicSettings settings, bool snapshotUsable, GameMapInfo map, bool found, int itemId, GameWindow game) {
		// Cổng 0 vẫn phải BÁO vì tính năng thật đi qua nó (AccountEngineCoordinator.cs:78 gate bằng masterEnabled),
		// nhưng KHÔNG chặn probe nữa: hai mục "Dùng ngay"/"phím tắt" gửi thẳng, không đòi công tắc.
		if (! game.AutoFsTransport.AutomationEnabled) return "KẾT LUẬN=CỔNG 0 ĐANG TẮT | tính năng THẬT sẽ không chạy khi account tắt, nhưng probe vẫn dùng được — các mục Dùng ngay / phím tắt gửi thẳng, không qua công tắc.";
		if (! settings.EnableLowHpReturnTalisman) return "KẾT LUẬN=CHƯA BẬT | tính năng đang tắt, engine thoát ngay ở dòng đầu Tick.";
		if (! snapshotUsable) return "KẾT LUẬN=HỎNG CỔNG 2 | không đọc được HP nên engine thoát ngay, không bao giờ tới bước dùng bùa.";
		if (game.LastObservedMapId <= 0) return "KẾT LUẬN=HỎNG CỔNG 4 | game.LastObservedMapId chưa có giá trị; bùa có bắn cũng không xác nhận được đổi map, engine sẽ thử lại 5 lần rồi đứng im.";
		if (! found) return "KẾT LUẬN=HỎNG CỔNG 5 | không tìm thấy bùa trong túi, engine ghi LOW_HP_NOT_FOUND rồi bỏ qua.";
		if (itemId is <= 0 or > MaximumPackedItemId) return "KẾT LUẬN=HỎNG CỔNG 6 | ItemId vượt trần, engine ghi LOW_HP_ITEM_ID_UNSUPPORTED.";
		if (! game.RuntimeLayout.Get(RuntimeSubsystem.AttackTransport).Available) return "KẾT LUẬN=HỎNG CỔNG 7 | kênh gửi lệnh chưa sẵn sàng.";
		return "KẾT LUẬN=CÁC CỔNG ĐỌC ĐƯỢC ĐỀU ĐẠT | phần còn lại chỉ kiểm được bằng \"Dùng Hồi thành phù bằng phím tắt\".";
	}

	// Dùng bùa bằng PHÍM TẮT trang bị nhanh. Đây là đường DUY NHẤT còn lại, và cũng là đường luồng thật dùng.
	//
	// Chủ dự án cho biết 2026-09-09: trong game, vật phẩm nằm ở ô trang bị nhanh (container 11, đánh số 1..4 trên
	// màn hình) thì bấm đúng phím số đó là dùng được. Đây là đường mà CHÍNH CLIENT xử lý nên không phụ thuộc
	// hằng số opcode nào của Auto.
	//
	// Slot trong bộ nhớ đếm từ 0 nên phím hiển thị là slot + 1.
	public static string UseByHotkey(GameWindow game) {
		StringBuilder output = new();
		output.AppendLine("===== Hồi thành phù: dùng bằng phím tắt =====");
		output.AppendLine($"TALISMAN_HOTKEY_START | BuildStamp={BuildStamp} | Mode=GAME_WRITE | ProcessId={game.ProcessId}");

		try {
			GameMapInfo before = GameMapReader.Read(game.ProcessId);
			if (! LowHpReturnTalismanEngine.TryFindTalisman(game.ProcessId, out int memoryIndex, out int container, out int itemId, out string itemName, out string findError)) {
				return output.Append($"TALISMAN_HOTKEY_FAIL | Không tìm thấy bùa | {findError}").ToString();
			}
			if (container != QuickSlotContainer || memoryIndex < 0 || memoryIndex >= QuickSlotCount) {
				return output.Append($"TALISMAN_HOTKEY_FAIL | Bùa không nằm trong trang bị nhanh | Container={container} (cần {QuickSlotContainer}) | Slot={memoryIndex} | Kéo bùa vào một trong {QuickSlotCount} ô trang bị nhanh rồi chạy lại.").ToString();
			}

			int quantityBefore = ReadQuantity(game.ProcessId, itemId);
			int key = memoryIndex + 1;
			output.AppendLine($"Trước khi dùng: MapId={before.MapId} | Tên={itemName} | ItemId={itemId} | Slot={memoryIndex} | Phím={key} | SốLượng={quantityBefore}");

			// Gọi đúng hàm mà luồng thật dùng, chỉ khác bản ForDebug bỏ công tắc tổng — probe chép lại chuỗi gửi
			// thì chỉ chứng minh bản chép chạy được, không chứng minh code đang ship chạy được.
			if (! game.AutoFsTransport.TryUseQuickSlotHotkeyForDebug(game.Handle, memoryIndex, out string sendError)) {
				return output.Append($"TALISMAN_HOTKEY_FAIL | Không gửi được phím | {sendError}").ToString();
			}
			output.AppendLine($"Đã gửi phím {key} tới cửa sổ game, đang chờ...");

			DateTime deadline = DateTime.UtcNow.AddMilliseconds(UseConfirmTimeoutMilliseconds);
			int quantityAfter = quantityBefore;
			while (DateTime.UtcNow < deadline) {
				Thread.Sleep(UsePollMilliseconds);
				quantityAfter = ReadQuantity(game.ProcessId, itemId);
				GameMapInfo current = GameMapReader.Read(game.ProcessId);
				if (current.Success && current.MapId > 0 && before.MapId > 0 && current.MapId != before.MapId) {
					output.AppendLine($"TALISMAN_HOTKEY_OK | MapId {before.MapId} -> {current.MapId} | SốLượng {quantityBefore} -> {quantityAfter} | phím tắt dùng được.");
					return output.ToString();
				}
			}

			output.AppendLine($"TALISMAN_HOTKEY_NO_MAP_CHANGE | Map ID vẫn {before.MapId} sau {UseConfirmTimeoutMilliseconds}ms | SốLượng {quantityBefore} -> {quantityAfter}");
			output.AppendLine(quantityBefore > 0 && quantityAfter >= 0 && quantityAfter < quantityBefore
				? "KẾT LUẬN=PHÍM TẮT DÙNG ĐƯỢC | bùa bị trừ nên client đã thi hành."
				: IsMultiCharge(itemName)
					? "KẾT LUẬN=KHÔNG KẾT LUẬN ĐƯỢC | bùa Siêu cấp dùng nhiều lần nên số lượng đứng yên là bình thường; chỉ Map ID mới nói được."
				: quantityBefore > 0 && quantityAfter == quantityBefore
					? "KẾT LUẬN=PHÍM TẮT KHÔNG ĂN | client không nhận phím gửi nền; phải bấm tay trong game để so sánh."
					: $"KẾT LUẬN=KHÔNG ĐỌC ĐƯỢC SỐ LƯỢNG | Trước={quantityBefore} Sau={quantityAfter}.");
			return output.ToString();
		} catch (Exception ex) {
			output.AppendLine($"TALISMAN_HOTKEY_FAIL | {ex.GetType().Name}: {ex.Message}");
			return output.ToString();
		}
	}

	// "Hồi thành phù (Siêu cấp)" có SỐ LƯỢNG = 1 nhưng dùng được nhiều lần (chủ dự án cho biết 2026-09-09,
	// khoảng 100 lượt). Với loại đó số lượng KHÔNG giảm sau khi dùng, nên "SốLượng n -> n" không còn là bằng
	// chứng lệnh trượt — chỉ Map ID mới nói được. Bản thường thì ngược lại, dùng một lần là mất.
	private static bool IsMultiCharge(string itemName) => itemName.Contains("Siêu", StringComparison.OrdinalIgnoreCase);

	// Số lượng nằm trong bản ghi vật phẩm của ItemTable, cùng đường mà LowHpReturnTalismanEngine dùng để đọc tên.
	private static int ReadQuantity(int processId, int itemId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
			if (itemTable == IntPtr.Zero) return -1;
			long record = itemTable.ToInt64() + (long)itemId * GameAddresses.Item.InventoryRecordStride;
			long quantity = record + GameAddresses.Item.Quantity;
			if (record <= 0 || quantity > uint.MaxValue) return -1;
			return reader.ReadInt32(new IntPtr(quantity));
		} catch {
			return -1;
		}
	}

	private static string Mark(bool ok) => ok ? "ĐẠT " : "HỎNG";
}
