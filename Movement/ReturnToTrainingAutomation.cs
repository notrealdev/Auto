namespace Auto.Movement;

using Auto.Runtime;
using Auto.Attack;
using Auto.Utils;

public sealed class ReturnToTrainingAutomation {
	private readonly AutoFsOrderQueue orderQueue = new();
	private ReturnToTrainingState state;
	private int destinationMapId;
	private int destinationRawX;
	private int destinationRawY;
	private string destinationName = "";
	private DateTime nextProgressLogUtc;

	public bool IsBusy => state != ReturnToTrainingState.Idle;

	// Ghi nhớ chính xác bãi đang dùng trước khi xử lý trạng thái chết
	public void Prepare(GameWindow game, GameSnapshot snapshot, Action<string>? log) {
		if (IsBusy || !game.AttackSettings.EnableReturnToTraining) return;
		if (!TryResolveDestination(game, game.LastObservedMapId, snapshot, out TrainingDestination destination)) {
			log?.Invoke($"Tự lên bãi AutoFS bỏ qua | PID={game.ProcessId} | Chưa có map/tọa độ bãi hợp lệ.");
			return;
		}
		destinationMapId = destination.MapId;
		destinationRawX = destination.RawX;
		destinationRawY = destination.RawY;
		destinationName = destination.Name;
		state = ReturnToTrainingState.WaitingForRespawn;
		log?.Invoke($"Tự lên bãi AutoFS đã ghi nhớ | PID={game.ProcessId} | Bãi={destinationName} | Map={destinationMapId} | Raw={destinationRawX}/{destinationRawY}");
	}

	public bool Tick(GameWindow game, GameSnapshot snapshot, bool manualInputActive, Action<string>? log) {
		if (!IsBusy) return false;
		if (!game.AttackSettings.EnableReturnToTraining) {
			Cancel(log, "đã tắt tùy chọn");
			return false;
		}
		if (!snapshot.Success) return true;
		if (manualInputActive) {
			orderQueue.InvalidateActiveCommand();
			return true;
		}
		if (state == ReturnToTrainingState.WaitingForRespawn) {
			state = ReturnToTrainingState.Moving;
			nextProgressLogUtc = DateTime.MinValue;
			orderQueue.Reset();
			log?.Invoke($"Tự lên bãi AutoFS bắt đầu | PID={game.ProcessId} | Đích={destinationName}/{destinationMapId}/{destinationRawX}/{destinationRawY}");
		}
		bool completed = orderQueue.Tick(game, snapshot, destinationMapId, destinationRawX, destinationRawY, out string detail);
		if (completed) {
			log?.Invoke($"Tự lên bãi AutoFS hoàn tất | PID={game.ProcessId} | {detail}");
			Reset();
			return false;
		}
		bool portalRecovery = detail.StartsWith("PortalRecovery20s=", StringComparison.Ordinal);
		if (portalRecovery || DateTime.UtcNow >= nextProgressLogUtc) {
			nextProgressLogUtc = DateTime.UtcNow.AddSeconds(10);
			log?.Invoke($"Tự lên bãi AutoFS | PID={game.ProcessId} | {detail}");
		}
		return true;
	}

	public void Cancel(Action<string>? log = null, string reason = "Auto dừng") {
		if (IsBusy) log?.Invoke($"Tự lên bãi AutoFS dừng | {reason}.");
		Reset();
	}

	// Giữ nguyên đích quay lại nhưng khởi tạo lại hàng đợi khi watchdog phát hiện đứng im.
	public void Recover(Action<string>? log, string reason) {
		if (!IsBusy) return;
		state = ReturnToTrainingState.Moving;
		orderQueue.Reset();
		nextProgressLogUtc = DateTime.MinValue;
		log?.Invoke($"Tự lên bãi AutoFS phục hồi | {reason} | Đã khởi tạo lại toàn bộ tuyến tới {destinationMapId}/{destinationRawX}/{destinationRawY}.");
	}

	private void Reset() {
		state = ReturnToTrainingState.Idle;
		destinationMapId = 0;
		destinationRawX = 0;
		destinationRawY = 0;
		destinationName = "";
		nextProgressLogUtc = DateTime.MinValue;
		orderQueue.Reset();
	}

	// Ưu tiên bãi đã lưu trên map hiện tại để hai bộ điều khiển không dùng hai đích khác nhau
	private static bool TryResolveDestination(GameWindow game, int currentMapId, GameSnapshot snapshot, out TrainingDestination destination) {
		Settings settings = game.AttackSettings;
		if (settings.TrainingEnabled && IsValid(settings.TrainingMapId, settings.TrainingRawX, settings.TrainingRawY)) destination = new(settings.TrainingMapId, settings.TrainingRawX, settings.TrainingRawY, settings.TrainingMap);
		else if (settings.TeachingEnabled && IsValid(settings.TeachingMapId, settings.TeachingRawX, settings.TeachingRawY)) destination = new(settings.TeachingMapId, settings.TeachingRawX, settings.TeachingRawY, settings.TeachingMap);
		else if (settings.ContinueEnabled && IsValid(settings.ContinueMapId, settings.ContinueRawX, settings.ContinueRawY)) destination = new(settings.ContinueMapId, settings.ContinueRawX, settings.ContinueRawY, settings.ContinueMap);
		else if (game.SavedTrainingMapId > 0 && game.TrainingPositionsByMap.TryGetValue(game.SavedTrainingMapId, out (int RawX, int RawY) savedPosition) && IsValid(game.SavedTrainingMapId, savedPosition.RawX, savedPosition.RawY)) destination = new(game.SavedTrainingMapId, savedPosition.RawX, savedPosition.RawY, "Bãi đã lưu");
		else if (settings.UseCenterPosition && IsValid(currentMapId, settings.CenterX, settings.CenterY)) destination = new(currentMapId, settings.CenterX, settings.CenterY, "Tâm bãi");
		else if (IsValid(currentMapId, snapshot.X, snapshot.Y)) destination = new(currentMapId, snapshot.X, snapshot.Y, "Vị trí trước khi chết");
		else {
			destination = default;
			return false;
		}
		return true;
	}

	private static bool IsValid(int mapId, int rawX, int rawY) => mapId > 0 && rawX > 0 && rawY > 0;
	private enum ReturnToTrainingState { Idle, WaitingForRespawn, Moving }
	private readonly record struct TrainingDestination(int MapId, int RawX, int RawY, string Name);
}
