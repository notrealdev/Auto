namespace Auto.DebugTools;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

public static class EquippedWeaponDurabilityProbe {
	public const string BuildStamp = "WEAPON-EQUIPPED-DURABILITY-20260817-12";
	private const int ProcessVmRead = 0x0010;
	private const int ProcessQueryInformation = 0x0400;
	private const int MemCommit = 0x1000;
	private const int MemPrivate = 0x20000;
	private const int PageGuard = 0x100;
	private const int PageNoAccess = 0x01;
	private const int PageReadWrite = 0x04;
	private const int PageWriteCopy = 0x08;
	private const int PageExecuteReadWrite = 0x40;
	private const int PageExecuteWriteCopy = 0x80;
	private const long MaximumAddress = 0x7FFF0000;
	private const long MaximumScannedBytes = 640L * 1024 * 1024;
	private const int ChunkSize = 4 * 1024 * 1024;
	private const int MaximumCandidates = 4000000;
	private const int MaximumOutputCandidates = 40;
	private const long EquippedMirrorDistance = 0x43C;
	private const long ConfirmedEquippedRecordOffsetAddress = 0x001982EC;
	private const long SupportingEquippedRecordOffsetAddress = 0x00197EB0;
	private const uint ItemRecordStride = 0x17C0;
	private const int GameRootPointerRva = 0x40EE1C;
	private const int EquipmentManagerOffset = 0x3BCB4;
	private const int EquipmentSlotArrayOffset = 0x0C;
	private const int EquipmentSlotStride = 0x08;
	private const int EquipmentSlotCount = 12;
	private const uint CurrentClientItemRecordStride = 0x1800;
	// Cập nhật sau bản game 2026-08-28: xác nhận qua symbol Ghidra DAT_00e7fe24 trong decompile hàm ATTACK mới đã đối chiếu, khớp delta +0x2020 như GameAddresses.Globals.InventoryRoot. Chưa build/test runtime.
	private const int CurrentClientGameRootPointerRva = 0xA7FE24;
	// Cập nhật sau bản game 2026-08-28: xác nhận bằng byte thật, khớp delta +0x2020 như GameAddresses.Globals.ItemTable. Chưa build/test runtime.
	private const int CurrentClientItemTablePointerRva = 0x541068;
	private const int CurrentClientEquipmentManagerOffset = 0x41D44;
	private const int CurrentClientEquipmentSlotArrayOffset = 0x0C;
	private const int CurrentClientEquipmentSlotStride = 0x08;
	private const int CurrentClientEquipmentSlotCount = 12;
	private const long CurrentClientDurabilityBias = 0x0AA4;
	private const int CurrentClientCharacterStride = 0x35F60;
	private const int CurrentClientCharacterClassRecordOffset = 0xBDDC;
	private const int CurrentClientAlternateEquipmentStateOffset = 0x168E4;
	// Cập nhật sau bản game 2026-08-28: cùng field với GameAddresses.Globals.EntityTable (stride 0xD87C khớp), đã xác nhận qua BSim+decompile trước đó. Chưa build/test runtime.
	private const int CurrentClientClassRelationTableRva = 0x95FF60;
	private const int CurrentClientClassRelationStride = 0xD87C;
	private const int CurrentClientClassRelationOffset = 0x27B8;
	// Cập nhật sau bản game 2026-08-28: cùng field với GameAddresses.Globals.MapCoordinateRoot (stride 0x260 khớp MapObjectStride), đã xác nhận qua byte thật trong hàm CONVERTER trước đó. Chưa build/test runtime.
	private const int CurrentClientClassStateTablePointerRva = 0x9FA080;
	private const int CurrentClientClassStateStride = 0x260;
	private const int CurrentClientClassStateOffset = 0x08;
	private const int CharacterStride = 0x2FF40;
	private const int CharacterClassRecordOffset = 0xBD6C;
	private const int AlternateEquipmentStateOffset = 0x107B8;
	private const int CharacterClassTablePointerRva = 0x6611F0;
	private const int CharacterClassStateRva = 0xB80D30;
	private const int CharacterClassEntryStride = 13627 * 4;
	private const int CharacterClassTypeOffset = 0x27A4;
	private const uint MaximumEquippedRecordIndex = (uint)((KnownItemRegionSize - ItemRecordFieldBias - 4) / ItemRecordStride);
	private const long ItemRecordCurrentBias = 0x0A9C;
	private const long ItemRecordCurrentMirrorBias = 0x0AA0;
	private const long ItemRecordMaximumBias = 0x0AC8;
	private const long ItemRecordHeaderBias = 0x0A98;
	private const long ItemRecordDurabilityMarkerBias = 0x0AC4;
	private const uint ExpectedItemRecordHeader = 3;
	private const uint ExpectedDurabilityMarker = 9;
	private const long KnownItemRegionSize = 0x21BD000;
	private const long ItemRecordFieldBias = ItemRecordCurrentBias;
	private static readonly object Sync = new();
	private static readonly Dictionary<int, Session> Sessions = new();
	private static readonly Dictionary<int, EquippedTransitionSession> TransitionSessions = new();
	private static readonly Dictionary<int, ConfirmedRecordCache> ConfirmedRecordByProcess = new();
	private static readonly Dictionary<int, EquipmentSlotArrayCache> EquipmentSlotArrayByProcess = new();

	public static string Run(int processId, string command) {
		string label = command.Trim();
		if (label.StartsWith("EQUIP_", StringComparison.OrdinalIgnoreCase)) return RunEquippedTransition(processId, label);
		if (string.Equals(label, "RESET", StringComparison.OrdinalIgnoreCase)) {
			lock (Sync) Sessions.Remove(processId);
			return BuildMessage(processId, "RESET", "Phiên đo độ bền trang bị của tài khoản này đã được xóa.");
		}
		if (string.Equals(label, "INSPECT", StringComparison.OrdinalIgnoreCase)) return InspectEquipped(processId);
		if (string.Equals(label, "AUDIT", StringComparison.OrdinalIgnoreCase)) return InspectAllDurabilitySources(processId);
		if (string.Equals(label, "SLOT_ARRAY", StringComparison.OrdinalIgnoreCase)) return InspectEquipmentSlotArrays(processId);
		if (label.StartsWith("SLOT_SOURCE", StringComparison.OrdinalIgnoreCase)) return InspectEquipmentSlotSource(processId, label);
		if (string.Equals(label, "COMPARE", StringComparison.OrdinalIgnoreCase)) return Compare(processId);
		if (!TryParseDurability(label, out uint current, out uint maximum)) return BuildMessage(processId, "FAIL", "Nhãn phải có dạng DUR_CUR13_MAX16_A1, DUR_CUR13_MAX16_A2 hoặc DUR_CUR12_MAX16.");
		if (current > maximum) return BuildMessage(processId, "FAIL", $"Độ bền hiện tại không thể lớn hơn tối đa | Current={current} Max={maximum}.");

		try {
			Session session = GetSession(processId);
			if (session.Samples.Count == 0) return CaptureBaseline(processId, label, current, maximum, session);
			if (session.Maximum != maximum) return BuildMessage(processId, "FAIL", $"Maximum không khớp phiên hiện tại | Expected={session.Maximum} Actual={maximum}. Hãy RESET nếu đổi vũ khí.");
			if (current != session.BaselineCurrent && session.ControlCaptures < 2) return BuildMessage(processId, "FAIL", "Cần capture hai control cùng độ bền trước khi làm giảm độ bền.");
			return VerifyCapture(processId, label, current, maximum, session);
		} catch (Exception ex) {
			return BuildMessage(processId, "FAIL", ex.ToString());
		}
	}

	private static string InspectEquipmentSlotArrays(int processId) {
		StringBuilder text = CreateHeader(processId);
		text.AppendLine("Mode = READ_ONLY | Probe = EQUIPMENT_SLOT_ARRAY");
		try {
			using ProcessScanner scanner = new(processId);
			List<EquipmentSequenceMatch> matches = scanner.FindEquipmentSequences();
			text.AppendLine($"Status = COMPLETED | Matches={matches.Count}");
			foreach (EquipmentSequenceMatch match in matches.Take(MaximumOutputCandidates)) text.AppendLine($"Address=0x{match.Address:X8} | Stride=0x{match.Stride:X} | Kind={match.Kind} | Values={string.Join(",", match.Values)}");
			if (matches.Count > MaximumOutputCandidates) text.AppendLine($"RemainingMatches={matches.Count - MaximumOutputCandidates}");
			if (matches.Count == 0) text.AppendLine("Không tìm thấy chuỗi slot theo thứ tự record đã quan sát; không suy diễn mảng equipment.");
		} catch (Exception ex) {
			text.AppendLine($"Status = FAIL | {ex.GetType().Name}: {ex.Message}");
		}
		return text.ToString();
	}

