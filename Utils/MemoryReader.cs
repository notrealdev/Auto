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
	// Buffer dùng lại cho các lần đọc số nguyên, KHÔNG bao giờ trả ra ngoài.
	//
	// Vì sao cần: AutoFsEntityScanner quét 510 entity mỗi lượt và mỗi trường là một ReadInt32 riêng, nên bản cũ
	// (new byte[4] mỗi lần) sinh hàng trăm nghìn mảng rác mỗi giây khi chạy nhiều account.
	//
	// RÀNG BUỘC: một MemoryReader chỉ được DÙNG TRÊN MỘT LUỒNG. Đúng với mọi nơi gọi hiện nay — worker luồng Đánh
	// giữ riêng một instance (Attack/Engine.cs), các chỗ còn lại đều "using" cục bộ trong một hàm.
	private readonly byte[] scalarBuffer = new byte[8];
	public int ProcessId => process.Id;

	public byte[] ReadBytes(IntPtr address, int size) {
		return ReadMemory(address, size);
	}

	public ushort ReadUInt16(IntPtr address) {
		return TryReadScalar(address, 2) ? BitConverter.ToUInt16(scalarBuffer, 0) : (ushort)0;
	}

	public float ReadFloat(IntPtr address) {
		return TryReadScalar(address, 4) ? BitConverter.ToSingle(scalarBuffer, 0) : 0f;
	}

	public int ReadInt32(IntPtr address) {
		return TryReadScalar(address, 4) ? BitConverter.ToInt32(scalarBuffer, 0) : 0;
	}

	// Đọc hai số nguyên 32-bit NẰM LIỀN NHAU bằng một lần gọi ReadProcessMemory thay vì hai.
	// Chỉ dùng khi hai offset chắc chắn liền kề (kiểm lại ở Utils/GameAddresses.cs trước khi gọi).
	public bool TryReadInt32Pair(IntPtr address, out int first, out int second) {
		if (TryReadScalar(address, 8)) {
			first = BitConverter.ToInt32(scalarBuffer, 0);
			second = BitConverter.ToInt32(scalarBuffer, 4);
			return true;
		}
		first = 0;
		second = 0;
		return false;
	}

	private bool TryReadScalar(IntPtr address, int size) {
		if (address == IntPtr.Zero) return false;
		return ReadProcessMemory(handle, address, scalarBuffer, size, out IntPtr bytesRead) && bytesRead.ToInt32() == size;
	}

	// Đọc vào buffer do nơi gọi cấp, không cấp phát gì.
	//
	// Dùng để gom nhiều trường của cùng một bản ghi vào MỘT lần gọi ReadProcessMemory. Đo ngày 2026-09-14: đọc 4 byte
	// tốn 0,58 us còn đọc 456 byte cũng chỉ 0,29 us — chi phí nằm ở lần chuyển vào kernel chứ gần như không phụ thuộc
	// số byte, nên gộp hai lần đọc nhỏ thành một lần đọc lớn là lãi thẳng.
	public bool ReadInto(IntPtr address, byte[] buffer, int size) {
		if (address == IntPtr.Zero || buffer.Length < size) return false;
		return ReadProcessMemory(handle, address, buffer, size, out IntPtr bytesRead) && bytesRead.ToInt32() == size;
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
