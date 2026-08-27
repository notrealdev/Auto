using System;
using System.Collections.Generic;
using System.Text;

namespace Auto.Utils;

public sealed class EntityFinder {
	private const int GlobalEntityTableOffset = GameAddresses.Globals.EntityTable;
	private const int CurrentTargetIndexOffset = GameAddresses.Globals.CurrentTargetIndex;
	private const int EntityStride = GameAddresses.Entity.Stride;

	private const int LocalPlayerEntityIndex = 1;

	private const int ScanIndexStart = 0;
	private const int ScanIndexCount = 128;
	private const int MaximumPrintedCandidates = 80;

	private const int HandleOffset = GameAddresses.Entity.Handle;
	private const int SlotIndexOffset = GameAddresses.Entity.SlotIndex;
	private const int ClassPointerOffset = GameAddresses.Entity.ClassPointer;
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

	public static string DebugScan(int processId) {
		try {
			GameSnapshot player = GameMemory.ReadSnapshot(processId);

			if (!player.Success) {
				return
					"Không đọc được Player Snapshot.\r\n" +
					$"Reason = {player.FailReason}";
			}

			using MemoryReader reader = new MemoryReader(processId);
			IntPtr moduleBase = reader.GetModuleBase("Game.exe");

			if (moduleBase == IntPtr.Zero) {
				return "Không tìm thấy module Game.exe.";
			}

			IntPtr tableBaseAddress = IntPtr.Add(moduleBase, GlobalEntityTableOffset);
			IntPtr targetIndexAddress = IntPtr.Add(moduleBase, CurrentTargetIndexOffset);

			int tableBaseRaw = reader.ReadInt32(tableBaseAddress);
			IntPtr tableBase = new IntPtr(tableBaseRaw);

			if (!IsLikelyAddress(tableBase)) {
				return $"TableBase không hợp lệ: {FormatAddress(tableBase)}";
			}

			int targetIndexAtStart = reader.ReadInt32(targetIndexAddress);

			List<EntityCandidate> candidates = new List<EntityCandidate>();
			ScanStatistics statistics = new ScanStatistics();

			int scanIndexEnd = ScanIndexStart + ScanIndexCount - 1;

			for (int index = ScanIndexStart; index <= scanIndexEnd; index++) {
				if (!TryReadEntityRow(reader, tableBase, index, out EntityRow row)) {
					continue;
				}

				statistics.ReadableRows++;

				if (row.SlotIndex != index || row.MirrorIndex != index) {
					continue;
				}

				statistics.IndexMatchedRows++;

				if (row.ActiveFlag != 1) {
					continue;
				}

				statistics.ActiveRows++;

				if (row.Handle <= 0 || row.Level <= 0) {
					continue;
				}

				if (row.Hp <= 0 || row.MaxHp <= 0 || row.Hp > row.MaxHp) {
					continue;
				}

				statistics.AliveRows++;

				if (row.RawX <= 0 || row.RawY <= 0) {
					continue;
				}

				if (row.RawX != row.RawXMirror || row.RawY != row.RawYMirror) {
					continue;
				}

				statistics.PositionMatchedRows++;

				if (index == LocalPlayerEntityIndex) {
					statistics.LocalPlayerRows++;
					continue;
				}

				long distanceSquared = GetNormalizedDistanceSquared(
					row.RawX,
					row.RawY,
					player.X,
					player.Y
				);

				candidates.Add(
					new EntityCandidate {
						Index = index,
						Handle = row.Handle,
						ClassPointer = row.ClassPointer,
						Level = row.Level,
						Hp = row.Hp,
						MaxHp = row.MaxHp,
						NameAscii = row.NameAscii,
						NameBytesHex = FormatBytes(row.NameBytes),
						RawX = row.RawX,
						RawY = row.RawY,
						DistanceSquared = distanceSquared,
						DistanceMapUnits = Math.Sqrt(distanceSquared) / RawYScale
					}
				);
			}

			int targetIndexAfterScan = reader.ReadInt32(targetIndexAddress);

			TargetDiagnostic targetDiagnostic = ReadTargetDiagnostic(
				reader,
				tableBase,
				targetIndexAfterScan
			);

			candidates.Sort(CompareCandidates);

			StringBuilder sb = new StringBuilder();

			sb.AppendLine("===== Entity Finder =====");
			sb.AppendLine($"ProcessId = {processId}");
			sb.AppendLine($"TableBase = {FormatAddress(tableBase)}");
			sb.AppendLine($"ScanIndexRange = {ScanIndexStart}..{scanIndexEnd}");
			sb.AppendLine($"LocalPlayerEntityIndex = {LocalPlayerEntityIndex}");
			sb.AppendLine($"TargetIndexAtStart = {targetIndexAtStart}");
			sb.AppendLine($"TargetIndexAfterScan = {targetIndexAfterScan}");
			sb.AppendLine($"TargetIndexChangedDuringScan = {targetIndexAtStart != targetIndexAfterScan}");
			sb.AppendLine();

			sb.AppendLine($"PlayerRawPosition = {player.X}/{player.Y}");
			sb.AppendLine($"PlayerGameCoordinate = {player.X / RawXScale}/{player.Y / RawYScale}");
			sb.AppendLine();

			AppendTargetDiagnostic(sb, targetDiagnostic);

			sb.AppendLine();
			sb.AppendLine("----- Scan Statistics -----");
			sb.AppendLine($"ReadableRows = {statistics.ReadableRows}");
			sb.AppendLine($"IndexMatchedRows = {statistics.IndexMatchedRows}");
			sb.AppendLine($"ActiveRows = {statistics.ActiveRows}");
			sb.AppendLine($"AliveRows = {statistics.AliveRows}");
			sb.AppendLine($"PositionMatchedRows = {statistics.PositionMatchedRows}");
			sb.AppendLine($"LocalPlayerRows = {statistics.LocalPlayerRows}");
			sb.AppendLine($"NonPlayerCandidateCount = {candidates.Count}");
			sb.AppendLine();

			sb.AppendLine(
				"Index | Current | Handle | Lv | HP | RawPosition | MapPosition | Distance | NameAscii | NameBytes"
			);

			int printedCount = Math.Min(candidates.Count, MaximumPrintedCandidates);

			for (int i = 0; i < printedCount; i++) {
				EntityCandidate candidate = candidates[i];
				string isCurrentTarget = candidate.Index == targetIndexAfterScan ? "Y" : "-";
				string nameAscii = string.IsNullOrWhiteSpace(candidate.NameAscii)
					? "<empty>"
					: candidate.NameAscii;
				string nameBytes = string.IsNullOrWhiteSpace(candidate.NameBytesHex)
					? "<empty>"
					: candidate.NameBytesHex;

				sb.AppendLine(
					$"[{candidate.Index:000}] | " +
					$"{isCurrentTarget} | " +
					$"{candidate.Handle:00000} | " +
					$"{candidate.Level:000} | " +
					$"{candidate.Hp}/{candidate.MaxHp} | " +
					$"{candidate.RawX}/{candidate.RawY} | " +
					$"{candidate.RawX / RawXScale}/{candidate.RawY / RawYScale} | " +
					$"{candidate.DistanceMapUnits:F2} | " +
					$"{nameAscii} | " +
					$"{nameBytes}"
				);
			}

			if (candidates.Count > printedCount) {
				sb.AppendLine();
				sb.AppendLine(
					$"Đã rút gọn output: hiển thị {printedCount}/{candidates.Count} candidate."
				);
			}

			return sb.ToString();
		} catch (Exception ex) {
			return ex.ToString();
		}
	}

