using System;
using System.Collections.Generic;
using System.Text;

namespace Auto.Utils;

public sealed class MonsterFinder {
	private const int GlobalEntityTableOffset = GameAddresses.Globals.EntityTable;
	private const int CurrentTargetIndexOffset = GameAddresses.Globals.CurrentTargetIndex;
	private const int EntityStride = GameAddresses.Entity.Stride;

	private const int LocalPlayerEntityIndex = 1;

	private const int ScanIndexStart = 0;
	private const int ScanIndexCount = 128;

	private const int HandleOffset = GameAddresses.Entity.Handle;
	private const int SlotIndexOffset = GameAddresses.Entity.SlotIndex;
	private const int MirrorIndexOffset = GameAddresses.Entity.MirrorIndex;
	private const int ActiveFlagOffset = GameAddresses.Entity.ActiveFlag;
	private const int LevelOffset = GameAddresses.Entity.Level;

	private const int HpOffset = GameAddresses.Entity.Hp;
	private const int MaxHpOffset = GameAddresses.Entity.MaxHp;
	private const int NameOffset = GameAddresses.Entity.Name;

	private const int RawXOffset = GameAddresses.Entity.RawX;
	private const int RawYOffset = GameAddresses.Entity.RawY;
	private const int RawXMirrorOffset = GameAddresses.Entity.RawXMirror;
	private const int RawYMirrorOffset = GameAddresses.Entity.RawYMirror;

	private const int RawXScale = 256;
	private const int RawYScale = 512;

	private const int EntityReadSize = 0x70B8;
	private const int MaximumNameLength = 32;

	private const long MinimumLikelyAddress = 0x01000000;
	private const long MaximumUserModeAddress = 0x7FFFFFFF;

	public static MonsterFinderResult FindSameAsCurrentTarget(int processId) {
		try {
			GameSnapshot player = GameMemory.ReadSnapshot(processId);

			if (!player.Success) {
				return CreateFail(
					processId,
					$"Không đọc được Player Snapshot: {player.FailReason}"
				);
			}

			using MemoryReader reader = new MemoryReader(processId);
			IntPtr moduleBase = reader.GetModuleBase("Game.exe");

			if (moduleBase == IntPtr.Zero) {
				return CreateFail(processId, "Không tìm thấy module Game.exe.");
			}

			IntPtr tableBaseAddress = IntPtr.Add(moduleBase, GlobalEntityTableOffset);
			IntPtr targetIndexAddress = IntPtr.Add(moduleBase, CurrentTargetIndexOffset);

			int tableBaseRaw = reader.ReadInt32(tableBaseAddress);
			int currentTargetIndex = reader.ReadInt32(targetIndexAddress);

			IntPtr tableBase = new IntPtr(tableBaseRaw);

			if (!IsLikelyAddress(tableBase)) {
				return CreateFail(
					processId,
					$"TableBase không hợp lệ: {FormatAddress(tableBase)}"
				);
			}

			if (currentTargetIndex < ScanIndexStart || currentTargetIndex >= ScanIndexCount) {
				return CreateFail(
					processId,
					$"CurrentTargetIndex={currentTargetIndex} nằm ngoài scan range {ScanIndexStart}..{ScanIndexCount - 1}."
				);
			}

			if (!TryReadEntityRow(reader, tableBase, currentTargetIndex, out EntityRow targetRow)) {
				return CreateFail(
					processId,
					$"Không đọc được record của CurrentTargetIndex={currentTargetIndex}."
				);
			}

			string validationReason = GetValidationReason(targetRow, currentTargetIndex);

			if (!string.IsNullOrWhiteSpace(validationReason)) {
				return CreateFail(
					processId,
					$"Current target không hợp lệ: {validationReason}"
				);
			}

			if (targetRow.NameBytes.Length == 0) {
				return CreateFail(
					processId,
					"Current target không có raw-name bytes."
				);
			}

			return FindBySignatureInternal(
				reader,
				processId,
				tableBase,
				player,
				currentTargetIndex,
				targetRow.NameBytes
			);
		} catch (Exception ex) {
			return CreateFail(processId, ex.Message);
		}
	}

