namespace Auto.Loot;

using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Auto.Utils;

public sealed class SpriteItemScanner {
	private const int ProcessVmRead = 0x0010;
	private const int ProcessQueryInformation = 0x0400;
	private const int MemCommit = 0x1000;
	private const int PageNoAccess = 0x01;
	private const int PageGuard = 0x100;
	private const int CoordinateOffsetBeforeSprite = 0x20;
	private const int ItemNameOffsetFromCoordinate = GameAddresses.Item.GroundName - GameAddresses.Item.GroundInternalX;
	private const int QualityCodeOffsetA = GameAddresses.Item.GroundQualityCodeA - GameAddresses.Item.GroundInternalX;
	private const int QualityCodeOffsetB = GameAddresses.Item.GroundQualityCodeB - GameAddresses.Item.GroundInternalX;
	private const int MaximumItemNameLength = 64;
	private const int MaximumSpriteLength = 96;
	private const int FingerprintBytesBeforeSprite = 0x20;
	private const int FingerprintBytesAfterSprite = 0x70;
	private const int MaximumModuleScanBytes = 32 * 1024 * 1024;
	private const int CachedWindowBeforeCoordinate = 0x2B0;
	private const int CachedWindowLength = 0x470;
	private const int FullScanRefreshMilliseconds = 600000;
	private const int ItemRecordStride = GameAddresses.Item.GroundRecordStride;
	private const int CachedSlotsBeforeAnchor = 8;
	private const int CachedSlotsAfterAnchor = 64;
	private const int MaximumCachedBatchLength = 1024 * 1024;
	private const int CachedBatchMaximumGap = ItemRecordStride * 2;
	private const int RecentResultMilliseconds = 400;
	private const int ItemStateA0Offset = GameAddresses.Item.GroundStateA0;
	private const int ItemStateA8Offset = GameAddresses.Item.GroundStateA8;
	private const int ItemStateACOffset = GameAddresses.Item.GroundStateAC;
	private const int ItemStateD8Offset = GameAddresses.Item.GroundStateD8;
	private const int ItemStateDCOffset = GameAddresses.Item.GroundStateDC;
	private static readonly byte[] ItemSpritePattern = Encoding.ASCII.GetBytes("\\spr\\obj\\item\\");
	private readonly List<long> cachedCoordinateAddresses = new();
	private int cachedProcessId;
	private DateTime nextFullScanUtc = DateTime.MinValue;
	private LootSpriteScanResult? recentResult;
	private int recentProcessId;
	private int recentCenterRawX;
	private int recentCenterRawY;
	private int recentRangeMap;
	private int recentMaxCount;
	private DateTime recentResultUntilUtc = DateTime.MinValue;

	public List<LootSnapshot> Find(int processId, int centerRawX, int centerRawY, int rangeMap, int maxCount) {
		return Scan(processId, centerRawX, centerRawY, rangeMap, maxCount).Items;
	}

	public LootSpriteScanResult Scan(int processId, int centerRawX, int centerRawY, int rangeMap, int maxCount) {
		DateTime now = DateTime.UtcNow;
		if (CanUseRecentResult(processId, centerRawX, centerRawY, rangeMap, maxCount, now)) return recentResult!;

		using ScannerReader reader = new(processId);
		if (cachedProcessId == processId && cachedCoordinateAddresses.Count > 0 && now < nextFullScanUtc) return RememberRecentResult(processId, centerRawX, centerRawY, rangeMap, maxCount, ScanCached(processId, reader, centerRawX, centerRawY, rangeMap, maxCount), now);
		ProcessModule? module = reader.FindModule("Game.exe");
		LootSpriteScanResult result = new();

		if (module == null) {
			result.Diagnostics.CaptureFirstRejected("Module Game.exe không tồn tại");
			return result;
		}

		long moduleStart = module.BaseAddress.ToInt64();
		long moduleEnd = moduleStart + Math.Min(module.ModuleMemorySize, MaximumModuleScanBytes);

		List<long> discoveredAddresses = new();
		foreach (MemoryRegion region in reader.GetReadableRegions(moduleStart, moduleEnd)) {
			result.Diagnostics.ReadableRegionCount++;
			byte[] bytes = reader.ReadMemory(new IntPtr(region.BaseAddress), region.Size);

			if (bytes.Length < ItemSpritePattern.Length + CoordinateOffsetBeforeSprite) {
				continue;
			}

			ScanRegion(processId, bytes, region.BaseAddress, centerRawX, centerRawY, rangeMap, maxCount, result, discoveredAddresses);

			if (result.Items.Count >= maxCount) {
				break;
			}
		}

		cachedProcessId = processId;
		cachedCoordinateAddresses.Clear();
		cachedCoordinateAddresses.AddRange(ExpandCachedSlots(discoveredAddresses, moduleStart, moduleEnd));
		nextFullScanUtc = DateTime.UtcNow.AddMilliseconds(FullScanRefreshMilliseconds);
		result.Items.Sort(CompareCandidates);
		return RememberRecentResult(processId, centerRawX, centerRawY, rangeMap, maxCount, result, now);
	}

