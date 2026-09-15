namespace Auto.DebugTools;

using System.Runtime.InteropServices;
using System.Text;
using Auto.Runtime;
using Auto.Utils;

// Tìm danh sách điểm đến của popup ĐIỂM CHUYỂN TIẾP bằng phép SO SÁNH TRƯỚC/SAU. Chỉ ĐỌC.
//
// Vì sao đổi hẳn cách làm: năm lần trước đều là đoán chỗ chứa danh sách rồi đọc thử, và cả năm đều sai —
//   1. entity tên "Cửa chuyển tiếp"        -> quét index 2..511, TênKhớp=0
//   2. layout DIRECT của menu NPC          -> Count=1236411690
//   3. layout AUTOFS (+0x1B4)              -> List=0x63706E5C, đảo byte ra "\npc", đang đọc giữa một chuỗi
//   4. đi theo con trỏ con của ModalState  -> rơi vào khung chat, bảng import DLL, mã máy
//   5. neo vào câu hỏi của popup           -> ba lần chạy ở Map 21/14/16 ra mốc ở ĐÚNG 3 địa chỉ giống hệt nhau,
//      tức câu hỏi đó nằm sẵn trong bộ nhớ bất kể popup có mở hay không, không dùng làm mốc được.
//
// Cách này không giả định gì về bố cục: quét mọi vùng nhớ đã cấp phát, gom tập chuỗi đọc được lúc popup ĐÓNG, rồi
// gom lại lúc popup MỞ và in ra phần CHÊNH LỆCH. Chữ nào chỉ có ở lần sau thì nó xuất hiện cùng popup.
//
// Cách quét vùng nhớ giống TalismanMenuProbe — cách đó đã dựng lại được bố cục menu Di ngoại phù.
internal static class TransitGatePopupProbe {
	private const string BuildStamp = "TRANSIT-GATE-DIFF-20260911-01";
	private const int ProcessQueryInformation = 0x0400;
	private const int ProcessVmRead = 0x0010;
	private const int MemCommit = 0x1000;
	private const int PageGuard = 0x100;
	private const long ScanUpperBound = 0x7FFF0000L;
	private const int MaximumRegionBytes = 0x0400_0000;
	private const long MaximumTotalScanBytes = 0x2000_0000L;
	private const int MinimumLetters = 4;
	private const int MinimumStringBytes = 5;
	private const int MaximumStringLength = 80;
	private const int MaximumReportedNewStrings = 600;
	// Tỉ lệ ký tự "sạch" tối thiểu. Lần chạy PID=32196 ngày 2026-09-11 ra 5215 chuỗi mới nhưng trần báo cáo bị rác
	// nhị phân 3 byte từ vùng mã và vùng đồ hoạ chiếm hết ("ồổ$", "0ẳớÀ@íV#"), đẩy chữ thật ra ngoài — trong đó có
	// "Xi Vưu mộ" ở 0x0C950064 là tên địa danh.
	private const double MinimumCleanRatio = 0.9;
	private const int MinimumDistinctCharacters = 5;

	// Ảnh chụp nền giữ giữa hai lần bấm. Khoá theo ProcessId để không so nhầm giữa các account.
	private static int baselineProcessId;
	private static DateTime baselineAtUtc;
	private static uint baselineModalState;
	private static HashSet<string>? baselineStrings;