	public static MonsterFinderResult FindByNameSignature(
		int processId,
		byte[] nameSignature
	) {
		if (nameSignature == null || nameSignature.Length == 0) {
			return CreateFail(processId, "Name signature trống.");
		}

		try {
			GameSnapshot player = GameMemory.ReadSnapshot(processId);

			if (!player.Success) {
				return CreateFail(
					processId,
					$"Không đọc được Player Snapshot: {player.FailReason}"
				);
			}

			using MemoryReader reader = new MemoryReader(processId);
			IntPtr moduleBase = reader.GetModuleBase("Game.exe");

			if (moduleBase == IntPtr.Zero) {
				return CreateFail(processId, "Không tìm thấy module Game.exe.");
			}

			IntPtr tableBaseAddress = IntPtr.Add(moduleBase, GlobalEntityTableOffset);
			int tableBaseRaw = reader.ReadInt32(tableBaseAddress);
			IntPtr tableBase = new IntPtr(tableBaseRaw);

			if (!IsLikelyAddress(tableBase)) {
				return CreateFail(
					processId,
					$"TableBase không hợp lệ: {FormatAddress(tableBase)}"
				);
			}

			return FindBySignatureInternal(
				reader,
				processId,
				tableBase,
				player,
				-1,
				CopyBytes(nameSignature)
			);
		} catch (Exception ex) {
			return CreateFail(processId, ex.Message);
		}
	}

	public static string DebugFindSameAsCurrentTarget(int processId) {
		MonsterFinderResult result = FindSameAsCurrentTarget(processId);
		StringBuilder sb = new StringBuilder();

		sb.AppendLine("===== Same Monster Finder =====");
		sb.AppendLine($"Success = {result.Success}");
		sb.AppendLine($"ProcessId = {result.ProcessId}");

		if (!result.Success) {
			sb.AppendLine($"FailReason = {result.FailReason}");
			return sb.ToString();
		}

		sb.AppendLine($"ReferenceTargetIndex = {result.ReferenceTargetIndex}");
		sb.AppendLine($"ReferenceNameAscii = {FormatText(result.ReferenceNameAscii)}");
		sb.AppendLine($"ReferenceNameBytes = {FormatBytes(result.ReferenceNameSignature)}");
		sb.AppendLine();

		sb.AppendLine($"PlayerRawPosition = {result.PlayerRawX}/{result.PlayerRawY}");
		sb.AppendLine($"PlayerGameCoordinate = {result.PlayerRawX / RawXScale}/{result.PlayerRawY / RawYScale}");
		sb.AppendLine($"ScanIndexRange = {ScanIndexStart}..{ScanIndexCount - 1}");
		sb.AppendLine($"MatchedMonsterCount = {result.Candidates.Count}");
		sb.AppendLine();

		sb.AppendLine(
			"Index | Current | Handle | Lv | HP | RawPosition | MapPosition | Distance"
		);

		foreach (MonsterCandidate candidate in result.Candidates) {
			string isCurrentTarget = candidate.Index == result.ReferenceTargetIndex
				? "Y"
				: "-";

			sb.AppendLine(
				$"[{candidate.Index:000}] | " +
				$"{isCurrentTarget} | " +
				$"{candidate.Handle:00000} | " +
				$"{candidate.Level:000} | " +
				$"{candidate.Hp}/{candidate.MaxHp} | " +
				$"{candidate.RawX}/{candidate.RawY} | " +
				$"{candidate.RawX / RawXScale}/{candidate.RawY / RawYScale} | " +
				$"{candidate.DistanceMapUnits:F2}"
			);
		}

		return sb.ToString();
	}

