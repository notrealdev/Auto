namespace Auto.DebugTools;

using System.Text;
using Auto.Attack;
using Auto.Runtime;
using Auto.Utils;

// Records how the player moves between maps while the owner walks by hand, to collect the entrance into Khổn Tiên cung
// (maps 47-51). Data\Maps\47-51.map hold only the doctor point and no map has a portal into them, so the route planner
// cannot reach them (Resource/GAME-CLIENT-REFERENCE.md, "Thám quân destination names").
//
// What one map change writes to map-transition.log, enough to add a "<toMap>=x,y,x,y" portal line to the source map:
//   - source map, destination map, last position on the source map and first position on the destination map;
//   - the walking trail of the last TrailSeconds on the source map;
//   - named non-monster entities near the player just before the change (NPC or gate object, with raw position);
//   - every popup/menu that opened on the way (an NPC teleport shows up here instead of a walk-in portal).
//
// Read-only: reads game memory, never sends a command and never writes to the game.
public static class MapTransitionRecorder {
	private const int PollMilliseconds = 200;
	private const int NearbyRefreshMilliseconds = 1000;
	private const int ArrivalLogDelayMilliseconds = 2000;
	private const int TrailSeconds = 15;
	private const double TrailStepCells = 0.5;
	private const double NearbyRadiusCells = 15;
	private const int MaximumNearbyEntities = 25;
	private static readonly object syncRoot = new();
	private static CancellationTokenSource? cancellation;
	private static int recordingProcessId;

	public static bool IsRunning {
		get {
			lock (syncRoot) return cancellation != null;
		}
	}

	public static string Start(GameWindow game) {
		lock (syncRoot) {
			if (cancellation != null) return $"Đang ghi sẵn cho PID={recordingProcessId}, bấm lại để dừng trước.";
			cancellation = new CancellationTokenSource();
			recordingProcessId = game.ProcessId;
			CancellationToken token = cancellation.Token;
			int processId = game.ProcessId;
			Task.Run(() => Record(processId, token), token);
		}
		DebugLog.AddMapTransition($"MAP_RECORD_START | PID={game.ProcessId} | Mode=READ_ONLY");
		return $"Bắt đầu ghi chuyển map cho PID={game.ProcessId}. Kết quả ghi vào map-transition.log.";
	}

	public static string Stop() {
		int processId;
		lock (syncRoot) {
			if (cancellation == null) return "Chưa ghi gì.";
			cancellation.Cancel();
			cancellation.Dispose();
			cancellation = null;
			processId = recordingProcessId;
			recordingProcessId = 0;
		}
		DebugLog.AddMapTransition($"MAP_RECORD_STOP | PID={processId}");
		return $"Đã dừng ghi chuyển map cho PID={processId}.";
	}

	private static void Record(int processId, CancellationToken token) {
		Queue<(DateTime Time, int MapId, int X, int Y)> trail = new();
		List<string> popupsSinceChange = [];
		string nearby = "";
		DateTime nextNearbyUtc = DateTime.MinValue;
		IntPtr lastModal = IntPtr.Zero;
		int lastMapId = 0;
		(int X, int Y) lastPosition = (0, 0);
		DateTime arrivalLogUtc = DateTime.MaxValue;
		while (! token.IsCancellationRequested) {
			try {
				GameMapInfo map = GameMapReader.Read(processId);
				GameSnapshot snapshot = GameMemory.ReadSnapshot(processId);
				if (map.Success && snapshot.Success && map.MapId > 0) {
					DateTime now = DateTime.Now;
					if (lastMapId > 0 && map.MapId != lastMapId) {
						DebugLog.AddMapTransition(DescribeChange(processId, lastMapId, map.MapId, lastPosition, (snapshot.X, snapshot.Y), trail, nearby, popupsSinceChange));
						trail.Clear();
						popupsSinceChange.Clear();
						nearby = "";
						nextNearbyUtc = DateTime.MinValue;
						arrivalLogUtc = DateTime.UtcNow.AddMilliseconds(ArrivalLogDelayMilliseconds);
					} else if (DateTime.UtcNow >= arrivalLogUtc) {
						// One extra line once the new map has loaded its entities: what stands at the arrival point.
						arrivalLogUtc = DateTime.MaxValue;
						DebugLog.AddMapTransition($"MAP_ARRIVED | PID={processId} | Map={map.MapId} {MapName(map.MapId)} | Raw={snapshot.X}/{snapshot.Y} | GầnĐó={ReadNearby(processId, snapshot.X, snapshot.Y)}");
					}
					lastMapId = map.MapId;
					lastPosition = (snapshot.X, snapshot.Y);
					AppendTrail(trail, now, map.MapId, snapshot.X, snapshot.Y);
					if (DateTime.UtcNow >= nextNearbyUtc) {
						nextNearbyUtc = DateTime.UtcNow.AddMilliseconds(NearbyRefreshMilliseconds);
						nearby = ReadNearby(processId, snapshot.X, snapshot.Y);
					}
				}
				IntPtr modal = ReadModal(processId);
				if (modal != IntPtr.Zero && modal != lastModal) {
					string popup = NpcMenuOptionCapture.Capture(processId, "MapTransition", requireMenuChange: false);
					popupsSinceChange.Add($"{DateTime.Now:HH:mm:ss.fff} Map{lastMapId} Raw={lastPosition.X}/{lastPosition.Y}");
					DebugLog.AddMapTransition($"MAP_POPUP | PID={processId} | Map={lastMapId} | Raw={lastPosition.X}/{lastPosition.Y} | Modal=0x{modal.ToInt64():X8}\r\n{popup}");
				}
				lastModal = modal;
			} catch (Exception ex) {
				DebugLog.AddMapTransition($"MAP_RECORD_READ_FAIL | PID={processId} | {ex.GetType().Name}: {ex.Message}");
			}
			try {
				Task.Delay(PollMilliseconds, token).Wait(token);
			} catch (OperationCanceledException) {
				break;
			}
		}
	}

