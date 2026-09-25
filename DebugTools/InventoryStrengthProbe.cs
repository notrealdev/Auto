namespace Auto.DebugTools;

using System.Text;
using Auto.Runtime;
using Auto.Utils;

// Đối chiếu sức lực mang đồ đọc từ bộ nhớ với số hiển thị trong game.
//
// Vì sao cần: cặp offset hiện dùng (StrengthRoot + 0x278/0x27C) dò ra bằng lọc vi sai trên DUY NHẤT một nhân vật
// (TiểuHồngĐơn, 2026-09-23). Chủ dự án đối chiếu 5 nhân vật còn lại thì báo SAI, nên phải có đường tự kiểm trên
// từng account thay vì tin vào một mẫu.
//
// Probe chỉ ĐỌC, không gửi lệnh nào vào client.
public static class InventoryStrengthProbe {
	public const string BuildStamp = "INVENTORY-STRENGTH-20260923-01";
	// Quét rộng quanh cặp đang dùng để nếu lệch thì vẫn nhìn thấy cặp đúng ở ngay gần.
	private const int WindowStart = 0x0200;
	private const int WindowEnd = 0x0340;

	public static string Read(GameWindow game) {
		StringBuilder text = new();
		text.AppendLine("===== Sức lực mang đồ =====");
		text.AppendLine($"BuildStamp = {BuildStamp}");
		text.AppendLine($"PID = {game.ProcessId} | Nhân vật = {game.CharacterName}");
		try {
			using MemoryReader reader = new(game.ProcessId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return text.AppendLine("Game.exe không tồn tại.").ToString();
			IntPtr root = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.StrengthRoot));
			text.AppendLine($"StrengthRoot = Game.exe+0x{GameAddresses.Globals.StrengthRoot:X} -> con trỏ = 0x{root.ToInt64():X8}");
			if (root == IntPtr.Zero) return text.AppendLine("Con trỏ bằng 0 — client chưa vào game hẳn.").ToString();

			InventoryStrengthReading raw = InventoryStrengthReader.ReadRaw(game.ProcessId);
			text.AppendLine($"GAME (số gốc, so với ô sức lực trong game): hiện tại(+0x{GameAddresses.Inventory.CurrentStrength:X}) / tối đa(+0x{GameAddresses.Inventory.MaximumStrength:X}) = "
				+ (raw.Success ? $"{raw.Current}/{raw.Maximum} | Còn lại = {raw.Free}" : "ĐỌC HỎNG | " + raw.FailureReason));
			// Số Auto dùng để quyết định đi bán / dừng nhặt: đã bù phần game cập nhật trễ bằng trọng lượng túi. Lệch số gốc
			// là BÌNH THƯỜNG lúc game chưa kịp cộng dồn các món vừa nhặt.
			InventoryStrengthReading reading = InventoryStrengthReader.Read(game.ProcessId);
			text.AppendLine("AUTO TÍNH (đã bù trọng lượng túi, dùng cho quyết định)       = "
				+ (reading.Success ? $"{reading.Current}/{reading.Maximum} | Còn lại = {reading.Free}" : "ĐỌC HỎNG | " + reading.FailureReason));
			text.AppendLine();
			text.AppendLine("So với ô sức lực trong game. Nếu KHÔNG khớp, tìm trong bảng dưới cặp nào đúng rồi báo lại:");
			text.AppendLine("(chỉ liệt kê ô có giá trị 1..9999, là khoảng hợp lý của sức lực)");
			text.AppendLine("RIÊNG = giá trị khác nhau giữa các client đang mở, tức dữ liệu của từng nhân vật.");
			text.AppendLine("CHUNG = mọi client đọc ra y hệt, tức hằng số dùng chung -> KHÔNG THỂ là sức lực.");
			text.AppendLine();

			// Đọc cùng offset trên các client khác để tự phân loại RIÊNG/CHUNG. Không có client nào khác thì bỏ cột này.
			List<int> otherProcessIds = [];
			foreach (System.Diagnostics.Process other in System.Diagnostics.Process.GetProcessesByName("Game")) {
				if (other.Id != game.ProcessId) otherProcessIds.Add(other.Id);
			}

			for (int offset = WindowStart; offset <= WindowEnd; offset += 4) {
				int value = reader.ReadInt32(IntPtr.Add(root, offset));
				if (value < 1 || value > 9999) continue;
				string mark = offset == GameAddresses.Inventory.CurrentStrength ? "  <= đang coi là HIỆN TẠI"
					: offset == GameAddresses.Inventory.MaximumStrength ? "  <= đang coi là TỐI ĐA" : "";
				text.AppendLine($"   +0x{offset:X3} = {value,-6} {Classify(offset, value, otherProcessIds),-6}{mark}");
			}
		} catch (Exception ex) {
			text.AppendLine($"{ex.GetType().Name}: {ex.Message}");
		}
		return text.ToString();
	}

	// So cùng một offset trên các client khác đang mở. Khác giá trị = dữ liệu riêng của nhân vật; giống hệt ở mọi
	// client = hằng số dùng chung, loại thẳng khỏi danh sách ứng viên.
	private static string Classify(int offset, int value, List<int> otherProcessIds) {
		if (otherProcessIds.Count == 0) return "";
		foreach (int processId in otherProcessIds) {
			try {
				using MemoryReader reader = new(processId);
				IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
				if (moduleBase == IntPtr.Zero) continue;
				IntPtr root = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.StrengthRoot));
				if (root == IntPtr.Zero) continue;
				if (reader.ReadInt32(IntPtr.Add(root, offset)) != value) return "RIÊNG";
			} catch {
				// Client vừa đóng giữa chừng: bỏ qua, các client còn lại vẫn đủ để kết luận.
			}
		}
		return "CHUNG";
	}
}
