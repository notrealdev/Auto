namespace Auto.Loot;

using Auto.Utils;

public sealed class InventoryPickupSnapshot {
	public bool Success { get; }
	public IntPtr InventoryObject { get; }
	public int ReceiptDecrement { get; }
	public int ReceiptFirstIncrement { get; }
	public int ReceiptSecondIncrement { get; }
	public string FailureReason { get; }

	private InventoryPickupSnapshot(bool success, IntPtr inventoryObject, int receiptDecrement, int receiptFirstIncrement, int receiptSecondIncrement, string failureReason) {
		Success = success;
		InventoryObject = inventoryObject;
		ReceiptDecrement = receiptDecrement;
		ReceiptFirstIncrement = receiptFirstIncrement;
		ReceiptSecondIncrement = receiptSecondIncrement;
		FailureReason = failureReason;
	}

	public static InventoryPickupSnapshot Capture(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			if (inventoryRoot == IntPtr.Zero) return Fail("InventoryRoot bằng 0.");
			IntPtr inventoryObject = IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object);
			int receiptDecrement = reader.ReadInt32(IntPtr.Add(inventoryObject, GameAddresses.Inventory.ReceiptDecrement));
			int receiptFirstIncrement = reader.ReadInt32(IntPtr.Add(inventoryObject, GameAddresses.Inventory.ReceiptFirstIncrement));
			int receiptSecondIncrement = reader.ReadInt32(IntPtr.Add(inventoryObject, GameAddresses.Inventory.ReceiptSecondIncrement));
			return new InventoryPickupSnapshot(true, inventoryObject, receiptDecrement, receiptFirstIncrement, receiptSecondIncrement, "");
		} catch (Exception ex) {
			return Fail($"{ex.GetType().Name}: {ex.Message}");
		}
	}

	public InventoryPickupVerification VerifyWith(InventoryPickupSnapshot after) {
		if (!Success || !after.Success || InventoryObject != after.InventoryObject) return InventoryPickupVerification.Unknown;
		if (after.ReceiptDecrement == ReceiptDecrement - 1 && after.ReceiptFirstIncrement == ReceiptFirstIncrement + 1 && after.ReceiptSecondIncrement == ReceiptSecondIncrement + 1) return InventoryPickupVerification.Received;
		if (after.ReceiptDecrement == ReceiptDecrement && after.ReceiptFirstIncrement == ReceiptFirstIncrement && after.ReceiptSecondIncrement == ReceiptSecondIncrement) return InventoryPickupVerification.NotReceived;
		return InventoryPickupVerification.Unknown;
	}

	public string CompareWith(InventoryPickupSnapshot after) {
		if (!Success) return $"Before=FAIL:{FailureReason}";
		if (!after.Success) return $"After=FAIL:{after.FailureReason}";
		if (InventoryObject != after.InventoryObject) return $"ObjectChanged={InventoryObject.ToInt64():X8}->{after.InventoryObject.ToInt64():X8}";
		return $"Verification={VerifyWith(after)} | Object=0x{InventoryObject.ToInt64():X8} | +0x{GameAddresses.Inventory.ReceiptDecrement:X5}:{ReceiptDecrement}->{after.ReceiptDecrement} | +0x{GameAddresses.Inventory.ReceiptFirstIncrement:X5}:{ReceiptFirstIncrement}->{after.ReceiptFirstIncrement} | +0x{GameAddresses.Inventory.ReceiptSecondIncrement:X5}:{ReceiptSecondIncrement}->{after.ReceiptSecondIncrement}";
	}

	private static InventoryPickupSnapshot Fail(string reason) => new(false, IntPtr.Zero, 0, 0, 0, reason);
}

public enum InventoryPickupVerification {
	Unknown,
	Received,
	NotReceived
}