	private bool CanUseRecentResult(int processId, int centerRawX, int centerRawY, int rangeMap, int maxCount, DateTime now) {
		return recentResult != null && recentProcessId == processId && recentCenterRawX == centerRawX && recentCenterRawY == centerRawY && recentRangeMap == rangeMap && recentMaxCount >= maxCount && now < recentResultUntilUtc;
	}

	private LootSpriteScanResult RememberRecentResult(int processId, int centerRawX, int centerRawY, int rangeMap, int maxCount, LootSpriteScanResult result, DateTime now) {
		recentResult = result;
		recentProcessId = processId;
		recentCenterRawX = centerRawX;
		recentCenterRawY = centerRawY;
		recentRangeMap = rangeMap;
		recentMaxCount = maxCount;
		recentResultUntilUtc = now.AddMilliseconds(RecentResultMilliseconds);
		return result;
	}

	private static IEnumerable<long> ExpandCachedSlots(IEnumerable<long> discoveredAddresses, long moduleStart, long moduleEnd) {
		HashSet<long> expanded = new();
		foreach (long anchor in discoveredAddresses.Distinct()) {
			for (int slot = -CachedSlotsBeforeAnchor; slot <= CachedSlotsAfterAnchor; slot++) {
				long address = anchor + (long)slot * ItemRecordStride;
				if (address - CachedWindowBeforeCoordinate >= moduleStart && address + CachedWindowLength <= moduleEnd) expanded.Add(address);
			}
		}
		return expanded.OrderBy(address => address);
	}

	private LootSpriteScanResult ScanCached(int processId, ScannerReader reader, int centerRawX, int centerRawY, int rangeMap, int maxCount) {
		LootSpriteScanResult result = new();
		for (int startIndex = 0; startIndex < cachedCoordinateAddresses.Count && result.Items.Count < maxCount;) {
			int endIndex = startIndex;
			long batchStart = cachedCoordinateAddresses[startIndex] - CachedWindowBeforeCoordinate;
			long batchEnd = cachedCoordinateAddresses[startIndex] + CachedWindowLength - CachedWindowBeforeCoordinate;

			while (endIndex + 1 < cachedCoordinateAddresses.Count) {
				long nextAddress = cachedCoordinateAddresses[endIndex + 1];
				long previousAddress = cachedCoordinateAddresses[endIndex];
				long nextEnd = nextAddress + CachedWindowLength - CachedWindowBeforeCoordinate;
				if (nextAddress - previousAddress > CachedBatchMaximumGap || nextEnd - batchStart > MaximumCachedBatchLength) break;
				endIndex++;
				batchEnd = nextEnd;
			}

			byte[] bytes = reader.ReadMemory(new IntPtr(batchStart), checked((int)(batchEnd - batchStart)));
			result.Diagnostics.ReadableRegionCount++;
			if (bytes.Length > 0) {
				for (int index = startIndex; index <= endIndex && result.Items.Count < maxCount; index++) {
					int coordinateOffset = checked((int)(cachedCoordinateAddresses[index] - batchStart));
					int spriteOffset = coordinateOffset + CoordinateOffsetBeforeSprite;
					if (!MatchesAt(bytes, spriteOffset, ItemSpritePattern)) continue;
					result.Diagnostics.PatternCount++;
					TryAddCandidate(processId, bytes, batchStart, spriteOffset, centerRawX, centerRawY, rangeMap, result);
				}
			}

			startIndex = endIndex + 1;
		}
		result.Items.Sort(CompareCandidates);
		return result;
	}

