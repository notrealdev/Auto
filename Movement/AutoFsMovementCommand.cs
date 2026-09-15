namespace Auto.Movement;

using Auto.Runtime;

internal static class AutoFsMovementCommand {
	private const int ResetCommand = 32;
	private const int XCommand = 0;
	private const int YCommand = 5;
	private const int PortalCommand = 27;
	private const int WalkCommand = 321;

	public static bool TryMoveTo(GameWindow game, int destinationRawX, int destinationRawY, out string result) {
		result = "";
		if (! game.RuntimeLayout.MovementReady) {
			result = "Runtime movement layout is unavailable: " + game.RuntimeLayout.DescribeUnavailable(RuntimeSubsystem.MovementTransport);
			return false;
		}
		if (destinationRawX <= 0 || destinationRawY <= 0) {
			result = $"AutoFS SAFE_REJECT | Raw={destinationRawX}/{destinationRawY}";
			return false;
		}
		int dispatchX = destinationRawX / 32;
		int dispatchY = destinationRawY / 32;
		string movementResult = "";
		bool moved = game.AutoFsActionGate.RunMovement(() => TryMoveToCore(game, destinationRawX, destinationRawY, dispatchX, dispatchY, out movementResult));
		result = movementResult;
		return moved;
	}

	// Bản chẩn đoán của TryMoveTo: bỏ qua công tắc Auto tổng nhưng vẫn giữ khoá chống gửi trùng, cùng lý do đã ghi ở
	// AutoFsActionGate.RunDebugCommand. Công cụ chẩn đoán do chủ dự án bấm từng lần và thường phải chạy lúc tài khoản
	// đang tắt; không có đường này thì probe không đi được bước nào.
	public static bool TryMoveToForDebug(GameWindow game, int destinationRawX, int destinationRawY, out string result) {
		result = "";
		if (! game.RuntimeLayout.MovementReady) {
			result = "Runtime movement layout is unavailable: " + game.RuntimeLayout.DescribeUnavailable(RuntimeSubsystem.MovementTransport);
			return false;
		}
		if (destinationRawX <= 0 || destinationRawY <= 0) {
			result = $"AutoFS SAFE_REJECT | Raw={destinationRawX}/{destinationRawY}";
			return false;
		}
		int dispatchX = destinationRawX / 32;
		int dispatchY = destinationRawY / 32;
		string movementResult = "";
		bool moved = game.AutoFsActionGate.RunDebugCommand(() => TryMoveToCore(game, destinationRawX, destinationRawY, dispatchX, dispatchY, true, out movementResult));
		result = movementResult;
		return moved;
	}

	private static bool TryMoveToCore(GameWindow game, int destinationRawX, int destinationRawY, int dispatchX, int dispatchY, out string result) {
		return TryMoveToCore(game, destinationRawX, destinationRawY, dispatchX, dispatchY, false, out result);
	}

	private static bool TryMoveToCore(GameWindow game, int destinationRawX, int destinationRawY, int dispatchX, int dispatchY, bool debugRun, out string result) {
		bool Send(int command, int payload, out string error) => debugRun
			? game.AutoFsTransport.TrySendCommandForDebug(game.Handle, command, payload, out error)
			: game.AutoFsTransport.TrySendCommand(game.Handle, command, payload, out error);
		if (!Send(ResetCommand, 0, out string resetError)) {
			result = $"AutoFS command {ResetCommand} FAIL | {resetError}";
			return false;
		}
		if (!Send(XCommand, dispatchX, out string xError)) {
			result = $"AutoFS command {XCommand} FAIL | {xError}";
			return false;
		}
		if (!Send(YCommand, dispatchY, out string yError)) {
			result = $"AutoFS command {YCommand} FAIL | {yError}";
			return false;
		}
		result = $"AutoFS SaveDevice | Commands=32/0,0/{dispatchX},5/{dispatchY} | Raw={destinationRawX}/{destinationRawY}";
		return true;
	}

