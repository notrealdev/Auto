namespace Auto.DebugTools;

using System.Diagnostics;
using System.Security.Cryptography;
using Auto.Runtime;
using Auto.Utils;

public static class ShopRepairDebugCommand {
	private const int DialogOptionCommand = 7;
	private const int EvidenceChunkSize = 0x10000;
	private const int MaximumReferenceCount = 128;
	private static readonly TimeSpan PopupTimeout = TimeSpan.FromSeconds(6);
	private static readonly TimeSpan DurabilityTimeout = TimeSpan.FromSeconds(10);

	// Runs one confirmed repair-all action against an already-open NPC shop and verifies durability afterward.
	public static string Run(GameWindow game, Action<string> log) {
		if (!TryReadShopState(game.ProcessId, out uint modalState, out uint shopState, out string stateFailure)) return "DEBUG_REPAIR_SHOP_FAIL | " + stateFailure;
		log($"DEBUG_REPAIR_SHOP_STATE | ModalState={modalState} | ShopState={shopState}");
		if (modalState != 0 || shopState != 2) return $"DEBUG_REPAIR_SHOP_FAIL | Shop chưa sẵn sàng | ModalState={modalState} | ShopState={shopState}";

		EquippedWeaponDurabilityReading before = game.WeaponRepairMonitor.ReadFresh(game.ProcessId);
		log($"DEBUG_REPAIR_BEFORE | Success={before.Success} | Minimum={before.Current} | Maximum={before.Maximum} | {before.Evidence} | Failure={before.FailureReason}");
		if (!before.Success) return "DEBUG_REPAIR_SHOP_FAIL | Không đọc được độ bền trước khi sửa | " + before.FailureReason;
		if (!FullMouseHandlerPickupCommand.TryOpenRepairAllConfirmation(game.ProcessId, game.Handle, out string repairResult)) return "DEBUG_REPAIR_COMMAND_FAIL | " + repairResult;
		log("DEBUG_REPAIR_COMMAND_INVOKED | " + repairResult);

		DateTime popupDeadline = DateTime.UtcNow.Add(PopupTimeout);
		while (DateTime.UtcNow < popupDeadline) {
			if (TryReadShopState(game.ProcessId, out modalState, out shopState, out _) && modalState != 0) break;
			Thread.Sleep(100);
		}
		if (modalState == 0) return $"DEBUG_REPAIR_POPUP_FAIL | Popup xác nhận không xuất hiện | ShopState={shopState}";
		log($"DEBUG_REPAIR_POPUP_OBSERVED | ModalState={modalState} | ShopState={shopState}");
		AppendPopupEvidence(game.ProcessId, log);

		if (!game.AutoFsTransport.TrySendCommand(game.Handle, DialogOptionCommand, -1, out string confirmResult)) return "DEBUG_REPAIR_CONFIRM_FAIL | " + confirmResult;
		log("DEBUG_REPAIR_CONFIRM_POSTED | Command=7 | Payload=-1 | " + confirmResult);

		DateTime durabilityDeadline = DateTime.UtcNow.Add(DurabilityTimeout);
		while (DateTime.UtcNow < durabilityDeadline) {
			Thread.Sleep(250);
			EquippedWeaponDurabilityReading after = game.WeaponRepairMonitor.ReadFresh(game.ProcessId);
			if (!after.Success) continue;
			if (after.Current > before.Current || after.Current == after.Maximum) {
				return $"DEBUG_REPAIR_CONFIRMED | Minimum={before.Current}->{after.Current} | Maximum={after.Maximum} | {after.Evidence}";
			}
		}
		EquippedWeaponDurabilityReading final = game.WeaponRepairMonitor.ReadFresh(game.ProcessId);
		return $"DEBUG_REPAIR_UNCONFIRMED | Minimum={before.Current}->{(final.Success ? final.Current.ToString() : "UNAVAILABLE")} | Failure={final.FailureReason}";
	}

