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
	private string lastUnresolvedLine = "";

	public bool IsBusy => state == MovementState.Moving;

	// Dùng chung cho các luồng khác (vd. Sửa đồ) cần biết đúng Map/X/Y của bãi đã cấu hình, không chỉ toạ độ thô.
	public static bool TryResolveTrainingPoint(GameWindow game, out int mapId, out int rawX, out int rawY) {
		if (TryResolveDestination(game, 0, out TrainingDestination destination)) {
			mapId = destination.MapId;
			rawX = destination.RawX;
			rawY = destination.RawY;
			return true;
		}
		mapId = 0;
		rawX = 0;
		rawY = 0;
		return false;
	}

	public bool Tick(GameWindow game, GameSnapshot snapshot, bool manualInputActive, Action<string>? log) {
		if (!snapshot.Success) return IsBusy;
		GameMapInfo map = GameMapReader.Read(game.ProcessId);
		if (!map.Success) return IsBusy;
		if (! TryResolveDestination(game, map.MapId, out TrainingDestination configuredDestination)) {
			LogUnresolvedDestination(game, map.MapId, log);
			Reset();
			return false;
		}
		lastUnresolvedLine = "";
		if (!string.Equals(destinationKey, configuredDestination.Key, StringComparison.Ordinal)) {
			Reset();
			destination = configuredDestination;
			destinationKey = configuredDestination.Key;
		}
		// Luồng này chỉ có nhiệm vụ đưa nhân vật LÊN đúng map bãi, không giữ nhân vật ở giữa bãi.
		// Đang đứng đúng map bãi thì dừng hẳn: trước đây nhánh này còn so khoảng cách với Range (đơn vị raw, 1200 raw chỉ
		// khoảng 4,7 ô X / 2,3 ô Y) nên vừa đánh nhau ra khỏi bán kính là bị bắn lệnh di chuyển có cờ kéo về tâm mỗi 5 giây.
		if (map.MapId == destination.MapId) {
			state = MovementState.Completed;
			orderQueue.Reset();
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

	// Trước đây nhánh này im lặng hoàn toàn, nên một account không có đích quay về vẫn kẹt ngoài phạm vi mà không dòng log nào.
	// Chỉ ghi lại khi nội dung đổi để không lặp mỗi vòng tick.
	private void LogUnresolvedDestination(GameWindow game, int mapId, Action<string>? log) {
		Settings settings = game.AttackSettings;
		string reason = ! settings.EnableReturnToTraining ? "Tắt Tự lên bãi"
			: ! settings.TrainingEnabled && ! settings.TeachingEnabled && ! settings.ContinueEnabled ? "Chưa bật chế độ bãi nào (Mê cung/Thành thị/Tân thủ thôn)"
			: "Toạ độ bãi không hợp lệ";
		string line = $"Lên bãi chưa có đích | PID={game.ProcessId} | Map={mapId} | Lý do={reason} | BãiĐãLưu={game.SavedTrainingMapId}";
		if (string.Equals(line, lastUnresolvedLine, StringComparison.Ordinal)) return;
		lastUnresolvedLine = line;
		log?.Invoke(line);
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

	private enum MovementState { Idle, Moving, Completed }
	private readonly record struct TrainingDestination(string ModeName, string MapName, string MonsterName, int MapId, int RawX, int RawY) {
		public string Key => $"{ModeName}|{MapId}|{RawX}|{RawY}|{MonsterName}";
	}
}
