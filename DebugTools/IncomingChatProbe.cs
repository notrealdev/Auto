namespace Auto.DebugTools;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Auto.Utils;

// Finds where the client keeps chat messages it RECEIVED (owner 2026-10-15: show other players' messages in a chat box).
// Market/ChatHistoryReader.cs only covers messages this client sent, and AutoFS has no incoming-chat reader to port.
//
// Use: from another character, send this character a message containing Marker (e.g. "AUTOIN1"), then run the probe.
// Read only: it never writes to the client.
//
// What two runs on PID 12972 MaiAnhNhe showed (2026-10-15 09:18 and 09:31, sender XinLỗiEm):
//   - Each received line sits in a heap record. Measured at 0x0EDC0C48 (text "AUTOIN2" at +0x68):
//       +0x00 int 1 | +0x04 sender name, zero padded | +0x48 "2B 01 FF CA FF FF 03 92 00 A7 FF 01 DC DC DC FF 03 00 00 00
//       FF" | sender + ":" | 02 04 | message | 00. Bytes after the 00 are leftovers of an older, longer line.
//   - The heap word 0x0EDF00BC pointed at the record start both times (0x263B2758 for AUTOIN1, 0x0EDC0C48 for AUTOIN2), so
//     something keeps a pointer to the newest record. Its static root is still unknown.
//   - A static buffer inside Game.exe (0x00EDCDF2 at scan time) held "AUTOIN2" briefly and a world chat line moments later:
//     a transient receive buffer, not a list.
// The record layout above is a HYPOTHESIS from two samples of one sender; this probe checks it on every hit.
internal static class IncomingChatProbe {
	public const string Marker = "AUTOIN";
	private const long ScanStart = 0x00010000;
	private const long ScanEnd = 0x7FFF0000;
	private const int ContextBefore = 0x80;
	private const int ContextAfter = 0x60;
	private const int MaximumHits = 40;
	// Record start search: the int 1 lies this far before the text at most (0x5D + a long name + ':' + 02 04).
	private const int RecordSearchBack = 0x100;
	private const int RecordLinePrefixOffset = GameAddresses.Chat.InboxLinePrefix;
	private const int RecordNameOffset = GameAddresses.Chat.InboxSender;
	private const int RecordNameLength = GameAddresses.Chat.InboxSenderLength;
	private static readonly byte[] LinePrefixTail = [0x03, 0x00, 0x00, 0x00, 0xFF];
	// Words shown around each pointer to a record, to see whether it is one field or a list of records.
	private const int HolderDumpRadius = 0x40;
	private const int RecordLinkScanLength = 0x300;
	private const int MaximumHoldersShown = 12;
	// Forward search from ChannelManager: the root object is read wide (ChatHistoryReader uses up to +0x1280 of the input box),
	// deeper objects narrower to keep the walk to a few seconds.
	private const int PathDepth = 5;
	private const int RootReadLength = 0x2000;
	private const int ObjectReadLength = 0x800;
	private const int MaximumVisitedObjects = 300000;
	private const int MaximumPaths = 40;

	private const int ProcessQueryInformation = 0x0400;
	private const int ProcessVmRead = 0x0010;
	private const int MemCommit = 0x1000;
	private const int PageNoAccess = 0x01;
	private const int PageGuard = 0x100;
	private const int MemImage = 0x1000000;

	private sealed record Hit(long Address, long RegionBase, int RegionType);


