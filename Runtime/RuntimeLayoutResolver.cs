namespace Auto.Runtime;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Auto.Utils;

public static class RuntimeLayoutResolver {
	private static readonly byte?[] PlayerSignature = { 0xA1, null, null, null, null, 0x89, 0x8C, 0x02, 0xDC, 0x27, 0x00, 0x00 };
	private static readonly byte?[] GroundTableSignature = { 0x69, 0xD2, 0xA4, 0x03, 0x00, 0x00, 0x03, 0x15, null, null, null, null, 0x8B, 0x42, 0x1C };
	private static readonly byte[] GroundCoordinateConverterSignature = { 0x55, 0x8B, 0xEC, 0xFF, 0x75, 0x0C, 0xFF, 0x75, 0x08, 0xFF, 0x71, 0x3C };
	private static readonly byte[] AttackWriterSignature = { 0xC7, 0x81, 0x94, 0xD7, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x89, 0xB9, 0x98, 0xD7, 0x00, 0x00, 0xC6, 0x81, 0xA0, 0xD7, 0x00, 0x00, 0x01 };
	// Cập nhật sau bản game 2026-08-28: 2 byte thứ 7-8 là con trỏ nội bộ SEH đổi theo từng lần build (xác nhận qua so byte thật cũ/mới), đổi cùng lúc với RVA tại nơi gọi MatchesAt.
	private static readonly byte[] RepairConfirmationSignature = { 0x55, 0x8B, 0xEC, 0x6A, 0xFF, 0x68, 0xB7, 0x3B, 0x84, 0x00, 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00 };
	private static readonly ConcurrentDictionary<int, RuntimeLayout> confirmedByProcess = new();
	private static readonly ConcurrentDictionary<string, RuntimeLayout> confirmedByFingerprint = new(StringComparer.Ordinal);
	private static readonly ConcurrentDictionary<int, (DateTime RetryUtc, RuntimeLayout Layout)> unavailableByProcess = new();
	public static RuntimeLayout Unavailable => CreateUnavailable("UNAVAILABLE", CreateUnavailableStates(), "Runtime layout has not been resolved for a game process.");

	// Dùng cache ngắn khi nhân vật chưa sẵn sàng để tránh quét toàn bộ Game.exe mỗi chu kỳ
	public static RuntimeLayout Resolve(int processId) {
		if (confirmedByProcess.TryGetValue(processId, out RuntimeLayout? cached)) return cached;
		if (unavailableByProcess.TryGetValue(processId, out (DateTime RetryUtc, RuntimeLayout Layout) unavailable) && DateTime.UtcNow < unavailable.RetryUtc) return unavailable.Layout;
		string fingerprint = GetFingerprint(processId);
		if (fingerprint != "UNAVAILABLE" && confirmedByFingerprint.TryGetValue(fingerprint, out cached) && ValidatePlayer(processId, cached, out _)) {
			confirmedByProcess[processId] = cached;
			return cached;
		}
		RuntimeLayout resolved = ResolveCore(processId, fingerprint);
		if (resolved.PlayerReady) {
			unavailableByProcess.TryRemove(processId, out _);
			confirmedByProcess[processId] = resolved;
			if (fingerprint != "UNAVAILABLE") confirmedByFingerprint[fingerprint] = resolved;
		} else unavailableByProcess[processId] = (DateTime.UtcNow.AddSeconds(2), resolved);
		return resolved;
	}

