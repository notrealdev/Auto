namespace Auto.Utils;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class GameMapReader {
	public static GameMapInfo Read(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			Dictionary<int, int> source = new() {
				[GameAddresses.Globals.MapId] = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.MapId)),
				[GameAddresses.Globals.MapIdMirror] = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.MapIdMirror)),
				[GameAddresses.Globals.MapIdRuntimeMirror] = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.MapIdRuntimeMirror))
			};
			int mapId = source[GameAddresses.Globals.MapId];
			int agreement = source.Values.Count(value => value == mapId);
			if (mapId <= 0 || agreement != source.Count) return GameMapInfo.Fail($"Ba bản sao Map ID không hợp lệ hoặc không đồng thuận | Values={string.Join(',', source.Select(pair => $"Game.exe+0x{pair.Key:X}=Map{pair.Value}"))}", source);

			string mapPath = Path.Combine(AppContext.BaseDirectory, "Data", "Maps", $"{mapId}.map");
			if (! File.Exists(mapPath)) return GameMapInfo.Fail($"Không tìm thấy file map | MapId={mapId} Path={mapPath}", source, mapId);

			string? doctorLine = File.ReadLines(mapPath).FirstOrDefault(line => line.StartsWith("Dai Phu=", StringComparison.OrdinalIgnoreCase));
			if (doctorLine == null) return GameMapInfo.SuccessWithoutDoctor(mapId, mapPath, source, agreement, "File map không khai báo Dai Phu.");
			string coordinate = doctorLine[(doctorLine.IndexOf('=') + 1)..].Trim();
			string[] parts = coordinate.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 2 || ! int.TryParse(parts[0], out int doctorRawX) || ! int.TryParse(parts[1], out int doctorRawY)) return GameMapInfo.SuccessWithoutDoctor(mapId, mapPath, source, agreement, "Tọa độ Dai Phu trống hoặc không hợp lệ.");

			return GameMapInfo.SuccessWithDoctor(mapId, mapPath, source, agreement, doctorRawX, doctorRawY);
		} catch (Exception ex) {
			return GameMapInfo.Fail(ex.Message, new Dictionary<int, int>());
		}
	}

	public static IReadOnlyList<GameMapNpc> ReadNpcs(GameMapInfo map) {
		if (!map.Success || string.IsNullOrWhiteSpace(map.MapPath) || !File.Exists(map.MapPath)) return Array.Empty<GameMapNpc>();
		List<GameMapNpc> npcs = new();
		foreach (string line in File.ReadLines(map.MapPath).SkipWhile(value => !value.Trim().Equals("[NPC]", StringComparison.OrdinalIgnoreCase)).Skip(1)) {
			int separator = line.IndexOf('=');
			if (separator <= 0) continue;
			string name = line[..separator].Trim();
			if (int.TryParse(name, out _)) continue;
			string[] parts = line[(separator + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2 || !int.TryParse(parts[0], out int rawX) || !int.TryParse(parts[1], out int rawY) || rawX <= 0 || rawY <= 0) continue;
			npcs.Add(new GameMapNpc(name, rawX, rawY));
		}
		return npcs;
	}

}

public sealed record GameMapNpc(string Name, int RawX, int RawY);

public sealed record GameMapInfo(bool Success, int MapId, string MapPath, bool HasDoctor, int DoctorRawX, int DoctorRawY, int MirrorAgreement, IReadOnlyDictionary<int, int> Mirrors, string FailureReason) {
	public static GameMapInfo Fail(string reason, IReadOnlyDictionary<int, int> mirrors, int mapId = 0) {
		return new GameMapInfo(false, mapId, "", false, 0, 0, 0, mirrors, reason);
	}

	public static GameMapInfo SuccessWithoutDoctor(int mapId, string mapPath, IReadOnlyDictionary<int, int> mirrors, int agreement, string reason) {
		return new GameMapInfo(true, mapId, mapPath, false, 0, 0, agreement, mirrors, reason);
	}

	public static GameMapInfo SuccessWithDoctor(int mapId, string mapPath, IReadOnlyDictionary<int, int> mirrors, int agreement, int doctorRawX, int doctorRawY) {
		return new GameMapInfo(true, mapId, mapPath, true, doctorRawX, doctorRawY, agreement, mirrors, "");
	}
}
