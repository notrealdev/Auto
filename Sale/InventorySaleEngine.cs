namespace Auto.Sale;

using Auto.Attack;
using Auto.Utils;
using LootSettings = Auto.Loot.Settings;

internal enum InventorySaleTickResult { Running, Completed, Failed }

internal sealed class InventorySaleEngine {
	private const int SaleCommand = 47;
	private const int ArrangeInventoryCommand = 24;
	private const int ConfirmationMilliseconds = 1000;
	private const int ArrangementWaitMilliseconds = 500;
	private const int MaximumNameLength = 64;
	private const int MaximumTypeLength = 32;
	private readonly LootSettings settings;
	private readonly AutoFsAttackTransport transport;
	private bool active;
	private int nextMemoryIndex;
	private int pendingMemoryIndex = -1;
	private int pendingItemId;
	private DateTime pendingDeadlineUtc;
	private int soldItemCount;
	private bool arrangementPosted;
	private DateTime arrangementDeadlineUtc;

	public bool IsAutomaticSaleEnabled => settings.SaleItemSelections.Any(entry => entry.Value) && (settings.EnableSaleQuantityThreshold || settings.EnableSaleRemainingStrengthThreshold);

	public InventorySaleEngine(LootSettings settings, AutoFsAttackTransport transport) {
		this.settings = settings;
		this.transport = transport;
	}

	public void Start(Action<string>? log) {
		Reset();
		active = true;
		log?.Invoke("SALE_START | Slots=35 | Command=47 | Safety=WHITE_BLUE_MEDICINE_ONLY | GreenYellowSpecialUnknown=BLOCKED");
	}

	public void Reset() {
		active = false;
		nextMemoryIndex = 0;
		pendingMemoryIndex = -1;
		pendingItemId = 0;
		pendingDeadlineUtc = DateTime.MinValue;
		soldItemCount = 0;
		arrangementPosted = false;
		arrangementDeadlineUtc = DateTime.MinValue;
	}