	// Captures both current-client popup roots so the internal confirmation class can be identified without input simulation.
	private static void AppendPopupEvidence(int processId, Action<string> log) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			uint modalVtable = AppendObjectEvidence(reader, moduleBase, "MODAL", GameAddresses.Globals.ModalState, log);
			AppendObjectEvidence(reader, moduleBase, "DIALOG", GameAddresses.Globals.DialogPointer, log);
			if (modalVtable != 0) AppendPopupCodeEvidence(processId, reader, moduleBase, modalVtable, log);
		} catch (Exception ex) {
			log($"DEBUG_REPAIR_POPUP_EVIDENCE_FAIL | {ex.GetType().Name}: {ex.Message}");
		}
	}

	// Emits one popup object pointer, vtable and bounded raw header for static client-code correlation.
	private static uint AppendObjectEvidence(MemoryReader reader, IntPtr moduleBase, string label, int pointerRva, Action<string> log) {
		IntPtr objectPointer = reader.ReadPointer32(IntPtr.Add(moduleBase, pointerRva));
		if (objectPointer == IntPtr.Zero) {
			log($"DEBUG_REPAIR_POPUP_OBJECT | Label={label} | GlobalRva=0x{pointerRva:X} | Pointer=0x00000000");
			return 0;
		}
		uint vtable = unchecked((uint)reader.ReadInt32(objectPointer));
		byte[] header = reader.ReadBytes(objectPointer, 0x200);
		string hash = header.Length == 0 ? "UNREADABLE" : Convert.ToHexString(SHA256.HashData(header));
		log($"DEBUG_REPAIR_POPUP_OBJECT | Label={label} | GlobalRva=0x{pointerRva:X} | Pointer=0x{objectPointer.ToInt64():X8} | Vtable=0x{vtable:X8} | VtableRva=0x{vtable - moduleBase.ToInt64():X} | HeaderLength={header.Length} | HeaderSha256={hash} | HeaderHex={Convert.ToHexString(header)}");
		return vtable;
	}

	// Captures the popup vtable, handler code and module references needed to map the missing AutoFS event 0x565 branch once.
	private static void AppendPopupCodeEvidence(int processId, MemoryReader reader, IntPtr moduleBase, uint vtable, Action<string> log) {
		using Process process = Process.GetProcessById(processId);
		int moduleSize = process.MainModule?.ModuleMemorySize ?? 0;
		byte[] vtableBytes = reader.ReadBytes(new IntPtr(vtable), 0x100);
		log($"DEBUG_REPAIR_POPUP_VTABLE | Address=0x{vtable:X8} | Length={vtableBytes.Length} | Bytes={Convert.ToHexString(vtableBytes)}");
		for (int offset = 0; offset + 4 <= vtableBytes.Length; offset += 4) {
			uint function = BitConverter.ToUInt32(vtableBytes, offset);
			long functionRva = function - moduleBase.ToInt64();
			if (functionRva < 0 || functionRva >= moduleSize) continue;
			byte[] code = reader.ReadBytes(new IntPtr(function), 0x100);
			log($"DEBUG_REPAIR_POPUP_HANDLER | VtableOffset=0x{offset:X2} | Address=0x{function:X8} | Rva=0x{functionRva:X} | CodeLength={code.Length} | Code={Convert.ToHexString(code)}");
		}

		AppendModuleReferences(reader, moduleBase, moduleSize, vtable, "VTABLE", log);
		AppendModuleReferences(reader, moduleBase, moduleSize, unchecked((uint)(moduleBase.ToInt64() + GameAddresses.Globals.ModalState)), "MODAL_GLOBAL", log);
	}

	// Finds direct references in the unpacked runtime image and emits bounded surrounding code without changing game memory.
	private static void AppendModuleReferences(MemoryReader reader, IntPtr moduleBase, int moduleSize, uint target, string label, Action<string> log) {
		byte[] pattern = BitConverter.GetBytes(target);
		int referenceCount = 0;
		for (int chunkOffset = 0; chunkOffset < moduleSize && referenceCount < MaximumReferenceCount; chunkOffset += EvidenceChunkSize) {
			int length = Math.Min(EvidenceChunkSize, moduleSize - chunkOffset);
			byte[] chunk = reader.ReadBytes(IntPtr.Add(moduleBase, chunkOffset), length);
			if (chunk.Length != length) continue;
			for (int offset = 0; offset + pattern.Length <= chunk.Length && referenceCount < MaximumReferenceCount; offset++) {
				if (!chunk.AsSpan(offset, pattern.Length).SequenceEqual(pattern)) continue;
				int referenceRva = chunkOffset + offset;
				int contextRva = Math.Max(0, referenceRva - 0x40);
				byte[] context = reader.ReadBytes(IntPtr.Add(moduleBase, contextRva), 0x100);
				log($"DEBUG_REPAIR_POPUP_REFERENCE | Label={label} | Target=0x{target:X8} | ReferenceRva=0x{referenceRva:X} | ContextRva=0x{contextRva:X} | Context={Convert.ToHexString(context)}");
				referenceCount++;
				offset += pattern.Length - 1;
			}
		}
		log($"DEBUG_REPAIR_POPUP_REFERENCE_SUMMARY | Label={label} | Target=0x{target:X8} | ModuleSize=0x{moduleSize:X} | References={referenceCount} | Limit={MaximumReferenceCount}");
	}

	// Reads the confirmed modal and shop state fields for the selected game process.
	private static bool TryReadShopState(int processId, out uint modalState, out uint shopState, out string failure) {
		modalState = 0;
		shopState = 0;
		failure = "";
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) {
				failure = "Game.exe không tồn tại.";
				return false;
			}
			modalState = unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState)));
			shopState = unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ShopState)));
			return true;
		} catch (Exception ex) {
			failure = ex.GetType().Name + ": " + ex.Message;
			return false;
		}
	}
}
