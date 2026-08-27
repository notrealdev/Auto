using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Collections.Concurrent;
using Auto.Runtime;

namespace Auto.Utils;

public sealed class GameMemory : IDisposable {
	private const int PROCESS_VM_READ = 0x0010;
	private const int PROCESS_QUERY_INFORMATION = 0x0400;

	private readonly Process process;
	private IntPtr processHandle;
	private static readonly ConcurrentDictionary<(int ProcessId, string ModuleName), IntPtr> moduleBases = new();

	public string DebugScanAroundPlayer() {
		return DebugAll();
	}

	public static string DebugScanAroundPlayer(int processId) {
		try {
			using GameMemory memory = new GameMemory(processId);
			return memory.DebugScanAroundPlayer();
		} catch (Exception ex) {
			return ex.ToString();
		}
	}

	public static bool DumpPlayerStructure(int processId, string fileName) {
		using GameMemory memory = new GameMemory(processId);
		return memory.DumpPlayerStructure(fileName);
	}

	public GameMemory(int processId) {
		process = Process.GetProcessById(processId);
	}

	public static GameSnapshot ReadSnapshot(int processId) {
		try {
			using GameMemory memory = new GameMemory(processId);
			return memory.ReadSnapshot();
		} catch (Exception ex) {
			return CreateFail(SnapshotStatus.Exception, ex.Message);
		}
	}

	public static string Debug(int processId) {
		try {
			using GameMemory memory = new GameMemory(processId);
			return memory.DebugAll();
		} catch (Exception ex) {
			return ex.ToString();
		}
	}

	public static string ReadCharacterName(int processId) {
		GameSnapshot snapshot = ReadSnapshot(processId);
		return snapshot.Success ? snapshot.CharacterName : "";
	}

	public static int ReadLevel(int processId) {
		GameSnapshot snapshot = ReadSnapshot(processId);
		return snapshot.Success ? snapshot.Level : 0;
	}

	public static int ReadHp(int processId) {
		GameSnapshot snapshot = ReadSnapshot(processId);
		return snapshot.Success ? snapshot.Hp : -1;
	}

	public static int ReadMaxHp(int processId) {
		GameSnapshot snapshot = ReadSnapshot(processId);
		return snapshot.Success ? snapshot.MaxHp : 0;
	}

	public static int ReadMp(int processId) {
		GameSnapshot snapshot = ReadSnapshot(processId);
		return snapshot.Success ? snapshot.Mp : -1;
	}

	public static int ReadMaxMp(int processId) {
		GameSnapshot snapshot = ReadSnapshot(processId);
		return snapshot.Success ? snapshot.MaxMp : 0;
	}

	public bool OpenProcess() {
		if (process == null || process.HasExited) {
			return false;
		}

		if (processHandle != IntPtr.Zero) {
			return true;
		}

		processHandle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, process.Id);

		if (processHandle == IntPtr.Zero) {
			int error = Marshal.GetLastWin32Error();

			throw new Exception(
				$"OpenProcess failed.\r\n" +
				$"PID = {process.Id}\r\n" +
				$"Process = {process.ProcessName}\r\n" +
				$"Win32Error = {error}");
		}

