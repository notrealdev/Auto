namespace Auto.Market;

using System.Text.RegularExpressions;
using Auto.Utils;

// Clickable item link inside a chat message, the same block the client puts in its chat input on Ctrl + left click on a
// bag item. Read from PID 4832 MaiAnhNhe on 2026-10-13: the link is 152 bytes starting with 0x0B, built from fields of
// the item record, and the client of a player next to it (PID 6596) received it byte for byte and showed the item.
// Layout checked on three items: "Lam Bảo Thạch", "Ly Cung phủ" and "Vũ Khúc Giáp" (the third was predicted from its
// record before the owner clicked it, and matched all 152 bytes). Bytes 8 and 14 were 1 on both equipment items and 0
// on the stone; which record field each comes from (+0xA78 / +0xA7C) is not proven, both held the same values.
internal static partial class ChatItemLink {
	public const int Length = 152;
	private const byte Marker = 0x0B;
	private const int UniqueIdOffset = GameAddresses.Item.UniqueId;
	private const int AttributeLevelOffset = GameAddresses.Item.AttributeLevels;
	private const int AttributeLevelCount = 6;
	private const int SeedOffset = GameAddresses.Item.Seed;
	private static readonly InventoryContainer[] LinkableContainers = [.. InventoryContainer.SearchOrder.Where(container => container.Number != 11)];

	// Text form stored in the advertise text box: "{item:3426F971:Ly Cung phủ}". The id is the number at record +0xA58
	// (different on two copies of "Vũ Khúc Giáp"), so the link still finds the item after it moves to another slot. It is
	// not unique on its own: "Trung Hồng đơn" and "Tiểu Hoàn đơn" of PID 4832 had the same value, so the name must match too.
	public static string ToPlaceholder(uint uniqueId, string name) => $"{{item:{uniqueId:X8}:{CleanName(name)}}}";

	// Map link of the client's "copy position" button. Layout read 2026-10-13 from the chat input of PID 4832 and from map
	// links other players sent, kept in the chat history of all 8 clients (9 different maps, e.g. "Triều Ca[216,195]"):
	// 0x0C | u16 total length = text length + 17 | u8 text length | 0x03 | text "<name>[x,y]" in TCVN3 | u32 map id |
	// u32 x | u32 y. "Triều Ca[216,195]" carried x = 1728 = 216 * 8 and y = 3120 = 195 * 16. The owner wants a dummy
	// position: [0,0] leads nowhere when clicked, so x and y are always 0.
	public static string ToMapPlaceholder(ChatMapOption map) => $"{{toado:{map.MapId}:{CleanName(map.Name)}}}";

	// The first placeholder form had no map: "{toado}" (2026-10-13 test), kept as Ngọc Tuyền (map 32).
	private static readonly ChatMapOption DefaultMap = new(32, "Ngọc Tuyền");

	// Teleport destinations of the Di ngoại phù menu (TalismanDestinationCatalog, same order), spelled like the map links
	// seen in chat ("Phong Thần đài", "Sùng Thành doanh", "Tuyệt Long lĩnh").
	public static IReadOnlyList<ChatMapOption> MapOptions { get; } = [
		new(1, "Phong Thần đài"), new(52, "Diêu Trì"), new(20, "Tây Kỳ"), new(21, "Triều Ca"), new(2, "Sùng Thành doanh"),
		new(3, "Ngọc Hư Cung"), new(4, "Xi Vưu Mộ"), new(5, "Sùng Thành"), new(6, "Bắc Hải"), new(7, "Yến Sơn"),
		new(8, "Chân núi Côn Lôn"), new(9, "Tây Côn Lôn"), new(10, "Thủ Dương Sơn"), new(11, "Du Hồn"), new(12, "Miêu Cương"),
		new(13, "Cự Lộc"), new(14, "Đồng Quan"), new(15, "Mạnh Tân"), new(16, "Tam Sơn"), new(17, "Kỳ Sơn"), new(18, "Mục Dã"),
		new(19, "Tuyệt Long lĩnh"), new(65, "Trần Đường")
	];

	// Groups: 1/2 = id/name of an item; 3 = "toado" for a map, 4/5 = map id/name (absent in the old "{toado}" form);
	// 6/7 = key/label of a link captured from the sent-message history (ChatHistoryReader).
	[GeneratedRegex(@"\{item:([0-9A-Fa-f]{8}):([^}]*)\}|\{(toado)(?::(\d{1,3}):([^}]*))?\}|\{link:([0-9A-Fa-f]{8}):([^}]*)\}")]
	public static partial Regex PlaceholderPattern();

	public static bool TryBuildMapLink(Match placeholder, out byte[] link, out string error) {
		link = [];
		ChatMapOption map = placeholder.Groups[4].Success ? new(int.Parse(placeholder.Groups[4].Value), placeholder.Groups[5].Value) : DefaultMap;
		if (! LegacyVietnameseText.TryEncode($"{map.Name}[0,0]", out byte[] text, out char unsupportedCharacter)) {
			error = $"Tên map chứa ký tự không hỗ trợ U+{(int)unsupportedCharacter:X4}.";
			return false;
		}
		if (text.Length > byte.MaxValue - 17) {
			error = $"Tên map quá dài: {text.Length} byte.";
			return false;
		}
		link = new byte[text.Length + 17];
		link[0] = 0x0C;
		BitConverter.TryWriteBytes(link.AsSpan(1, 2), (ushort)link.Length);
		link[3] = (byte)text.Length;
		link[4] = 0x03;
		text.CopyTo(link, 5);
		BitConverter.TryWriteBytes(link.AsSpan(5 + text.Length, 4), map.MapId);
		error = "";
		return true;
	}

