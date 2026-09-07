namespace Auto.Loot;

using Auto.Utils;

internal sealed class AutoFsGroundItemScanner {
	private const int FirstIndex = 1;
	private const int LastIndex = 127;
	private const int MaximumNameLength = 64;

	public List<LootSnapshot> Find(int processId, int centerRawX, int centerRawY, int rangeMap, int maxCount) {
		return Scan(processId, centerRawX, centerRawY, rangeMap, maxCount).Items;
	}

	public LootSpriteScanResult Scan(int processId, int centerRawX, int centerRawY, int rangeMap, int maxCount) {
		LootSpriteScanResult result = new();
		using MemoryReader reader = new(processId);
		IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
		if (moduleBase == IntPtr.Zero) {
			result.Diagnostics.CaptureRejected("Module Game.exe không tồn tại");
			return result;
		}

		IntPtr table = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Item.GroundRecordTablePointer));
		if (table == IntPtr.Zero) {
			result.Diagnostics.CaptureRejected($"GroundTablePointerZero Address=0x{IntPtr.Add(moduleBase, GameAddresses.Item.GroundRecordTablePointer).ToInt64():X8}");
			return result;
		}
		IntPtr mapCoordinateRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.MapCoordinateRoot));
		if (mapCoordinateRoot == IntPtr.Zero) {
			result.Diagnostics.CaptureRejected($"MapCoordinateRootZero Address=0x{IntPtr.Add(moduleBase, GameAddresses.Globals.MapCoordinateRoot).ToInt64():X8}");
			return result;
		}
		Dictionary<(int MapObjectIndex, int SegmentIndex), (int BaseX, int BaseY)> segmentCoordinates = new();
		int tableSize = (LastIndex + 1) * GameAddresses.Item.GroundRecordStride;
		byte[] records = reader.ReadBytes(table, tableSize);
		if (records.Length != tableSize) {
			result.Diagnostics.CoordinateReadFailureCount++;
			result.Diagnostics.CaptureRejected($"GroundTableRead Size={records.Length}/{tableSize} Address=0x{table.ToInt64():X8}");
			return result;
		}
		result.Diagnostics.ReadableRegionCount = 1;
		for (int index = FirstIndex; index <= LastIndex; index++) {
			int recordOffset = index * GameAddresses.Item.GroundRecordStride;
			int groundId = ReadInt32(records, recordOffset + GameAddresses.Item.GroundRecordId);
			int groundState = ReadInt32(records, recordOffset + GameAddresses.Item.GroundRecordKind);
			if (groundId <= 0 || groundState != 3) continue;
			result.Diagnostics.GroundPointerCount++;
			int internalX = ReadInt32(records, recordOffset + GameAddresses.Item.GroundInternalX);
			int internalY = ReadInt32(records, recordOffset + GameAddresses.Item.GroundInternalY);
			if (internalX <= 0 || internalY <= 0) {
				result.Diagnostics.InvalidCoordinateCount++;
				result.Diagnostics.CaptureRejected($"GroundInternalCoordinateInvalid Index={index} Record=0x{IntPtr.Add(table, recordOffset).ToInt64():X8} Internal={internalX}/{internalY}");
				continue;
			}
			IntPtr recordAddress = IntPtr.Add(table, recordOffset);
			if (! TryReadRawCoordinate(reader, records, recordOffset, mapCoordinateRoot, segmentCoordinates, out int rawX, out int rawY, out string coordinateFailure)) {
				result.Diagnostics.CoordinateReadFailureCount++;
				result.Diagnostics.CaptureRejected($"GroundRawCoordinateReadFailed Index={index} Record=0x{recordAddress.ToInt64():X8} | {coordinateFailure}");
				continue;
			}

			int nameOffset = recordOffset + GameAddresses.Item.GroundName;
			byte[] nameBytes = ReadName(records, nameOffset, MaximumNameLength);
			if (nameBytes.Length == 0) {
				result.Diagnostics.LayoutRejectedCount++;
				result.Diagnostics.CaptureRejected($"GroundNameEmpty Index={index} Record=0x{IntPtr.Add(table, recordOffset).ToInt64():X8}");
				continue;
			}
			string itemName = LegacyVietnameseText.Decode(nameBytes).Trim();
			if (string.IsNullOrWhiteSpace(itemName)) continue;

			result.Diagnostics.PatternCount++;
			result.Items.Add(new LootSnapshot {
				ProcessId = processId,
				Index = index,
				Address = IntPtr.Add(table, recordOffset),
				GroundObjectAddress = IntPtr.Zero,
				Handle = index,
				ActiveFlag = 1,
				RawX = rawX,
				RawY = rawY,
				RawXMirror = rawX,
				RawYMirror = rawY,
				InternalX = internalX,
				InternalY = internalY,
				NameAscii = itemName,
				NameBytes = nameBytes,
				ItemNameBytes = nameBytes,
				ItemNameRaw = itemName,
				QualityCodeA = ReadInt32(records, recordOffset + GameAddresses.Item.GroundQualityCodeA),
				QualityCodeB = ReadInt32(records, recordOffset + GameAddresses.Item.GroundQualityCodeB),
				GroundType = unchecked((uint)ReadInt32(records, recordOffset + GameAddresses.Item.GroundRecordType)),
				GroundKind = unchecked((uint)ReadInt32(records, recordOffset + GameAddresses.Item.GroundRecordKind)),
				GroundId = groundId,
				MemoryFingerprint = HashCode.Combine(index, groundId, internalX, internalY, rawX, rawY, itemName),
				DistanceToCenter = GetRawDistance(rawX, rawY, centerRawX, centerRawY),
				DistanceToPlayer = GetRawDistance(rawX, rawY, centerRawX, centerRawY),
				Source = "AutoFS-Table"
			});
		}
		result.Items.Sort((left, right) => {
			int distanceCompare = left.DistanceToPlayer.CompareTo(right.DistanceToPlayer);
			return distanceCompare != 0 ? distanceCompare : left.Index.CompareTo(right.Index);
		});
		return result;
	}

	private static byte[] ReadName(byte[] bytes, int offset, int maximumLength) {
		if (offset < 0 || offset >= bytes.Length) return Array.Empty<byte>();
		int length = 0;
		while (length < maximumLength && offset + length < bytes.Length && bytes[offset + length] != 0) length++;
		return length == 0 ? Array.Empty<byte>() : bytes.AsSpan(offset, length).ToArray();
	}

	private static int ReadInt32(byte[] bytes, int offset) {
		return offset >= 0 && offset + 4 <= bytes.Length ? BitConverter.ToInt32(bytes, offset) : 0;
	}

	internal static bool TryReadRawCoordinate(MemoryReader reader, byte[] records, int recordOffset, IntPtr mapCoordinateRoot, Dictionary<(int MapObjectIndex, int SegmentIndex), (int BaseX, int BaseY)> segmentCoordinates, out int rawX, out int rawY, out string failure) {
		rawX = 0;
		rawY = 0;
		failure = "";
		int mapObjectIndex = ReadInt32(records, recordOffset + GameAddresses.Item.GroundMapObjectIndex);
		int segmentIndex = ReadInt32(records, recordOffset + GameAddresses.Item.GroundMapSegmentIndex);
		int tileX = ReadInt32(records, recordOffset + GameAddresses.Item.GroundTileX);
		int tileY = ReadInt32(records, recordOffset + GameAddresses.Item.GroundTileY);
		int subTileX = ReadInt32(records, recordOffset + GameAddresses.Item.GroundSubTileX);
		int subTileY = ReadInt32(records, recordOffset + GameAddresses.Item.GroundSubTileY);
		if (mapObjectIndex < 0 || segmentIndex < 0) {
			failure = $"MapObjectIndex={mapObjectIndex} SegmentIndex={segmentIndex} Tile={tileX}/{tileY} SubTile={subTileX}/{subTileY}";
			return false;
		}
		if (! segmentCoordinates.TryGetValue((mapObjectIndex, segmentIndex), out (int BaseX, int BaseY) coordinateBase)) {
			IntPtr mapObject = IntPtr.Add(mapCoordinateRoot, mapObjectIndex * GameAddresses.Item.MapObjectStride);
			int segmentCount = reader.ReadInt32(IntPtr.Add(mapObject, GameAddresses.Item.MapSegmentCount));
			IntPtr segmentTable = reader.ReadPointer32(IntPtr.Add(mapObject, GameAddresses.Item.MapSegmentTable));
			if (segmentIndex >= segmentCount || segmentTable == IntPtr.Zero) {
				failure = $"MapCoordinateRoot=0x{mapCoordinateRoot.ToInt64():X8} MapObjectIndex={mapObjectIndex} MapObject=0x{mapObject.ToInt64():X8} SegmentIndex={segmentIndex} SegmentCount={segmentCount} SegmentTable=0x{segmentTable.ToInt64():X8} Tile={tileX}/{tileY} SubTile={subTileX}/{subTileY}";
				return false;
			}
			IntPtr segment = IntPtr.Add(segmentTable, segmentIndex * GameAddresses.Item.MapSegmentStride);
			coordinateBase = (
				reader.ReadInt32(IntPtr.Add(segment, GameAddresses.Item.MapSegmentBaseX)),
				reader.ReadInt32(IntPtr.Add(segment, GameAddresses.Item.MapSegmentBaseY)));
			segmentCoordinates[(mapObjectIndex, segmentIndex)] = coordinateBase;
		}
		rawX = coordinateBase.BaseX + tileX * 32 + (subTileX >> 10);
		rawY = coordinateBase.BaseY + tileY * 32 + (subTileY >> 10);
		if (rawX > 0 && rawY > 0) return true;
		failure = $"MapCoordinateRoot=0x{mapCoordinateRoot.ToInt64():X8} MapObjectIndex={mapObjectIndex} SegmentIndex={segmentIndex} Base={coordinateBase.BaseX}/{coordinateBase.BaseY} Tile={tileX}/{tileY} SubTile={subTileX}/{subTileY} Raw={rawX}/{rawY}";
		return false;
	}

	private static double GetRawDistance(int rawX1, int rawY1, int rawX2, int rawY2) {
		long deltaX = rawX1 - rawX2;
		long deltaY = rawY1 - rawY2;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

}