	// Đi bộ thường tới một toạ độ, KHÔNG gắn cờ lên màn hình.
	// Không dùng lệnh 0/5/32 như TryMoveTo: cả ba lệnh đó đều đổ về CoordinateOpcode 0x9F (SystemUint.cpp:365 và :382
	// gọi cùng một dispatcher), và 0x9F chính là thứ sinh ra lá cờ — bỏ lệnh reset 32 không thay đổi điều đó, runtime
	// beta 2026-09-06 đã chứng minh.
	// Thay vào đó gửi WalkToCommand để native gọi PickupMovementFunction(playerEntity, 3, rawX, rawY, 0), đúng nguyên
	// thủy mà luồng nhặt đồ dùng (SystemUint.cpp, TryDispatchPickup) và chủ dự án xác nhận là không hiện cờ.
	// Toạ độ giữ nguyên dạng RAW, không chia 32 — hàm này nhận raw như luồng nhặt đồ.
	// Gửi bằng TrySendConfirmedCommand chứ không phải TrySendCommand: TrySendCommand dùng PostMessageA nên chỉ chứng
	// minh được "đã vào hàng đợi", còn bản confirmed đọc giá trị trả về của native nên log phân biệt được nhận với từ chối.
	public static bool TryWalkTo(GameWindow game, int destinationRawX, int destinationRawY, out string result) {
		result = "";
		if (! game.RuntimeLayout.MovementReady) {
			result = "Runtime movement layout is unavailable: " + game.RuntimeLayout.DescribeUnavailable(RuntimeSubsystem.MovementTransport);
			return false;
		}
		if (destinationRawX <= 0 || destinationRawY <= 0) {
			result = $"AutoFS WALK SAFE_REJECT | Raw={destinationRawX}/{destinationRawY}";
			return false;
		}
		string walkResult = "";
		bool walked = game.AutoFsActionGate.RunMovement(() => TryWalkToCore(game, destinationRawX, destinationRawY, out walkResult));
		result = walkResult;
		return walked;
	}

	private static bool TryWalkToCore(GameWindow game, int destinationRawX, int destinationRawY, out string result) {
		if (!game.AutoFsTransport.TrySendConfirmedCommand(game.Handle, XCommand, destinationRawX, out string xError)) {
			result = $"AutoFS walk command {XCommand} FAIL | {xError}";
			return false;
		}
		if (!game.AutoFsTransport.TrySendConfirmedCommand(game.Handle, WalkCommand, destinationRawY, out string yError)) {
			result = $"AutoFS walk command {WalkCommand} FAIL | {yError}";
			return false;
		}
		result = $"AutoFS WalkTo | Commands=0/{destinationRawX},{WalkCommand}/{destinationRawY} | Raw={destinationRawX}/{destinationRawY} | Delivery=CONFIRMED";
		return true;
	}

	public static bool TryEnterPortal(GameWindow game, int portalRawX, int portalRawY, out string result) {
		result = "";
		if (! game.RuntimeLayout.MovementReady) {
			result = "Runtime movement layout is unavailable: " + game.RuntimeLayout.DescribeUnavailable(RuntimeSubsystem.MovementTransport);
			return false;
		}
		if (portalRawX <= 0 || portalRawY <= 0) {
			result = $"AutoFS Portal SAFE_REJECT | Raw={portalRawX}/{portalRawY}";
			return false;
		}
		string movementResult = "";
		bool moved = game.AutoFsActionGate.RunMovement(() => TryEnterPortalCore(game, portalRawX, portalRawY, out movementResult));
		result = movementResult;
		return moved;
	}

	private static bool TryEnterPortalCore(GameWindow game, int portalRawX, int portalRawY, out string result) {
		if (!game.AutoFsTransport.TrySendCommand(game.Handle, XCommand, portalRawX, out string xError)) {
			result = $"AutoFS portal command {XCommand} FAIL | {xError}";
			return false;
		}
		if (!game.AutoFsTransport.TrySendCommand(game.Handle, PortalCommand, portalRawY, out string portalError)) {
			result = $"AutoFS portal command {PortalCommand} FAIL | {portalError}";
			return false;
		}
		result = $"AutoFS JoinBuilder | Commands=0/{portalRawX},27/{portalRawY}";
		return true;
	}
}
