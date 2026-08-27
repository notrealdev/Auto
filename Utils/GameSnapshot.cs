namespace Auto.Utils;

public enum SnapshotStatus {
	Success,
	OpenProcessFailed,
	ModuleBaseFailed,
	StatsPointerFailed,
	NameReadFailed,
	InvalidLevel,
	InvalidStats,
	Exception
}

public sealed class GameSnapshot {
	public SnapshotStatus Status { get; set; }

	public string FailReason { get; set; } = "";

	public string CharacterName { get; set; } = "";

	public int Level { get; set; }

	public int Hp { get; set; } = -1;

	public int MaxHp { get; set; }

	public int Mp { get; set; } = -1;

	public int MaxMp { get; set; }

	public int X { get; set; }

	public int Y { get; set; }

	public int MoveTargetX { get; set; }

	public int MoveTargetY { get; set; }

	public CombatSnapshot Combat { get; set; } = new();

	public bool Success {
		get {
			return Status == SnapshotStatus.Success;
		}
	}

	public int ProcessId { get; set; }
}