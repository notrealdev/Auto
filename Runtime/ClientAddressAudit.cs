namespace Auto.Runtime;

using Auto.Attack;
using Auto.Utils;

// Kiểm tra một lượt toàn bộ địa chỉ client mà DEV auto phụ thuộc, rồi báo cáo trong một khối log duy nhất.
//
// Lý do: sau bản game 2026-08-28, mỗi tính năng chỉ lộ ra hỏng khi đúng tình huống của nó xảy ra (phải HP thấp mới
// biết Buff Chủ hỏng, phải chết mới biết Về thành hỏng), nên một bản cập nhật client biến thành nhiều lượt báo lỗi rời rạc.
// Bộ kiểm tra này chạy một lần cho mỗi tiến trình game và liệt kê ngay mọi hằng số đang lệch.
//
// Hai nguồn kiểm tra tách riêng vì chữ ký byte chỉ tồn tại trong SystemUint.cpp:
// - Phần native: gửi lệnh 320 để chính DLL so chữ ký và vtable bằng bảng hằng số của nó, không nhân bản chữ ký sang C#.
// - Phần managed: đọc bộ nhớ tiến trình để kiểm các hằng số trong GameAddresses.Globals mà native không dùng tới.
//
// Chỉ đọc bộ nhớ và so sánh; không gọi hàm nào của game và không gửi lệnh hành động nào.
public static class ClientAddressAudit {
	// Thứ tự phải khớp tuyệt đối với switch trong AuditAddress() của Native\SystemUint\SystemUint.cpp.
	private static readonly (string Name, string Constants)[] NativeEntries = [
		("ATTACK_MANAGER", "AttackManagerRva+ExpectedManagerVtableRva"),
		("MANAGER_VTABLE_SLOT_10", "DialogOptionMethodVtableOffset"),
		("MANAGER_VTABLE_SLOT_40", "AttackMethodVtableOffset"),
		("MANAGER_VTABLE_SLOT_48", "SelectGroundItemMethodVtableOffset"),
		("MANAGER_VTABLE_SLOT_74", "PrepareMethodVtableOffset"),
		("ENTITY_TABLE", "EntityTableRva"),
		("GROUND_RECORD_TABLE", "GroundRecordTablePointerRva"),
		("INVENTORY_ROOT", "InventoryRootRva"),
		("MODAL_STATE", "ModalStateRva"),
		("NPC_CONFIRM_MODAL_VTABLE", "NpcConfirmModalVtableRva"),
		("REPAIR_CONFIRM_MODAL_VTABLE", "RepairConfirmModalVtableRva"),
		("RETURN_TO_TOWN_OBJECT", "ReturnToTownObjectRva+ReturnToTownObjectVtableRva"),
		("RETURN_TO_TOWN_FUNCTION", "ReturnToTownFunctionRva"),
		("RESET_PICKUP_FUNCTION", "ResetPickupFunctionRva"),
		("GROUND_COORDINATE_CONVERTER", "GroundCoordinateConverterRva"),
		("PICKUP_MOVEMENT_FUNCTION", "PickupMovementFunctionRva"),
		("BUFF_ACTION_FUNCTION", "BuffActionFunctionRva"),
		("PICKUP_FUNCTION", "PickupFunctionRva"),
		("INVENTORY_COORDINATE_FUNCTION", "InventoryCoordinateFunctionRva"),
		("CAST_SKILL_SEND_SITE", "CastSkillFunctionRva+CastSendSiteOffset"),
		("PASSIVE_BUFF_SEND_SITE", "PassiveBuffFunctionRva+PassiveBuffSendSiteOffset"),
		("CHANNEL_CODE_FROM_INDEX", "ChannelCodeFromIndexRva"),
		("CHANNEL_TYPE_FROM_INDEX", "ChannelTypeFromIndexRva"),
		("CHAT_GATE_FUNCTION", "ChatGateFunctionRva"),
		("CHAT_PACK_FUNCTION", "ChatPackFunctionRva"),
		("CHAT_ENCODE_FUNCTION", "ChatEncodeFunctionRva"),
		("CHANNEL_ACTIVATE_FUNCTION", "ChannelActivateFunctionRva"),
		("CHAT_SEND_FUNCTION", "ChatSendFunctionRva")
	];

