namespace Auto.Loot;

using Auto.Utils;

// Đếm số lượng dược phẩm đang có trong túi để chặn nhặt khi đã đủ ngưỡng.
//
// Offset số lượng: GameAddresses.Item.Quantity (+0x754) đọc theo 16-bit KHÔNG dấu.
// Bằng chứng: tool Debug "Probe số lượng vật phẩm trong túi" (BuildStamp INVENTORY-QUANTITY-20260907-01,
// PID 21184) in u16 tại +0x754 cho C3#01..#04 lần lượt 15/8/10/4, chủ dự án mở túi trong game đối chiếu
// và xác nhận đúng số thật của cả 4 loại dược phẩm. Đọc int32 thì byte cao là rác nên phải dùng u16.
//
// Đếm trên cả 3 container: túi chính, ô trang bị nhanh và rương thứ 2. Offset của hai container sau đã được
// sửa lại ngày 2026-09-07 theo tool Debug "Probe dò container túi" - xem chú thích ở GameAddresses.Inventory.
// Log POTION_COUNT_BREAKDOWN in số ô đếm được theo từng container để phát hiện sớm nếu client lại đổi layout.
internal sealed class InventoryPotionCounter {
	private const int MaximumNameLength = 64;
	private const int MaximumPackedItemId = 0x001FFFFF;
	private const int MaximumTypeLength = 16;
	// Chỉ là cơ chế sửa sai cho các thay đổi không đi qua luồng nhặt (dùng thuốc, bán đồ, mua thêm).
	// Nhặt xong thì Invalidate() gọi thẳng nên không phải chờ hết khoảng này.
	private const int RefreshIntervalMilliseconds = 1500;

	private readonly object syncRoot = new();
	private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
	private readonly List<string> pendingDiagnostics = [];
	private DateTime nextRefreshUtc = DateTime.MinValue;
	private bool hasCounts;
	private bool breakdownLogged;
	private string lastFailure = "";

	// Chuẩn hoá tên để so khớp bất kể cách gõ dấu và bất kể hậu tố "xN" của tên dưới đất.
	public static string ToKey(string name) => AutoFsSpecialItemClassifier.ToAsciiUpper(ItemGroupClassifier.NormalizeName(name));

	public void Invalidate() {
		lock (syncRoot) {
			hasCounts = false;
			nextRefreshUtc = DateTime.MinValue;
		}
	}

	// Lấy dòng chẩn đoán đang chờ rồi xoá, để nơi gọi chỉ ghi log đúng một lần cho mỗi sự kiện.
	public string[] ConsumeDiagnostics() {
		lock (syncRoot) {
			if (pendingDiagnostics.Count == 0) return [];
			string[] diagnostics = [.. pendingDiagnostics];
			pendingDiagnostics.Clear();
			return diagnostics;
		}
	}

	// Trả false khi không đọc được túi: nơi gọi phải cho nhặt (fail-open), tuyệt đối không im lặng ngừng nhặt.
	public bool TryGetCount(int processId, string potionKey, out int count) {
		lock (syncRoot) {
			if (! hasCounts || DateTime.UtcNow >= nextRefreshUtc) {
				if (! Refresh(processId)) {
					count = 0;
					return false;
				}
			}
			counts.TryGetValue(potionKey, out count);
			return true;
		}
	}

	private bool Refresh(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
			if (inventoryRoot == IntPtr.Zero || itemTable == IntPtr.Zero) return Fail("InventoryRoot hoặc ItemTable bằng 0.");
			IntPtr inventoryObject = IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object);