	public static string Run(int processId) {
		StringBuilder output = new();
		output.AppendLine($"INCOMING_CHAT_PROBE | PID={processId} | Marker={Marker} | {DateTime.Now:HH:mm:ss}");
		IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
		if (handle == IntPtr.Zero) return output.AppendLine($"OpenProcess lỗi | Win32Error={Marshal.GetLastWin32Error()}").ToString();
		try {
			(long moduleBase, long moduleEnd) = GetGameModuleRange(processId);
			output.AppendLine($"Game.exe = 0x{moduleBase:X8}..0x{moduleEnd:X8}");
			byte[] marker = Encoding.ASCII.GetBytes(Marker);
			List<Hit> hits = [];
			List<(long Base, int Size)> regions = [];
			foreach ((long regionBase, int size, int type) in GetReadableRegions(handle)) {
				regions.Add((regionBase, size));
				if (hits.Count >= MaximumHits) continue;
				byte[] data = ReadMemory(handle, regionBase, size);
				int start = 0;
				while (data.Length > 0 && hits.Count < MaximumHits) {
					int found = data.AsSpan(start).IndexOf(marker);
					if (found < 0) break;
					hits.Add(new Hit(regionBase + start + found, regionBase, type));
					start += found + marker.Length;
				}
			}
			output.AppendLine($"SốChỗThấy={hits.Count}");
			if (hits.Count == 0) return output.AppendLine($"Không thấy '{Marker}'. Gửi một tin có chữ {Marker}1 tới nhân vật này rồi chạy lại.").ToString();

			// Record start of each hit, when the layout matches.
			Dictionary<long, int> recordToHit = [];
			for (int index = 0; index < hits.Count; index++) {
				Hit hit = hits[index];
				output.AppendLine();
				output.AppendLine($"#{index} | 0x{hit.Address:X8} | Vùng=0x{hit.RegionBase:X8} | {(hit.RegionType == MemImage ? "IMAGE" : "HEAP/PRIVATE")}");
				long record = FindRecordStart(handle, hit.Address);
				if (record == 0) {
					output.AppendLine("BảnGhi=KHÔNG_KHỚP khuôn (int 1 + tên + '2B 01' ở +0x48)");
					byte[] context = ReadMemory(handle, hit.Address - ContextBefore, ContextBefore + ContextAfter);
					AppendHexDump(output, hit.Address - ContextBefore, context);
				} else {
					recordToHit.TryAdd(record, index);
					output.AppendLine($"BảnGhi=0x{record:X8} (chữ ở +0x{hit.Address - record:X}) | {DescribeRecord(handle, record)}");
				}
			}
			if (recordToHit.Count == 0) return output.ToString();

			// Links between records: in the 09:38 run the AUTOIN3 record 0x0EDEFDD8 held, at +0x200, a pointer to the AUTOIN2
			// record 0x0EDC0C48, so records may form a list. Every aligned word of every record pointing at another record start
			// (found here or matching the layout) is printed.
			output.AppendLine();
			output.AppendLine("LiênKếtGiữaBảnGhi (từ trong bản ghi trỏ tới đầu bản ghi khác):");
			foreach (long record in recordToHit.Keys) AppendRecordLinks(output, handle, record, recordToHit);

			// Exact pointers to a record start, and what sits around them.
			Dictionary<long, List<long>> holders = FindExactPointers(handle, regions, recordToHit.Keys);
			output.AppendLine();
			foreach ((long record, List<long> holderList) in holders) {
				output.AppendLine($"ConTrỏTớiĐầuBảnGhi 0x{record:X8} (chữ #{recordToHit[record]}): {holderList.Count}{(holderList.Count == 0 ? "" : $" | {string.Join(", ", holderList.Take(MaximumHoldersShown).Select(holder => $"0x{holder:X8}"))}")}");
				foreach (long holder in holderList.Take(MaximumHoldersShown)) AppendHolderNeighbourhood(output, handle, holder);
			}

			// Forward search from the chat ChannelManager, the static root ChatHistoryReader already uses for the input box.
			// The reverse scan of the 09:41 run only reached the thread stack (0x001919DC) and look-alike tables, so it was replaced.
			long channelManager = BitConverter.ToUInt32(ReadMemory(handle, moduleBase + Auto.Market.ChatEngine.ChannelManagerRva, 4).Concat(new byte[4]).ToArray(), 0);
			List<string> paths = FindPathsFromRoot(handle, regions, channelManager, recordToHit);
			output.AppendLine();
			output.AppendLine($"ĐườngTừChannelManager [Game.exe+0x{Auto.Market.ChatEngine.ChannelManagerRva:X}]=0x{channelManager:X8} tới bản ghi (mỗi '→' = đọc con trỏ ở offset đó; tối đa {PathDepth} bước): {paths.Count}");
			foreach (string path in paths) output.AppendLine($"  {path}");
			return output.ToString();
		} catch (Exception ex) {
			return output.AppendLine($"Lỗi: {ex.GetType().Name}: {ex.Message}").ToString();
		} finally {
			CloseHandle(handle);
		}
	}

