namespace Auto.Movement;

using Auto.Runtime;
using Auto.Utils;
using Auto.Attack;

internal sealed class AutoFsOrderQueue {
	private const double ArrivalDistance = 1.5;
	private const int MapReadyDelayMilliseconds = 500;
	private const int RetryIntervalMilliseconds = 800;
	private const int PortalRecoveryMilliseconds = 20000;
	private int commandedMapId;
	private int commandedRawX;
	private int commandedRawY;
	private int observedRawX;
	private int observedRawY;
	private int observedMapId;
	private int portalFromMapId;
	private bool crossingPortal;
	private DateTime mapReadyUtc;
	private DateTime lastCommandUtc;
	private DateTime lastProgressUtc;
	private DateTime portalTransitionStartedUtc;
	private int portalTransitionMapId;

	public bool Tick(GameWindow game, GameSnapshot snapshot, int destinationMapId, int destinationRawX, int destinationRawY, out string detail) {
		detail = "";
		GameMapInfo map = GameMapReader.Read(game.ProcessId);
		if (!map.Success) {
			detail = map.FailureReason;
			return false;
		}
		if (observedMapId == 0) {
			observedMapId = map.MapId;
		} else if (observedMapId != map.MapId) {
			observedMapId = map.MapId;
			mapReadyUtc = DateTime.UtcNow.AddMilliseconds(MapReadyDelayMilliseconds);
			InvalidateActiveCommand();
		}
		if (crossingPortal && map.MapId != portalFromMapId) ResetPortal();
		if (DateTime.UtcNow < mapReadyUtc) {
			detail = $"WaitingMapReady | Map={map.MapId} | Delay={MapReadyDelayMilliseconds}ms";
			return false;
		}
		if (map.MapId == destinationMapId) {
			ResetPortal();
			double distance = GetDistance(snapshot.X, snapshot.Y, destinationRawX, destinationRawY);
			if (distance <= ArrivalDistance) {
				detail = $"Arrived | Map={map.MapId} | Raw={snapshot.X}/{snapshot.Y} | Distance={distance:F2}";
				return true;
			}
			return SendWhenNeeded(game, map.MapId, destinationRawX, destinationRawY, $"Destination={destinationMapId}", out detail);
		}
		IReadOnlyList<GameMapTransition> route = GameMapRoutePlanner.FindRoute(map.MapId, destinationMapId);
		if (route.Count == 0) {
			detail = $"No route {map.MapId}->{destinationMapId}";
			return false;
		}
		GameMapTransition transition = route[0];
		if (transition.Points.Count < 2) {
			detail = $"Invalid portal data {transition.FromMapId}->{transition.ToMapId}";
			return false;
		}
		double approachDistance = GetDistance(snapshot.X, snapshot.Y, transition.Approach.RawX, transition.Approach.RawY);
		// Khởi động lại toàn bộ bước tiếp cận nếu đã thử qua cổng 20 giây mà map vẫn không đổi.
		if (portalTransitionMapId == transition.FromMapId && portalTransitionStartedUtc != DateTime.MinValue && DateTime.UtcNow - portalTransitionStartedUtc >= TimeSpan.FromMilliseconds(PortalRecoveryMilliseconds)) {
			portalTransitionStartedUtc = DateTime.UtcNow;
			ResetPortalAttempt();
			InvalidateActiveCommand();
			return SendWhenNeeded(game, map.MapId, transition.Approach.RawX, transition.Approach.RawY, $"PortalRecovery20s={transition.FromMapId}->{transition.ToMapId}", out detail);
		}
		// Quay lại điểm tiếp cận khi nhân vật đã lệch khỏi cổng VÀ thật sự đứng yên.
		// Trước đây chốt này chỉ đếm 800ms kể từ lệnh vào cổng, không xét nhân vật có đang đi hay không. Với tuyến mà
		// điểm cổng nằm xa điểm tiếp cận thì nhân vật vừa rời điểm tiếp cận là approachDistance vượt ArrivalDistance
		// ngay, rồi 800ms sau bị gọi ngược — không bao giờ đi hết quãng đường tới cổng.
		// Đo trên Data/Maps: 98 tuyến, trung vị Approach<->Far = 1.93 ô, tối đa 8.85 ô, trong khi ArrivalDistance chỉ
		// 1.5 ô. Tuyến 20->32 (4.78 ô) kẹt vô hạn, log back-to-training.log 2026-09-08 01:43-01:50.
		// ShouldRetry chỉ trả true khi toạ độ KHÔNG đổi suốt 800ms, nên nhân vật đang đi thì không bị gọi ngược nữa.
		// Trường hợp tới được cổng mà map không đổi vẫn có PortalRecovery20s ở khối ngay trên lo.
		if (crossingPortal && approachDistance > ArrivalDistance && ShouldRetry(game)) {
			ResetPortalAttempt();
			InvalidateActiveCommand();
			return SendWhenNeeded(game, map.MapId, transition.Approach.RawX, transition.Approach.RawY, $"ReApproach={transition.FromMapId}->{transition.ToMapId}", out detail);
		}
		if (!crossingPortal && approachDistance > ArrivalDistance) {
			return SendWhenNeeded(game, map.MapId, transition.Approach.RawX, transition.Approach.RawY, $"Approach={transition.FromMapId}->{transition.ToMapId}", out detail);
		}
		if (!crossingPortal) {
			crossingPortal = true;
			portalFromMapId = transition.FromMapId;
			if (portalTransitionMapId != transition.FromMapId || portalTransitionStartedUtc == DateTime.MinValue) {
				portalTransitionMapId = transition.FromMapId;
				portalTransitionStartedUtc = DateTime.UtcNow;
			}
		}
		return SendPortalWhenNeeded(game, map.MapId, transition.Far.RawX, transition.Far.RawY, transition.FromMapId, transition.ToMapId, out detail);
	}

