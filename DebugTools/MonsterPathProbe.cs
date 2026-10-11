namespace Auto.DebugTools;

using System.Collections.Concurrent;
using Auto.Attack;
using Auto.Movement;
using Auto.Runtime;
using Auto.Utils;

// Question from the owner (2026-10-09): can the character reach a monster behind a wall the way the flagged route move does
// (game pathfinding, goes around terrain) WITHOUT the minimap flag? Today the attack command 300 runs straight into the
// wall and spins.
//
// One run = pick the nearest live monster, walk to its live position with ONE movement primitive, log until arrival or
// timeout. No attack command is sent. Run it once per mode with a wall between the character and the monster:
//   Pathfind: commands 32/0/5 -> CoordinateOpcode 0x9F, the flagged move of Tự lên bãi and Sửa/Bán (reference).
//   Walk:     commands 0/321 -> PickupMovementFunction mode 3, the flagless move of the loot flow (the candidate).
// Lines go to map-transition.log with the MONSTER_PATH_ prefix.
//
// Runs only while the account's master switch is off; AccountEngineCoordinator opens the action gate while IsRunning.
public static class MonsterPathProbe {
	// RouteNoFlag (test): native 331, the same client navigator as Pathfind but with the arguments of the client's own
	// flagless calls (1, 0, 0, 0). See GameClientAddresses.h NavigatorRouteRva.
	public enum Mode { Pathfind, Walk, RouteNoFlag }

	private const int TickMilliseconds = 100;
	private const int LogIntervalMilliseconds = 500;
	private const int TimeoutSeconds = 30;
	private const double ArrivalCells = 1.0;
	// Re-send only when the monster moved this far from the commanded point, or the character stood still for
	// StandStillMilliseconds. The first beta run re-sent every second; each send starts with command 32 (route reset), so the
	// flagged route was cancelled and recomputed every second and the character walked in jerks (owner, 2026-10-09 12:31).
	private const double TargetMovedCells = 2.0;
	private const int StandStillMilliseconds = 1000;
	private const double MaximumPickCells = 40;
	private static readonly ConcurrentDictionary<int, CancellationTokenSource> runs = new();

	public static bool IsRunning(int processId) => runs.ContainsKey(processId);

	public static string Start(GameWindow game, Mode mode) {
		int processId = game.ProcessId;
		if (game.Enabled) return $"PID={processId} | Phải TẮT Auto tổng của account này trước, để luồng đánh không chen lệnh 300 vào.";
		// Same reason as KhonTienRouteProbe: with the master switch off the coordinator never resolves the layout.
		lock (game.AutoSync) game.RuntimeLayout = RuntimeLayoutResolver.Resolve(processId);
		if (! game.RuntimeLayout.MovementReady) return $"PID={processId} | Layout di chuyển chưa sẵn sàng: {game.RuntimeLayout.DescribeUnavailable(RuntimeSubsystem.MovementTransport)}";
		GameSnapshot snapshot = GameMemory.ReadSnapshot(processId);
		if (! snapshot.Success) return $"PID={processId} | Không đọc được nhân vật: {snapshot.FailReason}";
		if (! TryPickNearestMonster(processId, snapshot.X, snapshot.Y, out MonsterReading monster, out string pickFailure)) return $"PID={processId} | {pickFailure}";
		CancellationTokenSource cancellation = new();
		if (! runs.TryAdd(processId, cancellation)) {
			cancellation.Dispose();
			return $"PID={processId} | Đang chạy sẵn, bấm Chạy lần nữa để dừng.";
		}
		string start = $"MONSTER_PATH_START | PID={processId} | Mode={mode} | Quái=#{monster.Index} '{monster.Name}' Raw={monster.RawX}/{monster.RawY} HP={monster.Hp} | NhânVật={snapshot.X}/{snapshot.Y} | Cách={Cells(snapshot.X, snapshot.Y, monster.RawX, monster.RawY):F1}ô";
		DebugLog.AddMapTransition(start);
		Task.Run(() => Run(game, mode, monster, cancellation.Token));
		return $"PID={processId} | {start}";
	}

	public static string Stop(int processId) {
		if (! runs.TryRemove(processId, out CancellationTokenSource? cancellation)) return $"PID={processId} | Không có lượt nào đang chạy.";
		cancellation.Cancel();
		DebugLog.AddMapTransition($"MONSTER_PATH_STOP | PID={processId} | Dừng tay");
		return $"PID={processId} | Đã dừng.";
	}