	private static void AppendRecordLinks(StringBuilder output, IntPtr handle, long record, Dictionary<long, int> recordToHit) {
		byte[] data = ReadMemory(handle, record, RecordLinkScanLength);
		for (int offset = 0; offset + 4 <= data.Length; offset += 4) {
			uint value = BitConverter.ToUInt32(data, offset);
			if (value == record || value < 0x10000) continue;
			bool known = recordToHit.TryGetValue(value, out int otherHit);
			if (! known && ! LooksLikeRecord(handle, value)) continue;
			output.AppendLine($"  0x{record:X8} (chữ #{recordToHit[record]}) +0x{offset:X} → 0x{value:X8}{(known ? $" (chữ #{otherHit})" : "")} | {DescribeRecord(handle, value)}");
		}
	}

	private static bool LooksLikeRecord(IntPtr handle, long address) {
		byte[] probe = ReadMemory(handle, address, RecordLinePrefixOffset + 2);
		return probe.Length == RecordLinePrefixOffset + 2 && BitConverter.ToInt32(probe, 0) == 1 && probe[RecordLinePrefixOffset] == 0x2B && probe[RecordLinePrefixOffset + 1] == 0x01;
	}

	// Walks back from the text to an int 1 whose +0x48 holds the line prefix 2B 01. Returns 0 when nothing matches.
	private static long FindRecordStart(IntPtr handle, long textAddress) {
		long from = textAddress - RecordSearchBack;
		byte[] data = ReadMemory(handle, from, RecordSearchBack);
		for (int offset = data.Length - 4; offset >= 0; offset--) {
			if (BitConverter.ToInt32(data, offset) != 1) continue;
			int prefix = offset + RecordLinePrefixOffset;
			if (prefix + 1 < data.Length && data[prefix] == 0x2B && data[prefix + 1] == 0x01) return from + offset;
		}
		return 0;
	}

	// Sender name field and the line text up to its 00, decoded.
	private static string DescribeRecord(IntPtr handle, long record) {
		byte[] data = ReadMemory(handle, record, 0x200);
		if (data.Length < RecordLinePrefixOffset + 1) return "đọc lỗi";
		string name = DecodeUntilZero(data, RecordNameOffset, RecordNameLength);
		// The colour prefix holds zero bytes; the visible line ("Sender:" 02 04 message) starts after its "03 00 00 00 FF".
		int prefixEnd = data.AsSpan(RecordLinePrefixOffset).IndexOf(LinePrefixTail);
		int lineStart = prefixEnd < 0 ? RecordLinePrefixOffset + 2 : RecordLinePrefixOffset + prefixEnd + LinePrefixTail.Length;
		int lineEnd = Array.IndexOf(data, (byte)0, lineStart);
		if (lineEnd < 0) lineEnd = data.Length;
		byte[] line = data[lineStart..lineEnd].Select(value => value < 0x20 ? (byte)'|' : value).ToArray();
		return $"TênNgườiGửi={name} | Dòng={LegacyVietnameseText.Decode(line)} | +0x00={BitConverter.ToString(data, 0, 4)} | Trước=" + BitConverter.ToString(ReadMemory(handle, record - 8, 8));
	}

	private static string DecodeUntilZero(byte[] data, int start, int maximumLength) {
		int end = Array.IndexOf(data, (byte)0, start, Math.Min(maximumLength, data.Length - start));
		if (end < 0) end = Math.Min(start + maximumLength, data.Length);
		return LegacyVietnameseText.Decode(data[start..end]);
	}

	private static Dictionary<long, List<long>> FindExactPointers(IntPtr handle, List<(long Base, int Size)> regions, IEnumerable<long> targets) {
		Dictionary<long, List<long>> result = targets.ToDictionary(target => target, _ => new List<long>());
		foreach ((long regionBase, int size) in regions) {
			byte[] data = ReadMemory(handle, regionBase, size);
			for (int offset = 0; offset + 4 <= data.Length; offset += 4) {
				if (result.TryGetValue(BitConverter.ToUInt32(data, offset), out List<long>? list)) list.Add(regionBase + offset);
			}
		}
		return result;
	}

	// Every word within HolderDumpRadius of the holder; words pointing at another record of the same layout are decoded, so a
	// list of message pointers shows up as several decoded lines next to each other.
	private static void AppendHolderNeighbourhood(StringBuilder output, IntPtr handle, long holder) {
		output.AppendLine($"  Quanh ô giữ 0x{holder:X8}:");
		byte[] data = ReadMemory(handle, holder - HolderDumpRadius, HolderDumpRadius * 2 + 4);
		for (int offset = 0; offset + 4 <= data.Length; offset += 4) {
			uint value = BitConverter.ToUInt32(data, offset);
			long at = holder - HolderDumpRadius + offset;
			string note = "";
			if (value > 0x10000 && LooksLikeRecord(handle, value)) {
				note = $" ← bản ghi chat: {DescribeRecord(handle, value)}";
			}
			output.AppendLine($"    {(at == holder ? ">" : " ")} 0x{at:X8} (+{at - holder,4}) = 0x{value:X8}{note}");
		}
	}

	// One step of a path from the root: the pointer read at Parent's object + Offset leads to Address.
	private sealed record PathStep(long Address, int Offset, PathStep? Parent);

	// Breadth first over pointer fields: shortest paths come first. Only 4-aligned values inside a readable region are
	// followed, and each object is read once.
	private static List<string> FindPathsFromRoot(IntPtr handle, List<(long Base, int Size)> regions, long root, Dictionary<long, int> recordToHit) {
		List<string> paths = [];
		if (root == 0) return paths;
		long[] bases = regions.Select(region => region.Base).ToArray();
		long[] ends = regions.Select(region => region.Base + region.Size).ToArray();
		bool IsReadable(long address) {
			int index = Array.BinarySearch(bases, address);
			if (index < 0) index = ~index - 1;
			return index >= 0 && address < ends[index];
		}
		HashSet<long> visited = [root];
		Queue<(PathStep Step, int Depth)> queue = new();
		queue.Enqueue((new PathStep(root, 0, null), 0));
		while (queue.Count > 0 && paths.Count < MaximumPaths) {
			(PathStep step, int depth) = queue.Dequeue();
			byte[] data = ReadMemory(handle, step.Address, depth == 0 ? RootReadLength : ObjectReadLength);
			for (int offset = 0; offset + 4 <= data.Length && paths.Count < MaximumPaths; offset += 4) {
				long value = BitConverter.ToUInt32(data, offset);
				if (recordToHit.TryGetValue(value, out int hitIndex)) {
					paths.Add($"{FormatPath(step)} → +0x{offset:X} = bản ghi chữ #{hitIndex} (0x{value:X8})");
					continue;
				}
				if (depth + 1 >= PathDepth || value % 4 != 0 || visited.Count >= MaximumVisitedObjects || ! IsReadable(value) || ! visited.Add(value)) continue;
				queue.Enqueue((new PathStep(value, offset, step), depth + 1));
			}
		}
		return paths;
	}

	private static string FormatPath(PathStep last) {
		List<int> offsets = [];
		for (PathStep? step = last; step?.Parent != null; step = step.Parent) offsets.Add(step.Offset);
		offsets.Reverse();
		return $"[Game.exe+0x{Auto.Market.ChatEngine.ChannelManagerRva:X}]{string.Concat(offsets.Select(offset => $" → +0x{offset:X}"))}";
	}
	private static void AppendHexDump(StringBuilder output, long baseAddress, byte[] data) {
		for (int row = 0; row < data.Length; row += 16) {
			int length = Math.Min(16, data.Length - row);
			string hex = BitConverter.ToString(data, row, length).Replace('-', ' ');
			StringBuilder text = new();
			for (int column = 0; column < length; column++) {
				byte value = data[row + column];
				text.Append(value >= 0x20 && value < 0x7F ? (char)value : '.');
			}
			output.AppendLine($"  0x{baseAddress + row:X8}  {hex,-47}  {text}");
		}
	}

	private static (long Base, long End) GetGameModuleRange(int processId) {
		using Process process = Process.GetProcessById(processId);
		foreach (ProcessModule module in process.Modules) {
			if (! string.Equals(module.ModuleName, GameAddresses.ModuleName, StringComparison.OrdinalIgnoreCase)) continue;
			long moduleBase = module.BaseAddress.ToInt64();
			return (moduleBase, moduleBase + module.ModuleMemorySize);
		}
		return (0, 0);
	}

	private static IEnumerable<(long Base, int Size, int Type)> GetReadableRegions(IntPtr handle) {
		long address = ScanStart;
		while (address < ScanEnd) {
			if (VirtualQueryEx(handle, new IntPtr(address), out MemoryBasicInformation mbi, Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero) {
				address += 0x1000;
				continue;
			}
			long regionBase = mbi.BaseAddress.ToInt64();
			long regionSize = mbi.RegionSize.ToInt64();
			if (regionSize > 0 && regionSize <= int.MaxValue && mbi.State == MemCommit && (mbi.Protect & PageNoAccess) == 0 && (mbi.Protect & PageGuard) == 0) {
				yield return (regionBase, (int)regionSize, mbi.Type);
			}
			long next = regionBase + regionSize;
			address = next > address ? next : address + 0x1000;
		}
	}

	private static byte[] ReadMemory(IntPtr handle, long address, int size) {
		if (address <= 0 || address > uint.MaxValue || size <= 0) return [];
		byte[] buffer = new byte[size];
		if (! ReadProcessMemory(handle, ToPointer(address), buffer, size, out IntPtr bytesRead) || bytesRead.ToInt64() <= 0) return [];
		if (bytesRead.ToInt64() < size) Array.Resize(ref buffer, (int)bytesRead.ToInt64());
		return buffer;
	}

	// Game.exe is 32-bit and Auto runs as x86: new IntPtr(long) throws above 0x7FFFFFFF there (OverflowException in the
	// 09:38 run on PID 12972, from a holder word whose value was not a pointer), so convert through uint.
	private static IntPtr ToPointer(long address) => IntPtr.Size == 4 ? new IntPtr(unchecked((int)(uint)address)) : new IntPtr(address);

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
	private static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesRead);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr VirtualQueryEx(IntPtr processHandle, IntPtr address, out MemoryBasicInformation memoryInfo, int memoryInfoLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);
}
