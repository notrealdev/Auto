namespace Auto.Runtime;

using Auto.Attack;
using Auto.Utils;

internal sealed class LowHpReturnTalismanEngine {
	private const int MaximumNameLength = 64;
	private const int MaximumPackedItemId = 0x001FFFFF;
	private const string ReturnTalismanNameFragment = "Hồi thành phù";
	private readonly BasicSettings settings;
	private readonly AutoFsAttackTransport transport;
	private bool pendingMapChange;
	private int sourceMapId;
	private DateTime nextDispatchUtc;
	private string lastFailure = "";

	public LowHpReturnTalismanEngine(BasicSettings settings, AutoFsAttackTransport transport) {
		this.settings = settings;
		this.transport = transport;
	}

	// Ưu tiên dùng Hồi thành phù khi phần trăm HP làm tròn lên đã chạm ngưỡng AutoFS.
	public bool Tick(int processId, IntPtr gameWindowHandle, GameSnapshot snapshot, int mapId, Action<string>? log) {
		if (! settings.EnableLowHpReturnTalisman || ! snapshot.Success || snapshot.Hp <= 0 || snapshot.MaxHp <= 0) {
			Reset();
			return false;
		}
		int hpPercent = Math.Clamp((int)Math.Ceiling(snapshot.Hp * 100.0 / snapshot.MaxHp), 0, 100);
		if (pendingMapChange) {
			if (sourceMapId > 0 && mapId > 0 && mapId != sourceMapId) {
				log?.Invoke($"RETURN_TALISMAN_CONFIRMED | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | SourceMapId={sourceMapId} | CurrentMapId={mapId}");
				Reset();
				return false;
			}
			if (hpPercent > settings.LowHpReturnTalismanThreshold) {
				Reset();
				return false;
			}
			if (DateTime.UtcNow < nextDispatchUtc) return true;
			log?.Invoke($"RETURN_TALISMAN_MAP_CHANGE_TIMEOUT | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | SourceMapId={sourceMapId} | CurrentMapId={mapId} | WaitMilliseconds=1000 | Action=RETRY_COMMAND_SEQUENCE");
			pendingMapChange = false;
			sourceMapId = 0;
		}
		if (hpPercent > settings.LowHpReturnTalismanThreshold) return false;
		if (! TryFindTalisman(processId, out int memoryIndex, out int container, out int itemId, out string itemName, out string findError)) {
			LogFailureOnce(log, $"RETURN_TALISMAN_NOT_FOUND | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | Threshold={settings.LowHpReturnTalismanThreshold} | Reason={findError}");
			return false;
		}
		if (itemId > MaximumPackedItemId) {
			LogFailureOnce(log, $"RETURN_TALISMAN_ITEM_ID_UNSUPPORTED | ItemId={itemId} | Container={container} | MemoryIndex={memoryIndex}");
			return false;
		}
		if (! transport.TryUseInventoryItem(gameWindowHandle, itemId, container, memoryIndex, out string sendError)) {
			LogFailureOnce(log, $"RETURN_TALISMAN_SEND_FAILED | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | ItemId={itemId} | Container={container} | MemoryIndex={memoryIndex} | Reason={sendError}");
			return false;
		}
		lastFailure = "";
		pendingMapChange = true;
		sourceMapId = mapId;
		nextDispatchUtc = DateTime.UtcNow.AddSeconds(1);
		log?.Invoke($"RETURN_TALISMAN_COMMAND_SEQUENCE_POSTED | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | Threshold={settings.LowHpReturnTalismanThreshold} | Name={itemName} | ItemId={itemId} | MemoryIndex={memoryIndex} | Container={container} | Command=310 | MapChangeConfirmed=False | Acceptance=NATIVE_ACCEPTED | RetryDelayMs=1000 | SourceMapId={mapId}");
		return true;
	}

	public void Reset() {
		pendingMapChange = false;
		sourceMapId = 0;
		nextDispatchUtc = DateTime.MinValue;
	}