	private static void Run(GameWindow game, Mode mode, MonsterReading monster, CancellationToken token) {
		int processId = game.ProcessId;
		DateTime startedUtc = DateTime.UtcNow;
		DateTime nextLogUtc = DateTime.MinValue;
		(int X, int Y) commanded = (0, 0);
		(int X, int Y) lastPlayer = (0, 0);
		DateTime lastMoveUtc = startedUtc;
		double bestCells = double.MaxValue;
		DateTime bestUtc = startedUtc;
		int sends = 0;
		string outcome = "";
		try {
			while (! token.IsCancellationRequested) {
				if (game.Enabled) {
					outcome = "Auto tổng vừa được bật, dừng";
					break;
				}
				double elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
				if (elapsed > TimeoutSeconds) {
					outcome = $"HẾT_GIỜ {TimeoutSeconds}s | GầnNhất={bestCells:F1}ô lúc +{(bestUtc - startedUtc).TotalSeconds:F1}s | SốLệnh={sends}";
					break;
				}
				GameSnapshot snapshot = GameMemory.ReadSnapshot(processId);
				if (! TryReadMonster(processId, monster.Index, out MonsterReading current) || current.Name != monster.Name || current.Hp <= 0) {
					outcome = $"QUÁI_MẤT_HOẶC_CHẾT | +{elapsed:F1}s | GầnNhất={bestCells:F1}ô";
					break;
				}
				if (! snapshot.Success) {
					Task.Delay(TickMilliseconds, token).Wait(token);
					continue;
				}
				double cells = Cells(snapshot.X, snapshot.Y, current.RawX, current.RawY);
				if (cells < bestCells - 0.1) {
					bestCells = cells;
					bestUtc = DateTime.UtcNow;
				}
				if (cells <= ArrivalCells) {
					outcome = $"TỚI_NƠI | +{elapsed:F1}s | Cách={cells:F2}ô | SốLệnh={sends} | NhânVật={snapshot.X}/{snapshot.Y} | Quái={current.RawX}/{current.RawY}";
					break;
				}
				if (snapshot.X != lastPlayer.X || snapshot.Y != lastPlayer.Y) {
					lastPlayer = (snapshot.X, snapshot.Y);
					lastMoveUtc = DateTime.UtcNow;
				}
				bool targetMoved = Cells(commanded.X, commanded.Y, current.RawX, current.RawY) > TargetMovedCells;
				bool standingStill = DateTime.UtcNow - lastMoveUtc >= TimeSpan.FromMilliseconds(StandStillMilliseconds);
				if (sends == 0 || targetMoved || standingStill) {
					string result;
					bool sent;
					lock (game.AutoSync) {
						sent = mode switch {
							Mode.Pathfind => AutoFsMovementCommand.TryMoveTo(game, current.RawX, current.RawY, out result),
							Mode.RouteNoFlag => AutoFsMovementCommand.TryRouteWithoutFlag(game, current.RawX, current.RawY, out result),
							_ => AutoFsMovementCommand.TryWalkTo(game, current.RawX, current.RawY, out result)
						};
					}
					// Give the new command a full stand-still window before judging it.
					lastMoveUtc = DateTime.UtcNow;
					if (sent) {
						sends++;
						commanded = (current.RawX, current.RawY);
					} else {
						DebugLog.AddMapTransition($"MONSTER_PATH_SEND_FAIL | PID={processId} | Mode={mode} | {result}");
					}
				}
				if (DateTime.UtcNow >= nextLogUtc) {
					nextLogUtc = DateTime.UtcNow.AddMilliseconds(LogIntervalMilliseconds);
					DebugLog.AddMapTransition($"MONSTER_PATH_STEP | PID={processId} | Mode={mode} | +{elapsed:F1}s | NhânVật={snapshot.X}/{snapshot.Y} | Quái={current.RawX}/{current.RawY} | Cách={cells:F2}ô | GầnNhất={bestCells:F2}ô | SốLệnh={sends}");
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
		if (outcome.Length > 0) DebugLog.AddMapTransition($"MONSTER_PATH_END | PID={processId} | Mode={mode} | Quái='{monster.Name}' | {outcome}");
	}

	private static bool TryPickNearestMonster(int processId, int playerX, int playerY, out MonsterReading nearest, out string failure) {
		nearest = default;
		failure = "";
		double best = double.MaxValue;
		for (int index = AutoFsClientProfile.FirstEntityIndex; index <= AutoFsClientProfile.LastEntityIndex; index++) {
			if (! TryReadMonster(processId, index, out MonsterReading monster) || monster.Hp <= 0) continue;
			double cells = Cells(playerX, playerY, monster.RawX, monster.RawY);
			if (cells > MaximumPickCells || cells >= best) continue;
			best = cells;
			nearest = monster;
		}
		if (best == double.MaxValue) failure = $"Không thấy quái còn sống nào trong {MaximumPickCells} ô.";
		return best != double.MaxValue;
	}

	private static bool TryReadMonster(int processId, int index, out MonsterReading monster) {
		monster = default;
		RuntimeLayout layout = RuntimeLayoutResolver.Resolve(processId);
		if (! layout.Get(RuntimeSubsystem.Entity).Available) return false;
		using MemoryReader reader = new(processId);
		IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
		IntPtr table = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
		if (table == IntPtr.Zero) return false;
		IntPtr entity = IntPtr.Add(table, index * layout.EntityStride);
		if (reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.EntityType)) != AutoFsClientProfile.MonsterType) return false;
		byte[] nameBytes = reader.ReadBytes(IntPtr.Add(entity, AutoFsClientProfile.Name), AutoFsClientProfile.MaximumNameLength);
		int length = Array.IndexOf(nameBytes, (byte)0);
		if (length <= 0) return false;
		int rawX = reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.RawX));
		int rawY = reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.RawY));
		if (rawX <= 0 || rawY <= 0) return false;
		monster = new MonsterReading(index, LegacyVietnameseText.Decode(nameBytes.AsSpan(0, length).ToArray()), rawX, rawY, reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.Hp)));
		return true;
	}

	// Game cell scale: X divided by 256, Y by 512.
	private static double Cells(int firstX, int firstY, int secondX, int secondY) {
		double deltaX = (firstX - (double)secondX) / 256.0;
		double deltaY = (firstY - (double)secondY) / 512.0;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	private readonly record struct MonsterReading(int Index, string Name, int RawX, int RawY, int Hp);
}
