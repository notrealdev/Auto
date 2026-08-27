using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;

namespace Auto.Utils;

public sealed class MemoryReader : IDisposable {
	private const int PROCESS_VM_READ = 0x0010;
	private const int PROCESS_QUERY_INFORMATION = 0x0400;

	private readonly Process process;
	private readonly IntPtr handle;
	private static readonly ConcurrentDictionary<(int ProcessId, string ModuleName), IntPtr> moduleBases = new();
	public int ProcessId => process.Id;

	public byte[] ReadBytes(IntPtr address, int size) {
		return ReadMemory(address, size);
	}

	public ushort ReadUInt16(IntPtr address) {
		byte[] buffer = ReadMemory(address, 2);
		return buffer.Length == 2 ? BitConverter.ToUInt16(buffer, 0) : (ushort)0;
	}

	public float ReadFloat(IntPtr address) {
		byte[] buffer = ReadMemory(address, 4);
		return buffer.Length == 4 ? BitConverter.ToSingle(buffer, 0) : 0f;
	}

	public int ReadInt32(IntPtr address) {
		byte[] buffer = ReadMemory(address, 4);
		return buffer.Length == 4 ? BitConverter.ToInt32(buffer, 0) : 0;
	}

	public IntPtr ReadPointer32(IntPtr address) {
		int val = ReadInt32(address);
		return val == 0 ? IntPtr.Zero : new IntPtr(val);
	}

	public string ReadString(IntPtr address, int maxLength = 64) {
		byte[] buffer = ReadMemory(address, maxLength);
		if (buffer.Length == 0) return string.Empty;

		int nullIndex = Array.IndexOf(buffer, (byte)0);
		if (nullIndex == 0) return string.Empty;

		int length = nullIndex > 0 ? nullIndex : buffer.Length;
		return Encoding.UTF8.GetString(buffer, 0, length);
	}

	public MemoryReader(int processId) {
		process = Process.GetProcessById(processId);
		handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, processId);

		if (handle == IntPtr.Zero) {
			throw new Exception("OpenProcess failed. Win32Error = " + Marshal.GetLastWin32Error());
		}
	}

	public IntPtr GetModuleBase(string moduleName) {
		string normalizedName = moduleName.ToUpperInvariant();
		if (moduleBases.TryGetValue((process.Id, normalizedName), out IntPtr cachedBase) && cachedBase != IntPtr.Zero) return cachedBase;

		process.Refresh();
		foreach (ProcessModule module in process.Modules) {
			if (module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase)) {
				moduleBases[(process.Id, normalizedName)] = module.BaseAddress;
				return module.BaseAddress;
			}
		}
		throw new Exception($"Không tìm thấy module: {moduleName}");
	}

	public byte[] ReadMemory(IntPtr address, int size) {
		if (address == IntPtr.Zero) return Array.Empty<byte>();

		byte[] buffer = new byte[size];
		bool ok = ReadProcessMemory(handle, address, buffer, size, out IntPtr bytesRead);

		if (!ok || bytesRead.ToInt32() != size) {
			return Array.Empty<byte>();
		}

		return buffer;
	}

	public void Dispose() {
		if (handle != IntPtr.Zero) {
			CloseHandle(handle);
		}
		process.Dispose();
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int size, out IntPtr lpNumberOfBytesRead);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr hObject);
}
