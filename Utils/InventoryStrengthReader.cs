namespace Auto.Utils;

public static class InventoryStrengthReader {
	public static InventoryStrengthReading Read(int processId) {
		return InventoryStrengthReading.Fail($"Remaining-strength layout is unconfirmed for the current client. PID={processId}.");
	}
}

public sealed record InventoryStrengthReading(bool Success, int Current, int Maximum, IntPtr InventoryRoot, IntPtr InventoryObject, int First, int Second, int Third, int ItemIndex, int ItemWeight, int ItemQuantity, int ItemContribution, string FailureReason) {
	public int Free => Maximum - Current;
	public static InventoryStrengthReading Fail(string reason) => new(false, 0, 0, IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 0, 0, 0, 0, reason);
}
