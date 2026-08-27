namespace Auto.Movement;

using Auto.Runtime;
using Auto.Attack;
using Auto.Utils;

public sealed class ConfiguredTrainingMovementAutomation {
	private readonly AutoFsTrainingOrderQueue orderQueue = new();
	private MovementState state;
	private TrainingDestination destination;
	private string destinationKey = "";
	private DateTime nextProgressLogUtc;

	public bool IsBusy => state == MovementState.Moving;

	public bool Tick(GameWindow game, GameSnapshot snapshot, bool manualInputActive, Action<string>? log) {
		if (!snapshot.Success) return IsBusy;
		GameMapInfo map = GameMapReader.Read(game.ProcessId);
		if (!map.Success) return IsBusy;
		if (!TryResolveDestination(game, map.MapId, out TrainingDestination configuredDestination)) {
			Reset();
			return false;
		}
		if (!string.Equals(destinationKey, configuredDestination.Key, StringComparison.Ordinal)) {
			Reset();
			destination = configuredDestination;
			destinationKey = configuredDestination.Key;
		}
		// Không kéo nhân vật về tâm khi vẫn đang đứng trong phạm vi bãi đã cấu hình.
		if (map.MapId == destination.MapId && IsInsideTrainingArea(game.AttackSettings, snapshot, destination)) {
			state = MovementState.Completed;
			orderQueue.Reset();
			return false;
		}
		if (state == MovementState.Completed && map.MapId == destination.MapId) {
			return false;
		}
		if (state != MovementState.Moving) Start(game, map.MapId, log);
		if (manualInputActive) {
			orderQueue.InvalidateActiveCommand();
			return true;
		}
		bool completed = orderQueue.Tick(game, snapshot, destination.MapId, destination.RawX, destination.RawY, out string detail);
		if (completed) {
			state = MovementState.Completed;
			log?.Invoke($"Lên bãi {destination.ModeName} AutoFS hoàn tất | PID={game.ProcessId} | {detail}");
			return false;
		}
		bool portalRecovery = detail.StartsWith("PortalRecovery20s=", StringComparison.Ordinal);
		if (portalRecovery || DateTime.UtcNow >= nextProgressLogUtc) {
			nextProgressLogUtc = DateTime.UtcNow.AddSeconds(10);
			log?.Invoke($"Lên bãi {destination.ModeName} AutoFS | PID={game.ProcessId} | {detail}");
		}
		return true;
	}

	public void Cancel(Action<string>? log = null, string reason = "Auto dừng") {
		if (IsBusy) log?.Invoke($"Lên bãi {destination.ModeName} dừng | {reason}.");
		Reset();
	}

	// Giữ nguyên đích đã cấu hình nhưng khởi tạo lại hàng đợi khi watchdog phát hiện đứng im.
	public void Recover(Action<string>? log, string reason) {
		if (!IsBusy) return;
		orderQueue.Reset();
		nextProgressLogUtc = DateTime.MinValue;
		log?.Invoke($"Lên bãi {destination.ModeName} phục hồi | {reason} | Đã khởi tạo lại toàn bộ tuyến tới {destination.MapId}/{destination.RawX}/{destination.RawY}.");
	}

	private void Start(GameWindow game, int currentMapId, Action<string>? log) {
		state = MovementState.Moving;
		nextProgressLogUtc = DateTime.MinValue;
		orderQueue.Reset();
		log?.Invoke($"Lên bãi {destination.ModeName} AutoFS bắt đầu | PID={game.ProcessId} | HiệnTạiMap={currentMapId} | Đích={destination.MapName}/{destination.MapId}/{destination.RawX}/{destination.RawY} | Quái={destination.MonsterName}");
	}

	private void Reset() {
		state = MovementState.Idle;
		destination = default;
		destinationKey = "";
		nextProgressLogUtc = DateTime.MinValue;
		orderQueue.Reset();
	}

	private static bool TryResolveDestination(GameWindow game, int currentMapId, out TrainingDestination destination) {
		Settings settings = game.AttackSettings;
		if (! settings.EnableReturnToTraining) {
			destination = default;
			return false;
		}
		if (settings.TrainingEnabled && IsValid(settings.TrainingMapId, settings.TrainingRawX, settings.TrainingRawY)) {
			destination = new TrainingDestination("Mê cung", settings.TrainingMap, settings.TrainingMonster, settings.TrainingMapId, settings.TrainingRawX, settings.TrainingRawY);
			return true;
		}
		if (settings.TeachingEnabled && IsValid(settings.TeachingMapId, settings.TeachingRawX, settings.TeachingRawY)) {
			destination = new TrainingDestination("Thành thị", settings.TeachingMap, settings.TeachingMonster, settings.TeachingMapId, settings.TeachingRawX, settings.TeachingRawY);
			return true;
		}
		if (settings.ContinueEnabled && IsValid(settings.ContinueMapId, settings.ContinueRawX, settings.ContinueRawY)) {
			destination = new TrainingDestination("Tân thủ thôn", settings.ContinueMap, settings.ContinueMonster, settings.ContinueMapId, settings.ContinueRawX, settings.ContinueRawY);
			return true;
		}
		if (settings.Enabled && settings.UseCenterPosition && game.SavedTrainingMapId > 0 && game.TrainingPositionsByMap.TryGetValue(game.SavedTrainingMapId, out (int RawX, int RawY) savedPosition) && savedPosition.RawX > 0 && savedPosition.RawY > 0) {
			destination = new TrainingDestination("đã lưu", $"Map {game.SavedTrainingMapId}", "Đánh toàn bộ", game.SavedTrainingMapId, savedPosition.RawX, savedPosition.RawY);
			return true;
		}
		destination = default;
		return false;
	}

	private static bool IsValid(int mapId, int rawX, int rawY) => mapId > 0 && rawX > 0 && rawY > 0;

	// Đối chiếu tọa độ hiện tại với đúng tâm và bán kính quét ban đầu của tab Đánh.
	private static bool IsInsideTrainingArea(Settings settings, GameSnapshot snapshot, TrainingDestination destination) {
		long deltaX = (long)snapshot.X - destination.RawX;
		long deltaY = (long)snapshot.Y - destination.RawY;
		long range = Math.Max(settings.Range, 1);
		return deltaX * deltaX + deltaY * deltaY <= range * range;
	}
	private enum MovementState { Idle, Moving, Completed }
	private readonly record struct TrainingDestination(string ModeName, string MapName, string MonsterName, int MapId, int RawX, int RawY) {
		public string Key => $"{ModeName}|{MapId}|{RawX}|{RawY}|{MonsterName}";
	}
}