	private static string InspectEquipmentSlotSource(int processId, string command) {
		StringBuilder text = CreateHeader(processId);
		text.AppendLine("Mode = READ_ONLY | Probe = EQUIPMENT_SLOT_SOURCE");
		string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2 || !long.TryParse(parts[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out long capturedAddress)) {
			text.AppendLine("Status = FAIL | Cú pháp: SLOT_SOURCE 03F82364");
			return text.ToString();
		}
		try {
			using ProcessScanner scanner = new(processId);
			MemoryLocation location = scanner.DescribeLocation(capturedAddress);
			List<long> references = scanner.FindPointerReferences((uint)capturedAddress);
			List<EquipmentSequenceMatch> liveArrays = scanner.FindEquipmentSlotArrays();
			text.AppendLine($"Status = COMPLETED | CapturedAddress=0x{capturedAddress:X8} | AllocationBase=0x{location.AllocationBase:X8} | Relative=0x{location.RelativeOffset:X} | LiveArrays={liveArrays.Count} | PointerReferences={references.Count}");
			text.AppendLine("----- Captured Address Neighborhood -----");
			for (int delta = -0x40; delta <= 0x70; delta += 4) text.AppendLine($"Address=0x{capturedAddress + delta:X8} | Delta={delta:+#;-#;0} | Value={scanner.ReadUInt32(capturedAddress + delta)}/0x{scanner.ReadUInt32(capturedAddress + delta):X8}");
			text.AppendLine("----- Direct Pointer References -----");
			foreach (long reference in references.Take(200)) {
				MemoryLocation referenceLocation = scanner.DescribeLocation(reference);
				text.AppendLine($"Reference=0x{reference:X8} | AllocationBase=0x{referenceLocation.AllocationBase:X8} | Relative=0x{referenceLocation.RelativeOffset:X}");
			}
			if (references.Count > 200) text.AppendLine($"RemainingReferences={references.Count - 200}");
		} catch (Exception ex) {
			text.AppendLine($"Status = FAIL | {ex.GetType().Name}: {ex.Message}");
		}
		return text.ToString();
	}

	public static EquippedWeaponDurabilityReading ReadCurrent(int processId) {
		try {
			using ProcessScanner scanner = new(processId);
			return ReadEquipmentSlotArrayCurrent(processId, scanner);
		} catch (Exception ex) {
			return EquippedWeaponDurabilityReading.Fail(ex.Message + " | SAFE_REJECT.");
		}
	}

	private static EquippedWeaponDurabilityReading ReadEquipmentSlotArrayCurrent(int processId, ProcessScanner scanner) {
		long gameRootGlobal = scanner.ModuleBase + CurrentClientGameRootPointerRva;
		long gameRoot = scanner.ReadUInt32(gameRootGlobal);
		if (gameRoot == 0) return EquippedWeaponDurabilityReading.Fail($"Game root chưa sẵn sàng | Global=Game.exe+0x{CurrentClientGameRootPointerRva:X}@0x{gameRootGlobal:X8} | SAFE_REJECT.");
		long equipmentManager = gameRoot + CurrentClientEquipmentManagerOffset;
		long itemTable = scanner.ReadUInt32(scanner.ModuleBase + CurrentClientItemTablePointerRva);
		if (itemTable == 0) return EquippedWeaponDurabilityReading.Fail($"Item table chưa sẵn sàng | Global=Game.exe+0x{CurrentClientItemTablePointerRva:X} | RootGlobal=0x{gameRootGlobal:X8} | Root=0x{gameRoot:X8} | EquipmentManager=0x{equipmentManager:X8} | SAFE_REJECT.");
		if (! TryReadCurrentClientActiveEquipmentBank(scanner, gameRoot, equipmentManager, out int activeBank, out string bankEvidence)) return EquippedWeaponDurabilityReading.Fail($"Không xác định được bank trang bị đang hoạt động | RootGlobal=0x{gameRootGlobal:X8} | Root=0x{gameRoot:X8} | EquipmentManager=0x{equipmentManager:X8} | {bankEvidence} | SAFE_REJECT.");
		List<EquippedWeaponDurabilityReading> equippedItems = new();
		List<string> occupiedSlots = new();
		List<string> durabilityRecords = new();
		List<string> slotAudit = new();
		long slotArrayAddress = equipmentManager + CurrentClientEquipmentSlotArrayOffset + activeBank * CurrentClientEquipmentSlotCount * CurrentClientEquipmentSlotStride;
		for (int slot = 0; slot < CurrentClientEquipmentSlotCount; slot++) {
			long slotAddress = slotArrayAddress + slot * CurrentClientEquipmentSlotStride;
			uint itemIndex = scanner.ReadUInt32(slotAddress);
			if (itemIndex == 0) continue;
			occupiedSlots.Add($"B{activeBank}:S{slot}=Index{itemIndex}@0x{slotAddress:X8}");
			if (itemIndex >= 4096) {
				slotAudit.Add($"B{activeBank}:S{slot}=Index{itemIndex}:REJECT_INDEX_RANGE");
				continue;
			}
			long recordAddress = itemTable + itemIndex * CurrentClientItemRecordStride;
			uint current = scanner.ReadUInt32(recordAddress + CurrentClientDurabilityBias);
			durabilityRecords.Add($"B{activeBank}:S{slot}=Index{itemIndex},Record=0x{recordAddress:X8},Current={current},Field=0x{recordAddress + CurrentClientDurabilityBias:X8}");
			if (current == uint.MaxValue || current > 1000) {
				slotAudit.Add($"B{activeBank}:S{slot}=Index{itemIndex}:NO_DURABILITY_{current}");
				continue;
			}
			slotAudit.Add($"B{activeBank}:S{slot}=Index{itemIndex}:ACCEPT_DURABILITY_{current}");
			equippedItems.Add(new EquippedWeaponDurabilityReading(true, slotAddress, itemTable, itemIndex * CurrentClientItemRecordStride, recordAddress + CurrentClientDurabilityBias, current, 0, true, false, ""));
		}
		if (equippedItems.Count == 0) return EquippedWeaponDurabilityReading.Fail($"Không có trang bị có độ bền trong bank đang hoạt động | Root=0x{gameRoot:X8} | EquipmentManager=0x{equipmentManager:X8} | ActiveBank={activeBank} | BankEvidence={bankEvidence} | ItemTable=0x{itemTable:X8} | OccupiedSlots={occupiedSlots.Count}[{string.Join(",", occupiedSlots)}] | SlotAudit=[{string.Join(",", slotAudit)}] | DurabilityRecords={durabilityRecords.Count}[{string.Join(",", durabilityRecords)}] | SAFE_REJECT.");
		EquippedWeaponDurabilityReading minimum = equippedItems.OrderBy(item => item.Current).First();
		string evidence = $"ActiveBank={activeBank} | BankEvidence={bankEvidence} | EquippedDurabilityCount={equippedItems.Count} | DurabilityRecords=[{string.Join(",", durabilityRecords)}]";
		return minimum with { Evidence = evidence };
	}

	private static bool TryReadCurrentClientActiveEquipmentBank(ProcessScanner scanner, long gameRoot, long equipmentManager, out int activeBank, out string evidence) {
		uint characterIndex = scanner.ReadUInt32(equipmentManager);
		long characterAddress = gameRoot + (long)characterIndex * CurrentClientCharacterStride;
		uint classRecordIndex = scanner.ReadUInt32(characterAddress + CurrentClientCharacterClassRecordOffset);
		long classRelationTable = scanner.ReadUInt32(scanner.ModuleBase + CurrentClientClassRelationTableRva);
		if (classRelationTable == 0) {
			activeBank = 0;
			evidence = $"CharacterIndex={characterIndex} | ClassRecordIndex={classRecordIndex} | ClassRelationTable=NULL";
			return false;
		}
		int classStateIndex = unchecked((int)scanner.ReadUInt32(classRelationTable + (long)classRecordIndex * CurrentClientClassRelationStride + CurrentClientClassRelationOffset));
		if (classStateIndex < 0) {
			activeBank = 0;
			evidence = $"CharacterIndex={characterIndex} | ClassRecordIndex={classRecordIndex} | ClassStateIndex={classStateIndex}";
			return true;
		}
		long classStateTable = scanner.ReadUInt32(scanner.ModuleBase + CurrentClientClassStateTablePointerRva);
		if (classStateTable == 0) {
			activeBank = 0;
			evidence = $"CharacterIndex={characterIndex} | ClassRecordIndex={classRecordIndex} | ClassStateIndex={classStateIndex} | ClassStateTable=NULL";
			return false;
		}
		uint classState = scanner.ReadUInt32(classStateTable + (long)classStateIndex * CurrentClientClassStateStride + CurrentClientClassStateOffset);
		if (classState == 0x20) {
			activeBank = 1;
			evidence = $"CharacterIndex={characterIndex} | ClassRecordIndex={classRecordIndex} | ClassStateIndex={classStateIndex} | ClassState=0x{classState:X}";
			return true;
		}
		uint globalClassState = scanner.ReadUInt32(classStateTable + CurrentClientClassStateOffset);
		if (globalClassState != 0x40) {
			activeBank = 0;
			evidence = $"CharacterIndex={characterIndex} | ClassRecordIndex={classRecordIndex} | ClassStateIndex={classStateIndex} | ClassState=0x{classState:X} | GlobalClassState=0x{globalClassState:X}";
			return true;
		}
		activeBank = scanner.ReadByte(characterAddress + CurrentClientAlternateEquipmentStateOffset) == 1 ? 1 : 0;
		evidence = $"CharacterIndex={characterIndex} | ClassRecordIndex={classRecordIndex} | ClassStateIndex={classStateIndex} | ClassState=0x{classState:X} | GlobalClassState=0x{globalClassState:X} | AlternateState={activeBank}";
		return true;
	}