	private static TargetDiagnostic ReadTargetDiagnostic(
		MemoryReader reader,
		IntPtr tableBase,
		int targetIndex
	) {
		if (targetIndex < 0) {
			return new TargetDiagnostic {
				Index = targetIndex,
				Success = false,
				Reason = "TargetIndex âm. Hiện không có target hợp lệ."
			};
		}

		if (!TryReadEntityRow(reader, tableBase, targetIndex, out EntityRow row)) {
			return new TargetDiagnostic {
				Index = targetIndex,
				Success = false,
				Reason = "Không đọc được entity record của current target."
			};
		}

		string validationReason = GetValidationReason(row, targetIndex);

		return new TargetDiagnostic {
			Index = targetIndex,
			Success = true,
			Row = row,
			ValidationReason = validationReason,
			WouldBeCandidate =
				string.IsNullOrWhiteSpace(validationReason) &&
				targetIndex != LocalPlayerEntityIndex
		};
	}

	private static void AppendTargetDiagnostic(
		StringBuilder sb,
		TargetDiagnostic diagnostic
	) {
		sb.AppendLine("----- Current Target Diagnostic -----");
		sb.AppendLine($"Index = {diagnostic.Index}");

		if (!diagnostic.Success) {
			sb.AppendLine($"ReadSuccess = False");
			sb.AppendLine($"Reason = {diagnostic.Reason}");
			return;
		}

		EntityRow row = diagnostic.Row!;

		sb.AppendLine("ReadSuccess = True");
		sb.AppendLine($"Handle = {row.Handle} / 0x{FormatDword(row.Handle)}");
		sb.AppendLine($"SlotIndex = {row.SlotIndex}");
		sb.AppendLine($"MirrorIndex = {row.MirrorIndex}");
		sb.AppendLine($"ActiveFlag = {row.ActiveFlag}");
		sb.AppendLine($"Level = {row.Level}");
		sb.AppendLine($"HP = {row.Hp}/{row.MaxHp}");
		sb.AppendLine($"RawPosition = {row.RawX}/{row.RawY}");
		sb.AppendLine($"RawPositionMirror = {row.RawXMirror}/{row.RawYMirror}");
		sb.AppendLine($"NameAscii = {FormatText(row.NameAscii)}");
		sb.AppendLine($"NameBytes = {FormatBytes(row.NameBytes)}");
		sb.AppendLine($"ValidationReason = {FormatText(diagnostic.ValidationReason)}");
		sb.AppendLine($"WouldBeCandidate = {diagnostic.WouldBeCandidate}");
	}

