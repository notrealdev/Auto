namespace Auto.Movement;

using Auto.Runtime;

internal static class AutoFsMovementCommand {
	private const int ResetCommand = 32;
	private const int XCommand = 0;
	private const int YCommand = 5;
	private const int PortalCommand = 27;

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

	private static bool TryMoveToCore(GameWindow game, int destinationRawX, int destinationRawY, int dispatchX, int dispatchY, out string result) {
		if (!game.AutoFsTransport.TrySendCommand(game.Handle, ResetCommand, 0, out string resetError)) {
			result = $"AutoFS command {ResetCommand} FAIL | {resetError}";
			return false;
		}
		if (!game.AutoFsTransport.TrySendCommand(game.Handle, XCommand, dispatchX, out string xError)) {
			result = $"AutoFS command {XCommand} FAIL | {xError}";
			return false;
		}
		if (!game.AutoFsTransport.TrySendCommand(game.Handle, YCommand, dispatchY, out string yError)) {
			result = $"AutoFS command {YCommand} FAIL | {yError}";
			return false;
		}
		result = $"AutoFS SaveDevice | Commands=32/0,0/{dispatchX},5/{dispatchY} | Raw={destinationRawX}/{destinationRawY}";
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