	public void Reset() {
		commandedMapId = 0;
		commandedRawX = 0;
		commandedRawY = 0;
		observedRawX = 0;
		observedRawY = 0;
		observedMapId = 0;
		mapReadyUtc = DateTime.MinValue;
		lastCommandUtc = DateTime.MinValue;
		lastProgressUtc = DateTime.MinValue;
		ResetPortal();
	}

	public void InvalidateActiveCommand() {
		commandedMapId = 0;
		commandedRawX = 0;
		commandedRawY = 0;
		lastCommandUtc = DateTime.MinValue;
		lastProgressUtc = DateTime.MinValue;
	}

	private bool SendWhenNeeded(GameWindow game, int mapId, int rawX, int rawY, string target, out string detail) {
		bool changed = commandedMapId != mapId || commandedRawX != rawX || commandedRawY != rawY;
		if (!changed && !ShouldRetry(game)) {
			detail = $"Active | {target} | Raw={rawX}/{rawY}";
			return false;
		}
		if (!AutoFsMovementCommand.TryMoveTo(game, rawX, rawY, out string result)) {
			detail = $"{target} | {result}";
			return false;
		}
		commandedMapId = mapId;
		commandedRawX = rawX;
		commandedRawY = rawY;
		RecordCommand(game);
		detail = $"{target} | {result}";
		return false;
	}

	private bool SendPortalWhenNeeded(GameWindow game, int mapId, int rawX, int rawY, int fromMapId, int toMapId, out string detail) {
		string target = $"Portal={fromMapId}->{toMapId}";
		bool changed = commandedMapId != mapId || commandedRawX != rawX || commandedRawY != rawY;
		if (!changed && DateTime.UtcNow - lastCommandUtc < TimeSpan.FromMilliseconds(RetryIntervalMilliseconds)) {
			detail = $"Active | {target} | Raw={rawX}/{rawY} | WaitingMap={toMapId}";
			return false;
		}
		if (!AutoFsMovementCommand.TryEnterPortal(game, rawX, rawY, out string result)) {
			detail = $"{target} | {result}";
			return false;
		}
		commandedMapId = mapId;
		commandedRawX = rawX;
		commandedRawY = rawY;
		RecordCommand(game);
		detail = $"{target} | {result}";
		return false;
	}

	private bool ShouldRetry(GameWindow game) {
		int rawX = game.X;
		int rawY = game.Y;
		if (rawX != observedRawX || rawY != observedRawY) {
			observedRawX = rawX;
			observedRawY = rawY;
			lastProgressUtc = DateTime.UtcNow;
			return false;
		}
		return DateTime.UtcNow - lastCommandUtc >= TimeSpan.FromMilliseconds(RetryIntervalMilliseconds) &&
			DateTime.UtcNow - lastProgressUtc >= TimeSpan.FromMilliseconds(RetryIntervalMilliseconds);
	}

	private void RecordCommand(GameWindow game) {
		observedRawX = game.X;
		observedRawY = game.Y;
		lastCommandUtc = DateTime.UtcNow;
		lastProgressUtc = DateTime.UtcNow;
	}

	private void ResetPortal() {
		ResetPortalAttempt();
		portalTransitionStartedUtc = DateTime.MinValue;
		portalTransitionMapId = 0;
	}

	private void ResetPortalAttempt() {
		crossingPortal = false;
		portalFromMapId = 0;
	}

	private static double GetDistance(int rawX1, int rawY1, int rawX2, int rawY2) {
		double deltaX = (rawX1 - rawX2) * 2.0;
		double deltaY = rawY1 - rawY2;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY) / 512.0;
	}
}