	// Hằng số thật sự không còn nơi nào tham chiếu. Trước 2026-09-08 danh sách này còn chứa 5 hằng số ĐANG được dùng
	// trong mã sản xuất, và nhãn UNUSED_NO_RULE khiến chúng không bao giờ được kiểm — đúng loại bẫy đã làm mất cả buổi
	// với NpcConfirmModalVtableRva. Đếm lại bằng grep ngày 2026-09-08, chỉ hằng số dưới đây là 0 tham chiếu.
	// AttackManager còn trùng vai trò với AttackManagerRva bên native, mà bản native đã dời sang 0x4E2660 trong khi
	// hằng số managed này vẫn giữ 0x4E0640 của bản game cũ — giữ lại chỉ để đối chiếu, không ai đọc.
	private static readonly (string Name, int Rva)[] UnusedGlobals = [
		("ATTACK_MANAGER_MANAGED", GameAddresses.Globals.AttackManager)
	];

	// Con trỏ toàn cục có tham chiếu thật trong mã. Đều đọc bằng ReadPointer32 rồi lấy field theo offset, nên quy tắc
	// chung là: bằng 0 thì chưa kết luận được (game chỉ gán khi cần), khác 0 thì phải đọc được vùng nhớ nó trỏ tới.
	private static readonly (string Name, int Rva, string Users)[] PointerGlobals = [
		("MAP_COORDINATE_ROOT", GameAddresses.Globals.MapCoordinateRoot, "Loot/Engine.cs:393, Loot/AutoFsGroundItemScanner.cs:28"),
		("COMBAT_TARGET_ROOT", GameAddresses.Globals.CombatTargetRoot, "Utils/CombatSnapshot.cs:7"),
		("DIALOG_POINTER", GameAddresses.Globals.DialogPointer, "chỉ DebugTools: ShopRepairDebugCommand.cs:58, ModalVtableProbe.cs:40")
	];

	// Trả về khối log đã sẵn sàng ghi. Dòng đầu là tóm tắt, các dòng sau là chi tiết từng mục.
	public static List<string> Run(GameWindow game) {
		List<string> lines = [];
		List<string> nativeFailures = [];
		List<string> nativeInconclusive = [];
		int nativePassed = 0;
		int nativeChecked = 0;
		string nativeError = "";

		if (! game.AutoFsTransport.TryQueryAddressAudit(game.Handle, -1, out ulong nativeCount, out nativeError)) {
			lines.Add($"ADDRESS_AUDIT_NATIVE_UNAVAILABLE | PID={game.ProcessId} | Reason={nativeError}");
		} else if (nativeCount != (ulong)NativeEntries.Length) {
			lines.Add($"ADDRESS_AUDIT_NATIVE_COUNT_MISMATCH | PID={game.ProcessId} | Native={nativeCount} | Managed={NativeEntries.Length} | Action=Đồng bộ lại bảng NativeEntries với switch trong SystemUint.cpp");
		} else {
			for (int index = 0; index < NativeEntries.Length; index++) {
				(string name, string constants) = NativeEntries[index];
				if (! game.AutoFsTransport.TryQueryAddressAudit(game.Handle, index, out ulong code, out string queryError)) {
					nativeFailures.Add(name);
					lines.Add($"ADDRESS_AUDIT_ENTRY | PID={game.ProcessId} | Source=NATIVE | Index={index} | Name={name} | Constants={constants} | Verdict=QUERY_FAILED | Detail={queryError}");
					continue;
				}
				nativeChecked++;
				string verdict = DescribeNativeResult(code);
				if (IsNativePass(code)) nativePassed++;
				else if (code == 4) nativeInconclusive.Add(name);
				else nativeFailures.Add(name);
				lines.Add($"ADDRESS_AUDIT_ENTRY | PID={game.ProcessId} | Source=NATIVE | Index={index} | Name={name} | Constants={constants} | Verdict={verdict} | Code={code}");
			}
		}

		List<string> managedFailures = [];
		int managedPassed = 0;
		int managedChecked = 0;
		foreach (ManagedResult result in RunManagedChecks(game)) {
			lines.Add($"ADDRESS_AUDIT_ENTRY | PID={game.ProcessId} | Source=MANAGED | Name={result.Name} | Rva=0x{result.Rva:X} | Verdict={result.Verdict} | Detail={result.Detail}");
			if (result.Verdict == "UNUSED_NO_RULE" || result.Verdict == "INCONCLUSIVE_NULL") continue;
			managedChecked++;
			if (result.Verdict.StartsWith("PASS", StringComparison.Ordinal)) managedPassed++;
			else managedFailures.Add(result.Name);
		}

		string summary = $"ADDRESS_AUDIT_SUMMARY | PID={game.ProcessId} | Native={nativePassed}/{nativeChecked} | Managed={managedPassed}/{managedChecked} | " +
			$"Fail={(nativeFailures.Count + managedFailures.Count)} | FailNames=[{string.Join(",", nativeFailures.Concat(managedFailures))}] | " +
			$"Inconclusive=[{string.Join(",", nativeInconclusive)}] | UnusedConstants={UnusedGlobals.Length}";
		lines.Insert(0, summary);
		return lines;
	}