	private static void AppendTrail(Queue<(DateTime Time, int MapId, int X, int Y)> trail, DateTime now, int mapId, int x, int y) {
		// Keep a point only when the player moved at least TrailStepCells, so standing still does not flood the trail.
		if (trail.Count == 0 || CellDistance(trail.Last().X, trail.Last().Y, x, y) >= TrailStepCells) trail.Enqueue((now, mapId, x, y));
		while (trail.Count > 0 && (now - trail.Peek().Time).TotalSeconds > TrailSeconds) trail.Dequeue();
	}

	private static string DescribeChange(int processId, int fromMapId, int toMapId, (int X, int Y) lastPosition, (int X, int Y) firstPosition, IEnumerable<(DateTime Time, int MapId, int X, int Y)> trail, string nearby, List<string> popups) {
		StringBuilder output = new();
		output.AppendLine($"MAP_CHANGE | PID={processId} | Map{fromMapId} {MapName(fromMapId)} -> Map{toMapId} {MapName(toMapId)}");
		output.AppendLine($"  CuốiMapCũ=Raw {lastPosition.X}/{lastPosition.Y} (ô {lastPosition.X / 256}/{lastPosition.Y / 512}) | ĐầuMapMới=Raw {firstPosition.X}/{firstPosition.Y} (ô {firstPosition.X / 256}/{firstPosition.Y / 512})");
		output.AppendLine($"  Vết {TrailSeconds}s cuối trên Map{fromMapId}: {string.Join(" > ", trail.Select(point => $"{point.Time:HH:mm:ss} {point.X}/{point.Y}"))}");
		output.AppendLine($"  Entity gần đó trước khi đổi map: {(nearby.Length == 0 ? "(không có)" : nearby)}");
		output.Append($"  Popup đã mở trên đường: {(popups.Count == 0 ? "(không có — đi thẳng qua cổng)" : string.Join(" | ", popups))}");
		return output.ToString();
	}

	// Named entities that are neither monsters nor players within NearbyRadiusCells, nearest first.
	private static string ReadNearby(int processId, int playerX, int playerY) {
		RuntimeLayout layout = RuntimeLayoutResolver.Resolve(processId);
		if (! layout.Get(RuntimeSubsystem.Entity).Available) return "ENTITY_UNAVAILABLE";
		using MemoryReader reader = new(processId);
		IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
		IntPtr table = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
		if (table == IntPtr.Zero) return "ENTITY_TABLE_NULL";
		List<(double Distance, string Text)> found = [];
		for (int index = AutoFsClientProfile.FirstEntityIndex; index <= AutoFsClientProfile.LastEntityIndex; index++) {
			IntPtr entity = IntPtr.Add(table, index * layout.EntityStride);
			int type = reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.EntityType));
			if (type == AutoFsClientProfile.MonsterType || type == AutoFsClientProfile.PlayerType) continue;
			byte[] nameBytes = reader.ReadBytes(IntPtr.Add(entity, AutoFsClientProfile.Name), AutoFsClientProfile.MaximumNameLength);
			int length = Array.IndexOf(nameBytes, (byte)0);
			if (length == 0) continue;
			int x = reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.RawX));
			int y = reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.RawY));
			double distance = x > 0 && y > 0 ? CellDistance(playerX, playerY, x, y) : double.MaxValue;
			if (distance > NearbyRadiusCells) continue;
			string name = LegacyVietnameseText.Decode(nameBytes.AsSpan(0, length < 0 ? nameBytes.Length : length).ToArray());
			found.Add((distance, $"#{index} '{name}' Type={type} Raw={x}/{y} Cách={distance:F1}ô"));
		}
		return string.Join(" ; ", found.OrderBy(entry => entry.Distance).Take(MaximumNearbyEntities).Select(entry => entry.Text));
	}

	private static IntPtr ReadModal(int processId) {
		using MemoryReader reader = new(processId);
		IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
		return reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState));
	}

	private static string MapName(int mapId) => GameMapCatalog.TryGetName(mapId, out string name) ? name : "?";

	// Game cell scale: X divided by 256, Y by 512.
	private static double CellDistance(int firstX, int firstY, int secondX, int secondY) {
		double deltaX = (firstX - (double)secondX) / 256.0;
		double deltaY = (firstY - (double)secondY) / 512.0;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}
}
