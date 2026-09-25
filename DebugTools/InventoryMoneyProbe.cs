namespace Auto.DebugTools;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Auto.Runtime;
using Auto.Utils;

// Dò ô nhớ SỐ TIỀN hiển thị trong túi đồ, nằm bên trái ô sức lực.
//
// Offset đã tìm ra và chủ dự án xác nhận với game 2026-09-24: GameAddresses.Inventory.Money (dòng đánh dấu bên dưới).
// Probe giữ lại làm công cụ đối chiếu/dò lại khi client đổi bản, không phải để đọc thường xuyên (đọc thường xuyên
// dùng InventoryMoneyReader).
//
// Cách dò gốc: AutoFS trong D:\G\DEV\Resource không ghi offset tiền, Auto cũng chưa từng đọc. Giả thuyết làm việc là ô
// tiền nằm CÙNG đối tượng với ô sức lực vì hai ô cùng hiện trong cửa sổ túi đồ, nên probe quét đúng vùng đối tượng
// sức lực ([Game.exe+Globals.StrengthRoot]) — giả thuyết đó đã đúng.
//
// Cách dùng (lọc vi sai, cùng phương pháp đã dò ra cặp sức lực):
//   1. Chạy lần 1: ghi mốc và in các ô có giá trị hợp lý.
//   2. Làm một giao dịch có số tiền biết trước (bán hoặc mua một món), rồi chạy lần 2.
//   3. Ô nào đổi đúng bằng số tiền giao dịch chính là ô tiền. Ô đổi theo lượt đánh quái (kinh nghiệm...) cũng lộ ở
//      đây, phân biệt bằng con số đổi: tiền đổi đúng bằng giá giao dịch.
// Tìm ra thì ghi offset vào GameAddresses.Inventory kèm bằng chứng, đừng để nó chỉ nằm trong probe.
//
// Chỉ ĐỌC, không gửi lệnh nào vào client.
public static class InventoryMoneyProbe {
	public const string BuildStamp = "INVENTORY-MONEY-20260924-01";
	private const int WindowStart = 0x0000;
	private const int WindowEnd = 0x1000;
	private const int ChunkSize = 0x100;
	// Trên mức này coi là rác/con trỏ/hash chứ không phải số tiền: vùng đối tượng có nhiều khối dữ liệu ngẫu nhiên, lấy
	// trần cao hơn thì tràn rác lên bảng. 1 vạn = 10.000 nên 50 triệu là 5.000 vạn, cao hơn xa mọi số đo được (2026-09-24).
	private const int MaximumPlausibleValue = 50_000_000;
	private const int MoneyUnit = 10_000;
	// Mốc lần chạy trước theo PID, để lọc vi sai giữa hai lần bấm.
	private static readonly ConcurrentDictionary<int, int[]> previousByProcess = new();

