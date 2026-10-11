namespace Auto.Runtime;

using Auto.Sale;
using Auto.Utils;

// Logs every blue item with "Xuất chiêu" (attackspeed_v / castspeed_v) once it is in the main bag, to loot-drops.log and
// the loot feed of the UI (owner rule 2026-10-09). Read-only; one instance per account.
//
// Works on the bag, not on the pickup: a new item table index in the 35 main bag slots is a new item, however it got
// there. The first scan after Auto starts only records what is already in the bag, so turning Auto on does not log the
// whole bag again.
internal sealed class BagAttributeWatcher {
	private const int ScanIntervalMilliseconds = 1000;
	// Item table index -> name, as seen on the last scan.
	private readonly Dictionary<int, string> seen = [];
	private bool baselineTaken;
	private DateTime nextScanUtc = DateTime.MinValue;

	public void Reset() {
		seen.Clear();
		baselineTaken = false;
		nextScanUtc = DateTime.MinValue;
	}

	public void Tick(int processId, Action<string> dropLog) {
		DateTime now = DateTime.UtcNow;
		if (now < nextScanUtc) return;
		nextScanUtc = now.AddMilliseconds(ScanIntervalMilliseconds);
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return;
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
			if (inventoryRoot == IntPtr.Zero || itemTable == IntPtr.Zero) return;
			IntPtr slotList = reader.ReadPointer32(IntPtr.Add(IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object), GameAddresses.Inventory.SaleSlotListPointer));
			if (slotList == IntPtr.Zero) return;
			Dictionary<int, string> current = [];
			for (int slot = 0; slot < GameAddresses.Inventory.SaleSlotCount; slot++) {
				int itemId = reader.ReadInt32(IntPtr.Add(slotList, slot * sizeof(int)));
				if (itemId <= 0 || current.ContainsKey(itemId)) continue;
				InventorySaleItem item = InventorySaleEngine.ReadItem(reader, itemTable, itemId);
				current[itemId] = item.Name;
				bool isNew = ! seen.TryGetValue(itemId, out string? previousName) || previousName != item.Name;
				if (! baselineTaken || ! isNew || item.Category != InventorySaleCategory.Blue) continue;
				IntPtr record = new(itemTable.ToInt64() + (long)itemId * GameAddresses.Item.InventoryRecordStride);
				IReadOnlyList<ItemMagicAttribute> attributes = ItemMagicAttributeReader.Read(reader, moduleBase, record);
				if (! ItemMagicAttributeReader.HasAttackSpeed(attributes)) continue;
				string stats = ItemMagicAttributeReader.Describe(attributes);
				// "LOOT_PICKED_UP" in the marker routes the line to loot-drops.log (DebugLog.AddLootDrop).
				dropLog($"LOOT_PICKED_UP_ATTACK_SPEED_ITEM | PID={processId} | Ô={slot} | ItemId={itemId} | Name={item.Name} | ChỉSố={stats}");
				LootFeed.Add(processId, $"{item.Name} [{stats}]");
			}
			seen.Clear();
			foreach ((int itemId, string name) in current) seen[itemId] = name;
			baselineTaken = true;
		} catch {
			// A failed read only skips this scan; the next one retries.
		}
	}
}