	private static void ScanRegion(int processId, byte[] bytes, long regionBase, int centerRawX, int centerRawY, int rangeMap, int maxCount, LootSpriteScanResult result, List<long> discoveredAddresses) {
		int searchOffset = 0;

		while (searchOffset <= bytes.Length - ItemSpritePattern.Length && result.Items.Count < maxCount) {
			int spriteOffset = IndexOf(bytes, ItemSpritePattern, searchOffset);

			if (spriteOffset < 0) {
				return;
			}

			result.Diagnostics.PatternCount++;
			int coordinateOffset = spriteOffset - CoordinateOffsetBeforeSprite;
			if (coordinateOffset >= 0 && coordinateOffset + 8 <= bytes.Length && IsValidCoordinate(BitConverter.ToInt32(bytes, coordinateOffset), BitConverter.ToInt32(bytes, coordinateOffset + 4))) discoveredAddresses.Add(regionBase + coordinateOffset);
			TryAddCandidate(processId, bytes, regionBase, spriteOffset, centerRawX, centerRawY, rangeMap, result);
			searchOffset = spriteOffset + ItemSpritePattern.Length;
		}
	}

	private static bool MatchesAt(byte[] bytes, int offset, byte[] pattern) {
		if (offset < 0 || offset + pattern.Length > bytes.Length) return false;
		for (int index = 0; index < pattern.Length; index++) if (bytes[offset + index] != pattern[index]) return false;
		return true;
	}

	private static void TryAddCandidate(int processId, byte[] bytes, long regionBase, int spriteOffset, int centerRawX, int centerRawY, int rangeMap, LootSpriteScanResult result) {
		int coordinateOffset = spriteOffset - CoordinateOffsetBeforeSprite;
		long address = regionBase + Math.Max(coordinateOffset, 0);

		if (coordinateOffset < 0 || coordinateOffset + 8 > bytes.Length) {
			result.Diagnostics.LayoutRejectedCount++;
			result.Diagnostics.CaptureFirstRejected($"Layout Address=0x{address:X8} SpriteOffset=0x{spriteOffset:X}");
			return;
		}

		int rawX = BitConverter.ToInt32(bytes, coordinateOffset);
		int rawY = BitConverter.ToInt32(bytes, coordinateOffset + 4);
		double distance = GetMapDistance(rawX, rawY, centerRawX, centerRawY);

		if (!IsValidCoordinate(rawX, rawY)) {
			result.Diagnostics.InvalidCoordinateCount++;
			result.Diagnostics.CaptureFirstRejected($"BadCoord Address=0x{address:X8} Raw={rawX}/{rawY}");
			return;
		}

		if (distance > Math.Max(rangeMap, 1)) {
			result.Diagnostics.OutOfRangeCount++;
			result.Diagnostics.CaptureFirstRejected($"Out Address=0x{address:X8} Raw={rawX}/{rawY} Distance={distance:F2}");
			return;
		}

		if (IsPickedOrStaleItemState(bytes, coordinateOffset)) {
			result.Diagnostics.StaleCount++;
			result.Diagnostics.CaptureFirstRejected($"Stale Address=0x{address:X8} Raw={rawX}/{rawY} Distance={distance:F2} {DescribeState(bytes, coordinateOffset)}");
			return;
		}

		string spritePath = ReadAscii(bytes, spriteOffset, MaximumSpriteLength);
		byte[] itemNameBytes = ReadRawName(bytes, coordinateOffset + ItemNameOffsetFromCoordinate, MaximumItemNameLength);

		if (string.IsNullOrWhiteSpace(spritePath)) {
			result.Diagnostics.PathRejectedCount++;
			result.Diagnostics.CaptureFirstRejected($"Path Address=0x{address:X8} Raw={rawX}/{rawY}");
			return;
		}

		if (result.Items.Any(item => Math.Abs(item.RawX - rawX) <= 4 && Math.Abs(item.RawY - rawY) <= 4 && item.NameAscii == spritePath)) {
			result.Diagnostics.DuplicateCount++;
			return;
		}

		result.Items.Add(new LootSnapshot {
			ProcessId = processId,
			Index = -1,
			Address = new IntPtr(address),
			Handle = unchecked((int)address),
			ActiveFlag = 1,
			RawX = rawX,
			RawY = rawY,
			RawXMirror = rawX,
			RawYMirror = rawY,
			NameAscii = spritePath,
			NameBytes = Encoding.ASCII.GetBytes(spritePath),
			ItemNameBytes = itemNameBytes,
			ItemNameRaw = LegacyVietnameseText.Decode(itemNameBytes),
			QualityCodeA = ReadInt32(bytes, coordinateOffset + QualityCodeOffsetA),
			QualityCodeB = ReadInt32(bytes, coordinateOffset + QualityCodeOffsetB),
			GroundType = unchecked((uint)ReadInt32(bytes, coordinateOffset - 0x29C)),
			MemoryFingerprint = ComputeFingerprint(bytes, coordinateOffset, FingerprintBytesBeforeSprite + FingerprintBytesAfterSprite),
			DistanceToCenter = GetMapDistance(rawX, rawY, centerRawX, centerRawY),
			DistanceToPlayer = GetMapDistance(rawX, rawY, centerRawX, centerRawY),
			Source = "Sprite"
		});
	}