	public InventorySaleTriggerReading ReadAutomaticTrigger(int processId) {
		if (! IsAutomaticSaleEnabled) return InventorySaleTriggerReading.Disabled;
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return InventorySaleTriggerReading.Fail("Game.exe không tồn tại.");
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			if (inventoryRoot == IntPtr.Zero) return InventorySaleTriggerReading.Fail("InventoryRoot bằng 0.");
			IntPtr inventoryObject = IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object);
			IntPtr slotList = reader.ReadPointer32(IntPtr.Add(inventoryObject, GameAddresses.Inventory.SaleSlotListPointer));
			if (slotList == IntPtr.Zero) return InventorySaleTriggerReading.Fail("SlotList bằng 0.");
			int occupiedSlots = 0;
			for (int index = 0; index < GameAddresses.Inventory.SaleSlotCount; index++) {
				if (reader.ReadInt32(IntPtr.Add(slotList, index * sizeof(int))) > 0) occupiedSlots++;
			}
			bool quantityTriggered = settings.EnableSaleQuantityThreshold && occupiedSlots > settings.SaleQuantityThreshold;
			InventoryStrengthReading strength = settings.EnableSaleRemainingStrengthThreshold ? InventoryStrengthReader.Read(processId) : InventoryStrengthReading.Fail("DISABLED");
			bool strengthTriggered = settings.EnableSaleRemainingStrengthThreshold && strength.Success && strength.Free < settings.SaleRemainingStrengthThreshold;
			return new InventorySaleTriggerReading(true, quantityTriggered || strengthTriggered, occupiedSlots, strength.Success ? strength.Free : null, quantityTriggered, strengthTriggered, strength.Success ? "" : strength.FailureReason);
		} catch (Exception ex) {
			return InventorySaleTriggerReading.Fail(ex.Message);
		}
	}

	public IReadOnlyList<string> DiscoverItemOptions(int processId) {
		using MemoryReader reader = new(processId);
		IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
		if (moduleBase == IntPtr.Zero) return Array.Empty<string>();
		IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
		IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
		if (inventoryRoot == IntPtr.Zero || itemTable == IntPtr.Zero) return Array.Empty<string>();
		IntPtr inventoryObject = IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object);
		IntPtr slotList = reader.ReadPointer32(IntPtr.Add(inventoryObject, GameAddresses.Inventory.SaleSlotListPointer));
		if (slotList == IntPtr.Zero) return Array.Empty<string>();
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
		for (int memoryIndex = 0; memoryIndex < GameAddresses.Inventory.SaleSlotCount; memoryIndex++) {
			int itemId = reader.ReadInt32(IntPtr.Add(slotList, memoryIndex * sizeof(int)));
			if (itemId <= 0) continue;
			InventorySaleItem item = ReadItem(reader, itemTable, itemId);
			if (item.Name.Length > 0) names.Add(item.Name);
		}
		return names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
	}

	public InventorySaleTickResult Tick(int processId, IntPtr gameWindowHandle, Action<string>? log, out string failureReason) {
		failureReason = "";
		if (! active) return InventorySaleTickResult.Completed;
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return Fail("Game.exe không tồn tại.", out failureReason);
			if (ReadUInt32(reader, IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState)) != 0) return Fail("SALE_MODAL_DETECTED | Không xác nhận popup bán.", out failureReason);
			if (ReadUInt32(reader, IntPtr.Add(moduleBase, GameAddresses.Globals.ShopState)) != 2) return Fail("SALE_SHOP_NOT_READY | ShopState khác 2.", out failureReason);

			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			if (inventoryRoot == IntPtr.Zero) return Fail("SALE_INVENTORY_ROOT_ZERO", out failureReason);
			IntPtr inventoryObject = IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object);
			IntPtr slotList = reader.ReadPointer32(IntPtr.Add(inventoryObject, GameAddresses.Inventory.SaleSlotListPointer));
			if (slotList == IntPtr.Zero) return Fail("SALE_SLOT_LIST_ZERO", out failureReason);

			if (pendingMemoryIndex >= 0) {
				int currentItemId = reader.ReadInt32(IntPtr.Add(slotList, pendingMemoryIndex * sizeof(int)));
				if (currentItemId != pendingItemId) {
					log?.Invoke($"SALE_CONFIRMED | MemoryIndex={pendingMemoryIndex} | ItemId={pendingItemId} | CurrentItemId={currentItemId} | State=ORIGINAL_ITEM_LEFT_SLOT");
					soldItemCount++;
					pendingMemoryIndex = -1;
					pendingItemId = 0;
					nextMemoryIndex++;
				} else if (DateTime.UtcNow < pendingDeadlineUtc) {
					return InventorySaleTickResult.Running;
				} else {
					log?.Invoke($"SALE_UNCONFIRMED | MemoryIndex={pendingMemoryIndex} | ItemId={pendingItemId} | CurrentItemId={currentItemId} | TimeoutMs={ConfirmationMilliseconds}");
					pendingMemoryIndex = -1;
					pendingItemId = 0;
					nextMemoryIndex++;
				}
			}

			IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
			if (itemTable == IntPtr.Zero) return Fail("SALE_ITEM_TABLE_ZERO", out failureReason);
			while (nextMemoryIndex < GameAddresses.Inventory.SaleSlotCount) {
				int itemId = reader.ReadInt32(IntPtr.Add(slotList, nextMemoryIndex * sizeof(int)));
				if (itemId <= 0) {
					nextMemoryIndex++;
					continue;
				}

				InventorySaleItem item = ReadItem(reader, itemTable, itemId);
				(bool selected, string reason) = Evaluate(item);
				log?.Invoke($"SALE_FILTER | MemoryIndex={nextMemoryIndex} | ItemId={itemId} | Name={item.Name} | Type={item.Type} | GameClass={item.GameClass?.ToString() ?? "UNCONFIRMED"} | F2A4={item.Field2A4} | F6D8={item.Field6D8} | F6DC={item.Field6DC} | F6E0={item.Field6E0} | F700={item.Field700} | FAA8={item.FieldAA8} | Category={item.Category} | Selected={selected} | Reason={reason}");
				if (! selected) {
					nextMemoryIndex++;
					continue;
				}
				if (! transport.TrySendCommand(gameWindowHandle, SaleCommand, itemId, out string error)) return Fail("SALE_COMMAND_FAILED | " + error, out failureReason);
				pendingMemoryIndex = nextMemoryIndex;
				pendingItemId = itemId;
				pendingDeadlineUtc = DateTime.UtcNow.AddMilliseconds(ConfirmationMilliseconds);
				log?.Invoke($"SALE_COMMAND_POSTED | Command={SaleCommand} | Payload={itemId} | MemoryIndex={nextMemoryIndex} | Name={item.Name} | Delivery=POSTED | Acceptance=UNVERIFIED");
				return InventorySaleTickResult.Running;
			}

			if (soldItemCount > 0 && ! arrangementPosted) {
				if (! transport.TrySendCommand(gameWindowHandle, ArrangeInventoryCommand, 1, out string arrangeError)) return Fail("SALE_ARRANGE_COMMAND_FAILED | " + arrangeError, out failureReason);
				arrangementPosted = true;
				arrangementDeadlineUtc = DateTime.UtcNow.AddMilliseconds(ArrangementWaitMilliseconds);
				log?.Invoke($"SALE_ARRANGE_POSTED | Command={ArrangeInventoryCommand} | Payload=1 | Sold={soldItemCount} | WaitMs={ArrangementWaitMilliseconds} | Delivery=POSTED | Acceptance=UNVERIFIED");
				return InventorySaleTickResult.Running;
			}
			if (arrangementPosted && DateTime.UtcNow < arrangementDeadlineUtc) return InventorySaleTickResult.Running;
			active = false;
			log?.Invoke($"SALE_COMPLETE | ScannedSlots=35 | Sold={soldItemCount} | ArrangeCommandPosted={arrangementPosted} | Arranged=UNVERIFIED");
			return InventorySaleTickResult.Completed;
		} catch (Exception ex) {
			return Fail($"SALE_EXCEPTION | {ex.GetType().Name}: {ex.Message}", out failureReason);
		}
	}

	private (bool Selected, string Reason) Evaluate(InventorySaleItem item) {
		bool exactSelected = settings.SaleItemSelections.Any(entry => entry.Value && ! IsBuiltIn(entry.Key) && NamesMatch(entry.Key, item.Name));
		return item.Category switch {
			InventorySaleCategory.White => (exactSelected || IsSelected("Đồ Trắng"), exactSelected ? "EXACT_SAFE_WHITE" : IsSelected("Đồ Trắng") ? "WHITE_ENABLED" : "WHITE_DISABLED"),
			InventorySaleCategory.Blue => (exactSelected || IsSelected("Đồ Xanh"), exactSelected ? "EXACT_SAFE_BLUE" : IsSelected("Đồ Xanh") ? "BLUE_ENABLED" : "BLUE_DISABLED"),
			InventorySaleCategory.Medicine => (exactSelected || IsSelected("Dược Phẩm"), exactSelected ? "EXACT_SAFE_MEDICINE" : IsSelected("Dược Phẩm") ? "MEDICINE_ENABLED" : "MEDICINE_DISABLED"),
			InventorySaleCategory.Green => (false, "GREEN_POPUP_BLOCKED"),
			InventorySaleCategory.Yellow => (false, "YELLOW_VALUABLE_BLOCKED"),
			_ when exactSelected => (false, "EXACT_ITEM_UNCONFIRMED_SAFETY_BLOCKED"),
			_ => (false, "CATEGORY_NOT_SELECTED_OR_UNCONFIRMED")
		};
	}

	private InventorySaleItem ReadItem(MemoryReader reader, IntPtr itemTable, int itemId) {
		IntPtr record = new(itemTable.ToInt64() + (long)itemId * GameAddresses.Item.InventoryRecordStride);
		string name = ReadLegacyString(reader, IntPtr.Add(record, GameAddresses.Item.InventoryName), MaximumNameLength);
		string type = ReadAsciiString(reader, IntPtr.Add(record, GameAddresses.Item.InventoryType), MaximumTypeLength);
		int field2A4 = reader.ReadInt32(IntPtr.Add(record, GameAddresses.Item.InventoryClassField2A4));
		int field6D8 = reader.ReadInt32(IntPtr.Add(record, GameAddresses.Item.InventoryClassField6D8));
		int field6DC = reader.ReadInt32(IntPtr.Add(record, GameAddresses.Item.InventoryClassField6DC));
		int field6E0 = reader.ReadInt32(IntPtr.Add(record, GameAddresses.Item.InventoryClassField6E0));
		int field700 = reader.ReadInt32(IntPtr.Add(record, GameAddresses.Item.InventoryClassField700));
		int fieldAA8 = reader.ReadInt32(IntPtr.Add(record, GameAddresses.Item.InventoryClassFieldAA8));
		int? gameClass = GetGameClass(field2A4, field6D8, field6DC, field6E0, field700, fieldAA8);
		InventorySaleCategory category = Classify(type, gameClass);
		return new(itemId, name, type, field2A4, field6D8, field6DC, field6E0, field700, fieldAA8, gameClass, category);
	}

	private static InventorySaleCategory Classify(string type, int? gameClass) {
		if (type.Contains("medecin", StringComparison.OrdinalIgnoreCase)) return InventorySaleCategory.Medicine;
		bool equipment = type.Contains("equip", StringComparison.OrdinalIgnoreCase) || type.Contains("weapen", StringComparison.OrdinalIgnoreCase);
		if (! equipment) return InventorySaleCategory.Unknown;
		return gameClass switch {
			0 => InventorySaleCategory.White,
			1 => InventorySaleCategory.Blue,
			2 => InventorySaleCategory.Green,
			4 => InventorySaleCategory.Yellow,
			_ => InventorySaleCategory.Unknown
		};
	}

	private static int? GetGameClass(int field2A4, int field6D8, int field6DC, int field6E0, int field700, int fieldAA8) {
		if (IsSpecialClass(field6DC, field6E0) || field700 == 3000) return 5;
		if (field700 == 0) {
			if (field2A4 == 0) return 0;
			return fieldAA8 > 0 ? 3 : 1;
		}
		if (field700 > 0 && field700 < 1000 && field6D8 == 0) return 2;
		if (field700 is 1000 or 5000) return 4;
		return null;
	}

	private static bool IsSpecialClass(int field6DC, int field6E0) {
		if (field6DC is 2 or 5 or 6 or 7 or 9) return field6E0 is >= 0x27 and <= 0x29;
		if (field6DC == 0) return field6E0 is >= 0x48 and <= 0x53;
		if (field6DC == 10) return field6E0 is >= 0x2D and <= 0x2F;
		return false;
	}

	private bool IsSelected(string name) => settings.SaleItemSelections.TryGetValue(name, out bool enabled) && enabled;
	private static bool IsBuiltIn(string name) => name is "Đồ Trắng" or "Đồ Xanh" or "Dược Phẩm";
	private static bool NamesMatch(string selectedName, string itemName) => selectedName.Equals(itemName, StringComparison.OrdinalIgnoreCase);

	private static string ReadLegacyString(MemoryReader reader, IntPtr address, int maximumLength) {
		byte[] bytes = ReadNullTerminated(reader.ReadBytes(address, maximumLength));
		return LegacyVietnameseText.Decode(bytes).Trim();
	}

	private static string ReadAsciiString(MemoryReader reader, IntPtr address, int maximumLength) {
		byte[] bytes = ReadNullTerminated(reader.ReadBytes(address, maximumLength));
		return System.Text.Encoding.ASCII.GetString(bytes).Trim();
	}

	private static byte[] ReadNullTerminated(byte[] bytes) {
		int length = Array.IndexOf(bytes, (byte)0);
		return length < 0 ? bytes : bytes[..length];
	}

	private static uint ReadUInt32(MemoryReader reader, IntPtr address) => unchecked((uint)reader.ReadInt32(address));

	private InventorySaleTickResult Fail(string reason, out string failureReason) {
		failureReason = reason;
		Reset();
		return InventorySaleTickResult.Failed;
	}
}

internal enum InventorySaleCategory { Unknown, White, Blue, Green, Yellow, Medicine }
internal sealed record InventorySaleItem(int ItemId, string Name, string Type, int Field2A4, int Field6D8, int Field6DC, int Field6E0, int Field700, int FieldAA8, int? GameClass, InventorySaleCategory Category);
internal sealed record InventorySaleTriggerReading(bool Success, bool Triggered, int OccupiedSlots, int? RemainingStrength, bool QuantityTriggered, bool StrengthTriggered, string FailureReason) {
	public static InventorySaleTriggerReading Disabled { get; } = new(true, false, 0, null, false, false, "DISABLED");
	public static InventorySaleTriggerReading Fail(string reason) => new(false, false, 0, null, false, false, reason);
}
