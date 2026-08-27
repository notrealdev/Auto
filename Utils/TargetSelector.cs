namespace Auto.Utils;

using System.Diagnostics;
using System.Runtime.InteropServices;

public static class TargetSelector {
	private const int ProcessVmOperation = 0x0008;
	private const int ProcessVmWrite = 0x0020;
	private const int ProcessQueryInformation = 0x0400;

	private const int CurrentTargetIndexOffset = GameAddresses.Globals.CurrentTargetIndex;

	public static bool TrySelect(int processId, int targetIndex, out string result) {
		result = "";

		if (processId <= 0) {
			result = "ProcessId không hợp lệ.";
			return false;
		}

		if (targetIndex < 0) {
			result = $"TargetIndex không hợp lệ: {targetIndex}";
			return false;
		}

		Process? process = null;
		IntPtr processHandle = IntPtr.Zero;

		try {
			process = Process.GetProcessById(processId);

			IntPtr moduleBase = GetModuleBase(process, "Game.exe");

			if (moduleBase == IntPtr.Zero) {
				result = "Không tìm thấy module Game.exe.";
				return false;
			}

			processHandle = OpenProcess(
				ProcessVmOperation | ProcessVmWrite | ProcessQueryInformation,
				false,
				processId
			);

			if (processHandle == IntPtr.Zero) {
				int error = Marshal.GetLastWin32Error();

				result =
					$"OpenProcess write failed. " +
					$"PID={processId} Win32Error={error}";

				return false;
			}

			IntPtr targetIndexAddress = IntPtr.Add(
				moduleBase,
				CurrentTargetIndexOffset
			);

			byte[] buffer = BitConverter.GetBytes(targetIndex);

			bool success = WriteProcessMemory(
				processHandle,
				targetIndexAddress,
				buffer,
				buffer.Length,
				out IntPtr bytesWritten
			);

			if (!success) {
				int error = Marshal.GetLastWin32Error();

				result =
					$"WriteProcessMemory failed. " +
					$"Address={targetIndexAddress.ToInt64():X8} " +
					$"Win32Error={error}";

				return false;
			}

			if (bytesWritten.ToInt64() != buffer.Length) {
				result =
					$"WriteProcessMemory ghi thiếu byte. " +
					$"Expected={buffer.Length} Actual={bytesWritten.ToInt64()}";

				return false;
			}

			result =
				$"Game.exe+{CurrentTargetIndexOffset:X} = {targetIndex} | " +
				$"Address={targetIndexAddress.ToInt64():X8}";

			return true;
		} catch (Exception ex) {
			result = ex.Message;
			return false;
		} finally {
			if (processHandle != IntPtr.Zero) {
				CloseHandle(processHandle);
			}

			process?.Dispose();
		}
	}

	private static IntPtr GetModuleBase(Process process, string moduleName) {
		foreach (ProcessModule module in process.Modules) {
			if (string.Equals(
				module.ModuleName,
				moduleName,
				StringComparison.OrdinalIgnoreCase
			)) {
				return module.BaseAddress;
			}
		}

		return process.MainModule?.BaseAddress ?? IntPtr.Zero;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(
		int desiredAccess,
		bool inheritHandle,
		int processId
	);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool WriteProcessMemory(
		IntPtr processHandle,
		IntPtr baseAddress,
		byte[] buffer,
		int size,
		out IntPtr bytesWritten
	);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);
}
