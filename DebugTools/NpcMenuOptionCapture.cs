namespace Auto.DebugTools;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Auto.Utils;

public static class NpcMenuOptionCapture {
	private const int MenuPointerRva = GameAddresses.Globals.ModalState;
	private const int AutoFsListPointerOffset = 0x1B4;
	private const int DirectOptionBaseOffset = 0x15C;
	private const int OptionStride = 0x57C;
	private const int OptionCountOffset = 0x35DC;
	private const int DirectTextOffset = 0x454;
	private const int AutoFsTextOffset = 0x5B0;
	private const int TextLengthOffset = 0x554;
	private const int MaximumOptionCount = 16;
	private const int MaximumTextLength = 128;
	private const int AutoFsFixedTextLength = 25;
	private const int ObjectScanLength = 0x4000;
	private const int ChildPointerScanLength = 0x1000;
	private const int CurrentTextOffset = 0x7EC;
	private const int CurrentSlotStride = 0x69C;
	private const int CurrentSlotCount = 8;
	private const int CurrentSlotEvidenceLength = 128;
	private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(15);

	// Waits for one NPC menu and lists every option through both confirmed historical layouts without invoking an option.
	public static string Capture(int processId, string npcLabel) {
		try {
			using Process process = Process.GetProcessById(processId);
			IntPtr moduleBase = process.MainModule?.BaseAddress ?? IntPtr.Zero;
			if (process.HasExited || moduleBase == IntPtr.Zero) return "NPC_MENU_CAPTURE_FAIL | Game process hoặc module base không hợp lệ.";
			using MemoryReader reader = new(processId);
			IntPtr baselineMenu = reader.ReadPointer32(IntPtr.Add(moduleBase, MenuPointerRva));
			DateTime deadlineUtc = DateTime.UtcNow.Add(CaptureTimeout);
			IntPtr menu = IntPtr.Zero;
			while (DateTime.UtcNow < deadlineUtc) {
				menu = reader.ReadPointer32(IntPtr.Add(moduleBase, MenuPointerRva));
				if (menu != IntPtr.Zero && menu != baselineMenu) break;
				Thread.Sleep(50);
			}
			if (menu == IntPtr.Zero || menu == baselineMenu) return $"NPC_MENU_CAPTURE_FAIL | Không thấy menu NPC mới trong {CaptureTimeout.TotalSeconds:0} giây | Baseline=0x{baselineMenu.ToInt64():X8}.";

			StringBuilder output = new();
			uint vtable = unchecked((uint)reader.ReadInt32(menu));
			output.AppendLine("===== NPC Menu Option Capture =====");
			output.AppendLine("BuildStamp = NPC-MENU-OPTIONS-20260823-03");
			output.AppendLine($"ProcessId = {processId}");
			output.AppendLine($"NpcLabel = {npcLabel}");
			output.AppendLine($"BaselineMenuPointer = 0x{baselineMenu.ToInt64():X8}");
			output.AppendLine($"MenuPointer = 0x{menu.ToInt64():X8}");
			output.AppendLine($"MenuVtable = 0x{vtable:X8} | Rva=0x{vtable - moduleBase.ToInt64():X6}");
			bool directFound = AppendDirectLayout(output, reader, menu);
			bool autoFsFound = AppendAutoFsLayout(output, reader, menu);
			if (! directFound && ! autoFsFound) AppendCurrentClientEvidence(output, reader, menu);
			output.AppendLine("Result = COMPLETED_READ_ONLY");
			return output.ToString();
		} catch (Exception ex) {
			return $"NPC_MENU_CAPTURE_FAIL | {ex.GetType().Name}: {ex.Message}";
		}
	}

	// Reads the historical DEV direct-object representation of the AutoFS option structure.
	private static bool AppendDirectLayout(StringBuilder output, MemoryReader reader, IntPtr menu) {
		int count = reader.ReadInt32(IntPtr.Add(menu, OptionCountOffset));
		output.AppendLine($"----- DIRECT_LAYOUT | Count={count} -----");
		if (count <= 0 || count > MaximumOptionCount) {
			output.AppendLine("DIRECT_LAYOUT_REJECTED | Reason=COUNT_OUT_OF_RANGE");
			return false;
		}
		bool found = false;
		for (int index = 0; index < count; index++) {
			IntPtr control = IntPtr.Add(menu, DirectOptionBaseOffset + index * OptionStride);
			found |= AppendOption(output, reader, "DIRECT", index, control, IntPtr.Add(control, DirectTextOffset), false);
		}
		return found;
	}

	// Reads the original AutoFS B_Bảng+0x1B4 pointer representation of the same option structure.
	private static bool AppendAutoFsLayout(StringBuilder output, MemoryReader reader, IntPtr menu) {
		IntPtr list = reader.ReadPointer32(IntPtr.Add(menu, AutoFsListPointerOffset));
		output.AppendLine($"----- AUTOFS_LAYOUT | List=0x{list.ToInt64():X8} -----");
		if (list == IntPtr.Zero) {
			output.AppendLine("AUTOFS_LAYOUT_REJECTED | Reason=LIST_POINTER_ZERO");
			return false;
		}
		int count = reader.ReadInt32(IntPtr.Add(list, OptionCountOffset));
		output.AppendLine($"AUTOFS_LAYOUT_COUNT | Count={count}");
		if (count <= 0 || count > MaximumOptionCount) {
			output.AppendLine("AUTOFS_LAYOUT_REJECTED | Reason=COUNT_OUT_OF_RANGE");
			return false;
		}
		bool found = false;
		for (int index = 0; index < count; index++) {
			IntPtr control = IntPtr.Add(list, index * OptionStride);
			found |= AppendOption(output, reader, "AUTOFS", index, control, IntPtr.Add(control, AutoFsTextOffset), true);
		}
		return found;
	}