	private static EquippedWeaponDurabilityReading ReadValidatedEquippedRecord(ProcessScanner scanner, long regionBase, uint recordOffset, long slotAddress) {
		long recordAddress = regionBase + recordOffset;
		uint header = scanner.ReadUInt32(recordAddress + ItemRecordHeaderBias);
		uint marker = scanner.ReadUInt32(recordAddress + ItemRecordDurabilityMarkerBias);
		uint current = scanner.ReadUInt32(recordAddress + ItemRecordMaximumBias);
		if (header != ExpectedItemRecordHeader || marker != ExpectedDurabilityMarker || current > 1000) return EquippedWeaponDurabilityReading.Fail($"Equipped weapon record không hợp lệ | Header={header} Marker={marker} Current={current} Record={recordOffset} | SAFE_REJECT.");
		return new EquippedWeaponDurabilityReading(true, slotAddress, 0, recordOffset, recordAddress + ItemRecordMaximumBias, current, current, true, false, "");
	}

	private static int ReadActiveEquipmentBank(ProcessScanner scanner, long gameRoot, long equipmentManager) {
		uint characterIndex = scanner.ReadUInt32(equipmentManager);
		long characterBase = gameRoot + (long)characterIndex * CharacterStride;
		uint classRecordIndex = scanner.ReadUInt32(characterBase + CharacterClassRecordOffset);
		long classTable = scanner.ReadUInt32(scanner.ModuleBase + CharacterClassTablePointerRva);
		int classTypeIndex = unchecked((int)scanner.ReadUInt32(classTable + (long)classRecordIndex * CharacterClassEntryStride + CharacterClassTypeOffset));
		if (classTypeIndex >= 0) {
			uint classType = scanner.ReadUInt32(scanner.ModuleBase + CharacterClassStateRva + classTypeIndex * 496L);
			if (classType == 0x20) return 1;
		}
		uint globalClassState = scanner.ReadUInt32(scanner.ModuleBase + CharacterClassStateRva);
		return globalClassState == 0x40 && scanner.ReadByte(characterBase + AlternateEquipmentStateOffset) == 1 ? 1 : 0;
	}

	private static void CacheConfirmedRecord(int processId, ProcessScanner scanner, EquippedWeaponDurabilityReading reading) {
		long fieldBias = reading.UsesDirectCurrent ? ItemRecordMaximumBias : ItemRecordCurrentBias;
		long regionBase = reading.Address - fieldBias - reading.RecordOffset;
		lock (Sync) ConfirmedRecordByProcess[processId] = new ConfirmedRecordCache(scanner.ProcessStartTimeUtcTicks, regionBase, reading.RecordOffset);
	}

	private static EquippedWeaponDurabilityReading ReadCachedConfirmedRecord(int processId, ProcessScanner scanner) {
		ConfirmedRecordCache? cache;
		lock (Sync) ConfirmedRecordByProcess.TryGetValue(processId, out cache);
		if (cache == null || cache.ProcessStartTimeUtcTicks != scanner.ProcessStartTimeUtcTicks) return EquippedWeaponDurabilityReading.Fail("Không có equipped record cache hợp lệ cho phiên process hiện tại.");
		List<long> itemRegionBases = scanner.FindKnownItemRegionBases();
		if (itemRegionBases.Count != 1 || itemRegionBases[0] != cache.ItemRegionBase) return EquippedWeaponDurabilityReading.Fail("Equipped record cache không khớp vùng item hiện tại.");
		EquippedWeaponDurabilityReading reading = ReadValidatedRecord(scanner, cache.ItemRegionBase, cache.RecordOffset, 0, 0);
		return reading.Success ? reading with { UsesCachedRecord = true } : reading;
	}

	public static EquippedWeaponDurabilityReading ReadKnownRecordOffset(int processId, uint recordOffset) {
		if (!IsRecordOffset(recordOffset)) return EquippedWeaponDurabilityReading.Fail($"Known record offset không hợp lệ | Record={recordOffset}.");
		try {
			using ProcessScanner scanner = new(processId);
			List<long> itemRegionBases = scanner.FindKnownItemRegionBases();
			if (itemRegionBases.Count != 1) return EquippedWeaponDurabilityReading.Fail($"Item region không duy nhất | Count={itemRegionBases.Count}.");
			return ReadValidatedRecord(scanner, itemRegionBases[0], recordOffset, 0, 0);
		} catch (Exception ex) {
			return EquippedWeaponDurabilityReading.Fail(ex.Message);
		}
	}

	public static string InspectKnownRecordNeighborhood(int processId, uint recordOffset) {
		StringBuilder text = new();
		text.AppendLine("===== Equipped Durability Record Neighborhood =====");
		text.AppendLine("Mode=READ_ONLY | GameMemoryWrite=NO");
		try {
			using ProcessScanner scanner = new(processId);
			uint confirmedOffset = scanner.ReadUInt32(ConfirmedEquippedRecordOffsetAddress);
			uint supportingOffset = scanner.ReadUInt32(SupportingEquippedRecordOffsetAddress);
			List<long> itemRegionBases = scanner.FindKnownItemRegionBases();
			text.AppendLine($"Status=COMPLETED | PID={processId} | Record={recordOffset} | ItemRegions={itemRegionBases.Count}");
			text.AppendLine($"EquippedRecordSlots=Supporting:{supportingOffset}/Secondary:{confirmedOffset} | SameRecord={supportingOffset == confirmedOffset} | Addresses=0x{SupportingEquippedRecordOffsetAddress:X8}/0x{ConfirmedEquippedRecordOffsetAddress:X8}");
			foreach (long regionBase in itemRegionBases) {
				long assumedAddress = regionBase + ItemRecordFieldBias + recordOffset;
				text.AppendLine($"RegionBase=0x{regionBase:X8} | AssumedAddress=0x{assumedAddress:X8}");
				for (int delta = -0x200; delta <= 0x200; delta += 4) {
					uint value = scanner.ReadUInt32(assumedAddress + delta);
					if (value <= 64) text.AppendLine($"  Delta={delta:+#;-#;0} | Address=0x{assumedAddress + delta:X8} | Dword={value}");
				}
			}
		} catch (Exception ex) {
			text.AppendLine($"Status=FAIL | {ex.GetType().Name}: {ex.Message}");
		}
		return text.ToString();
	}

	private static EquippedWeaponDurabilityReading ReadConfirmedCurrent(ProcessScanner scanner) {
		uint recordOffset = scanner.ReadUInt32(SupportingEquippedRecordOffsetAddress);
		if (!IsRecordOffset(recordOffset)) return EquippedWeaponDurabilityReading.Fail("Supporting equipped weapon record offset không hợp lệ.");
		uint secondaryOffset = scanner.ReadUInt32(ConfirmedEquippedRecordOffsetAddress);
		List<long> itemRegionBases = scanner.FindKnownItemRegionBases();
		if (itemRegionBases.Count != 1) return EquippedWeaponDurabilityReading.Fail($"Item region không duy nhất | Count={itemRegionBases.Count}.");
		long secondaryAddress = secondaryOffset == recordOffset ? ConfirmedEquippedRecordOffsetAddress : 0;
		return ReadValidatedRecord(scanner, itemRegionBases[0], recordOffset, SupportingEquippedRecordOffsetAddress, secondaryAddress);
	}

	private static EquippedWeaponDurabilityReading ReadValidatedRecord(ProcessScanner scanner, long regionBase, uint recordOffset, long mirrorAddressA, long mirrorAddressB) {
		long currentAddress = regionBase + ItemRecordCurrentBias + recordOffset;
		uint currentA = scanner.ReadUInt32(currentAddress);
		uint currentB = scanner.ReadUInt32(regionBase + ItemRecordCurrentMirrorBias + recordOffset);
		uint trailingValue = scanner.ReadUInt32(regionBase + ItemRecordMaximumBias + recordOffset);
		uint header = scanner.ReadUInt32(regionBase + ItemRecordHeaderBias + recordOffset);
		uint durabilityMarker = scanner.ReadUInt32(regionBase + ItemRecordDurabilityMarkerBias + recordOffset);
		bool structureMatches = header == ExpectedItemRecordHeader && durabilityMarker == ExpectedDurabilityMarker;
		if (structureMatches && currentA > 0 && currentA == currentB && trailingValue >= currentA && trailingValue <= 1000) return new EquippedWeaponDurabilityReading(true, mirrorAddressA, mirrorAddressB, recordOffset, currentAddress, currentA, trailingValue, false, false, "");
		if (structureMatches && currentA == 0 && currentB == 0 && trailingValue > 0 && trailingValue <= 1000) {
			long directCurrentAddress = regionBase + ItemRecordMaximumBias + recordOffset;
			return new EquippedWeaponDurabilityReading(true, mirrorAddressA, mirrorAddressB, recordOffset, directCurrentAddress, trailingValue, trailingValue, true, false, "");
		}
		return EquippedWeaponDurabilityReading.Fail($"Cấu trúc độ bền không hợp lệ | CurrentA={currentA} CurrentB={currentB} Trailing={trailingValue} Header={header} Marker={durabilityMarker} Record={recordOffset} | SAFE_REJECT.");
	}