		return true;
	}

	public GameSnapshot ReadSnapshot() {
		if (!OpenProcess()) {
			return CreateFail(SnapshotStatus.OpenProcessFailed, "OpenProcess failed.");
		}

		IntPtr moduleBase = GetModuleBase("Game.exe");

		if (moduleBase == IntPtr.Zero) {
			return CreateFail(SnapshotStatus.StatsPointerFailed, "Không tìm thấy module Game.exe.");
		}

		RuntimeLayout layout = RuntimeLayoutResolver.Resolve(process.Id);
		if (! layout.PlayerReady) return CreateFail(SnapshotStatus.StatsPointerFailed, layout.Get(RuntimeSubsystem.Player).FailureReason);

		IntPtr playerDataBase = ResolvePlayerDataBase(moduleBase, layout);

		if (playerDataBase == IntPtr.Zero) {
			return CreateFail(SnapshotStatus.StatsPointerFailed, "ResolvePlayerDataBase() failed.");
		}

		IntPtr nameAddress = IntPtr.Add(playerDataBase, layout.NameOffset);
		byte[] playerData = ReadMemory(playerDataBase, layout.RawYOffset + 4);
		if (playerData.Length < layout.RawYOffset + 4) return CreateFail(SnapshotStatus.StatsPointerFailed, $"Không đọc được player data block. PlayerDataBase={playerDataBase:X8}");

		string characterName = ReadAsciiString(playerData, layout.NameOffset, 32);
		int level = ReadInt32(playerData, layout.LevelOffset);
		int hp = ReadInt32(playerData, layout.HpOffset);
		int maxHp = ReadInt32(playerData, layout.MaxHpOffset);
		int mp = ReadInt32(playerData, layout.MpOffset);
		int maxMp = ReadInt32(playerData, layout.MaxMpOffset);
		int x = ReadInt32(playerData, layout.RawXOffset);
		int y = ReadInt32(playerData, layout.RawYOffset);

		if (string.IsNullOrWhiteSpace(characterName)) {
			return CreateFail(SnapshotStatus.NameReadFailed, $"Character name is empty. NameAddress={nameAddress:X8}");
		}

		if (level <= 0) {
			return CreateFail(SnapshotStatus.InvalidLevel, $"Level={level} LevelAddress={IntPtr.Add(playerDataBase, layout.LevelOffset):X8}");
		}

		if (maxHp <= 0 || maxMp <= 0 || hp < 0 || mp < 0) {
			return CreateFail(SnapshotStatus.InvalidStats, $"HP={hp} MaxHP={maxHp} MP={mp} MaxMP={maxMp} PlayerDataBase={playerDataBase:X8}");
		}

		return new GameSnapshot {
			Status = SnapshotStatus.Success,
			CharacterName = characterName,
			Level = level,
			Hp = hp,
			MaxHp = maxHp,
			Mp = mp,
			MaxMp = maxMp,
			ProcessId = process.Id,
			X = x,
			Y = y,
			MoveTargetX = 0,
			MoveTargetY = 0,
			Combat = new CombatSnapshot()
		};
	}

	public string DebugAll() {
		GameSnapshot snapshot = ReadSnapshot();
		RuntimeLayout layout = RuntimeLayoutResolver.Resolve(process.Id);
		return $"Process={process.ProcessName} | PID={process.Id}\r\nFingerprint={layout.Fingerprint}\r\nPlayerReady={layout.PlayerReady} | Evidence={layout.Get(RuntimeSubsystem.Player).Evidence} | Failure={layout.Get(RuntimeSubsystem.Player).FailureReason}\r\nSnapshot={snapshot.Success}/{snapshot.Status} | Reason={snapshot.FailReason}\r\nName={snapshot.CharacterName} | Level={snapshot.Level} | HP={snapshot.Hp}/{snapshot.MaxHp} | MP={snapshot.Mp}/{snapshot.MaxMp} | Raw={snapshot.X}/{snapshot.Y}";
	}

	private IntPtr ResolvePlayerDataBase(IntPtr moduleBase, RuntimeLayout layout) {
		IntPtr tableBase = ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
		return tableBase == IntPtr.Zero ? IntPtr.Zero : IntPtr.Add(tableBase, layout.PlayerRecordOffset);
	}

	private static GameSnapshot CreateFail(SnapshotStatus status, string reason) {
		return new GameSnapshot {
			Status = status,
			FailReason = reason
		};
	}

	private IntPtr ReadPointer32(IntPtr address) {
		byte[] buffer = ReadMemory(address, 4);

		if (buffer.Length != 4) {
			return IntPtr.Zero;
		}

		return new IntPtr(BitConverter.ToInt32(buffer, 0));
	}

	private int ReadInt32(IntPtr address) {
		byte[] buffer = ReadMemory(address, 4);

		if (buffer.Length != 4) {
			return 0;
		}

		return BitConverter.ToInt32(buffer, 0);
	}

	private string ReadAsciiString(IntPtr address, int maxLength) {
		byte[] buffer = ReadMemory(address, maxLength);

		if (buffer.Length == 0) {
			return "";
		}

		int length = Array.IndexOf(buffer, (byte)0);

		if (length < 0) {
			length = buffer.Length;
		}

		return LegacyVietnameseText.Decode(buffer[..length]).Trim();
	}

	private static int ReadInt32(byte[] buffer, int offset) {
		return offset >= 0 && offset + 4 <= buffer.Length ? BitConverter.ToInt32(buffer, offset) : 0;
	}

	private static string ReadAsciiString(byte[] buffer, int offset, int maxLength) {
		if (offset < 0 || offset >= buffer.Length || maxLength <= 0) return "";
		int length = 0;
		while (length < maxLength && offset + length < buffer.Length && buffer[offset + length] != 0) length++;
		return LegacyVietnameseText.Decode(buffer.AsSpan(offset, length).ToArray()).Trim();
	}

	private byte[] ReadMemory(IntPtr address, int size) {
		byte[] buffer = new byte[size];
		bool ok = ReadProcessMemory(processHandle, address, buffer, size, out IntPtr bytesRead);

		if (!ok || bytesRead.ToInt32() != size) {
			return Array.Empty<byte>();
		}

		return buffer;
	}

	private IntPtr GetModuleBase(string moduleName) {
		string normalizedName = moduleName.ToUpperInvariant();
		if (moduleBases.TryGetValue((process.Id, normalizedName), out IntPtr cachedBase) && cachedBase != IntPtr.Zero) return cachedBase;

		foreach (ProcessModule module in process.Modules) {
			if (string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase)) {
				moduleBases[(process.Id, normalizedName)] = module.BaseAddress;
				return module.BaseAddress;
			}
		}

		if (process.MainModule != null) {
			moduleBases[(process.Id, normalizedName)] = process.MainModule.BaseAddress;
			return process.MainModule.BaseAddress;
		}

		return IntPtr.Zero;
	}

	public void Dispose() {
		if (processHandle != IntPtr.Zero) {
			CloseHandle(processHandle);
			processHandle = IntPtr.Zero;
		}

		process.Dispose();
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr hObject);

	public bool DumpPlayerStructure(string fileName, int size = 0x4000) {
		if (!OpenProcess()) {
			return false;
		}

		IntPtr moduleBase = GetModuleBase("Game.exe");

		if (moduleBase == IntPtr.Zero) {
			return false;
		}

		RuntimeLayout layout = RuntimeLayoutResolver.Resolve(process.Id);
		if (! layout.PlayerReady) return false;
		IntPtr playerDataBase = ResolvePlayerDataBase(moduleBase, layout);

		if (playerDataBase == IntPtr.Zero) {
			return false;
		}

		byte[] data = ReadMemory(playerDataBase, size);

		if (data.Length != size) {
			return false;
		}

		string folder = Path.Combine(
			AppDomain.CurrentDomain.BaseDirectory,
			"Dumps"
		);

		Directory.CreateDirectory(folder);

		string path = Path.Combine(folder, fileName);

		File.WriteAllBytes(path, data);

		return true;
	}
}