	// Emits raw bytes and decoded Vietnamese text for one option without selecting it.
	private static bool AppendOption(StringBuilder output, MemoryReader reader, string layout, int index, IntPtr control, IntPtr textAddress, bool useAutoFsFixedLength) {
		int lengthCandidate = reader.ReadInt32(IntPtr.Add(control, TextLengthOffset));
		int readLength = useAutoFsFixedLength ? AutoFsFixedTextLength : lengthCandidate;
		if (readLength <= 0 || readLength > MaximumTextLength) readLength = AutoFsFixedTextLength;
		byte[] bytes = reader.ReadBytes(textAddress, readLength);
		if (bytes.Length == 0) {
			output.AppendLine($"NPC_MENU_OPTION | Layout={layout} | Index={index} | Control=0x{control.ToInt64():X8} | TextAddress=0x{textAddress.ToInt64():X8} | TextLengthCandidate={lengthCandidate} | Result=TEXT_UNREADABLE");
			return false;
		}
		int nullIndex = Array.IndexOf(bytes, (byte)0);
		if (nullIndex >= 0) bytes = bytes[..nullIndex];
		string decoded = LegacyVietnameseText.Decode(bytes).TrimEnd('\0');
		output.AppendLine($"NPC_MENU_OPTION | Layout={layout} | Index={index} | Control=0x{control.ToInt64():X8} | TextAddress=0x{textAddress.ToInt64():X8} | TextLengthCandidate={lengthCandidate} | BytesRead={bytes.Length} | TextHex={Convert.ToHexString(bytes)} | Decoded={decoded}");
		return ! string.IsNullOrWhiteSpace(decoded);
	}

	// Captures the complete current-client menu object and concise slot evidence for comparison across NPCs.
	private static void AppendCurrentClientEvidence(StringBuilder output, MemoryReader reader, IntPtr menu) {
		byte[] objectBytes = reader.ReadBytes(menu, ObjectScanLength);
		if (objectBytes.Length != ObjectScanLength) {
			output.AppendLine($"CURRENT_OBJECT_CAPTURE_REJECTED | Expected={ObjectScanLength} | Actual={objectBytes.Length}");
			return;
		}
		output.AppendLine("----- CURRENT_CLIENT_OBJECT -----");
		output.AppendLine($"MENU_OBJECT | Length={objectBytes.Length} | Sha256={Convert.ToHexString(SHA256.HashData(objectBytes))} | Base64={Convert.ToBase64String(objectBytes)}");
		output.AppendLine("----- CURRENT_SLOT_EVIDENCE -----");
		for (int index = 0; index < CurrentSlotCount; index++) {
			int offset = CurrentTextOffset + index * CurrentSlotStride;
			byte[] bytes = objectBytes[offset..(offset + CurrentSlotEvidenceLength)];
			int nullIndex = Array.IndexOf(bytes, (byte)0);
			byte[] textBytes = nullIndex > 0 ? bytes[..nullIndex] : Array.Empty<byte>();
			string decoded = textBytes.Length == 0 ? "" : LegacyVietnameseText.Decode(textBytes);
			output.AppendLine($"CURRENT_SLOT | Index={index} | TextOffset=+0x{offset:X} | RawHex={Convert.ToHexString(bytes)} | LeadingTextHex={Convert.ToHexString(textBytes)} | Decoded={decoded}");
		}
		output.AppendLine("----- CURRENT_OBJECT_TEXT_CANDIDATES -----");
		HashSet<long> scannedAddresses = new();
		HashSet<string> emittedCandidates = new(StringComparer.Ordinal);
		int candidateCount = AppendTextCandidates(output, reader, "MENU", menu, ObjectScanLength, scannedAddresses, emittedCandidates);
		output.AppendLine($"CURRENT_TEXT_SUMMARY | CandidateCount={candidateCount}");
	}

	// Emits plausible null-terminated strings from one readable object without interpreting them as confirmed menu options.
	private static int AppendTextCandidates(StringBuilder output, MemoryReader reader, string source, IntPtr address, int length, HashSet<long> scannedAddresses, HashSet<string> emittedCandidates) {
		if (! scannedAddresses.Add(address.ToInt64())) return 0;
		int found = 0;
		for (int blockOffset = 0; blockOffset < length; blockOffset += ChildPointerScanLength) {
			byte[] block = reader.ReadBytes(IntPtr.Add(address, blockOffset), Math.Min(ChildPointerScanLength, length - blockOffset));
			if (block.Length == 0) continue;
			int start = 0;
			while (start < block.Length) {
				while (start < block.Length && ! IsTextByte(block[start])) start++;
				int end = start;
				while (end < block.Length && IsTextByte(block[end]) && end - start < 80) end++;
				if (end - start >= 3 && end < block.Length && block[end] == 0) {
					byte[] bytes = block[start..end];
					string decoded = LegacyVietnameseText.Decode(bytes);
					int letterCount = decoded.Count(char.IsLetterOrDigit);
					string key = Convert.ToHexString(bytes);
					if (letterCount >= 3 && emittedCandidates.Add(key)) {
						long textAddress = address.ToInt64() + blockOffset + start;
						output.AppendLine($"NPC_MENU_TEXT_CANDIDATE | Source={source} | Address=0x{textAddress:X8} | ObjectOffset=+0x{blockOffset + start:X} | TextHex={key} | Decoded={decoded}");
						found++;
					}
				}
				start = end > start ? end + 1 : start + 1;
			}
		}
		return found;
	}

	// Accepts printable legacy single-byte text while excluding control bytes.
	private static bool IsTextByte(byte value) {
		return value is >= 0x20 and <= 0x7E || value >= 0x80;
	}
}
