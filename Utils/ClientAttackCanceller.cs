namespace Auto.Utils;

using System.Runtime.InteropServices;

// Switches off the client's own attack order before a retreat walk. Command 300 hands the target to the client
// (player record +0xD798 = target index, +0xD7A0 = 1), and the client keeps chasing and hitting it until it dies:
// a walk command does not clear it. movement.log 2026-10-07 13:33:35-13:33:56 PID=13292: retreat walk CONFIRMED
// toward 61337/90108 yet the player went from 755 to 52 raw of the boss and stood there 14 s with the client
// destination at 135/-1 (skill 135 cast state). Read live on 6 clients 2026-10-07: every attacking client held
// +0xD798 = its monster index and +0xD7A0 = 1.
public static class ClientAttackCanceller {
	private const int ProcessVmOperation = 0x0008;
	private const int ProcessVmWrite = 0x0020;

	// Same cut-off for any flow that must walk away while the client may still be fighting: the boss retreat, and the
	// sale/repair trip (repair.log 2026-10-08 15:35:17-15:35:29: ZALO0988777666 and MaiAnhNhe triggered a sale next to a
	// boss and both logged "đứng yên 8 giây" before the first step, while trips started outside a fight walked at once).
	public static string Cancel(Auto.Runtime.GameWindow game) {
		try {
			Auto.Runtime.RuntimeLayout layout = game.RuntimeLayout;
			IntPtr entityTable;
			using (MemoryReader reader = new(game.ProcessId)) {
				IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
				entityTable = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			}
			return entityTable == IntPtr.Zero ? "HuỷĐánh=KHÔNG_ĐỌC_ĐƯỢC" : Cancel(game.ProcessId, entityTable, layout.PlayerRecordOffset);
		} catch (Exception ex) {
			return $"HuỷĐánh=LỖI({ex.GetType().Name})";
		}
	}

	public static string Cancel(int processId, IntPtr entityTable, int playerRecordOffset) {
		try {
			IntPtr player = IntPtr.Add(entityTable, playerRecordOffset);
			IntPtr active = IntPtr.Add(player, GameAddresses.Attack.CommandActive);
			int target;
			int activeBefore;
			using (MemoryReader reader = new(processId)) {
				target = reader.ReadInt32(IntPtr.Add(player, GameAddresses.Attack.TargetIndexCommand));
				activeBefore = reader.ReadInt32(active);
			}
			if (activeBefore == 0) return $"HuỷĐánh=KHÔNG_CẦN(D798={target})";
			IntPtr handle = OpenProcess(ProcessVmOperation | ProcessVmWrite, false, processId);
			if (handle == IntPtr.Zero) return $"HuỷĐánh=LỖI_OPEN({Marshal.GetLastWin32Error()})";
			try {
				byte[] zero = BitConverter.GetBytes(0);
				bool written = WriteProcessMemory(handle, active, zero, zero.Length, out IntPtr count) && count.ToInt64() == zero.Length;
				return written ? $"HuỷĐánh=OK(D798={target},D7A0={activeBefore}->0)" : $"HuỷĐánh=LỖI_GHI({Marshal.GetLastWin32Error()})";
			} finally {
				CloseHandle(handle);
			}
		} catch (Exception ex) {
			return $"HuỷĐánh=LỖI({ex.GetType().Name})";
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool WriteProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesWritten);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);
}
