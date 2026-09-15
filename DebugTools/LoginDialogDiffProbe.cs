namespace Auto.DebugTools;

using System.Runtime.InteropServices;
using System.Text;
using Auto.Runtime;
using Auto.Utils;

// Dò con trỏ hộp thoại đăng nhập bằng ĐÚNG cách Resource/GAME-ADDRESSES-GUIDE.md §5.1 quy định: chụp toàn bộ ảnh
// Game.exe ở hai trạng thái runtime khác nhau rồi lọc ô nào đổi.
//
// Vì sao bỏ cách cũ: probe trước giả định offset +0x54 và +0x58 của AutoFS còn đúng ở client này. Giả định đó vô căn
// cứ — §1 của chính tài liệu ghi "Không chuyển địa chỉ từ AutoFS sang DEV auto chỉ vì hai client có cấu trúc tương
// tự", và AutoFS viết cho client khác. Lượt PID=13116 ngày 2026-09-12 ra 0 ứng viên, đúng như hệ quả của giả định sai.
//
// Cách này KHÔNG giả định offset nào. Dấu hiệu duy nhất nó dùng: hộp thoại đang mở thì ô chứa con trỏ tới đối tượng
// đó; đóng hộp thoại thì ô về 0 hoặc đổi giá trị.
//
// CHỈ ĐỌC bộ nhớ. Người dùng tự bấm nút trong client giữa các lần chụp — đó là "thay đổi duy nhất trạng thái cần tìm"
// ở bước 2, không có cách nào tự động vì chính cái nút đó là thứ đang đi tìm.
internal static class LoginDialogDiffProbe {
	private const string BuildStamp = "LOGIN-DIALOG-DIFF-20260912-02";
	private const int ChunkSize = 0x100000;
	private const int MaximumReportedSlots = 60;
	// Đầu mỗi đối tượng chụp bấy nhiêu byte. AutoFS đọc sâu nhất tới +0x106C nhưng cờ hiển thị thường nằm gần đầu;
	// lấy 0x400 để giữ bộ nhớ trong tầm kiểm soát.
	private const int ObjectWindow = 0x400;
	private const int MaximumTrackedObjects = 60000;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern int VirtualQueryEx(IntPtr processHandle, IntPtr address, out MemoryBasicInformation buffer, int length);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);

	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryBasicInformation {
		public IntPtr BaseAddress;
		public IntPtr AllocationBase;
		public uint AllocationProtect;
		public IntPtr RegionSize;
		public uint State;
		public uint Protect;
		public uint Type;
	}

	private readonly record struct Region(long Start, long End);

	// Mỗi ô con trỏ trong ảnh kèm theo ObjectWindow byte đầu của đối tượng nó trỏ tới.
	private sealed record Snapshot(int ProcessId, long ImageBase, byte[] Image, List<Region> Readable,
		Dictionary<long, long> PointerSlots, Dictionary<long, byte[]> ObjectHeads, DateTime TakenUtc);

	// Giữ giữa hai lần bấm. Auto không tắt giữa chừng nên để trong bộ nhớ là đủ, không cần ghi file.
	private static Snapshot? previous;
	private static int captureIndex;

	public static string Reset() {
		previous = null;
		captureIndex = 0;
		return "Đã xoá ảnh chụp cũ. Lần bấm tới sẽ là ảnh chụp số 1.";
	}

	public static string Run(GameWindow game) {
		StringBuilder output = new();
		output.AppendLine($"{BuildStamp} | PID={game.ProcessId} | Mode=CHỈ_ĐỌC");
		try {
			Snapshot current = Capture(game.ProcessId);
			if (current.Image.Length == 0) return output.AppendLine("Không đọc được ảnh Game.exe.").ToString();

			if (previous == null || previous.ProcessId != current.ProcessId) {
				if (previous != null && previous.ProcessId != current.ProcessId) {
					output.AppendLine($"Ảnh chụp cũ thuộc PID={previous.ProcessId}, khác PID hiện tại — bỏ, chụp lại từ đầu.");
				}
				previous = current;
				captureIndex = 1;
				output.AppendLine($"Đã chụp ảnh số 1 lúc {current.TakenUtc.ToLocalTime():HH:mm:ss} | {current.Image.Length / 1024} KB.");
				output.AppendLine("Giờ bấm nút trong client để sang màn kế, rồi chạy lại công cụ này.");
				return output.ToString();
			}

			captureIndex++;
			output.AppendLine($"So ảnh chụp số {captureIndex - 1} với số {captureIndex}.");
			List<(long Rva, long Before, long After)> freed = [];
			List<(long Rva, long Before, long After)> changed = [];
			int length = Math.Min(previous.Image.Length, current.Image.Length);
			for (int index = 0; index + 4 <= length; index += 4) {
				long before = (uint)BitConverter.ToInt32(previous.Image, index);
				long after = (uint)BitConverter.ToInt32(current.Image, index);
				if (before == after) continue;
				// Chỉ giữ ô mà TRƯỚC đó là con trỏ heap hợp lệ, căn 4 byte — đúng dạng con trỏ tới đối tượng hộp thoại.
				if ((before & 3) != 0 || before == 0) continue;
				if (before >= previous.ImageBase && before < previous.ImageBase + previous.Image.Length) continue;
				if (! IsInside(previous.Readable, before)) continue;
				if (after == 0) freed.Add((index, before, after));
				else changed.Add((index, before, after));
			}

			output.AppendLine($"Ô con trỏ heap chuyển thành 0: {freed.Count} | Chuyển sang giá trị khác: {changed.Count}");
			output.AppendLine();
			output.AppendLine("=== Ô về 0 (dấu hiệu đối tượng vừa bị huỷ — ứng viên mạnh nhất) ===");
			foreach ((long rva, long before, long after) in freed.Take(MaximumReportedSlots)) {
				output.AppendLine($"ỨNG_VIÊN | Rva=0x{rva:X8} | Va=0x{current.ImageBase + rva:X8} | Trước=0x{before:X8} | Sau=0x{after:X8}");
			}
			if (freed.Count > MaximumReportedSlots) output.AppendLine($"... còn {freed.Count - MaximumReportedSlots} ô nữa.");
			if (freed.Count == 0) {
				output.AppendLine("(không có ô nào)");
				output.AppendLine();
				output.AppendLine("=== Ô đổi sang con trỏ khác ===");
				foreach ((long rva, long before, long after) in changed.Take(MaximumReportedSlots)) {
					output.AppendLine($"ỨNG_VIÊN | Rva=0x{rva:X8} | Va=0x{current.ImageBase + rva:X8} | Trước=0x{before:X8} | Sau=0x{after:X8}");
				}
				if (changed.Count > MaximumReportedSlots) output.AppendLine($"... còn {changed.Count - MaximumReportedSlots} ô nữa.");
			}

			output.AppendLine();
			output.AppendLine("=== Đối tượng mà ô con trỏ trỏ tới có nội dung đổi ===");
			output.AppendLine($"Đối tượng theo dõi được ở cả hai lần chụp: {previous.ObjectHeads.Keys.Count(key => current.ObjectHeads.ContainsKey(key))}");
			List<(long Rva, long Target, int ChangedDwords, string FirstChange)> objectChanges = [];
			foreach ((long slotRva, byte[] beforeHead) in previous.ObjectHeads) {
				if (! current.ObjectHeads.TryGetValue(slotRva, out byte[]? afterHead)) continue;
				if (previous.PointerSlots[slotRva] != current.PointerSlots[slotRva]) continue;
				int changedDwords = 0;
				string firstChange = "";
				for (int fieldOffset = 0; fieldOffset + 4 <= ObjectWindow; fieldOffset += 4) {
					long beforeValue = (uint)BitConverter.ToInt32(beforeHead, fieldOffset);
					long afterValue = (uint)BitConverter.ToInt32(afterHead, fieldOffset);
					if (beforeValue == afterValue) continue;
					changedDwords++;
					if (firstChange.Length == 0) firstChange = $"+0x{fieldOffset:X3}: 0x{beforeValue:X8} -> 0x{afterValue:X8}";
				}
				if (changedDwords > 0) objectChanges.Add((slotRva, previous.PointerSlots[slotRva], changedDwords, firstChange));
			}
			output.AppendLine($"Đối tượng có nội dung đổi: {objectChanges.Count}");
			foreach ((long rva, long target, int changedDwords, string firstChange) in objectChanges.OrderBy(item => item.ChangedDwords).Take(MaximumReportedSlots)) {
				output.AppendLine($"ĐỐI_TƯỢNG_ĐỔI | Rva=0x{rva:X8} | Va=0x{current.ImageBase + rva:X8} | Trỏ=0x{target:X8} | SốDwordĐổi={changedDwords} | {firstChange}");
			}
			if (objectChanges.Count > MaximumReportedSlots) output.AppendLine($"... còn {objectChanges.Count - MaximumReportedSlots} đối tượng nữa.");

			previous = current;
			output.AppendLine();
			output.AppendLine($"Đã giữ ảnh chụp số {captureIndex} làm mốc. Bấm nút kế trong client rồi chạy lại để so tiếp.");
		} catch (Exception ex) {
			output.AppendLine($"Lỗi: {ex.GetType().Name}: {ex.Message}");
		}
		return output.ToString();
	}

	private static Snapshot Capture(int processId) {
		using MemoryReader reader = new(processId);
		IntPtr moduleBase = reader.GetModuleBase("Game.exe");
		long imageBase = moduleBase.ToInt64();
		int imageSize = 0;
		using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId)) {
			foreach (System.Diagnostics.ProcessModule module in process.Modules) {
				if (module.ModuleName.Equals("Game.exe", StringComparison.OrdinalIgnoreCase)) imageSize = module.ModuleMemorySize;
			}
		}
		byte[] image = new byte[imageSize];
		for (long offset = 0; offset < imageSize; offset += ChunkSize) {
			int length = (int)Math.Min(ChunkSize, imageSize - offset);
			byte[] block = reader.ReadMemory((IntPtr)(imageBase + offset), length);
			// Trang không đọc được để nguyên 0; hai ảnh chụp cùng bị 0 nên không sinh khác biệt giả.
			if (block.Length == length) Array.Copy(block, 0, image, offset, length);
		}

		// Chụp thêm đầu mỗi đối tượng mà ảnh đang trỏ tới. Lượt PID=31404 ngày 2026-09-12 cho thấy ô con trỏ KHÔNG đổi
		// khi hộp thoại đóng (0 ô về 0 trong cả 32 MB), nên dấu hiệu phải nằm bên trong đối tượng chứ không ở ô.
		List<Region> readable = ReadReadableRegions(processId);
		Dictionary<long, long> pointerSlots = [];
		Dictionary<long, byte[]> objectHeads = [];
		for (int index = 0; index + 4 <= image.Length; index += 4) {
			long value = (uint)BitConverter.ToInt32(image, index);
			if ((value & 3) != 0 || value == 0) continue;
			if (value >= imageBase && value < imageBase + imageSize) continue;
			if (! IsInside(readable, value)) continue;
			if (objectHeads.Count >= MaximumTrackedObjects) break;
			byte[] head = reader.ReadMemory((IntPtr)value, ObjectWindow);
			if (head.Length != ObjectWindow) continue;
			pointerSlots[index] = value;
			objectHeads[index] = head;
		}
		return new Snapshot(processId, imageBase, image, readable, pointerSlots, objectHeads, DateTime.UtcNow);
	}

	private static bool IsInside(List<Region> regions, long address) {
		foreach (Region region in regions) {
			if (region.Start > address) break;
			if (address >= region.Start && address < region.End) return true;
		}
		return false;
	}

	private static List<Region> ReadReadableRegions(int processId) {
		List<Region> regions = [];
		IntPtr handle = OpenProcess(0x0410, false, processId);
		if (handle == IntPtr.Zero) return regions;
		try {
			long address = 0x10000;
			while (address < 0x7FFF0000) {
				if (VirtualQueryEx(handle, (IntPtr)address, out MemoryBasicInformation information, Marshal.SizeOf<MemoryBasicInformation>()) == 0) break;
				long size = information.RegionSize.ToInt64();
				if (size <= 0) break;
				const uint MemCommit = 0x1000;
				const uint ReadableProtect = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
				const uint Blocked = 0x100 | 0x01;
				if (information.State == MemCommit && (information.Protect & ReadableProtect) != 0 && (information.Protect & Blocked) == 0) {
					regions.Add(new Region(information.BaseAddress.ToInt64(), information.BaseAddress.ToInt64() + size));
				}
				address = information.BaseAddress.ToInt64() + size;
			}
		} finally {
			CloseHandle(handle);
		}
		regions.Sort((left, right) => left.Start.CompareTo(right.Start));
		return regions;
	}
}