	private static bool IsValidCoordinate(int rawX, int rawY) {
		return rawX > 0 && rawY > 0 && rawX <= 2000000 && rawY <= 2000000;
	}

	private static bool IsPickedOrStaleItemState(byte[] bytes, int coordinateOffset) {
		if (!CanReadInt32(bytes, coordinateOffset + ItemStateDCOffset)) {
			return false;
		}

		int stateA0 = ReadInt32(bytes, coordinateOffset + ItemStateA0Offset);
		int stateA8 = ReadInt32(bytes, coordinateOffset + ItemStateA8Offset);
		int stateAC = ReadInt32(bytes, coordinateOffset + ItemStateACOffset);
		int stateD8 = ReadInt32(bytes, coordinateOffset + ItemStateD8Offset);
		int stateDC = ReadInt32(bytes, coordinateOffset + ItemStateDCOffset);
		return stateA0 == 0 && stateA8 == 0 && stateD8 == 0 && stateDC == 0 && stateAC == 1;
	}

	private static double GetMapDistance(int rawX1, int rawY1, int rawX2, int rawY2) {
		long deltaX = (long)rawX1 - rawX2;
		long deltaY = (long)rawY1 - rawY2;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	private static int ComputeFingerprint(byte[] bytes, int offset, int length) {
		unchecked {
			uint hash = 2166136261;
			int start = Math.Max(0, offset);
			int end = Math.Min(bytes.Length, start + Math.Max(length, 0));

			for (int i = start; i < end; i++) {
				hash ^= bytes[i];
				hash *= 16777619;
			}

			return (int)hash;
		}
	}

	private static bool CanReadInt32(byte[] bytes, int offset) {
		return offset >= 0 && offset + 4 <= bytes.Length;
	}

	private static int ReadInt32(byte[] bytes, int offset) {
		return CanReadInt32(bytes, offset) ? BitConverter.ToInt32(bytes, offset) : 0;
	}

	private static string DescribeState(byte[] bytes, int coordinateOffset) {
		int stateA0 = ReadInt32(bytes, coordinateOffset + ItemStateA0Offset);
		int stateA8 = ReadInt32(bytes, coordinateOffset + ItemStateA8Offset);
		int stateAC = ReadInt32(bytes, coordinateOffset + ItemStateACOffset);
		int stateD8 = ReadInt32(bytes, coordinateOffset + ItemStateD8Offset);
		int stateDC = ReadInt32(bytes, coordinateOffset + ItemStateDCOffset);
		return $"State=A0:{stateA0}/A8:{stateA8}/AC:{stateAC}/D8:{stateD8}/DC:{stateDC}";
	}

	private static int CompareCandidates(LootSnapshot left, LootSnapshot right) {
		int distanceCompare = left.DistanceToPlayer.CompareTo(right.DistanceToPlayer);
		return distanceCompare != 0 ? distanceCompare : left.Address.ToInt64().CompareTo(right.Address.ToInt64());
	}

	private static int IndexOf(byte[] bytes, byte[] pattern, int startOffset) {
		for (int i = startOffset; i <= bytes.Length - pattern.Length; i++) {
			bool matched = true;

			for (int j = 0; j < pattern.Length; j++) {
				if (bytes[i + j] != pattern[j]) {
					matched = false;
					break;
				}
			}

			if (matched) {
				return i;
			}
		}

		return -1;
	}

	private static string ReadAscii(byte[] bytes, int offset, int maxLength) {
		int length = 0;

		while (offset + length < bytes.Length && length < maxLength) {
			byte value = bytes[offset + length];

			if (value == 0) {
				break;
			}

			if (value < 32 || value > 126) {
				break;
			}

			length++;
		}

		return length <= 0 ? "" : Encoding.ASCII.GetString(bytes, offset, length);
	}

	private static byte[] ReadRawName(byte[] bytes, int offset, int maxLength) {
		if (offset < 0 || offset >= bytes.Length || maxLength <= 0) return Array.Empty<byte>();
		int length = 0;
		while (offset + length < bytes.Length && length < maxLength && bytes[offset + length] != 0 && bytes[offset + length] >= 32) length++;
		if (length == 0) return Array.Empty<byte>();
		byte[] result = new byte[length];
		Array.Copy(bytes, offset, result, 0, length);
		return result;
	}

	private sealed class ScannerReader : IDisposable {
		private readonly Process process;
		private readonly IntPtr handle;

		public ScannerReader(int processId) {
			process = Process.GetProcessById(processId);
			handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, processId);

			if (handle == IntPtr.Zero) {
				throw new Exception("OpenProcess failed. Win32Error = " + Marshal.GetLastWin32Error());
			}
		}

		public ProcessModule? FindModule(string moduleName) {
			foreach (ProcessModule module in process.Modules) {
				if (string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase)) {
					return module;
				}
			}

			return process.MainModule;
		}