	public static string InspectAllDurabilitySources(int processId) {
		StringBuilder text = new();
		text.AppendLine("===== Equipped Durability Full Source Audit =====");
		text.AppendLine($"BuildStamp={BuildStamp} | Mode=READ_ONLY | GameMemoryWrite=NO | PID={processId}");
		try {
			using ProcessScanner scanner = new(processId);
			uint supportingOffset = scanner.ReadUInt32(SupportingEquippedRecordOffsetAddress);
			uint secondaryOffset = scanner.ReadUInt32(ConfirmedEquippedRecordOffsetAddress);
			List<long> itemRegionBases = scanner.FindKnownItemRegionBases();
			List<EquippedWeaponDurabilityReading> structuralReadings = FindStructuralReadings(scanner, itemRegionBases);
			RecordOffsetScanResult references = scanner.FindRecordOffsetValues();
			text.AppendLine($"Status=COMPLETED | Supporting={supportingOffset}@0x{SupportingEquippedRecordOffsetAddress:X8} | Secondary={secondaryOffset}@0x{ConfirmedEquippedRecordOffsetAddress:X8} | ItemRegions={itemRegionBases.Count} | StructuralRecords={structuralReadings.Count} | References={references.Values.Count} | ScannedRegions={references.ScannedRegions} | ScannedBytes={references.ScannedBytes} | LimitReached={references.LimitReached}");
			foreach (EquippedWeaponDurabilityReading reading in structuralReadings) {
				List<KeyValuePair<long, uint>> matchingReferences = references.Values.Where(value => value.Value == reading.RecordOffset).OrderBy(value => value.Key).ToList();
				text.AppendLine($"Record={reading.RecordOffset}/0x{reading.RecordOffset:X8} | Index={reading.RecordOffset / ItemRecordStride} | Current={reading.Current} | Layout={(reading.UsesDirectCurrent ? "Direct" : "Paired")} | Field=0x{reading.Address:X8} | ReferenceCount={matchingReferences.Count}");
				foreach (KeyValuePair<long, uint> reference in matchingReferences.Take(80)) {
					MemoryLocation location = scanner.DescribeLocation(reference.Key);
					text.AppendLine($"  Ref=0x{reference.Key:X8} | AllocationBase=0x{location.AllocationBase:X8} | Relative=0x{location.RelativeOffset:X}");
				}
				if (matchingReferences.Count > 80) text.AppendLine($"  RemainingReferences={matchingReferences.Count - 80}");
			}
			if (structuralReadings.Count == 0) text.AppendLine("StructuralRecords=NONE");
		} catch (Exception ex) {
			text.AppendLine($"Status=FAIL | {ex.GetType().Name}: {ex.Message}");
		}
		return text.ToString();
	}

	private static List<EquippedWeaponDurabilityReading> FindStructuralReadings(ProcessScanner scanner, IReadOnlyList<long> itemRegionBases) {
		if (itemRegionBases.Count != 1) return new List<EquippedWeaponDurabilityReading>();
		List<EquippedWeaponDurabilityReading> readings = new();
		long regionBase = itemRegionBases[0];
		for (uint recordOffset = ItemRecordStride; recordOffset / ItemRecordStride <= MaximumEquippedRecordIndex; recordOffset += ItemRecordStride) {
			EquippedWeaponDurabilityReading reading = ReadValidatedRecord(scanner, regionBase, recordOffset, 0, 0);
			if (reading.Success) readings.Add(reading);
		}
		return readings;
	}

	private static string RunEquippedTransition(int processId, string command) {
		try {
			command = command.ToUpperInvariant();
			if (string.Equals(command, "EQUIP_RESET", StringComparison.OrdinalIgnoreCase)) {
				lock (Sync) TransitionSessions.Remove(processId);
				return BuildMessage(processId, "RESET", "Phiên nhận diện vũ khí đang trang bị đã được xóa.");
			}
			if (string.Equals(command, "EQUIP_COMPARE", StringComparison.OrdinalIgnoreCase)) return CompareEquippedTransition(processId);
			if (command is not ("EQUIP_A1" or "EQUIP_A2" or "EQUIP_B" or "EQUIP_A3")) return BuildMessage(processId, "FAIL", "Lệnh hợp lệ: EQUIP_RESET, EQUIP_A1, EQUIP_A2, EQUIP_B, EQUIP_A3, EQUIP_COMPARE.");

			EquippedTransitionSession session = GetTransitionSession(processId);
			return command switch {
				"EQUIP_A1" => CaptureEquippedA1(processId, session),
				"EQUIP_A2" => CaptureEquippedA2(processId, session),
				"EQUIP_B" => CaptureEquippedB(processId, session),
				_ => CaptureEquippedA3(processId, session)
			};
		} catch (Exception ex) {
			return BuildMessage(processId, "FAIL", ex.ToString());
		}
	}

	private static string CaptureEquippedA1(int processId, EquippedTransitionSession session) {
		using ProcessScanner scanner = new(processId);
		RecordOffsetScanResult scan = scanner.FindRecordOffsetValues();
		List<long> itemRegionBases = scanner.FindKnownItemRegionBases();
		if (scan.Values.Count == 0) return BuildMessage(processId, "FAIL", "Không tìm thấy giá trị record offset hợp lệ trong private writable memory.");
		Dictionary<long, EquippedTransitionCandidate> candidates = scan.Values.ToDictionary(
			pair => pair.Key,
			pair => new EquippedTransitionCandidate(pair.Key, pair.Value, 0, ReadDurability(scanner, itemRegionBases, pair.Value), 0)
		);
		lock (Sync) {
			session.Stage = EquippedTransitionStage.A1;
			session.Candidates = candidates;
			session.ItemRegionBases = itemRegionBases;
			session.StageReports.Clear();
			session.StageReports.Add(new EquippedTransitionStageReport("EQUIP_A1", scan.Values.Count, candidates.Count, itemRegionBases.Count, scan.ScannedRegions, scan.ScannedBytes, scan.LimitReached));
		}
		return BuildTransitionSample(processId, "A1_SAVED", "EQUIP_A1", candidates.Count, scan, "Giữ nguyên vũ khí A và chạy EQUIP_A2.");
	}

	private static string CaptureEquippedA2(int processId, EquippedTransitionSession session) {
		if (session.Stage != EquippedTransitionStage.A1) return BuildMessage(processId, "FAIL", "Cần chạy EQUIP_RESET rồi EQUIP_A1 trước.");
		using ProcessScanner scanner = new(processId);
		int inputCount = session.Candidates.Count;
		Dictionary<long, EquippedTransitionCandidate> survivors = session.Candidates
			.Where(pair => scanner.ReadUInt32(pair.Key) == pair.Value.AOffset)
			.ToDictionary(pair => pair.Key, pair => pair.Value);
		lock (Sync) {
			session.Stage = EquippedTransitionStage.A2;
			session.Candidates = survivors;
			session.StageReports.Add(new EquippedTransitionStageReport("EQUIP_A2", inputCount, survivors.Count, session.ItemRegionBases.Count, 0, 0, false));
		}
		return BuildTransitionSample(processId, survivors.Count > 0 ? "A2_SAVED" : "NO_CANDIDATE", "EQUIP_A2", survivors.Count, null, "Trang bị vũ khí B rồi chạy EQUIP_B.");
	}

	private static string CaptureEquippedB(int processId, EquippedTransitionSession session) {
		if (session.Stage != EquippedTransitionStage.A2) return BuildMessage(processId, "FAIL", "Cần hoàn tất EQUIP_A1 và EQUIP_A2 trước.");
		using ProcessScanner scanner = new(processId);
		List<long> itemRegionBases = scanner.FindKnownItemRegionBases();
		int inputCount = session.Candidates.Count;
		Dictionary<long, EquippedTransitionCandidate> survivors = new();
		foreach ((long address, EquippedTransitionCandidate candidate) in session.Candidates) {
			uint value = scanner.ReadUInt32(address);
			if (!IsRecordOffset(value) || value == candidate.AOffset) continue;
			survivors[address] = candidate with { BOffset = value, BDurability = ReadDurability(scanner, itemRegionBases, value) };
		}
		lock (Sync) {
			session.Stage = EquippedTransitionStage.B;
			session.Candidates = survivors;
			session.ItemRegionBases = itemRegionBases;
			session.StageReports.Add(new EquippedTransitionStageReport("EQUIP_B", inputCount, survivors.Count, itemRegionBases.Count, 0, 0, false));
		}
		return BuildTransitionSample(processId, survivors.Count > 0 ? "B_SAVED" : "NO_CANDIDATE", "EQUIP_B", survivors.Count, null, "Trang bị lại vũ khí A rồi chạy EQUIP_A3.");
	}

