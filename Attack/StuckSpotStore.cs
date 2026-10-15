namespace Auto.Attack;

using System.Globalization;
using System.IO;
using Auto.Runtime;

// Monster spots given up as unreachable (a wall stands in the way), per map, shared by every account through config.ini
// next to the exe (owner 2026-10-14). Without it each account found the same wall again: MaiAnhNhe was stuck at one spot
// (monster ~62441/90296, Map 32) at 23:37, 23:49, 23:58 and 00:00 on 2026-10-13, and ZALO at the same spot at 23:46 and
// 23:49, because the 20 s memory was per monster Index and a respawned monster gets a new one.
// Format: one section per map, one "x,y=time" line per spot. Deleting a line forgets the spot; the file is re-read when
// it changes, so it can be edited while Auto runs.
internal static class StuckSpotStore {
	private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "config.ini");
	private const string SectionPrefix = "StuckSpots_Map";
	// A spot within this distance of a stored one is the same spot (one X cell).
	public const int SpotRadiusRaw = 256;
	private static readonly TimeSpan ReloadCheckInterval = TimeSpan.FromSeconds(5);

	private static readonly object SyncRoot = new();
	private static Dictionary<int, List<(int X, int Y)>> spotsByMap = new();
	private static DateTime loadedWriteTimeUtc = DateTime.MinValue;
	private static DateTime lastReloadCheckUtc = DateTime.MinValue;

	public static bool IsNearStoredSpot(int mapId, int rawX, int rawY) {
		if (mapId <= 0) return false;
		lock (SyncRoot) {
			ReloadIfChanged();
			return spotsByMap.TryGetValue(mapId, out List<(int X, int Y)>? spots)
				&& spots.Any(spot => EliteAvoidance.Distance(rawX, rawY, spot.X, spot.Y) <= SpotRadiusRaw);
		}
	}

	// false = already stored (or no map), nothing written.
	public static bool Add(int mapId, int rawX, int rawY) {
		if (mapId <= 0) return false;
		lock (SyncRoot) {
			lastReloadCheckUtc = DateTime.MinValue;
			ReloadIfChanged();
			if (! spotsByMap.TryGetValue(mapId, out List<(int X, int Y)>? spots)) spotsByMap[mapId] = spots = [];
			if (spots.Any(spot => EliteAvoidance.Distance(rawX, rawY, spot.X, spot.Y) <= SpotRadiusRaw)) return false;
			spots.Add((rawX, rawY));
			try {
				List<string> lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath).ToList() : [];
				string header = $"[{SectionPrefix}{mapId}]";
				string entry = $"{rawX},{rawY}={DateTime.Now:yyyy-MM-dd HH:mm:ss}";
				int headerIndex = lines.FindIndex(line => line.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
				if (headerIndex < 0) {
					if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
					lines.Add(header);
					lines.Add(entry);
				} else {
					int insertAt = headerIndex + 1;
					while (insertAt < lines.Count && ! lines[insertAt].TrimStart().StartsWith('[')) insertAt++;
					while (insertAt > headerIndex + 1 && lines[insertAt - 1].Trim().Length == 0) insertAt--;
					lines.Insert(insertAt, entry);
				}
				string temporaryPath = FilePath + ".tmp";
				File.WriteAllLines(temporaryPath, lines);
				File.Move(temporaryPath, FilePath, true);
				loadedWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);
			} catch (Exception ex) {
				DebugLog.AddProfileEvent($"STUCK_SPOT_SAVE_HỎNG | {ex.GetType().Name}: {ex.Message}");
			}
			return true;
		}
	}

	private static void ReloadIfChanged() {
		DateTime now = DateTime.UtcNow;
		if (now - lastReloadCheckUtc < ReloadCheckInterval) return;
		lastReloadCheckUtc = now;
		try {
			DateTime writeTimeUtc = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
			if (writeTimeUtc == loadedWriteTimeUtc) return;
			loadedWriteTimeUtc = writeTimeUtc;
			Dictionary<int, List<(int X, int Y)>> loaded = new();
			if (writeTimeUtc != DateTime.MinValue) {
				List<(int X, int Y)>? current = null;
				foreach (string rawLine in File.ReadAllLines(FilePath)) {
					string line = rawLine.Trim();
					if (line.Length == 0 || line.StartsWith(';')) continue;
					if (line.StartsWith('[')) {
						string name = line.Trim('[', ']');
						current = name.StartsWith(SectionPrefix, StringComparison.OrdinalIgnoreCase)
							&& int.TryParse(name[SectionPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId)
							? (loaded.TryGetValue(mapId, out List<(int X, int Y)>? existing) ? existing : loaded[mapId] = [])
							: null;
						continue;
					}
					if (current == null) continue;
					string[] coordinates = line.Split('=')[0].Split(',');
					if (coordinates.Length == 2
						&& int.TryParse(coordinates[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
						&& int.TryParse(coordinates[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) current.Add((x, y));
				}
			}
			spotsByMap = loaded;
		} catch (Exception ex) {
			DebugLog.AddProfileEvent($"STUCK_SPOT_LOAD_HỎNG | {ex.GetType().Name}: {ex.Message}");
		}
	}
}
