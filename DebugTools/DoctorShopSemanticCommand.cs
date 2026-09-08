namespace Auto.DebugTools;

using System.Diagnostics;
using Auto.Runtime;
using Auto.Utils;

public static class DoctorShopSemanticCommand {
	// Phải khớp GameClientAddresses.h NpcConfirmModalVtableRva — sửa một bên mà quên bên kia thì managed và native
	// nhận diện popup khác nhau. Sửa 0x469E34 -> 0x46AF3C ngày 2026-09-08, xem bằng chứng ở header đó.
	private const int ExpectedDialogVtableRva = 0x46AF3C;
	private const int DialogOptionCommand = 7;
	private const int CurrentMenuTextOffset = 0x7EC;
	private const int CurrentMenuOptionStride = 0x69C;
	private const int CurrentMenuOptionCount = 8;
	private const int MaximumMenuTextLength = 128;

	// Confirms the AutoFS modal branch or reports the unconfirmed NPC-menu branch without guessing an index.
	public static bool TryInvoke(GameWindow game, out string result) {
		result = "";
		try {
			using Process process = Process.GetProcessById(game.ProcessId);
			IntPtr moduleBase = process.MainModule?.BaseAddress ?? IntPtr.Zero;
			if (process.HasExited || moduleBase == IntPtr.Zero) {
				result = "Game process hoặc module base không hợp lệ.";
				return false;
			}
			using MemoryReader reader = new(game.ProcessId);
			IntPtr dialog = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState));
			if (IsCurrentDialog(reader, moduleBase, dialog)) {
				bool confirmed = game.AutoFsTransport.TrySendCommand(game.Handle, DialogOptionCommand, -1, out string confirmationResult);
				result = $"Doctor confirmation modal | AutoFSFlow=Command7/-1 | Dialog=0x{dialog.ToInt64():X8} | VtableRva=0x{ExpectedDialogVtableRva:X} | Result={confirmed} | {confirmationResult}";
				return confirmed;
			}
			if (dialog == IntPtr.Zero) {
				result = "Không tìm thấy dialog xác nhận hoặc menu NPC.";
				return false;
			}
			if (! TryFindShopOption(reader, dialog, out int optionIndex, out string optionText, out string menuEvidence)) {
				result = $"Menu NPC không có duy nhất chức năng Dược phẩm/Mua bán | MenuState=0x{dialog.ToInt64():X8} | {menuEvidence}";
				return false;
			}
			bool selected = game.AutoFsTransport.TrySendCommand(game.Handle, DialogOptionCommand, optionIndex, out string selectionResult);
			result = $"Doctor shop menu | AutoFSFlow=Command7/{optionIndex} | Option={optionText} | MenuState=0x{dialog.ToInt64():X8} | Result={selected} | {selectionResult} | {menuEvidence}";
			return selected;
		} catch (Exception ex) {
			result = ex.Message;
			return false;
		}
	}

	// Finds exactly one current-client NPC option whose decoded legacy text is Dược phẩm or Mua bán.
	private static bool TryFindShopOption(MemoryReader reader, IntPtr menu, out int optionIndex, out string optionText, out string evidence) {
		optionIndex = -1;
		optionText = "";
		List<string> options = new(CurrentMenuOptionCount);
		for (int index = 0; index < CurrentMenuOptionCount; index++) {
			byte[] bytes = reader.ReadBytes(IntPtr.Add(menu, CurrentMenuTextOffset + index * CurrentMenuOptionStride), MaximumMenuTextLength);
			int terminator = Array.IndexOf(bytes, (byte)0);
			if (terminator >= 0) bytes = bytes[..terminator];
			string decoded = LegacyVietnameseText.Decode(bytes).Trim();
			if (string.IsNullOrWhiteSpace(decoded)) continue;
			options.Add($"#{index}={decoded}");
			if (! IsShopOption(decoded)) continue;
			if (optionIndex >= 0) {
				evidence = "Options=[" + string.Join(",", options) + "] | Match=AMBIGUOUS";
				return false;
			}
			optionIndex = index;
			optionText = decoded;
		}
		evidence = "Options=[" + string.Join(",", options) + $"] | Match={(optionIndex >= 0 ? "UNIQUE" : "NOT_FOUND")}";
		return optionIndex >= 0;
	}

	// Matches only the two shop labels confirmed by the developer for the current client.
	private static bool IsShopOption(string text) {
		return text.Contains("Dược phẩm", StringComparison.OrdinalIgnoreCase) || text.Contains("Mua bán", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsCurrentDialog(MemoryReader reader, IntPtr moduleBase, IntPtr dialog) {
		if (dialog == IntPtr.Zero) return false;
		uint vtable = unchecked((uint)reader.ReadInt32(dialog));
		return vtable == moduleBase.ToInt64() + ExpectedDialogVtableRva;
	}
}