			counts.Clear();
			List<string> occupancy = [];
			int readFailures = 0;
			foreach (InventoryContainer container in InventoryContainer.SearchOrder) {
				int occupied = 0;
				try {
					IntPtr slotList = reader.ReadPointer32(IntPtr.Add(inventoryObject, container.ListPointerOffset));
					if (slotList == IntPtr.Zero) {
						occupancy.Add($"C{container.Number}=NULL_LIST");
						continue;
					}
					// Một lời gọi cho cả container thay vì mỗi ô một lời gọi: 3 lần đọc thay vì 74.
					byte[] idBytes = reader.ReadBytes(slotList, container.SlotCount * sizeof(int));
					if (idBytes.Length != container.SlotCount * sizeof(int)) {
						occupancy.Add($"C{container.Number}=LIST_READ_FAILED");
						continue;
					}
					for (int index = 0; index < container.SlotCount; index++) {
						try {
							int itemId = BitConverter.ToInt32(idBytes, index * sizeof(int));
							if (itemId <= 0 || itemId > MaximumPackedItemId) continue;
							long record = checked(itemTable.ToInt64() + checked((long)itemId * GameAddresses.Item.InventoryRecordStride));
							long nameAddress = record + GameAddresses.Item.InventoryName;
							if (record <= 0 || nameAddress <= 0 || nameAddress > uint.MaxValue) continue;
							// Bộ lọc id theo ngưỡng thôi là chưa đủ: mảng rác ở container 16 có giá trị 4 ở ô #33, lọt
							// qua ngưỡng rồi giải ra một record bừa. Item thật luôn có Type dạng "medecine\...",
							// "equip\...", "weapen\..." nên đòi thêm dấu '\' mới loại được id rác.
							if (! ReadAsciiString(reader, new IntPtr(record + GameAddresses.Item.InventoryType), MaximumTypeLength).Contains('\\')) continue;
							string key = ToKey(InventoryContainer.ReadLegacyString(reader, new IntPtr(nameAddress), MaximumNameLength));
							if (key.Length == 0) continue;
							occupied++;
							// Ô đã có item thì tính tối thiểu 1: số lượng đọc ra 0 chỉ có thể là đọc hụt, không phải ô rỗng.
							int quantity = Math.Max(1, (int)reader.ReadUInt16(new IntPtr(record + GameAddresses.Item.Quantity)));
							counts[key] = counts.GetValueOrDefault(key) + quantity;
						} catch {
							readFailures++;
						}
					}
					occupancy.Add($"C{container.Number}={occupied}/{container.SlotCount}");
				} catch {
					occupancy.Add($"C{container.Number}=CONTAINER_FAILED");
				}
			}

			hasCounts = true;
			nextRefreshUtc = DateTime.UtcNow.AddMilliseconds(RefreshIntervalMilliseconds);
			lastFailure = "";
			if (! breakdownLogged) {
				breakdownLogged = true;
				pendingDiagnostics.Add($"POTION_COUNT_BREAKDOWN | PID={processId} | Occupancy={string.Join(",", occupancy)} | SlotReadFailures={readFailures} | QuantityOffset=+0x{GameAddresses.Item.Quantity:X} | QuantityWidth=UInt16");
			}
			return true;
		} catch (Exception ex) {
			return Fail($"{ex.GetType().Name}: {ex.Message}");
		}
	}

	private static string ReadAsciiString(MemoryReader reader, IntPtr address, int maximumLength) {
		byte[] bytes = reader.ReadBytes(address, maximumLength);
		int length = Array.IndexOf(bytes, (byte)0);
		if (length >= 0) bytes = bytes[..length];
		return System.Text.Encoding.ASCII.GetString(bytes);
	}

	private bool Fail(string reason) {
		hasCounts = false;
		// Chỉ xếp hàng khi thông điệp đổi, để lỗi lặp lại không làm ngập log.
		if (! string.Equals(lastFailure, reason, StringComparison.Ordinal)) {
			lastFailure = reason;
			pendingDiagnostics.Add($"POTION_COUNT_READ_FAILED | Reason={reason} | Action=FAIL_OPEN_ALLOW_PICKUP");
		}
		// Lùi lần thử kế tiếp để lỗi đọc không biến thành vòng lặp đọc bộ nhớ mỗi lượt quét.
		nextRefreshUtc = DateTime.UtcNow.AddMilliseconds(RefreshIntervalMilliseconds);
		return false;
	}
}
