namespace Auto.DebugTools;

using System.Collections.Concurrent;
using Auto.Movement;
using Auto.Runtime;
using Auto.Utils;

// Walks Diêu Trì (52) -> Khổn Tiên cung Tầng 1..5 (47-51) with the SAME queue the Thám quân quest uses
// (AutoFsTrainingOrderQueue + Data\Maps portal lines), to check the portal lines recorded on 2026-10-09 before a real quest
// is sent there. Destination is the Tầng 5 doctor point, the farthest leg of the chain.
//
// Runs only while the account's master switch is off, so no other engine sends commands during the test.
// AccountEngineCoordinator opens the action gate while IsWalking is true.
// Every step goes to map-transition.log: route lines are KHONTIEN_ROUTE_*.
public static class KhonTienRouteProbe {
	private const int DestinationMapId = 51;
	private const int TickMilliseconds = 100;
	private const int TimeoutMinutes = 6;
	private static readonly int[] StartMapIds = [52, 47, 48, 49, 50];
	private static readonly ConcurrentDictionary<int, CancellationTokenSource> runs = new();

	public static bool IsWalking(int processId) => runs.ContainsKey(processId);

	public static string Start(GameWindow game) {
		int processId = game.ProcessId;
		if (game.Enabled) return $"PID={processId} | Phải TẮT Auto tổng của account này trước, để không engine nào chen lệnh vào.";
		// The coordinator resolves the runtime layout only while the master switch is on (AccountEngineCoordinator, line
		// "RuntimeLayout layout = masterEnabled || ..."), so with it off game.RuntimeLayout is still empty and every move
		// fails with "Runtime movement layout is unavailable" (map-transition.log 2026-10-09 10:08:46). Resolve it here,
		// same as LootViewModel/AttackViewModel do.
		lock (game.AutoSync) game.RuntimeLayout = RuntimeLayoutResolver.Resolve(processId);
		if (! game.RuntimeLayout.MovementReady) return $"PID={processId} | Layout di chuyển chưa sẵn sàng: {game.RuntimeLayout.DescribeUnavailable(RuntimeSubsystem.MovementTransport)}";
		GameMapInfo map = GameMapReader.Read(processId);
		if (! map.Success) return $"PID={processId} | Không đọc được map: {map.FailureReason}";
		if (! StartMapIds.Contains(map.MapId)) return $"PID={processId} | Đang ở Map{map.MapId}. Phải đứng ở Diêu Trì (Map52) hoặc Khổn Tiên Tầng 1-4 (Map47-50).";
		if (! GameMapReader.TryReadDoctorPoint(DestinationMapId, out int targetRawX, out int targetRawY, out string failure)) return $"PID={processId} | Không có toạ độ Đại Phu Map{DestinationMapId}: {failure}";
		IReadOnlyList<GameMapTransition> route = GameMapRoutePlanner.FindRoute(map.MapId, DestinationMapId);
		if (route.Count == 0) return $"PID={processId} | Không có tuyến Map{map.MapId}->Map{DestinationMapId} trong Data\\Maps (bản Data\\Maps đang chạy chưa có dòng cổng mới?).";
		CancellationTokenSource cancellation = new();
		if (! runs.TryAdd(processId, cancellation)) {
			cancellation.Dispose();
			return $"PID={processId} | Đang chạy sẵn, bấm Chạy lần nữa để dừng.";
		}
		string plan = string.Join(" > ", route.Select(transition => $"{transition.FromMapId}->{transition.ToMapId} A={transition.Approach.RawX}/{transition.Approach.RawY} F={transition.Far.RawX}/{transition.Far.RawY}"));
		DebugLog.AddMapTransition($"KHONTIEN_ROUTE_START | PID={processId} | Từ=Map{map.MapId} | Đích=Map{DestinationMapId} Đại Phu {targetRawX}/{targetRawY} | Tuyến={plan}");
		Task.Run(() => Walk(game, targetRawX, targetRawY, cancellation.Token));
		return $"PID={processId} | Bắt đầu đi Map{map.MapId} -> Map{DestinationMapId} ({route.Count} chặng).\r\nTuyến: {plan}";
	}

	public static string Stop(int processId) {
		if (! runs.TryRemove(processId, out CancellationTokenSource? cancellation)) return $"PID={processId} | Không có lượt nào đang chạy.";
		cancellation.Cancel();
		DebugLog.AddMapTransition($"KHONTIEN_ROUTE_STOP | PID={processId} | Dừng tay");
		return $"PID={processId} | Đã dừng.";
	}

	private static void Walk(GameWindow game, int targetRawX, int targetRawY, CancellationToken token) {
		int processId = game.ProcessId;
		AutoFsTrainingOrderQueue queue = new();
		DateTime startedUtc = DateTime.UtcNow;
		DateTime legStartedUtc = startedUtc;
		int lastMapId = 0;
		string lastDetail = "";
		string outcome = "";
		try {
			while (! token.IsCancellationRequested) {
				if (game.Enabled) {
					outcome = "Auto tổng vừa được bật, dừng để không tranh lệnh";
					break;
				}
				if (DateTime.UtcNow - startedUtc > TimeSpan.FromMinutes(TimeoutMinutes)) {
					outcome = $"HẾT GIỜ {TimeoutMinutes} phút | Map={lastMapId} | Bước cuối={lastDetail}";
					break;
				}
				GameSnapshot snapshot = GameMemory.ReadSnapshot(processId);
				if (snapshot.Success) {
					GameMapInfo map = GameMapReader.Read(processId);
					if (map.Success && map.MapId != lastMapId) {
						if (lastMapId > 0) DebugLog.AddMapTransition($"KHONTIEN_ROUTE_MAP | PID={processId} | Map{lastMapId}->Map{map.MapId} | Chặng={(DateTime.UtcNow - legStartedUtc).TotalSeconds:F1}s | Tới=Raw {snapshot.X}/{snapshot.Y}");
						lastMapId = map.MapId;
						legStartedUtc = DateTime.UtcNow;
					}
					bool arrived;
					string detail;
					// The coordinator does not copy the snapshot into GameWindow while the master switch is off, and the
					// queue's stand-still check reads game.X/game.Y.
					lock (game.AutoSync) {
						game.X = snapshot.X;
						game.Y = snapshot.Y;
						arrived = queue.Tick(game, snapshot, DestinationMapId, targetRawX, targetRawY, out detail);
					}
					// Log only steps that differ, ignoring the repeated "Active" waits.
					if (detail != lastDetail && ! detail.StartsWith("Active")) DebugLog.AddMapTransition($"KHONTIEN_ROUTE_STEP | PID={processId} | Map={lastMapId} | Raw={snapshot.X}/{snapshot.Y} | {detail}");
					lastDetail = detail;
					if (arrived) {
						outcome = $"TỚI NƠI | Tổng={(DateTime.UtcNow - startedUtc).TotalSeconds:F1}s | {detail}";
						break;
					}
				}
				Task.Delay(TickMilliseconds, token).Wait(token);
			}
		} catch (OperationCanceledException) {
			return;
		} catch (Exception ex) {
			outcome = $"LỖI {ex.GetType().Name}: {ex.Message}";
		} finally {
			if (runs.TryRemove(processId, out CancellationTokenSource? cancellation)) cancellation.Dispose();
		}
		if (outcome.Length > 0) DebugLog.AddMapTransition($"KHONTIEN_ROUTE_END | PID={processId} | {outcome}");
	}
}
