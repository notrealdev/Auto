namespace Auto.Runtime;

using Auto.Sale;
using Auto.Utils;

// Logs a blue item with "Xuất chiêu" (attackspeed_v / castspeed_v) to loot-drops.log and the loot feed of the UI, by name
// only, once, when the character picked it up (owner rules 2026-10-09 and 2026-10-11). Read-only; one instance per account.
//
// A new item table index in the 35 main bag slots marks the candidate, and the attributes are read from its bag record.
// It is logged only if the loot engine confirmed a pickup of that name (PickupJournal). Moving the item inside the bag
// (it leaves the slots for a moment and comes back) or receiving it in a trade also shows a new index, and owner testing
// 2026-10-11 saw both logged again. Picking by hand is not logged either: the journal only knows Auto's pickups.
// The first scan after Auto starts only records what is already in the bag.
internal sealed class BagAttributeWatcher {
	private const int ScanIntervalMilliseconds = 1000;
	// The bag can show the item a scan before the loot engine records the pickup, so a candidate waits this long for it.
	private const int PickupWaitSeconds = 15;
	private static readonly TimeSpan PickupMaximumAge = TimeSpan.FromSeconds(30);
	// Item table index -> name, as seen on the last scan.
	private readonly Dictionary<int, string> seen = [];
	// New "Xuất chiêu" items waiting for their pickup record: item table index -> (name, slot, first seen).
	private readonly Dictionary<int, (string Name, int Slot, DateTime SinceUtc)> pending = [];
	private bool baselineTaken;
	private DateTime nextScanUtc = DateTime.MinValue;

	public void Reset() {
		seen.Clear();
		pending.Clear();
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
				if (ItemMagicAttributeReader.HasAttackSpeed(attributes)) pending[itemId] = (item.Name, slot, now);
			}
			foreach ((int itemId, (string name, int slot, DateTime sinceUtc)) in pending.ToArray()) {
				bool stillThere = current.TryGetValue(itemId, out string? currentName) && currentName == name;
				if (stillThere && PickupJournal.TryConsume(processId, name, PickupMaximumAge, out bool alreadyOnFeed)) {
					// "LOOT_PICKED_UP" in the marker routes the line to loot-drops.log (DebugLog.AddLootDrop).
					dropLog($"LOOT_PICKED_UP_ATTACK_SPEED_ITEM | PID={processId} | Ô={slot} | ItemId={itemId} | Name={name} | ĐãLênUI={alreadyOnFeed}");
					// A listed blue weapon is already on the feed from the pickup itself: one line per item (loot-drops.log
					// 2026-10-11 21:35:13 BốCủaPhiDương "Ly Cung phủ" was shown twice).
					if (! alreadyOnFeed) LootFeed.Add(processId, name);
					pending.Remove(itemId);
				} else if (! stillThere || now - sinceUtc > TimeSpan.FromSeconds(PickupWaitSeconds)) {
					pending.Remove(itemId);
				}
			}
			seen.Clear();
			foreach ((int itemId, string name) in current) seen[itemId] = name;
			baselineTaken = true;
		} catch {
			// A failed read only skips this scan; the next one retries.
		}
	}
}