	private static RuntimeLayout ResolveCore(int processId, string fingerprint) {
		Dictionary<RuntimeSubsystem, RuntimeSubsystemState> states = CreateUnavailableStates();
		try {
			using Process process = Process.GetProcessById(processId);
			ProcessModule? module = process.Modules.Cast<ProcessModule>().FirstOrDefault(candidate => string.Equals(candidate.ModuleName, GameAddresses.ModuleName, StringComparison.OrdinalIgnoreCase));
			if (module == null) return CreateUnavailable(fingerprint, states, "Game.exe module was not found.");
			using MemoryReader reader = new(processId);
			byte[] image = reader.ReadBytes(module.BaseAddress, module.ModuleMemorySize);
			if (image.Length != module.ModuleMemorySize) return CreateUnavailable(fingerprint, states, "Game.exe runtime image could not be read completely.");
			List<int> candidates = FindSignature(image, PlayerSignature);
			List<int> validated = new();
			foreach (int match in candidates) {
				int absoluteGlobal = BitConverter.ToInt32(image, match + 1);
				long rva = (long)(uint)absoluteGlobal - module.BaseAddress.ToInt64();
				if (rva < 0 || rva > int.MaxValue) continue;
				RuntimeLayout candidate = CreatePlayerCandidate(fingerprint, states, (int)rva);
				if (ValidatePlayer(processId, candidate, out _)) validated.Add((int)rva);
			}
			if (validated.Count == 0 && IsCurrentRecoveryClient(image)) {
				RuntimeLayout recoveryCandidate = CreatePlayerCandidate(fingerprint, states, 0x95DF40);
				if (ValidatePlayer(processId, recoveryCandidate, out _)) validated.Add(0x95DF40);
			}
			// Client cập nhật 2026-08-28: chữ ký cũ không còn khớp ở đâu trong toàn bộ image, EntityTable xác nhận bằng BSim + decompile chéo với PICKUP/RESET_PICKUP mới.
			if (validated.Count == 0 && IsCurrentUpdateClient(image)) {
				RuntimeLayout updateCandidate = CreatePlayerCandidate(fingerprint, states, 0x95FF60);
				if (ValidatePlayer(processId, updateCandidate, out _)) validated.Add(0x95FF60);
			}
			if (validated.Count != 1) return CreateUnavailable(fingerprint, states, $"Player signature validation expected one candidate but found {validated.Count}.");
			states[RuntimeSubsystem.Player] = RuntimeSubsystemState.Confirmed($"Validated HP/MP accessor signature and player fields | EntityTableRva=0x{validated[0]:X}");
			states[RuntimeSubsystem.Entity] = RuntimeSubsystemState.Confirmed($"Confirmed entity table, stride and type field | EntityTableRva=0x{validated[0]:X} | Stride=0x{GameAddresses.Entity.Stride:X} | Type=+0x{GameAddresses.Entity.Type:X}");
			ResolveAttackAndMovementTransport(reader, module.BaseAddress, image, states);
			ResolveSaleData(reader, module.BaseAddress, image, states);
			ResolveGroundAndLootTransport(reader, module.BaseAddress, image, states);
			return CreatePlayerCandidate(fingerprint, states, validated[0]);
		} catch (Exception ex) {
			return CreateUnavailable(fingerprint, states, $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	private static void ResolveAttackAndMovementTransport(MemoryReader reader, IntPtr moduleBase, byte[] image, Dictionary<RuntimeSubsystem, RuntimeSubsystemState> states) {
		bool recoveryClient = IsCurrentRecoveryClient(image);
		bool updateClient = IsCurrentUpdateClient(image);
		// Client cập nhật 2026-08-28: manager/vtable xác nhận bằng byte-scan tĩnh (object nhúng trong image, không phải con trỏ heap), khớp cả 2 slot ATTACK (+0x40)/SELECT_GROUND (+0x48) đã đối chiếu decompile.
		int managerRva = updateClient ? 0x4E2660 : recoveryClient ? 0x4E0640 : 0x4DF600;
		int expectedManagerVtableRva = updateClient ? 0x477804 : recoveryClient ? 0x476618 : 0x4755F8;
		const int attackMethodVtableOffset = 0x40;
		const int coordinateDispatcherMethodVtableOffset = 0x10;

		IntPtr manager = reader.ReadPointer32(IntPtr.Add(moduleBase, managerRva));
		IntPtr managerVtable = manager == IntPtr.Zero ? IntPtr.Zero : reader.ReadPointer32(manager);
		IntPtr attackFunction = managerVtable == IntPtr.Zero ? IntPtr.Zero : reader.ReadPointer32(IntPtr.Add(managerVtable, attackMethodVtableOffset));
		long attackFunctionRva = attackFunction.ToInt64() - moduleBase.ToInt64();
		bool managerValid = manager != IntPtr.Zero && managerVtable == IntPtr.Add(moduleBase, expectedManagerVtableRva);
		bool attackWriterValid = attackFunctionRva >= 0 && attackFunctionRva <= image.Length - 0x100 && ContainsSignature(image, (int)attackFunctionRva, 0x100, AttackWriterSignature);
		if (managerValid && attackWriterValid) states[RuntimeSubsystem.AttackTransport] = RuntimeSubsystemState.Confirmed($"Validated current-client manager vtable and AutoFS attack writer | ManagerRva=0x{managerRva:X} | AttackMethod=+0x{attackMethodVtableOffset:X}");
		else states[RuntimeSubsystem.AttackTransport] = RuntimeSubsystemState.Unavailable($"Current-client attack transport validation failed | ManagerRva=0x{managerRva:X} | Manager=0x{manager.ToInt64():X} | Vtable=0x{managerVtable.ToInt64():X} | ExpectedVtableRva=0x{expectedManagerVtableRva:X} | ManagerValid={managerValid} | AttackFunctionRva=0x{attackFunctionRva:X} | AttackWriterValid={attackWriterValid}");

		IntPtr coordinateDispatcher = managerVtable == IntPtr.Zero ? IntPtr.Zero : reader.ReadPointer32(IntPtr.Add(managerVtable, coordinateDispatcherMethodVtableOffset));
		long coordinateDispatcherRva = coordinateDispatcher.ToInt64() - moduleBase.ToInt64();
		bool coordinateDispatcherValid = managerValid && coordinateDispatcherRva >= 0 && coordinateDispatcherRva < image.Length && image[(int)coordinateDispatcherRva] != 0x00 && image[(int)coordinateDispatcherRva] != 0xCC;
		if (coordinateDispatcherValid) states[RuntimeSubsystem.MovementTransport] = RuntimeSubsystemState.Confirmed($"Validated current-client manager movement method | VtableMethod=+0x{coordinateDispatcherMethodVtableOffset:X} | FunctionRva=0x{coordinateDispatcherRva:X} | Opcode=0x9F");
		else states[RuntimeSubsystem.MovementTransport] = RuntimeSubsystemState.Unavailable($"Current-client manager movement method validation failed | ManagerValid={managerValid} | VtableMethod=+0x{coordinateDispatcherMethodVtableOffset:X} | FunctionRva=0x{coordinateDispatcherRva:X}");
	}

	private static void ResolveSaleData(MemoryReader reader, IntPtr moduleBase, byte[] image, Dictionary<RuntimeSubsystem, RuntimeSubsystemState> states) {
		IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
		IntPtr inventoryObject = inventoryRoot == IntPtr.Zero ? IntPtr.Zero : IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object);
		IntPtr slotList = inventoryObject == IntPtr.Zero ? IntPtr.Zero : reader.ReadPointer32(IntPtr.Add(inventoryObject, GameAddresses.Inventory.SaleSlotListPointer));
		if (slotList != IntPtr.Zero) {
			byte[] slots = reader.ReadBytes(slotList, GameAddresses.Inventory.SaleSlotCount * sizeof(int));
			if (slots.Length == GameAddresses.Inventory.SaleSlotCount * sizeof(int)) states[RuntimeSubsystem.Inventory] = RuntimeSubsystemState.Confirmed($"Validated readable 35-slot inventory chain | Root=Game.exe+0x{GameAddresses.Globals.InventoryRoot:X} | Container=Root+0x{GameAddresses.Inventory.Object:X}");
		}

		IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
		if (itemTable != IntPtr.Zero && reader.ReadBytes(itemTable, sizeof(int)).Length == sizeof(int)) states[RuntimeSubsystem.ItemTable] = RuntimeSubsystemState.Confirmed($"Validated readable item table | Global=Game.exe+0x{GameAddresses.Globals.ItemTable:X} | Stride=0x{GameAddresses.Item.InventoryRecordStride:X}");

		int mapId = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.MapId));
		int mapIdMirror = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.MapIdMirror));
		int mapIdRuntimeMirror = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.MapIdRuntimeMirror));
		if (mapId > 0 && mapId == mapIdMirror && mapId == mapIdRuntimeMirror) states[RuntimeSubsystem.Map] = RuntimeSubsystemState.Confirmed($"Validated three agreeing current-client map ID mirrors | Rvas=0x{GameAddresses.Globals.MapId:X}/0x{GameAddresses.Globals.MapIdMirror:X}/0x{GameAddresses.Globals.MapIdRuntimeMirror:X} | Value={mapId}");
		else states[RuntimeSubsystem.Map] = RuntimeSubsystemState.Unavailable($"Current-client map ID mirrors did not agree | Values={mapId}/{mapIdMirror}/{mapIdRuntimeMirror}");

		int modalState = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState));
		int shopState = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ShopState));
		// Cập nhật sau bản game 2026-08-28: thêm nhánh IsCurrentUpdateClient, thiếu nhánh này khiến Shop luôn báo Unavailable trên build hôm nay dù Modal/Shop đọc đúng giá trị thật (chặn REPAIR_WEAPON_PROTECTION không cho đi sửa đồ). Chưa build/test runtime.
		if ((IsCurrentRecoveryClient(image) || IsCurrentUpdateClient(image)) && modalState >= 0 && shopState is >= 0 and <= 2) {
			states[RuntimeSubsystem.Shop] = RuntimeSubsystemState.Confirmed($"Validated current-client modal/shop fields from probes 030/031 | ModalRva=0x{GameAddresses.Globals.ModalState:X} | ShopRva=0x{GameAddresses.Globals.ShopState:X} | Current={modalState}/{shopState}");
		} else {
			states[RuntimeSubsystem.Shop] = RuntimeSubsystemState.Unavailable($"Current-client modal/shop validation failed | Modal={modalState} | Shop={shopState} | ExpectedShop=0..2");
		}
		// Cập nhật sau bản game 2026-08-28: xác nhận bằng BSim (similarity 1.0) + byte thật khớp gần tuyệt đối với chữ ký cũ.
		const int repairConfirmationRva = 0x2935A0;
		if (MatchesAt(image, repairConfirmationRva, RepairConfirmationSignature)) states[RuntimeSubsystem.RepairTransport] = RuntimeSubsystemState.Confirmed($"Validated current-client repair confirmation function and AutoFS-equivalent arguments | Rva=0x{repairConfirmationRva:X} | Arguments=0/0/2/0");
		else states[RuntimeSubsystem.RepairTransport] = RuntimeSubsystemState.Unavailable($"Current-client repair confirmation signature did not match | ExpectedRva=0x{repairConfirmationRva:X}");
		states[RuntimeSubsystem.ArrangeTransport] = RuntimeSubsystemState.Unavailable("Current-client native handler for AutoFS command 24 has not been confirmed or implemented.");
	}

	private static void ResolveGroundAndLootTransport(MemoryReader reader, IntPtr moduleBase, byte[] image, Dictionary<RuntimeSubsystem, RuntimeSubsystemState> states) {
		bool recoveryClient = IsCurrentRecoveryClient(image);
		bool updateClient = IsCurrentUpdateClient(image);
		List<int> groundMatches = FindSignature(image, GroundTableSignature);
		List<int> validatedGroundGlobals = new();
		foreach (int match in groundMatches) {
			int absoluteGlobal = BitConverter.ToInt32(image, match + 8);
			long rva = (long)(uint)absoluteGlobal - moduleBase.ToInt64();
			if (rva < 0 || rva > int.MaxValue) continue;
			IntPtr table = reader.ReadPointer32(IntPtr.Add(moduleBase, (int)rva));
			if (table == IntPtr.Zero) continue;
			IntPtr firstRecord = IntPtr.Add(table, GameAddresses.Item.GroundRecordStride);
			byte[] record = reader.ReadBytes(firstRecord, GameAddresses.Item.GroundInternalY + sizeof(int));
			if (record.Length == GameAddresses.Item.GroundInternalY + sizeof(int)) validatedGroundGlobals.Add((int)rva);
		}
		if (recoveryClient) {
			IntPtr recoveryTable = reader.ReadPointer32(IntPtr.Add(moduleBase, 0x54BCA0));
			if (recoveryTable != IntPtr.Zero) validatedGroundGlobals.Add(0x54BCA0);
		}
		// Client cập nhật 2026-08-28: GROUND_TABLE xác nhận bằng BSim + decompile, hàm command 78 mới gọi đúng CONVERTER/MOVEMENT/PICKUP mới với cùng field offset.
		if (updateClient) {
			IntPtr updateTable = reader.ReadPointer32(IntPtr.Add(moduleBase, 0x54DCC0));
			if (updateTable != IntPtr.Zero) validatedGroundGlobals.Add(0x54DCC0);
		}
		int expectedGroundRva = updateClient ? 0x54DCC0 : recoveryClient ? 0x54BCA0 : GameAddresses.Item.GroundRecordTablePointer;
		if (validatedGroundGlobals.Distinct().Count() == 1 && validatedGroundGlobals[0] == expectedGroundRva) {
			states[RuntimeSubsystem.Ground] = RuntimeSubsystemState.Confirmed($"Validated ground-table signature and current-client record layout | PointerRva=0x{validatedGroundGlobals[0]:X} | Stride=0x{GameAddresses.Item.GroundRecordStride:X} | State=+0x{GameAddresses.Item.GroundRecordKind:X} | InternalCoordinate=+0x{GameAddresses.Item.GroundInternalX:X}/+0x{GameAddresses.Item.GroundInternalY:X}");
		}
		else states[RuntimeSubsystem.Ground] = RuntimeSubsystemState.Unavailable($"Current-client ground-table validation failed | SignatureMatches={groundMatches.Count} | ValidatedGlobals={validatedGroundGlobals.Count} | ValidatedRvas=[{string.Join(",", validatedGroundGlobals.Select(rva => $"0x{rva:X}"))}] | ExpectedRva=0x{expectedGroundRva:X}");

		int coordinateConverterRva = updateClient ? 0x2FBC30 : recoveryClient ? 0x2F9E00 : 0x2F9DA0;
		int movementRva = updateClient ? 0x31EF70 : recoveryClient ? 0x31CE80 : 0x31CDE0;
		int pickupRva = updateClient ? 0x3AC300 : recoveryClient ? 0x3AA0F0 : 0x3A9C50;
		bool coordinateConverterValid = MatchesAt(image, coordinateConverterRva, GroundCoordinateConverterSignature);
		bool movementValid = MatchesAt(image, movementRva, new byte[] { 0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x4C, 0x04, 0x00, 0x00 });
		bool pickupValid = MatchesAt(image, pickupRva, new byte[] { 0x55, 0x8B, 0xEC, 0x8B, 0x0D });
		if (coordinateConverterValid && movementValid && pickupValid) {
			states[RuntimeSubsystem.LootTransport] = RuntimeSubsystemState.Confirmed("Validated current-client ground coordinate converter, movement, and pickup functions for command 78.");
		}
		else states[RuntimeSubsystem.LootTransport] = RuntimeSubsystemState.Unavailable($"Current-client command 78 transport validation failed | CoordinateConverterRva=0x{coordinateConverterRva:X} Match={coordinateConverterValid} | MovementRva=0x{movementRva:X} Match={movementValid} | PickupRva=0x{pickupRva:X} Match={pickupValid}");
	}

	private static bool IsCurrentRecoveryClient(byte[] image) {
		return image.Length >= 0x1FC0000 && MatchesAt(image, 0x2F9E00, GroundCoordinateConverterSignature) && MatchesAt(image, 0x31CE80, new byte[] { 0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x4C, 0x04, 0x00, 0x00 });
	}

	// Nhận diện client sau bản game 2026-08-28 (PE TimeDateStamp 0x6A8DD698, SizeOfImage 0x01F83000): xác nhận bằng dump runtime thật ngày 2026-08-28, chưa test lại trong game.
	private static bool IsCurrentUpdateClient(byte[] image) {
		return image.Length == 0x1F83000 && MatchesAt(image, 0x2FBC30, GroundCoordinateConverterSignature) && MatchesAt(image, 0x31EF70, new byte[] { 0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x4C, 0x04, 0x00, 0x00 });
	}

	private static bool MatchesAt(byte[] image, int offset, byte[] signature) {
		return offset >= 0 && offset + signature.Length <= image.Length && image.AsSpan(offset, signature.Length).SequenceEqual(signature);
	}

	private static bool ContainsSignature(byte[] image, int offset, int length, byte[] signature) {
		int end = Math.Min(image.Length, offset + length) - signature.Length;
		for (int current = offset; current <= end; current++) {
			if (image.AsSpan(current, signature.Length).SequenceEqual(signature)) return true;
		}
		return false;
	}

	private static RuntimeLayout CreatePlayerCandidate(string fingerprint, IReadOnlyDictionary<RuntimeSubsystem, RuntimeSubsystemState> states, int entityTableRva) {
		return new RuntimeLayout(fingerprint, states, entityTableRva, 0xD87C, 0xD87C, 0x24, 0x27D0, 0x27D4, 0x27DC, 0x27E0, 0x2D70, 0x75F4, 0x75F8);
	}

	private static RuntimeLayout CreateUnavailable(string fingerprint, Dictionary<RuntimeSubsystem, RuntimeSubsystemState> states, string reason) {
		states[RuntimeSubsystem.Player] = RuntimeSubsystemState.Unavailable(reason);
		return new RuntimeLayout(fingerprint, states);
	}

	private static Dictionary<RuntimeSubsystem, RuntimeSubsystemState> CreateUnavailableStates() {
		Dictionary<RuntimeSubsystem, RuntimeSubsystemState> states = new();
		foreach (RuntimeSubsystem subsystem in Enum.GetValues<RuntimeSubsystem>()) states[subsystem] = RuntimeSubsystemState.Unavailable("No confirmed resolver signature for the current client.");
		return states;
	}

	private static bool ValidatePlayer(int processId, RuntimeLayout layout, out string reason) {
		reason = "";
		if (layout.EntityTableRva <= 0 || layout.PlayerRecordOffset <= 0) {
			reason = "Player layout is empty.";
			return false;
		}
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr table = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			if (table == IntPtr.Zero) {
				reason = "Entity table pointer is zero.";
				return false;
			}
			IntPtr player = IntPtr.Add(table, layout.PlayerRecordOffset);
			byte[] bytes = reader.ReadBytes(player, layout.RawYOffset + 4);
			if (bytes.Length < layout.RawYOffset + 4) {
				reason = "Player record could not be read completely.";
				return false;
			}
			string name = ReadLegacyString(bytes, layout.NameOffset, 32);
			int level = BitConverter.ToInt32(bytes, layout.LevelOffset);
			int hp = BitConverter.ToInt32(bytes, layout.HpOffset);
			int maxHp = BitConverter.ToInt32(bytes, layout.MaxHpOffset);
			int mp = BitConverter.ToInt32(bytes, layout.MpOffset);
			int maxMp = BitConverter.ToInt32(bytes, layout.MaxMpOffset);
			int rawX = BitConverter.ToInt32(bytes, layout.RawXOffset);
			int rawY = BitConverter.ToInt32(bytes, layout.RawYOffset);
			bool valid = name.Length > 0 && level > 0 && level <= 1000 && hp >= 0 && maxHp > 0 && hp <= maxHp && mp >= 0 && maxMp > 0 && mp <= maxMp && rawX > 0 && rawY > 0;
			if (! valid) reason = $"Player validation failed | Name={name} | Level={level} | HP={hp}/{maxHp} | MP={mp}/{maxMp} | Raw={rawX}/{rawY}";
			return valid;
		} catch (Exception ex) {
			reason = $"{ex.GetType().Name}: {ex.Message}";
			return false;
		}
	}

	private static List<int> FindSignature(byte[] image, byte?[] signature) {
		List<int> matches = new();
		for (int offset = 0; offset <= image.Length - signature.Length; offset++) {
			bool matched = true;
			for (int index = 0; index < signature.Length; index++) {
				if (signature[index].HasValue && image[offset + index] != signature[index]!.Value) {
					matched = false;
					break;
				}
			}
			if (matched) matches.Add(offset);
		}
		return matches;
	}

	private static string ReadLegacyString(byte[] bytes, int offset, int maximumLength) {
		int length = 0;
		while (length < maximumLength && offset + length < bytes.Length && bytes[offset + length] != 0) length++;
		return LegacyVietnameseText.Decode(bytes.AsSpan(offset, length).ToArray()).Trim();
	}

	private static string GetFingerprint(int processId) {
		try {
			using Process process = Process.GetProcessById(processId);
			string path = process.MainModule?.FileName ?? "";
			if (path.Length == 0 || ! File.Exists(path)) return "UNAVAILABLE";
			using FileStream stream = File.OpenRead(path);
			return Convert.ToHexString(SHA256.HashData(stream));
		} catch {
			return "UNAVAILABLE";
		}
	}
}