	public static string Read(GameWindow game) {
		StringBuilder output = new();
		GameSnapshot snapshot = GameMemory.ReadSnapshot(game.ProcessId);
		output.AppendLine("===== Popup Điểm chuyển tiếp: so sánh trước/sau =====");
		output.AppendLine($"TRANSIT_GATE_DIFF | BuildStamp={BuildStamp} | Mode=READ_ONLY | ProcessId={game.ProcessId}"
			+ $" | Map={game.LastObservedMapId} | NhânVật={(snapshot.Success ? $"{snapshot.X}/{snapshot.Y}" : "không đọc được")}");
		uint modalState = ReadModalState(game.ProcessId);
		output.AppendLine($"TRẠNG THÁI POPUP | ModalState=0x{modalState:X8}");

		IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, game.ProcessId);
		if (handle == IntPtr.Zero) {
			output.AppendLine($"HỎNG | OpenProcess thất bại | Win32Error={Marshal.GetLastWin32Error()}");
			return output.ToString();
		}
		try {
			List<MemoryRegion> regions = EnumerateRegions(handle);
			Dictionary<string, uint> current = ScanStrings(handle, regions);
			output.AppendLine($"VÙNG NHỚ | Số vùng={regions.Count} | Tổng={regions.Sum(region => (long)region.Size) / 1024 / 1024}MB | Số chuỗi đọc được={current.Count}");

			bool haveBaseline = baselineStrings != null && baselineProcessId == game.ProcessId;
			if (! haveBaseline) {
				baselineProcessId = game.ProcessId;
				baselineAtUtc = DateTime.UtcNow;
				baselineModalState = modalState;
				baselineStrings = new HashSet<string>(current.Keys, StringComparer.Ordinal);
				output.AppendLine("===== BƯỚC 1/2: ĐÃ CHỤP NỀN =====");
				output.AppendLine($"Đã ghi nhớ {baselineStrings.Count} chuỗi lúc popup ĐÓNG.");
				output.AppendLine("Giờ đứng lên Điểm chuyển tiếp cho popup hiện ra, ĐỂ NGUYÊN popup rồi bấm Chạy lần nữa.");
				output.AppendLine("Lần bấm sau sẽ in ra những chữ CHỈ xuất hiện khi popup mở.");
				return output.ToString();
			}

			HashSet<string> baseline = baselineStrings!;
			List<KeyValuePair<string, uint>> added = current.Where(entry => ! baseline.Contains(entry.Key)).ToList();
			output.AppendLine("===== BƯỚC 2/2: CHỮ MỚI XUẤT HIỆN =====");
			output.AppendLine($"Nền chụp lúc {baselineAtUtc.ToLocalTime():HH:mm:ss} | ModalState lúc đó=0x{baselineModalState:X8} | {baseline.Count} chuỗi | Lần này {current.Count} chuỗi | Mới {added.Count} chuỗi");
			// Nền chụp lúc popup đã mở thì danh sách điểm đến nằm sẵn trong nền, phần "mới" sẽ không bao giờ chứa nó.
			if (baselineModalState != 0) output.AppendLine($"CẢNH BÁO: lúc chụp nền ĐÃ có popup mở (ModalState=0x{baselineModalState:X8}). Đóng hẳn popup, bấm lại để chụp nền sạch, rồi mới mở popup và bấm lần nữa.");
			foreach (KeyValuePair<string, uint> entry in added.OrderBy(entry => entry.Value).Take(MaximumReportedNewStrings)) {
				byte[] bytes = Convert.FromHexString(entry.Key);
				output.AppendLine($"MỚI | 0x{entry.Value:X8} | Hex={entry.Key} | {LegacyVietnameseText.Decode(bytes)}");
			}
			if (added.Count > MaximumReportedNewStrings) output.AppendLine($"... còn {added.Count - MaximumReportedNewStrings} chuỗi nữa, đã cắt bớt.");
			// Chụp lại nền để lần bấm kế tiếp là một phép so mới, không phải so với ảnh cũ đã lệch thời điểm.
			baselineAtUtc = DateTime.UtcNow;
			baselineModalState = modalState;
			baselineStrings = new HashSet<string>(current.Keys, StringComparer.Ordinal);
			output.AppendLine("Đã chụp lại nền theo lần chạy này; lần bấm tiếp theo sẽ so với thời điểm vừa rồi.");
		} finally {
			CloseHandle(handle);
		}
		return output.ToString();
	}

	// ClientAddressAudit.CheckModalState ghi: ModalState=0 nghĩa là không có modal nào đang mở.
	private static uint ReadModalState(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			return moduleBase == IntPtr.Zero ? 0 : unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState)));
		} catch {
			return 0;
		}
	}

	// Gom mọi chuỗi kết thúc bằng 0 đọc được trong toàn bộ vùng nhớ. Khoá là byte thô dạng hex nên hai chuỗi giống
	// nhau ở hai địa chỉ khác nhau vẫn tính là một — đúng ý đồ, vì thứ cần tìm là CHỮ mới, không phải địa chỉ mới.
	private static Dictionary<string, uint> ScanStrings(IntPtr handle, List<MemoryRegion> regions) {
		Dictionary<string, uint> found = new(StringComparer.Ordinal);
		foreach (MemoryRegion region in regions) {
			byte[] buffer = ReadRegion(handle, region);
			if (buffer.Length == 0) continue;
			int start = 0;
			while (start < buffer.Length) {
				while (start < buffer.Length && ! IsTextByte(buffer[start])) start++;
				int end = start;
				while (end < buffer.Length && IsTextByte(buffer[end]) && end - start < MaximumStringLength) end++;
				if (end - start >= MinimumStringBytes && end < buffer.Length && buffer[end] == 0) {
					string decoded = LegacyVietnameseText.Decode(buffer[start..end]);
					if (LooksLikeText(decoded)) found.TryAdd(Convert.ToHexString(buffer[start..end]), (uint)(region.Base + start));
				}
				start = end > start ? end + 1 : start + 1;
			}
		}
		return found;
	}

	// Chỉ giữ chuỗi trông như chữ người viết: đủ dài, đủ ký tự chữ, và gần như không lẫn ký hiệu lạ.
	private static bool LooksLikeText(string decoded) {
		if (decoded.Length < MinimumStringBytes) return false;
		if (decoded.Contains(".spr", StringComparison.OrdinalIgnoreCase) || decoded.Contains(".ini", StringComparison.OrdinalIgnoreCase)) return false;
		int letters = 0;
		int clean = 0;
		foreach (char character in decoded) {
			bool isLetter = char.IsLetter(character);
			if (isLetter) letters++;
			if (isLetter || char.IsDigit(character) || character is ' ' or '.' or ',' or '-' or '(' or ')' or '[' or ']' or '/' or '\'' or '!' or '?' or ':') clean++;
		}
		if (letters < MinimumLetters || (double)clean / decoded.Length < MinimumCleanRatio) return false;
		// Rác đồ hoạ giải mã ra toàn chữ Việt nên hai điều kiện trên không chặn được: "ÄAÄAồAồA", "ĐZĐZĐZốZ".
		// Chữ người viết thì hoặc có dấu cách, hoặc thuần ASCII; và không lặp đi lặp lại vài ký tự.
		if (decoded.Distinct().Count() < MinimumDistinctCharacters) return false;
		return decoded.Contains(' ') || decoded.All(char.IsAscii);
	}

	private static bool IsTextByte(byte value) {
		return value is >= 0x20 and <= 0x7E || value >= 0x80;
	}

	private static byte[] ReadRegion(IntPtr handle, MemoryRegion region) {
		byte[] buffer = new byte[region.Size];
		return ReadProcessMemory(handle, (IntPtr)region.Base, buffer, buffer.Length, out int read) && read == buffer.Length
			? buffer
			: [];
	}

	private static List<MemoryRegion> EnumerateRegions(IntPtr handle) {
		List<MemoryRegion> regions = [];
		long address = 0;
		long total = 0;
		while (address < ScanUpperBound && total < MaximumTotalScanBytes) {
			if (VirtualQueryEx(handle, (IntPtr)address, out MemoryBasicInformation information, Marshal.SizeOf<MemoryBasicInformation>()) == 0) break;
			long regionSize = (long)information.RegionSize;
			if (regionSize <= 0) break;
			bool readable = information.State == MemCommit
				&& (information.Protect & PageGuard) == 0
				&& (information.Protect & 0xEE) != 0;
			if (readable && regionSize <= MaximumRegionBytes) {
				regions.Add(new MemoryRegion((long)information.BaseAddress, (int)regionSize));
				total += regionSize;
			}
			address = (long)information.BaseAddress + regionSize;
		}
		return regions;
	}

	private readonly record struct MemoryRegion(long Base, int Size);

	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryBasicInformation {
		public IntPtr BaseAddress;
		public IntPtr AllocationBase;
		public int AllocationProtect;
		public IntPtr RegionSize;
		public int State;
		public int Protect;
		public int Type;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int size, out int read);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern int VirtualQueryEx(IntPtr handle, IntPtr address, out MemoryBasicInformation information, int length);
}