	public static IReadOnlyList<ChatItemOption> ListItems(int processId) {
		List<ChatItemOption> items = [];
		using MemoryReader reader = new(processId);
		if (! TryReadRoots(reader, out IntPtr inventoryObject, out IntPtr itemTable)) return items;
		foreach (InventoryContainer container in LinkableContainers) {
			IntPtr slotList = reader.ReadPointer32(IntPtr.Add(inventoryObject, container.ListPointerOffset));
			if (slotList == IntPtr.Zero) continue;
			for (int slot = 0; slot < container.SlotCount; slot++) {
				int itemId = reader.ReadInt32(IntPtr.Add(slotList, slot * sizeof(int)));
				if (itemId <= 0) continue;
				IntPtr record = GetRecord(itemTable, itemId);
				string name = InventoryContainer.ReadLegacyString(reader, IntPtr.Add(record, GameAddresses.Item.InventoryName), 64);
				uint uniqueId = unchecked((uint)reader.ReadInt32(IntPtr.Add(record, UniqueIdOffset)));
				if (name.Length == 0 || uniqueId == 0) continue;
				// The chat tab names the main bag "Rương 1" to pair with "Rương 2" (owner request 2026-10-13); the shared
				// InventoryContainer name stays "Túi chính" because other logs use it.
				string containerName = container.Number == 3 ? "Rương 1" : container.DisplayName;
				items.Add(new ChatItemOption(containerName, slot, uniqueId, name));
			}
		}
		return items;
	}

	// Finds the item by id and name in the main bag or chest 2 and builds its link from the live record.
	public static bool TryBuild(int processId, uint uniqueId, string name, out byte[] link, out string error) {
		link = [];
		using MemoryReader reader = new(processId);
		if (! TryReadRoots(reader, out IntPtr inventoryObject, out IntPtr itemTable)) {
			error = "Không đọc được túi đồ của client.";
			return false;
		}
		foreach (InventoryContainer container in LinkableContainers) {
			IntPtr slotList = reader.ReadPointer32(IntPtr.Add(inventoryObject, container.ListPointerOffset));
			if (slotList == IntPtr.Zero) continue;
			for (int slot = 0; slot < container.SlotCount; slot++) {
				int itemId = reader.ReadInt32(IntPtr.Add(slotList, slot * sizeof(int)));
				if (itemId <= 0) continue;
				IntPtr record = GetRecord(itemTable, itemId);
				if (unchecked((uint)reader.ReadInt32(IntPtr.Add(record, UniqueIdOffset))) != uniqueId) continue;
				link = Build(reader, record);
				error = "";
				return true;
			}
		}
		error = $"Không còn vật phẩm \"{name}\" ({uniqueId:X8}) trong Túi chính / Rương 2.";
		return false;
	}

	private static byte[] Build(MemoryReader reader, IntPtr record) {
		byte[] link = new byte[Length];
		link[0] = Marker;
		link[1] = (byte)ReadField(reader, record, GameAddresses.Item.InventoryClassField6D8);
		BitConverter.TryWriteBytes(link.AsSpan(2, 2), (ushort)ReadField(reader, record, GameAddresses.Item.InventoryClassField6DC));
		BitConverter.TryWriteBytes(link.AsSpan(4, 2), (ushort)ReadField(reader, record, GameAddresses.Item.InventoryClassField6E0));
		link[6] = (byte)ReadField(reader, record, GameAddresses.Item.InventoryClassField700);
		link[7] = (byte)ReadField(reader, record, GameAddresses.Item.InventoryClassField6FC);
		link[8] = (byte)ReadField(reader, record, GameAddresses.Item.InventoryClassFieldA78);
		reader.ReadBytes(IntPtr.Add(record, SeedOffset), 4).CopyTo(link, 9);
		link[14] = (byte)ReadField(reader, record, GameAddresses.Item.InventoryClassFieldA7C);
		for (int index = 0; index < AttributeLevelCount; index++) {
			link[15 + index] = (byte)ReadField(reader, record, AttributeLevelOffset + index * sizeof(int));
		}
		link[21] = (byte)ReadField(reader, record, GameAddresses.Item.InventoryClassFieldAA4);
		reader.ReadBytes(IntPtr.Add(record, UniqueIdOffset), 4).CopyTo(link, 31);
		// Both 1 on all three items read; no record field was matched to them.
		link[35] = 1;
		link[106] = 1;
		return link;
	}

	private static string CleanName(string name) => name.Replace("}", "", StringComparison.Ordinal);

	private static int ReadField(MemoryReader reader, IntPtr record, int offset) => reader.ReadInt32(IntPtr.Add(record, offset));

	private static IntPtr GetRecord(IntPtr itemTable, int itemId) => new(itemTable.ToInt64() + (long)itemId * GameAddresses.Item.InventoryRecordStride);

	private static bool TryReadRoots(MemoryReader reader, out IntPtr inventoryObject, out IntPtr itemTable) {
		inventoryObject = IntPtr.Zero;
		itemTable = IntPtr.Zero;
		IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
		if (moduleBase == IntPtr.Zero) return false;
		IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
		itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
		if (inventoryRoot == IntPtr.Zero || itemTable == IntPtr.Zero) return false;
		inventoryObject = IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object);
		return true;
	}
}

public sealed record ChatItemOption(string ContainerName, int Slot, uint UniqueId, string Name) {
	public string Display => $"[{ContainerName} #{Slot}] {Name}";

	// The ComboBox selection box showed the record's default text ("ChatItemOption { ContainerName = ...", owner screenshot
	// 2026-10-13) instead of DisplayMemberPath, so the selected item is shown through ToString.
	public override string ToString() => Display;
}

public sealed record ChatMapOption(int MapId, string Name) {
	public override string ToString() => Name;
}