	public static string Read(GameWindow game) {
		StringBuilder text = new();
		text.AppendLine("===== Tiền vạn trong túi =====");
		text.AppendLine($"BuildStamp = {BuildStamp}");
		text.AppendLine($"PID = {game.ProcessId} | Nhân vật = {game.CharacterName}");
		try {
			int[]? values = ReadWindow(game.ProcessId, out IntPtr root, out string failure);
			if (values == null) return text.AppendLine(failure).ToString();
			text.AppendLine($"StrengthRoot = Game.exe+0x{GameAddresses.Globals.StrengthRoot:X} -> đối tượng = 0x{root.ToInt64():X8}");

			// Các client khác đang mở, để phân loại RIÊNG/CHUNG: tiền là dữ liệu của từng nhân vật nên KHÔNG thể CHUNG.
			List<int[]> others = [];
			foreach (Process other in Process.GetProcessesByName("Game")) {
				using (other) {
					if (other.Id == game.ProcessId) continue;
					int[]? otherValues = ReadWindow(other.Id, out _, out _);
					if (otherValues != null) others.Add(otherValues);
				}
			}

			bool hasPrevious = previousByProcess.TryGetValue(game.ProcessId, out int[]? previous) && previous.Length == values.Length;
			previousByProcess[game.ProcessId] = values;
			text.AppendLine(hasPrevious
				? "Đã có mốc lần chạy trước: cột ĐỔI = giá trị hiện tại trừ giá trị lần trước."
				: "Lần chạy đầu: mới ghi mốc. Làm một giao dịch có số tiền biết trước rồi chạy lại để lọc vi sai.");
			text.AppendLine($"Đối chiếu {others.Count} client khác. Chỉ liệt kê ô 1..{MaximumPlausibleValue:N0} khác nhau giữa các nhân vật (hoặc vừa đổi).");
			text.AppendLine("VẠN = giá trị / 10.000, để so nếu ô này lưu số xu thay vì lưu sẵn theo vạn.");
			text.AppendLine();
			text.AppendLine("Offset    Giá trị      VẠN      Loại    ĐỔI");

			int listed = 0;
			for (int index = 0; index < values.Length; index++) {
				int value = values[index];
				int offset = WindowStart + index * sizeof(int);
				// Chỉ tính đổi khi CẢ HAI phía đều trong khoảng hợp lý, để ô hash/ngẫu nhiên không lọt vào bảng.
				int? change = hasPrevious && IsPlausible(previous![index]) && IsPlausible(value) && previous[index] != value ? value - previous[index] : null;
				bool plausible = value >= 1 && value <= MaximumPlausibleValue;
				if (! plausible && change == null) continue;
				string kind = others.Count == 0 ? "" : others.Any(other => other[index] != value) ? "RIÊNG" : "CHUNG";
				// Hai nhân vật cùng đọc ra y hệt thì không thể là tiền, trừ khi vừa đổi.
				if (kind == "CHUNG" && change == null) continue;
				string vanText = value >= MoneyUnit ? $"{value / (double)MoneyUnit:F1}" : "";
				string mark = offset == GameAddresses.Inventory.CurrentStrength ? "  <= sức lực HIỆN TẠI (đã biết)"
					: offset == GameAddresses.Inventory.MaximumStrength ? "  <= sức lực TỐI ĐA (đã biết)"
					: offset == GameAddresses.Inventory.Money ? "  <= TIỀN, đơn vị xu (đã biết, chủ dự án xác nhận)" : "";
				string changeText = change == null ? "" : $"ĐỔI {change:+#;-#;0}";
				text.AppendLine($"+0x{offset:X4}   {value,-11}  {vanText,-7}  {kind,-6}  {changeText}{mark}");
				listed++;
			}
			if (listed == 0) text.AppendLine("(không có ô nào thoả điều kiện — client chưa vào game hẳn hoặc tất cả đều là hằng số dùng chung)");
		} catch (Exception ex) {
			text.AppendLine($"{ex.GetType().Name}: {ex.Message}");
		}
		return text.ToString();
	}

	private static bool IsPlausible(int value) => value >= 0 && value <= MaximumPlausibleValue;

	// Đọc theo khối 0x100 thay vì một lần: một khối không đọc được thì bỏ khối đó (để 0) chứ không hỏng cả phép dò.
	private static int[]? ReadWindow(int processId, out IntPtr root, out string failure) {
		root = IntPtr.Zero;
		failure = "";
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) {
				failure = "Game.exe không tồn tại.";
				return null;
			}
			root = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.StrengthRoot));
			if (root == IntPtr.Zero) {
				failure = "Con trỏ bằng 0 — client chưa vào game hẳn.";
				return null;
			}
			int[] values = new int[(WindowEnd - WindowStart) / sizeof(int)];
			for (int offset = WindowStart; offset < WindowEnd; offset += ChunkSize) {
				byte[] chunk = reader.ReadBytes(IntPtr.Add(root, offset), ChunkSize);
				if (chunk.Length == ChunkSize) Buffer.BlockCopy(chunk, 0, values, offset - WindowStart, ChunkSize);
			}
			return values;
		} catch (Exception ex) {
			failure = $"{ex.GetType().Name}: {ex.Message}";
			return null;
		}
	}
}