	private static string CaptureEquippedA3(int processId, EquippedTransitionSession session) {
		if (session.Stage != EquippedTransitionStage.B) return BuildMessage(processId, "FAIL", "Cần hoàn tất EQUIP_A1, EQUIP_A2 và EQUIP_B trước.");
		using ProcessScanner scanner = new(processId);
		int inputCount = session.Candidates.Count;
		Dictionary<long, EquippedTransitionCandidate> survivors = session.Candidates
			.Where(pair => scanner.ReadUInt32(pair.Key) == pair.Value.AOffset)
			.ToDictionary(pair => pair.Key, pair => pair.Value);
		lock (Sync) {
			session.Stage = EquippedTransitionStage.A3;
			session.Candidates = survivors;
			session.StageReports.Add(new EquippedTransitionStageReport("EQUIP_A3", inputCount, survivors.Count, session.ItemRegionBases.Count, 0, 0, false));
		}
		return BuildTransitionSample(processId, survivors.Count > 0 ? "A3_SAVED" : "NO_CANDIDATE", "EQUIP_A3", survivors.Count, null, "Chạy EQUIP_COMPARE để xuất kết quả.");
	}

	private static string CompareEquippedTransition(int processId) {
		EquippedTransitionSession? session;
		lock (Sync) TransitionSessions.TryGetValue(processId, out session);
		if (session == null || session.Stage != EquippedTransitionStage.A3) return BuildMessage(processId, "FAIL", "Cần hoàn tất chuỗi EQUIP_A1, EQUIP_A2, EQUIP_B, EQUIP_A3 trước.");
		using ProcessScanner scanner = new(processId);
		StringBuilder sb = CreateHeader(processId);
		sb.AppendLine(session.Candidates.Count > 0 ? "Status = COMPLETED" : "Status = NO_CANDIDATE");
		sb.AppendLine("Sequence = A -> A -> B -> A");
		sb.AppendLine($"ItemRegions = {session.ItemRegionBases.Count} | Survivors = {session.Candidates.Count}");
		sb.AppendLine("----- All Stage Results -----");
		foreach (EquippedTransitionStageReport report in session.StageReports) {
			string scan = report.ScannedRegions > 0 ? $" | ScannedRegions={report.ScannedRegions} | ScannedBytes={report.ScannedBytes:N0} | LimitReached={report.LimitReached}" : "";
			sb.AppendLine($"Sample={report.Sample} | Input={report.InputCount} | Output={report.OutputCount} | Removed={report.InputCount - report.OutputCount} | ItemRegions={report.ItemRegionCount}{scan}");
		}
		EquippedTransitionStageReport? firstEmpty = session.StageReports.FirstOrDefault(report => report.OutputCount == 0);
		if (firstEmpty != null) sb.AppendLine($"FirstEmptyStage = {firstEmpty.Sample}");
		sb.AppendLine("----- Stable Equipped Record Candidates -----");
		foreach (EquippedTransitionCandidate candidate in session.Candidates.Values.Take(MaximumOutputCandidates)) {
			MemoryLocation location = scanner.DescribeLocation(candidate.Address);
			sb.AppendLine($"Address=0x{candidate.Address:X8} | AllocationBase=0x{location.AllocationBase:X8} | Relative=+0x{location.RelativeOffset:X} | AOffset={candidate.AOffset}/Index={candidate.AOffset / ItemRecordStride}/Durability={FormatDurability(candidate.ADurability)} | BOffset={candidate.BOffset}/Index={candidate.BOffset / ItemRecordStride}/Durability={FormatDurability(candidate.BDurability)}");
		}
		if (session.Candidates.Count > MaximumOutputCandidates) sb.AppendLine($"Còn {session.Candidates.Count - MaximumOutputCandidates} candidate không in ra.");
		return sb.ToString();
	}

	private static EquippedWeaponDurabilityReading ReadLearnedCurrent(int processId, ProcessScanner scanner) {
		EquippedTransitionSession? session;
		lock (Sync) TransitionSessions.TryGetValue(processId, out session);
		if (session == null || session.Stage != EquippedTransitionStage.A3 || session.Candidates.Count == 0 || session.ItemRegionBases.Count != 1) return EquippedWeaponDurabilityReading.Fail("Chưa có kết quả EQUIP_A/A/B/A trong phiên hiện tại.");
		List<EquippedWeaponDurabilityReading> readings = new();
		foreach (EquippedTransitionCandidate candidate in session.Candidates.Values) {
			uint recordOffset = scanner.ReadUInt32(candidate.Address);
			if (!IsRecordOffset(recordOffset)) continue;
			long durabilityAddress = session.ItemRegionBases[0] + ItemRecordFieldBias + recordOffset;
			uint current = scanner.ReadUInt32(durabilityAddress);
			uint currentMirror = scanner.ReadUInt32(durabilityAddress + (ItemRecordCurrentMirrorBias - ItemRecordCurrentBias));
			uint maximum = scanner.ReadUInt32(durabilityAddress + (ItemRecordMaximumBias - ItemRecordCurrentBias));
			if (current > 0 && current == currentMirror && maximum > 0 && maximum <= 1000 && current <= maximum) readings.Add(new EquippedWeaponDurabilityReading(true, candidate.Address, 0, recordOffset, durabilityAddress, current, maximum, false, false, ""));
		}
		var groups = readings.GroupBy(value => new { value.RecordOffset, value.Current }).ToList();
		if (groups.Count != 1) return EquippedWeaponDurabilityReading.Fail($"Kết quả A/A/B/A chưa duy nhất | ValidGroups={groups.Count}.");
		List<EquippedWeaponDurabilityReading> matching = groups[0].ToList();
		EquippedWeaponDurabilityReading first = matching[0];
		return first with { MirrorAddressB = matching.Count > 1 ? matching[1].MirrorAddressA : 0 };
	}

	private static string BuildTransitionSample(int processId, string status, string sample, int survivors, RecordOffsetScanResult? scan, string nextStep) {
		StringBuilder sb = CreateHeader(processId);
		sb.AppendLine($"Status = {status}");
		sb.AppendLine($"Sample = {sample} | Survivors={survivors}");
		if (scan != null) sb.AppendLine($"ScannedRegions={scan.ScannedRegions} | ScannedBytes={scan.ScannedBytes:N0} | LimitReached={scan.LimitReached}");
		sb.AppendLine(nextStep);
		return sb.ToString();
	}

	private static uint? ReadDurability(ProcessScanner scanner, IReadOnlyList<long> itemRegionBases, uint recordOffset) {
		if (itemRegionBases.Count != 1) return null;
		uint value = scanner.ReadUInt32(itemRegionBases[0] + ItemRecordFieldBias + recordOffset);
		return value <= 1000 ? value : null;
	}

	private static string FormatDurability(uint? value) {
		return value.HasValue ? value.Value.ToString() : "UNRESOLVED";
	}

	private static bool IsRecordOffset(uint value) {
		return value >= ItemRecordStride && value % ItemRecordStride == 0 && value / ItemRecordStride <= MaximumEquippedRecordIndex;
	}

	private static EquippedTransitionSession GetTransitionSession(int processId) {
		lock (Sync) {
			if (!TransitionSessions.TryGetValue(processId, out EquippedTransitionSession? session)) {
				session = new EquippedTransitionSession();
				TransitionSessions[processId] = session;
			}
			return session;
		}
	}