	private static MonsterFinderResult FindBySignatureInternal(
		MemoryReader reader,
		int processId,
		IntPtr tableBase,
		GameSnapshot player,
		int referenceTargetIndex,
		byte[] nameSignature
	) {
		MonsterFinderResult result = new MonsterFinderResult {
			Success = true,
			ProcessId = processId,
			TableBase = tableBase,
			ReferenceTargetIndex = referenceTargetIndex,
			ReferenceNameSignature = CopyBytes(nameSignature),
			ReferenceNameAscii = DecodeAscii(nameSignature),
			PlayerRawX = player.X,
			PlayerRawY = player.Y
		};

		int scanIndexEnd = ScanIndexStart + ScanIndexCount - 1;

		for (int index = ScanIndexStart; index <= scanIndexEnd; index++) {
			if (!TryReadEntityRow(reader, tableBase, index, out EntityRow row)) {
				continue;
			}

			if (index == LocalPlayerEntityIndex) {
				continue;
			}

			if (!string.IsNullOrWhiteSpace(GetValidationReason(row, index))) {
				continue;
			}

			if (!NameBytesEqual(row.NameBytes, nameSignature)) {
				continue;
			}

			long distanceSquared = GetNormalizedDistanceSquared(
				row.RawX,
				row.RawY,
				player.X,
				player.Y
			);

			result.Candidates.Add(
				new MonsterCandidate {
					Index = index,
					Handle = row.Handle,
					Level = row.Level,
					Hp = row.Hp,
					MaxHp = row.MaxHp,
					RawX = row.RawX,
					RawY = row.RawY,
					DistanceSquared = distanceSquared,
					DistanceMapUnits = Math.Sqrt(distanceSquared) / RawYScale
				}
			);
		}

		result.Candidates.Sort(CompareCandidates);

		return result;
	}

	private static bool TryReadEntityRow(
		MemoryReader reader,
		IntPtr tableBase,
		int index,
		out EntityRow row
	) {
		row = new EntityRow();

		long entityBaseValue =
			unchecked((uint)tableBase.ToInt64()) +
			(long)index * EntityStride;

		if (entityBaseValue < MinimumLikelyAddress || entityBaseValue > MaximumUserModeAddress) {
			return false;
		}

		IntPtr entityBase = new IntPtr((int)entityBaseValue);
		byte[] bytes;

		try {
			bytes = reader.ReadBytes(entityBase, EntityReadSize);
		} catch {
			return false;
		}

		if (bytes.Length < EntityReadSize) {
			return false;
		}

		row.Handle = ReadInt32(bytes, HandleOffset);
		row.SlotIndex = ReadInt32(bytes, SlotIndexOffset);
		row.MirrorIndex = ReadInt32(bytes, MirrorIndexOffset);
		row.ActiveFlag = ReadInt32(bytes, ActiveFlagOffset);
		row.Level = ReadInt32(bytes, LevelOffset);

		row.Hp = ReadInt32(bytes, HpOffset);
		row.MaxHp = ReadInt32(bytes, MaxHpOffset);

		row.RawX = ReadInt32(bytes, RawXOffset);
		row.RawY = ReadInt32(bytes, RawYOffset);

		row.RawXMirror = ReadInt32(bytes, RawXMirrorOffset);
		row.RawYMirror = ReadInt32(bytes, RawYMirrorOffset);

		row.NameBytes = ReadNullTerminatedBytes(
			bytes,
			NameOffset,
			MaximumNameLength
		);

		return true;
	}

	private static string GetValidationReason(EntityRow row, int expectedIndex) {
		List<string> reasons = new List<string>();

		if (row.SlotIndex != expectedIndex) {
			reasons.Add($"SlotIndex={row.SlotIndex}");
		}

		if (row.MirrorIndex != expectedIndex) {
			reasons.Add($"MirrorIndex={row.MirrorIndex}");
		}

		if (row.ActiveFlag != 1) {
			reasons.Add($"ActiveFlag={row.ActiveFlag}");
		}

		if (row.Handle <= 0) {
			reasons.Add($"Handle={row.Handle}");
		}

		if (row.Level <= 0) {
			reasons.Add($"Level={row.Level}");
		}

		if (row.Hp <= 0 || row.MaxHp <= 0 || row.Hp > row.MaxHp) {
			reasons.Add($"HP={row.Hp}/{row.MaxHp}");
		}

		if (row.RawX <= 0 || row.RawY <= 0) {
			reasons.Add($"RawPosition={row.RawX}/{row.RawY}");
		}

		if (row.RawX != row.RawXMirror || row.RawY != row.RawYMirror) {
			reasons.Add(
				$"PositionMirror={row.RawXMirror}/{row.RawYMirror}"
			);
		}

		return string.Join("; ", reasons);
	}

	private static byte[] ReadNullTerminatedBytes(
		byte[] bytes,
		int offset,
		int maxLength
	) {
		if (offset < 0 || offset >= bytes.Length || maxLength <= 0) {
			return Array.Empty<byte>();
		}

		int availableLength = Math.Min(maxLength, bytes.Length - offset);
		int zeroIndex = Array.IndexOf(
			bytes,
			(byte)0,
			offset,
			availableLength
		);

		int length = zeroIndex >= 0
			? zeroIndex - offset
			: availableLength;

		if (length <= 0) {
			return Array.Empty<byte>();
		}

		byte[] result = new byte[length];
		Array.Copy(bytes, offset, result, 0, length);

		return result;
	}