	// Tìm vật phẩm theo đúng thứ tự container 11, 3 và 16 của AutoFS.
	private static bool TryFindTalisman(int processId, out int memoryIndex, out int container, out int itemId, out string itemName, out string error) {
		memoryIndex = -1;
		container = 0;
		itemId = 0;
		itemName = "";
		error = "";
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
			if (inventoryRoot == IntPtr.Zero || itemTable == IntPtr.Zero) {
				error = "InventoryRoot hoặc ItemTable bằng 0.";
				return false;
			}
			IntPtr inventoryObject = IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object);
			List<string> scannedItems = [];
			foreach (InventoryContainer inventoryContainer in InventoryContainer.SearchOrder) {
				// Cô lập từng container và từng slot để dữ liệu rỗng không làm hỏng toàn bộ lượt quét.
				try {
					IntPtr slotList = reader.ReadPointer32(IntPtr.Add(inventoryObject, inventoryContainer.ListPointerOffset));
					if (slotList == IntPtr.Zero) continue;
					for (int index = 0; index < inventoryContainer.SlotCount; index++) {
						try {
							int currentItemId = reader.ReadInt32(IntPtr.Add(slotList, index * sizeof(int)));
							if (currentItemId <= 0) continue;
							if (currentItemId > MaximumPackedItemId) {
								scannedItems.Add($"C{inventoryContainer.Number}#{index}:{currentItemId}:INVALID_ID");
								continue;
							}
							long recordAddress = checked(itemTable.ToInt64() + checked((long)currentItemId * GameAddresses.Item.InventoryRecordStride));
							long nameAddress = checked(recordAddress + GameAddresses.Item.InventoryName);
							if (recordAddress <= 0 || nameAddress <= 0 || nameAddress > uint.MaxValue) {
								scannedItems.Add($"C{inventoryContainer.Number}#{index}:{currentItemId}:INVALID_ADDRESS");
								continue;
							}
							string currentName = ReadLegacyString(reader, new IntPtr(nameAddress), MaximumNameLength);
							scannedItems.Add($"C{inventoryContainer.Number}#{index}:{currentItemId}:{currentName}");
							if (currentName.Contains(ReturnTalismanNameFragment, StringComparison.OrdinalIgnoreCase)) {
								memoryIndex = index;
								container = inventoryContainer.Number;
								itemId = currentItemId;
								itemName = currentName;
								return true;
							}
						} catch (Exception ex) {
							scannedItems.Add($"C{inventoryContainer.Number}#{index}:READ_FAIL:{ex.GetType().Name}");
						}
					}
				} catch (Exception ex) {
					scannedItems.Add($"C{inventoryContainer.Number}:CONTAINER_FAIL:{ex.GetType().Name}");
				}
			}
			error = $"Không có Hồi thành phù hoặc Hồi thành phù (Siêu cấp) trong container 11, 3 hoặc 16. Items=[{string.Join(";", scannedItems)}]";
			return false;
		} catch (Exception ex) {
			error = $"{ex.GetType().Name}: {ex.Message}";
			return false;
		}
	}

	private static string ReadLegacyString(MemoryReader reader, IntPtr address, int maximumLength) {
		byte[] bytes = reader.ReadBytes(address, maximumLength);
		int length = Array.IndexOf(bytes, (byte)0);
		if (length >= 0) bytes = bytes[..length];
		return LegacyVietnameseText.Decode(bytes).Trim();
	}

	private void LogFailureOnce(Action<string>? log, string failure) {
		if (string.Equals(lastFailure, failure, StringComparison.Ordinal)) return;
		lastFailure = failure;
		log?.Invoke(failure);
	}

	private sealed record InventoryContainer(int Number, int ListPointerOffset, int SlotCount) {
		public static InventoryContainer[] SearchOrder { get; } = [
			new(11, GameAddresses.Inventory.QuickSlotListPointer, GameAddresses.Inventory.QuickSlotCount),
			new(3, GameAddresses.Inventory.SaleSlotListPointer, GameAddresses.Inventory.SaleSlotCount),
			new(16, GameAddresses.Inventory.ExtendedSlotListPointer, GameAddresses.Inventory.ExtendedSlotCount)
		];
	}
}