	private static string InspectEquipped(int processId) {
		try {
			using ProcessScanner scanner = new(processId);
			EquippedWeaponDurabilityReading reading = ReadCurrent(processId);
			StringBuilder sb = CreateHeader(processId);
			sb.AppendLine(reading.Success ? "Status = COMPLETED" : "Status = FAIL");
			long mirrorDistance = reading.MirrorAddressA > 0 && reading.MirrorAddressB > 0 ? Math.Abs(reading.MirrorAddressB - reading.MirrorAddressA) : 0;
			sb.AppendLine($"MirrorA=0x{reading.MirrorAddressA:X8} | MirrorB=0x{reading.MirrorAddressB:X8} | Distance=0x{mirrorDistance:X}");
			sb.AppendLine($"RecordOffset={reading.RecordOffset}/0x{reading.RecordOffset:X8} | RecordStride=0x{ItemRecordStride:X} | RecordIndex={(reading.RecordOffset > 0 ? reading.RecordOffset / ItemRecordStride : 0)}");
			sb.AppendLine($"DurabilityAddress=0x{reading.Address:X8} | Current={reading.Current}");
			sb.AppendLine($"FailureReason={reading.FailureReason}");
			uint confirmedOffset = scanner.ReadUInt32(ConfirmedEquippedRecordOffsetAddress);
			uint supportingOffset = scanner.ReadUInt32(SupportingEquippedRecordOffsetAddress);
			sb.AppendLine("----- Confirmed Address Audit -----");
			sb.AppendLine($"MirrorA@0x{ConfirmedEquippedRecordOffsetAddress:X8}={confirmedOffset}/0x{confirmedOffset:X8} | MirrorB@0x{SupportingEquippedRecordOffsetAddress:X8}={supportingOffset}/0x{supportingOffset:X8} | Match={confirmedOffset == supportingOffset}");
			List<long> itemRegionBases = scanner.FindKnownItemRegionBases();
			sb.AppendLine($"ItemRegions={itemRegionBases.Count} | Bases={string.Join(",", itemRegionBases.Select(value => $"0x{value:X8}"))}");
			sb.AppendLine("----- All Matching Record Mirrors -----");
			List<EquippedMirrorPair> mirrorPairs = scanner.FindEquippedMirrorPairs();
			if (mirrorPairs.Count == 0) sb.AppendLine("Không tìm thấy cặp mirror record offset đồng thuận.");
			foreach (EquippedMirrorPair pair in mirrorPairs.Take(MaximumOutputCandidates)) {
				uint? durability = ReadDurability(scanner, itemRegionBases, pair.RecordOffset);
				bool isLegacyPair = pair.AddressA == SupportingEquippedRecordOffsetAddress && pair.AddressB == ConfirmedEquippedRecordOffsetAddress;
				sb.AppendLine($"MirrorA=0x{pair.AddressA:X8} | MirrorB=0x{pair.AddressB:X8} | Offset={pair.RecordOffset}/0x{pair.RecordOffset:X8} | Index={pair.RecordOffset / ItemRecordStride} | ValueAtDurabilityField={FormatDurability(durability)} | IsLegacyPair={isLegacyPair}");
			}
			if (mirrorPairs.Count > MaximumOutputCandidates) sb.AppendLine($"Còn {mirrorPairs.Count - MaximumOutputCandidates} cặp mirror không in ra.");
			return sb.ToString();
		} catch (Exception ex) {
			return BuildMessage(processId, "FAIL", ex.ToString());
		}
	}

	private static string CaptureBaseline(int processId, string label, uint current, uint maximum, Session session) {
		using ProcessScanner scanner = new(processId);
			ScanResult result = scanner.FindValues(current);
			if (result.Candidates.Count == 0) return BuildMessage(processId, "FAIL", $"Không tìm thấy Current={current} trong vùng private writable.");
		lock (Sync) {
			session.BaselineCurrent = current;
			session.Maximum = maximum;
			session.ControlCaptures = 1;
			session.Candidates = result.Candidates;
			session.Samples.Add(new Sample(label, current, maximum, result.Candidates.Count));
		}
		StringBuilder sb = CreateHeader(processId);
		sb.AppendLine("Status = BASELINE_SAVED");
		sb.AppendLine($"Sample = {label} | Expected={current}/{maximum}");
		sb.AppendLine($"ScannedRegions = {result.ScannedRegions} | ScannedBytes = {result.ScannedBytes:N0} | LimitReached={result.LimitReached}");
		AppendCounts(sb, result.Candidates);
		sb.AppendLine("Giữ nguyên vũ khí đang trang bị và độ bền hiện tại, chạy mẫu A2 tiếp theo.");
		return sb.ToString();
	}

	private static string VerifyCapture(int processId, string label, uint current, uint maximum, Session session) {
		using ProcessScanner scanner = new(processId);
		ScanResult currentScan = scanner.FindValues(current);
		List<Candidate> survivors = IntersectCandidates(session.Candidates, currentScan.Candidates);
		bool isControl = current == session.BaselineCurrent;
		lock (Sync) {
			session.Candidates = survivors;
			if (isControl) session.ControlCaptures++;
			session.Samples.RemoveAll(value => string.Equals(value.Label, label, StringComparison.OrdinalIgnoreCase));
			session.Samples.Add(new Sample(label, current, maximum, survivors.Count));
		}
		StringBuilder sb = CreateHeader(processId);
		sb.AppendLine(survivors.Count == 0 ? "Status = NO_CANDIDATE" : "Status = SAMPLE_SAVED");
		sb.AppendLine($"Sample = {label} | Expected={current}/{maximum} | Control={isControl}");
		sb.AppendLine($"SurvivingCandidates = {survivors.Count}");
		AppendCounts(sb, survivors);
		if (isControl) sb.AppendLine("Control đã xác minh. Giữ vũ khí đang trang bị, đánh giảm đúng 1 độ bền rồi capture mẫu kế tiếp.");
		else sb.AppendLine(survivors.Count == 0 ? "Chưa thấy field độ bền theo mô hình Current/Maximum gần nhau." : "Đã có candidate đổi đúng theo độ bền. Nhập COMPARE để xem địa chỉ và khoảng cách.");
		return sb.ToString();
	}

	private static string Compare(int processId) {
		Session? session;
		lock (Sync) Sessions.TryGetValue(processId, out session);
		if (session == null || session.Samples.Count < 3) return BuildMessage(processId, "FAIL", $"Cần A1, A2 và một mẫu đã giảm độ bền. Hiện có: {session?.Samples.Count ?? 0}.");
		if (session.Samples.Select(value => value.Current).Distinct().Count() < 2) return BuildMessage(processId, "FAIL", "Chưa có mẫu giảm độ bền.");
		StringBuilder sb = CreateHeader(processId);
		sb.AppendLine(session.Candidates.Count == 0 ? "Status = NO_CANDIDATE" : "Status = COMPLETED");
		sb.AppendLine("----- Sample Matrix -----");
		foreach (Sample sample in session.Samples) sb.AppendLine($"Sample={sample.Label} | Expected={sample.Current}/{sample.Maximum} | Survivors={sample.Survivors}");
		sb.AppendLine("----- Stable Equipped Durability Candidates -----");
		if (session.Candidates.Count == 0) {
			sb.AppendLine("Không tìm thấy candidate ổn định.");
			return sb.ToString();
		}
		foreach (Candidate candidate in session.Candidates.Take(MaximumOutputCandidates)) sb.AppendLine($"Width={candidate.Width * 8} | CurrentAddress=0x{candidate.CurrentAddress:X8}");
		if (session.Candidates.Count > MaximumOutputCandidates) sb.AppendLine($"Còn {session.Candidates.Count - MaximumOutputCandidates} candidate không in ra.");
		return sb.ToString();
	}

	private static void AppendCounts(StringBuilder sb, IReadOnlyList<Candidate> candidates) {
		foreach (int width in new[] { 1, 2, 4 }) sb.AppendLine($"Width{width * 8} = {candidates.Count(value => value.Width == width)}");
	}

	private static List<Candidate> IntersectCandidates(IReadOnlyList<Candidate> left, IReadOnlyList<Candidate> right) {
		List<Candidate> result = new();
		int leftIndex = 0;
		int rightIndex = 0;
		while (leftIndex < left.Count && rightIndex < right.Count) {
			int comparison = CompareCandidate(left[leftIndex], right[rightIndex]);
			if (comparison == 0) {
				result.Add(left[leftIndex]);
				leftIndex++;
				rightIndex++;
			} else if (comparison < 0) leftIndex++;
			else rightIndex++;
		}
		return result;
	}

	private static int CompareCandidate(Candidate left, Candidate right) {
		int widthComparison = left.Width.CompareTo(right.Width);
		return widthComparison != 0 ? widthComparison : left.CurrentAddress.CompareTo(right.CurrentAddress);
	}

	private static Session GetSession(int processId) {
		lock (Sync) {
			if (!Sessions.TryGetValue(processId, out Session? session)) {
				session = new Session();
				Sessions[processId] = session;
			}
			return session;
		}
	}

	private static bool TryParseDurability(string label, out uint current, out uint maximum) {
		current = ParseMarkerNumber(label, "CUR");
		maximum = ParseMarkerNumber(label, "MAX");
		return label.Contains("DUR", StringComparison.OrdinalIgnoreCase) && current > 0 && maximum > 0;
	}

	private static uint ParseMarkerNumber(string label, string markerText) {
		int marker = label.IndexOf(markerText, StringComparison.OrdinalIgnoreCase);
		if (marker < 0) return 0;
		int start = marker + markerText.Length;
		int end = start;
		while (end < label.Length && char.IsDigit(label[end])) end++;
		return end > start && uint.TryParse(label[start..end], out uint value) ? value : 0;
	}

	private static StringBuilder CreateHeader(int processId) {
		StringBuilder sb = new();
		sb.AppendLine("===== Equipped Weapon Durability Probe =====");
		sb.AppendLine($"BuildStamp = {BuildStamp}");
		sb.AppendLine($"ProcessId = {processId}");
		return sb;
	}

	private static string BuildMessage(int processId, string status, string message) {
		StringBuilder sb = CreateHeader(processId);
		sb.AppendLine($"Status = {status}");
		sb.AppendLine(message);
		return sb.ToString();
	}