	private static List<ManagedResult> RunManagedChecks(GameWindow game) {
		List<ManagedResult> results = [];
		try {
			using MemoryReader reader = new(game.ProcessId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) {
				results.Add(new ManagedResult("MODULE_BASE", 0, "FAIL_READ", "GetModuleBase trả 0."));
				return results;
			}
			results.Add(CheckEntityTable(reader, moduleBase));
			results.Add(CheckInventoryRoot(reader, moduleBase));
			results.Add(CheckItemTable(reader, moduleBase));
			results.Add(CheckGroundRecordTable(reader, moduleBase));
			results.Add(CheckMapIdMirrors(reader, moduleBase));
			results.Add(CheckShopState(reader, moduleBase));
			results.Add(CheckModalState(reader, moduleBase));
			results.Add(CheckCurrentTargetIndex(reader, moduleBase));
			results.Add(CheckReturnToTownModal(reader, moduleBase));
			foreach ((string name, int rva, string users) in PointerGlobals) results.Add(CheckPointerGlobal(reader, moduleBase, name, rva, users));
			foreach ((string name, int rva) in UnusedGlobals) {
				results.Add(new ManagedResult(name, rva, "UNUSED_NO_RULE", $"Value=0x{ReadRaw(reader, moduleBase, rva):X8} | Note=Hằng số không được tham chiếu ở đâu trong mã nguồn"));
			}
			return results;
		} catch (Exception ex) {
			results.Add(new ManagedResult("PROCESS_MEMORY", 0, "FAIL_READ", $"{ex.GetType().Name}: {ex.Message}"));
			return results;
		}
	}