		public IEnumerable<MemoryRegion> GetReadableRegions(long startAddress, long endAddress) {
			long address = startAddress;

			while (address < endAddress) {
				if (VirtualQueryEx(handle, new IntPtr(address), out MemoryBasicInformation mbi, Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero) {
					address += 0x1000;
					continue;
				}

				long regionBase = mbi.BaseAddress.ToInt64();
				long regionSize = mbi.RegionSize.ToInt64();
				long regionEnd = regionBase + regionSize;

				if (regionSize > 0 && IsReadable(mbi)) {
					long readStart = Math.Max(regionBase, startAddress);
					long readEnd = Math.Min(regionEnd, endAddress);

					if (readEnd > readStart && readEnd - readStart <= int.MaxValue) {
						yield return new MemoryRegion(readStart, (int)(readEnd - readStart));
					}
				}

				address = regionEnd > address ? regionEnd : address + 0x1000;
			}
		}

		public byte[] ReadMemory(IntPtr address, int size) {
			byte[] buffer = new byte[size];
			bool ok = ReadProcessMemory(handle, address, buffer, size, out IntPtr bytesRead);

			if (!ok || bytesRead.ToInt64() <= 0) {
				return Array.Empty<byte>();
			}

			if (bytesRead.ToInt64() == size) {
				return buffer;
			}

			Array.Resize(ref buffer, (int)bytesRead.ToInt64());
			return buffer;
		}

		public void Dispose() {
			if (handle != IntPtr.Zero) {
				CloseHandle(handle);
			}

			process.Dispose();
		}
	}

	private sealed record MemoryRegion(long BaseAddress, int Size);

	private static bool IsReadable(MemoryBasicInformation mbi) {
		return mbi.State == MemCommit && (mbi.Protect & PageNoAccess) == 0 && (mbi.Protect & PageGuard) == 0;
	}

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