	private static string GetValidationReason(EntityRow row, int expectedIndex) {
		List<string> reasons = new List<string>();

		if (row.SlotIndex != expectedIndex) {
			reasons.Add($"SlotIndex={row.SlotIndex}, expected={expectedIndex}");
		}

		if (row.MirrorIndex != expectedIndex) {
			reasons.Add($"MirrorIndex={row.MirrorIndex}, expected={expectedIndex}");
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
		row.ClassPointer = ReadInt32(bytes, ClassPointerOffset);
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

		row.NameAscii = DecodeAscii(row.NameBytes);

		return true;
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

	private static string DecodeAscii(byte[] bytes) {
		if (bytes.Length == 0) {
			return "";
		}

		return Encoding.ASCII.GetString(bytes).Trim();
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
		EntityCandidate left,
		EntityCandidate right
	) {
		int distanceCompare = left.DistanceSquared.CompareTo(
			right.DistanceSquared
		);

		if (distanceCompare != 0) {
			return distanceCompare;
		}

		return left.Index.CompareTo(right.Index);
	}

	private static bool IsLikelyAddress(IntPtr address) {
		long value = unchecked((uint)address.ToInt64());

		return value >= MinimumLikelyAddress && value <= MaximumUserModeAddress;
	}

	private static string FormatAddress(IntPtr address) {
		return unchecked((uint)address.ToInt64()).ToString("X8");
	}

	private static string FormatDword(int value) {
		return unchecked((uint)value).ToString("X8");
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
		public int ClassPointer { get; set; }
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
		public string NameAscii { get; set; } = "";
	}

	private sealed class EntityCandidate {
		public int Index { get; set; }
		public int Handle { get; set; }
		public int ClassPointer { get; set; }
		public int Level { get; set; }

		public int Hp { get; set; }
		public int MaxHp { get; set; }

		public string NameAscii { get; set; } = "";
		public string NameBytesHex { get; set; } = "";

		public int RawX { get; set; }
		public int RawY { get; set; }

		public long DistanceSquared { get; set; }
		public double DistanceMapUnits { get; set; }
	}

	private sealed class TargetDiagnostic {
		public int Index { get; set; }
		public bool Success { get; set; }
		public string Reason { get; set; } = "";

		public EntityRow? Row { get; set; }
		public string ValidationReason { get; set; } = "";
		public bool WouldBeCandidate { get; set; }
	}

	private sealed class ScanStatistics {
		public int ReadableRows { get; set; }
		public int IndexMatchedRows { get; set; }
		public int ActiveRows { get; set; }
		public int AliveRows { get; set; }
		public int PositionMatchedRows { get; set; }
		public int LocalPlayerRows { get; set; }
	}
}