	private sealed class ProcessScanner : IDisposable {
		private readonly Process process;
		private readonly IntPtr handle;

		public ProcessScanner(int processId) {
			process = Process.GetProcessById(processId);
			handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, processId);
			if (handle == IntPtr.Zero) throw new Exception("OpenProcess failed. Win32Error=" + Marshal.GetLastWin32Error());
		}

		public long ProcessStartTimeUtcTicks => process.StartTime.ToUniversalTime().Ticks;
		public long ModuleBase => process.MainModule?.BaseAddress.ToInt64() ?? throw new Exception("Không tìm thấy Game.exe module base.");

		public ScanResult FindValues(uint current) {
			List<Candidate> candidates = new();
			long scannedBytes = 0;
			int scannedRegions = 0;
			bool limitReached = false;
			long address = 0x10000;
			while (address < MaximumAddress && scannedBytes < MaximumScannedBytes && candidates.Count < MaximumCandidates) {
				if (VirtualQueryEx(handle, new IntPtr(address), out MemoryBasicInformation mbi, Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero) {
					address += 0x1000;
					continue;
				}
				long regionBase = mbi.BaseAddress.ToInt64();
				long regionSize = mbi.RegionSize.ToInt64();
				long regionEnd = regionBase + regionSize;
				if (IsWritablePrivate(mbi) && regionSize > 0) {
					scannedRegions++;
					ScanRegion(regionBase, regionSize, current, candidates, ref scannedBytes);
				}
				address = regionEnd > address ? regionEnd : address + 0x1000;
			}
			if (scannedBytes >= MaximumScannedBytes || candidates.Count >= MaximumCandidates) limitReached = true;
			candidates.Sort(CompareCandidate);
			return new ScanResult(candidates, scannedRegions, scannedBytes, limitReached);
		}

		public uint ReadUInt32(long address) {
			byte[] bytes = Read(address, 4);
			return bytes.Length == 4 ? BitConverter.ToUInt32(bytes, 0) : 0;
		}

		public byte ReadByte(long address) {
			byte[] bytes = Read(address, 1);
			return bytes.Length == 1 ? bytes[0] : (byte)0;
		}

		public List<RegionDurabilityCandidate> FindRegionDurabilityCandidates(uint recordOffset) {
			List<RegionDurabilityCandidate> candidates = new();
			long address = 0x10000;
			while (address < MaximumAddress) {
				if (VirtualQueryEx(handle, new IntPtr(address), out MemoryBasicInformation mbi, Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero) {
					address += 0x1000;
					continue;
				}
				long regionBase = mbi.BaseAddress.ToInt64();
				long regionSize = mbi.RegionSize.ToInt64();
				long regionEnd = regionBase + regionSize;
				long fieldOffset = ItemRecordFieldBias + recordOffset;
				if (IsWritablePrivate(mbi) && fieldOffset >= 0 && fieldOffset + 4 <= regionSize) {
					long durabilityAddress = regionBase + fieldOffset;
					uint value = ReadUInt32(durabilityAddress);
					if (value <= 1000) candidates.Add(new RegionDurabilityCandidate(regionBase, regionSize, durabilityAddress, value));
				}
				address = regionEnd > address ? regionEnd : address + 0x1000;
			}
			return candidates.OrderByDescending(value => value.RegionSize == KnownItemRegionSize).ThenBy(value => value.RegionBase).ToList();
		}

		public List<EquippedMirrorPair> FindEquippedMirrorPairs() {
			List<EquippedMirrorPair> pairs = new();
			long address = 0x10000;
			long maximumMirrorAddress = 0x10000000;
			while (address < maximumMirrorAddress) {
				if (VirtualQueryEx(handle, new IntPtr(address), out MemoryBasicInformation mbi, Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero) {
					address += 0x1000;
					continue;
				}
				long regionBase = mbi.BaseAddress.ToInt64();
				long regionSize = mbi.RegionSize.ToInt64();
				long regionEnd = regionBase + regionSize;
				if (IsWritablePrivate(mbi) && regionSize > 0) {
					byte[] bytes = Read(regionBase, (int)Math.Min(regionSize, int.MaxValue));
					for (int offset = 0; offset + 4 <= bytes.Length; offset += 4) {
						uint value = BitConverter.ToUInt32(bytes, offset);
						if (value < ItemRecordStride || value % ItemRecordStride != 0 || value / ItemRecordStride > MaximumEquippedRecordIndex) continue;
						long mirrorA = regionBase + offset;
						long mirrorB = mirrorA + EquippedMirrorDistance;
						if (mirrorB + 4 > regionEnd || ReadUInt32(mirrorB) != value) continue;
						pairs.Add(new EquippedMirrorPair(mirrorA, mirrorB, value));
					}
				}
				address = regionEnd > address ? regionEnd : address + 0x1000;
			}
			return pairs.GroupBy(value => new { value.AddressA, value.RecordOffset }).Select(group => group.First()).ToList();
		}

		public List<long> FindKnownItemRegionBases() {
			List<long> bases = new();
			long address = 0x10000;
			while (address < MaximumAddress) {
				if (VirtualQueryEx(handle, new IntPtr(address), out MemoryBasicInformation mbi, Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero) {
					address += 0x1000;
					continue;
				}
				long regionBase = mbi.BaseAddress.ToInt64();
				long regionSize = mbi.RegionSize.ToInt64();
				long regionEnd = regionBase + regionSize;
				if (IsWritablePrivate(mbi) && regionSize == KnownItemRegionSize) bases.Add(regionBase);
				address = regionEnd > address ? regionEnd : address + 0x1000;
			}
			return bases;
		}

		public RecordOffsetScanResult FindRecordOffsetValues() {
			Dictionary<long, uint> values = new();
			long scannedBytes = 0;
			int scannedRegions = 0;
			long address = 0x10000;
			while (address < MaximumAddress && scannedBytes < MaximumScannedBytes && values.Count < MaximumCandidates) {
				if (VirtualQueryEx(handle, new IntPtr(address), out MemoryBasicInformation mbi, Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero) {
					address += 0x1000;
					continue;
				}
				long regionBase = mbi.BaseAddress.ToInt64();
				long regionSize = mbi.RegionSize.ToInt64();
				long regionEnd = regionBase + regionSize;
				if (IsWritablePrivate(mbi) && regionSize > 0) {
					scannedRegions++;
					long offset = 0;
					while (offset < regionSize && scannedBytes < MaximumScannedBytes && values.Count < MaximumCandidates) {
						int requested = (int)Math.Min(ChunkSize, regionSize - offset);
						byte[] bytes = Read(regionBase + offset, requested);
						long chunkBase = regionBase + offset;
						for (int byteOffset = 0; byteOffset + 4 <= bytes.Length; byteOffset += 4) {
							uint value = BitConverter.ToUInt32(bytes, byteOffset);
							if (IsRecordOffset(value)) values[chunkBase + byteOffset] = value;
						}
						scannedBytes += bytes.Length;
						offset += Math.Max(1, requested);
					}
				}
				address = regionEnd > address ? regionEnd : address + 0x1000;
			}
			return new RecordOffsetScanResult(values, scannedRegions, scannedBytes, scannedBytes >= MaximumScannedBytes || values.Count >= MaximumCandidates);
		}

		public List<EquipmentSequenceMatch> FindEquipmentSequences() {
			uint[][] patterns = {
				[72960, 79040, 97280, 85120, 91200, 6080],
				[72960, 79040, 97280, 85120, 91200, 158080],
				[12, 13, 16, 14, 15, 1],
				[12, 13, 16, 14, 15, 26]
			};
			string[] kinds = { "Offsets-B", "Offsets-A", "Indices-B", "Indices-A" };
			List<EquipmentSequenceMatch> matches = new();
			long address = 0x10000;
			while (address < MaximumAddress) {
				if (VirtualQueryEx(handle, new IntPtr(address), out MemoryBasicInformation mbi, Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero) {
					address += 0x1000;
					continue;
				}
				long regionBase = mbi.BaseAddress.ToInt64();
				long regionSize = mbi.RegionSize.ToInt64();
				long regionEnd = regionBase + regionSize;
				if (IsWritablePrivate(mbi) && regionSize > 0 && regionSize <= int.MaxValue) {
					byte[] bytes = Read(regionBase, (int)regionSize);
					for (int offset = 0; offset + 24 <= bytes.Length; offset += 4) {
						for (int patternIndex = 0; patternIndex < patterns.Length; patternIndex++) {
							uint[] pattern = patterns[patternIndex];
							if (BitConverter.ToUInt32(bytes, offset) != pattern[0]) continue;
							for (int stride = 4; stride <= 0x40; stride += 4) {
								if (offset + stride * (pattern.Length - 1) + 4 > bytes.Length) break;
								bool match = true;
								for (int valueIndex = 1; valueIndex < pattern.Length; valueIndex++) {
									if (BitConverter.ToUInt32(bytes, offset + stride * valueIndex) == pattern[valueIndex]) continue;
									match = false;
									break;
								}
								if (match) matches.Add(new EquipmentSequenceMatch(regionBase + offset, stride, kinds[patternIndex], pattern));
							}
						}
					}
				}
				address = regionEnd > address ? regionEnd : address + 0x1000;
			}
			return matches.GroupBy(value => new { value.Address, value.Stride, value.Kind }).Select(group => group.First()).OrderBy(value => value.Address).ToList();
		}

		public List<EquipmentSequenceMatch> FindEquipmentSlotArrays() {
			uint[] prefix = { 12, 13, 16, 14, 15 };
			List<EquipmentSequenceMatch> matches = new();
			long address = 0x10000;
			while (address < MaximumAddress) {
				if (VirtualQueryEx(handle, new IntPtr(address), out MemoryBasicInformation mbi, Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero) {
					address += 0x1000;
					continue;
				}
				long regionBase = mbi.BaseAddress.ToInt64();
				long regionSize = mbi.RegionSize.ToInt64();
				long regionEnd = regionBase + regionSize;
				if (IsWritablePrivate(mbi) && regionSize > 0 && regionSize <= int.MaxValue) {
					byte[] bytes = Read(regionBase, (int)regionSize);
					for (int offset = 0; offset + 44 <= bytes.Length; offset += 4) {
						bool match = true;
						for (int index = 0; index < prefix.Length; index++) {
							if (BitConverter.ToUInt32(bytes, offset + index * 8) == prefix[index]) continue;
							match = false;
							break;
						}
						if (!match) continue;
						uint weaponIndex = BitConverter.ToUInt32(bytes, offset + 40);
						if (weaponIndex > 0 && weaponIndex <= MaximumEquippedRecordIndex) matches.Add(new EquipmentSequenceMatch(regionBase + offset, 8, "Indices", prefix.Append(weaponIndex).ToArray()));
					}
				}
				address = regionEnd > address ? regionEnd : address + 0x1000;
			}
			return matches.GroupBy(value => value.Address).Select(group => group.First()).OrderBy(value => value.Address).ToList();
		}

		public List<long> FindPointerReferences(uint targetAddress) {
			List<long> references = new();
			long address = 0x10000;
			while (address < MaximumAddress) {
				if (VirtualQueryEx(handle, new IntPtr(address), out MemoryBasicInformation mbi, Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero) {
					address += 0x1000;
					continue;
				}
				long regionBase = mbi.BaseAddress.ToInt64();
				long regionSize = mbi.RegionSize.ToInt64();
				long regionEnd = regionBase + regionSize;
				if (IsWritablePrivate(mbi) && regionSize > 0 && regionSize <= int.MaxValue) {
					byte[] bytes = Read(regionBase, (int)regionSize);
					for (int offset = 0; offset + 4 <= bytes.Length; offset += 4) {
						if (BitConverter.ToUInt32(bytes, offset) == targetAddress) references.Add(regionBase + offset);
					}
				}
				address = regionEnd > address ? regionEnd : address + 0x1000;
			}
			return references;
		}

		public bool IsEquipmentSlotArray(long address, int stride) {
			if (stride != 8) return false;
			uint[] expected = { 12, 13, 16, 14, 15 };
			for (int index = 0; index < expected.Length; index++) {
				if (ReadUInt32(address + index * stride) != expected[index]) return false;
			}
			uint weaponIndex = ReadUInt32(address + 5 * stride);
			return weaponIndex > 0 && weaponIndex <= MaximumEquippedRecordIndex;
		}

		public MemoryLocation DescribeLocation(long address) {
			if (VirtualQueryEx(handle, new IntPtr(address), out MemoryBasicInformation mbi, Marshal.SizeOf<MemoryBasicInformation>()) == IntPtr.Zero) return new MemoryLocation(0, 0);
			long allocationBase = mbi.AllocationBase.ToInt64();
			return new MemoryLocation(allocationBase, address - allocationBase);
		}

		private void ScanRegion(long regionBase, long regionSize, uint current, List<Candidate> candidates, ref long scannedBytes) {
			long offset = 0;
			while (offset < regionSize && scannedBytes < MaximumScannedBytes && candidates.Count < MaximumCandidates) {
				int requested = (int)Math.Min(ChunkSize, regionSize - offset);
				byte[] bytes = Read(regionBase + offset, requested);
				if (bytes.Length > 0) {
					long chunkBase = regionBase + offset;
					foreach (int width in new[] { 4, 2, 1 }) {
						FindValuesInChunk(chunkBase, bytes, current, width, candidates);
						if (candidates.Count >= MaximumCandidates) break;
					}
					scannedBytes += bytes.Length;
				}
				offset += Math.Max(1, requested);
			}
		}

		private static void FindValuesInChunk(long chunkBase, byte[] bytes, uint value, int width, List<Candidate> candidates) {
			byte[] pattern = width switch {
				1 => new[] { (byte)value },
				2 => BitConverter.GetBytes((ushort)value),
				_ => BitConverter.GetBytes(value)
			};
			int searchOffset = 0;
			while (searchOffset <= bytes.Length - pattern.Length) {
				int relative = bytes.AsSpan(searchOffset).IndexOf(pattern);
				if (relative < 0) break;
				int offset = searchOffset + relative;
				if ((chunkBase + offset) % width == 0) candidates.Add(new Candidate(chunkBase + offset, width));
				if (candidates.Count >= MaximumCandidates) break;
				searchOffset = offset + 1;
			}
		}

		private byte[] Read(long address, int size) {
			if (address < 0x10000 || address > MaximumAddress - size) return Array.Empty<byte>();
			byte[] buffer = new byte[size];
			bool success = ReadProcessMemory(handle, new IntPtr(address), buffer, size, out IntPtr bytesRead);
			int length = (int)Math.Max(0, bytesRead.ToInt64());
			if (!success && length == 0) return Array.Empty<byte>();
			if (length == size) return buffer;
			Array.Resize(ref buffer, length);
			return buffer;
		}

		public void Dispose() {
			if (handle != IntPtr.Zero) CloseHandle(handle);
			process.Dispose();
		}
	}

	private static bool IsWritablePrivate(MemoryBasicInformation mbi) {
		if (mbi.State != MemCommit || mbi.Type != MemPrivate || (mbi.Protect & PageGuard) != 0 || (mbi.Protect & PageNoAccess) != 0) return false;
		int access = mbi.Protect & 0xFF;
		return access is PageReadWrite or PageWriteCopy or PageExecuteReadWrite or PageExecuteWriteCopy;
	}

	private sealed class Session {
		public uint BaselineCurrent { get; set; }
		public uint Maximum { get; set; }
		public int ControlCaptures { get; set; }
		public List<Candidate> Candidates { get; set; } = new();
		public List<Sample> Samples { get; } = new();
	}

	private sealed class EquippedTransitionSession {
		public EquippedTransitionStage Stage { get; set; }
		public Dictionary<long, EquippedTransitionCandidate> Candidates { get; set; } = new();
		public List<long> ItemRegionBases { get; set; } = new();
		public List<EquippedTransitionStageReport> StageReports { get; } = new();
	}

	private enum EquippedTransitionStage {
		None,
		A1,
		A2,
		B,
		A3
	}

	private readonly record struct Candidate(long CurrentAddress, int Width);
	private readonly record struct RegionDurabilityCandidate(long RegionBase, long RegionSize, long Address, uint Value);
	private readonly record struct EquippedMirrorPair(long AddressA, long AddressB, uint RecordOffset);
	private readonly record struct EquippedTransitionCandidate(long Address, uint AOffset, uint BOffset, uint? ADurability, uint? BDurability);
	private sealed record EquippedTransitionStageReport(string Sample, int InputCount, int OutputCount, int ItemRegionCount, int ScannedRegions, long ScannedBytes, bool LimitReached);
	private sealed record ConfirmedRecordCache(long ProcessStartTimeUtcTicks, long ItemRegionBase, uint RecordOffset);
	private sealed record EquipmentSlotArrayCache(long ProcessStartTimeUtcTicks, long Address, int Stride);
	private sealed record EquipmentSequenceMatch(long Address, int Stride, string Kind, IReadOnlyList<uint> Values);
	private readonly record struct MemoryLocation(long AllocationBase, long RelativeOffset);
	private sealed record Sample(string Label, uint Current, uint Maximum, int Survivors);
	private sealed record ScanResult(List<Candidate> Candidates, int ScannedRegions, long ScannedBytes, bool LimitReached);
	private sealed record RecordOffsetScanResult(Dictionary<long, uint> Values, int ScannedRegions, long ScannedBytes, bool LimitReached);

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

public sealed record EquippedWeaponDurabilityReading(bool Success, long MirrorAddressA, long MirrorAddressB, uint RecordOffset, long Address, uint Current, uint Maximum, bool UsesDirectCurrent, bool UsesCachedRecord, string FailureReason, string Evidence = "") {
	public static EquippedWeaponDurabilityReading Fail(string reason) {
		return new EquippedWeaponDurabilityReading(false, 0, 0, 0, 0, 0, 0, false, false, reason, "");
	}
}