	private static int ReadInt32(byte[] bytes, int offset) {
		return BitConverter.ToInt32(bytes, offset);
	}

	private static long GetNormalizedDistanceSquared(
		int entityRawX,
		int entityRawY,
		int playerRawX,
		int playerRawY
	) {
		long deltaX = (long)entityRawX - playerRawX;
		long deltaY = (long)entityRawY - playerRawY;

		long normalizedDeltaX = deltaX * 2L;
		long normalizedDeltaY = deltaY;

		return normalizedDeltaX * normalizedDeltaX + normalizedDeltaY * normalizedDeltaY;
	}

	private static int CompareCandidates(
		MonsterCandidate left,
		MonsterCandidate right
	) {
		int distanceCompare = left.DistanceSquared.CompareTo(
			right.DistanceSquared
		);

		if (distanceCompare != 0) {
			return distanceCompare;
		}

		return left.Index.CompareTo(right.Index);
	}

	private static bool NameBytesEqual(byte[] left, byte[] right) {
		if (left.Length != right.Length) {
			return false;
		}

		for (int i = 0; i < left.Length; i++) {
			if (left[i] != right[i]) {
				return false;
			}
		}

		return true;
	}

	private static byte[] CopyBytes(byte[] source) {
		byte[] copy = new byte[source.Length];
		Array.Copy(source, copy, source.Length);

		return copy;
	}

	private static string DecodeAscii(byte[] bytes) {
		if (bytes.Length == 0) {
			return "";
		}

		return Encoding.ASCII.GetString(bytes).Trim();
	}

	private static bool IsLikelyAddress(IntPtr address) {
		long value = unchecked((uint)address.ToInt64());

		return value >= MinimumLikelyAddress && value <= MaximumUserModeAddress;
	}

	private static MonsterFinderResult CreateFail(int processId, string reason) {
		return new MonsterFinderResult {
			Success = false,
			ProcessId = processId,
			FailReason = reason
		};
	}

	private static string FormatAddress(IntPtr address) {
		return unchecked((uint)address.ToInt64()).ToString("X8");
	}

	private static string FormatBytes(byte[] bytes) {
		if (bytes.Length == 0) {
			return "<empty>";
		}

		StringBuilder sb = new StringBuilder();

		for (int i = 0; i < bytes.Length; i++) {
			if (i > 0) {
				sb.Append(' ');
			}

			sb.Append(bytes[i].ToString("X2"));
		}

		return sb.ToString();
	}

	private static string FormatText(string value) {
		return string.IsNullOrWhiteSpace(value) ? "<empty>" : value;
	}

	private sealed class EntityRow {
		public int Handle { get; set; }
		public int SlotIndex { get; set; }
		public int MirrorIndex { get; set; }
		public int ActiveFlag { get; set; }
		public int Level { get; set; }

		public int Hp { get; set; }
		public int MaxHp { get; set; }

		public int RawX { get; set; }
		public int RawY { get; set; }

		public int RawXMirror { get; set; }
		public int RawYMirror { get; set; }

		public byte[] NameBytes { get; set; } = Array.Empty<byte>();
	}
}

public sealed class MonsterFinderResult {
	public bool Success { get; set; }
	public string FailReason { get; set; } = "";

	public int ProcessId { get; set; }
	public IntPtr TableBase { get; set; }

	public int ReferenceTargetIndex { get; set; } = -1;
	public string ReferenceNameAscii { get; set; } = "";
	public byte[] ReferenceNameSignature { get; set; } = Array.Empty<byte>();

	public int PlayerRawX { get; set; }
	public int PlayerRawY { get; set; }

	public List<MonsterCandidate> Candidates { get; } = new List<MonsterCandidate>();
}

public sealed class MonsterCandidate {
	public int Index { get; set; }
	public int Handle { get; set; }
	public int Level { get; set; }

	public int Hp { get; set; }
	public int MaxHp { get; set; }

	public int RawX { get; set; }
	public int RawY { get; set; }

	public long DistanceSquared { get; set; }
	public double DistanceMapUnits { get; set; }
}