	// Áp đúng bộ điều kiện mà RuntimeLayoutResolver.ValidatePlayer dùng để công nhận bảng entity.
	private static ManagedResult CheckEntityTable(MemoryReader reader, IntPtr moduleBase) {
		int rva = GameAddresses.Globals.EntityTable;
		try {
			IntPtr table = reader.ReadPointer32(IntPtr.Add(moduleBase, rva));
			if (table == IntPtr.Zero) return new ManagedResult("ENTITY_TABLE", rva, "FAIL_NULL", "Con trỏ bảng entity bằng 0.");
			IntPtr player = IntPtr.Add(table, GameAddresses.Entity.PlayerIndex * GameAddresses.Entity.Stride);
			byte[] bytes = reader.ReadBytes(player, GameAddresses.Entity.RawY + sizeof(int));
			if (bytes.Length < GameAddresses.Entity.RawY + sizeof(int)) return new ManagedResult("ENTITY_TABLE", rva, "FAIL_READ", $"Đọc thiếu bản ghi nhân vật: {bytes.Length} byte.");
			int level = BitConverter.ToInt32(bytes, GameAddresses.Entity.Level);
			int hp    = BitConverter.ToInt32(bytes, GameAddresses.Entity.Hp);
			int maxHp = BitConverter.ToInt32(bytes, GameAddresses.Entity.MaxHp);
			int mp    = BitConverter.ToInt32(bytes, GameAddresses.Entity.Mp);
			int maxMp = BitConverter.ToInt32(bytes, GameAddresses.Entity.MaxMp);
			int rawX  = BitConverter.ToInt32(bytes, GameAddresses.Entity.RawX);
			int rawY  = BitConverter.ToInt32(bytes, GameAddresses.Entity.RawY);
			string detail = $"Level={level} | HP={hp}/{maxHp} | MP={mp}/{maxMp} | Raw={rawX}/{rawY}";
			bool valid = level > 0 && level <= 1000 && hp >= 0 && maxHp > 0 && hp <= maxHp && mp >= 0 && maxMp > 0 && mp <= maxMp && rawX > 0 && rawY > 0;
			return new ManagedResult("ENTITY_TABLE", rva, valid ? "PASS_PLAYER_FIELDS" : "FAIL_PLAYER_FIELDS", detail);
		} catch (Exception ex) {
			return new ManagedResult("ENTITY_TABLE", rva, "FAIL_READ", $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	// Cùng chuỗi con trỏ mà RuntimeLayoutResolver.ResolveSaleData dùng để công nhận túi đồ 35 ô.
	private static ManagedResult CheckInventoryRoot(MemoryReader reader, IntPtr moduleBase) {
		int rva = GameAddresses.Globals.InventoryRoot;
		try {
			IntPtr root = reader.ReadPointer32(IntPtr.Add(moduleBase, rva));
			// Con trỏ này chỉ được game gán khi cần nên giá trị 0 không chứng minh RVA lệch.
			if (root == IntPtr.Zero) return new ManagedResult("INVENTORY_ROOT", rva, "INCONCLUSIVE_NULL", "Con trỏ bằng 0, game chưa gán.");
			IntPtr container = IntPtr.Add(root, GameAddresses.Inventory.Object);
			IntPtr slotList = reader.ReadPointer32(IntPtr.Add(container, GameAddresses.Inventory.SaleSlotListPointer));
			if (slotList == IntPtr.Zero) return new ManagedResult("INVENTORY_ROOT", rva, "FAIL_NULL", "Danh sách ô túi đồ bằng 0.");
			int expected = GameAddresses.Inventory.SaleSlotCount * sizeof(int);
			byte[] slots = reader.ReadBytes(slotList, expected);
			return new ManagedResult("INVENTORY_ROOT", rva, slots.Length == expected ? "PASS_POINTER_CHAIN" : "FAIL_READ", $"Root=0x{root.ToInt64():X8} | SlotBytes={slots.Length}/{expected}");
		} catch (Exception ex) {
			return new ManagedResult("INVENTORY_ROOT", rva, "FAIL_READ", $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	private static ManagedResult CheckItemTable(MemoryReader reader, IntPtr moduleBase) {
		int rva = GameAddresses.Globals.ItemTable;
		try {
			IntPtr table = reader.ReadPointer32(IntPtr.Add(moduleBase, rva));
			if (table == IntPtr.Zero) return new ManagedResult("ITEM_TABLE", rva, "FAIL_NULL", "Con trỏ bảng vật phẩm bằng 0.");
			byte[] head = reader.ReadBytes(table, sizeof(int));
			return new ManagedResult("ITEM_TABLE", rva, head.Length == sizeof(int) ? "PASS_POINTER" : "FAIL_READ", $"Table=0x{table.ToInt64():X8}");
		} catch (Exception ex) {
			return new ManagedResult("ITEM_TABLE", rva, "FAIL_READ", $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	// Cùng điều kiện mà RuntimeLayoutResolver.ResolveGroundAndLootTransport dùng: bản ghi đầu tiên phải đọc được đủ tới toạ độ nội bộ.
	private static ManagedResult CheckGroundRecordTable(MemoryReader reader, IntPtr moduleBase) {
		int rva = GameAddresses.Item.GroundRecordTablePointer;
		try {
			IntPtr table = reader.ReadPointer32(IntPtr.Add(moduleBase, rva));
			if (table == IntPtr.Zero) return new ManagedResult("GROUND_RECORD_TABLE", rva, "FAIL_NULL", "Con trỏ bảng vật phẩm rơi bằng 0.");
			int expected = GameAddresses.Item.GroundInternalY + sizeof(int);
			byte[] record = reader.ReadBytes(IntPtr.Add(table, GameAddresses.Item.GroundRecordStride), expected);
			return new ManagedResult("GROUND_RECORD_TABLE", rva, record.Length == expected ? "PASS_POINTER_CHAIN" : "FAIL_READ", $"Table=0x{table.ToInt64():X8} | RecordBytes={record.Length}/{expected}");
		} catch (Exception ex) {
			return new ManagedResult("GROUND_RECORD_TABLE", rva, "FAIL_READ", $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	// Ba bản sao map ID phải cùng lớn hơn 0 và bằng nhau, đúng quy tắc đang dùng trong RuntimeLayoutResolver.
	private static ManagedResult CheckMapIdMirrors(MemoryReader reader, IntPtr moduleBase) {
		int rva = GameAddresses.Globals.MapId;
		try {
			int first  = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.MapId));
			int second = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.MapIdMirror));
			int third  = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.MapIdRuntimeMirror));
			bool valid = first > 0 && first == second && first == third;
			return new ManagedResult("MAP_ID_MIRRORS", rva, valid ? "PASS_MIRRORS_AGREE" : "FAIL_MIRRORS_DISAGREE", $"Values={first}/{second}/{third} | Rvas=0x{GameAddresses.Globals.MapId:X}/0x{GameAddresses.Globals.MapIdMirror:X}/0x{GameAddresses.Globals.MapIdRuntimeMirror:X}");
		} catch (Exception ex) {
			return new ManagedResult("MAP_ID_MIRRORS", rva, "FAIL_READ", $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	private static ManagedResult CheckShopState(MemoryReader reader, IntPtr moduleBase) {
		int rva = GameAddresses.Globals.ShopState;
		try {
			int value = reader.ReadInt32(IntPtr.Add(moduleBase, rva));
			return new ManagedResult("SHOP_STATE", rva, value is >= 0 and <= 2 ? "PASS_RANGE" : "FAIL_RANGE", $"Value={value} | Expected=0..2");
		} catch (Exception ex) {
			return new ManagedResult("SHOP_STATE", rva, "FAIL_READ", $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	// Không có quy tắc nào xác định giá trị hợp lệ khi không có modal nào mở, nên chỉ in giá trị thô và địa chỉ mong đợi của popup Về thành.
	private static ManagedResult CheckModalState(MemoryReader reader, IntPtr moduleBase) {
		int rva = GameAddresses.Globals.ModalState;
		try {
			uint value = unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, rva)));
			uint expectedReturnToTown = unchecked((uint)IntPtr.Add(moduleBase, GameAddresses.Globals.ReturnToTownModal).ToInt64());
			if (value == 0) return new ManagedResult("MODAL_STATE", rva, "INCONCLUSIVE_NULL", $"Value=0 | ReturnToTownModal=0x{expectedReturnToTown:X8} | Note=Không có modal nào đang mở");
			return new ManagedResult("MODAL_STATE", rva, "PASS_POINTER", $"Value=0x{value:X8} | ReturnToTownModal=0x{expectedReturnToTown:X8}");
		} catch (Exception ex) {
			return new ManagedResult("MODAL_STATE", rva, "FAIL_READ", $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	// Ô này giữ chỉ số entity đang được client target. Giá trị hợp lệ theo AutoFsClientProfile là -1/0 (không có mục
	// tiêu) hoặc FirstEntityIndex..LastEntityIndex. Đây là ô mà toàn bộ luồng chọn mục tiêu của Đánh dựa vào.
	private static ManagedResult CheckCurrentTargetIndex(MemoryReader reader, IntPtr moduleBase) {
		int rva = GameAddresses.Globals.CurrentTargetIndex;
		try {
			int value = reader.ReadInt32(IntPtr.Add(moduleBase, rva));
			bool valid = value >= -1 && value <= AutoFsClientProfile.LastEntityIndex;
			string detail = $"Value={value} (0x{unchecked((uint)value):X8}) | Expected=-1..{AutoFsClientProfile.LastEntityIndex} | Users=Attack/AutoFsEntityScanner.cs:13,53 + Utils/TargetSelector,MonsterFinder,EntitySnapshot,EntityFinder";
			return new ManagedResult("CURRENT_TARGET_INDEX", rva, valid ? "PASS_RANGE" : "FAIL_RANGE", detail);
		} catch (Exception ex) {
			return new ManagedResult("CURRENT_TARGET_INDEX", rva, "FAIL_READ", $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	// RVA này KHÔNG phải con trỏ để đọc: AccountEngineCoordinator.cs:485 dùng chính địa chỉ moduleBase+rva làm giá trị
	// modal mong đợi khi nhân vật chết. Nên quy tắc là địa chỉ đó phải nằm trong ảnh module, và phải trùng hằng số
	// ReturnToTownObjectRva bên native — cái đã được mục NATIVE RETURN_TO_TOWN_OBJECT kiểm độc lập.
	private static ManagedResult CheckReturnToTownModal(MemoryReader reader, IntPtr moduleBase) {
		int rva = GameAddresses.Globals.ReturnToTownModal;
		try {
			uint expected = unchecked((uint)IntPtr.Add(moduleBase, rva).ToInt64());
			uint vtable = unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, rva)));
			// Object tĩnh của popup Về thành: ô đầu tiên phải là con trỏ vtable nằm trong module.
			uint vtableRva = vtable - unchecked((uint)moduleBase.ToInt64());
			bool valid = vtable != 0 && vtableRva < 0x2000000;
			string detail = $"ObjectAddress=0x{expected:X8} | Vtable=0x{vtable:X8} | VtableRva=0x{vtableRva:X} | Users=Runtime/AccountEngineCoordinator.cs:485";
			return new ManagedResult("RETURN_TO_TOWN_MODAL", rva, valid ? "PASS_VTABLE_IN_MODULE" : "FAIL_VTABLE_OUT_OF_MODULE", detail);
		} catch (Exception ex) {
			return new ManagedResult("RETURN_TO_TOWN_MODAL", rva, "FAIL_READ", $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	private static ManagedResult CheckPointerGlobal(MemoryReader reader, IntPtr moduleBase, string name, int rva, string users) {
		try {
			IntPtr pointer = reader.ReadPointer32(IntPtr.Add(moduleBase, rva));
			if (pointer == IntPtr.Zero) return new ManagedResult(name, rva, "INCONCLUSIVE_NULL", $"Con trỏ bằng 0, game chưa gán | Users={users}");
			// Khác 0 thì phải đọc được vùng nó trỏ tới; RVA lệch thường cho ra số rác không map được.
			byte[] probe = reader.ReadBytes(pointer, sizeof(int));
			bool readable = probe.Length == sizeof(int);
			string detail = $"Pointer=0x{pointer.ToInt64():X8} | Readable={readable} | Users={users}";
			return new ManagedResult(name, rva, readable ? "PASS_POINTER_READABLE" : "FAIL_POINTER_UNREADABLE", detail);
		} catch (Exception ex) {
			return new ManagedResult(name, rva, "FAIL_READ", $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	private static uint ReadRaw(MemoryReader reader, IntPtr moduleBase, int rva) {
		try {
			return unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, rva)));
		} catch {
			return 0;
		}
	}

	private static bool IsNativePass(ulong code) {
		return code is 1 or 2 or 3;
	}

	private static string DescribeNativeResult(ulong code) {
		return code switch {
			1  => "PASS_SIGNATURE",
			2  => "PASS_POINTER",
			3  => "PASS_VTABLE",
			4  => "INCONCLUSIVE_NULL",
			10 => "FAIL_NOT_EXECUTABLE",
			11 => "FAIL_SIGNATURE",
			12 => "FAIL_NULL",
			13 => "FAIL_UNREADABLE",
			14 => "FAIL_VTABLE_MISMATCH",
			20 => "FAIL_IMAGE_UNSUPPORTED",
			0  => "FAIL_INDEX_OUT_OF_RANGE",
			_  => $"FAIL_UNKNOWN_{code}"
		};
	}

	private sealed record ManagedResult(string Name, int Rva, string Verdict, string Detail);
}
