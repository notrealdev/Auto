namespace Auto.Runtime;

public enum RuntimeSubsystem {
	Player,
	Entity,
	AttackTransport,
	MovementTransport,
	Ground,
	LootTransport,
	Inventory,
	ItemTable,
	Map,
	Shop,
	RepairTransport,
	ArrangeTransport
}

public readonly record struct RuntimeSubsystemState(bool Available, string Evidence, string FailureReason) {
	public static RuntimeSubsystemState Confirmed(string evidence) => new(true, evidence, "");
	public static RuntimeSubsystemState Unavailable(string failureReason) => new(false, "", failureReason);
}

public sealed class RuntimeLayout {
	private readonly IReadOnlyDictionary<RuntimeSubsystem, RuntimeSubsystemState> subsystems;

	public string Fingerprint { get; }
	public int EntityTableRva { get; }
	public int EntityStride { get; }
	public int PlayerRecordOffset { get; }
	public int LevelOffset { get; }
	public int HpOffset { get; }
	public int MaxHpOffset { get; }
	public int MpOffset { get; }
	public int MaxMpOffset { get; }
	public int NameOffset { get; }
	public int RawXOffset { get; }
	public int RawYOffset { get; }

	public RuntimeLayout(string fingerprint, IReadOnlyDictionary<RuntimeSubsystem, RuntimeSubsystemState> subsystems, int entityTableRva = 0, int entityStride = 0, int playerRecordOffset = 0, int levelOffset = 0, int hpOffset = 0, int maxHpOffset = 0, int mpOffset = 0, int maxMpOffset = 0, int nameOffset = 0, int rawXOffset = 0, int rawYOffset = 0) {
		Fingerprint = fingerprint;
		this.subsystems = subsystems;
		EntityTableRva = entityTableRva;
		EntityStride = entityStride;
		PlayerRecordOffset = playerRecordOffset;
		LevelOffset = levelOffset;
		HpOffset = hpOffset;
		MaxHpOffset = maxHpOffset;
		MpOffset = mpOffset;
		MaxMpOffset = maxMpOffset;
		NameOffset = nameOffset;
		RawXOffset = rawXOffset;
		RawYOffset = rawYOffset;
	}

	public RuntimeSubsystemState Get(RuntimeSubsystem subsystem) {
		return subsystems.TryGetValue(subsystem, out RuntimeSubsystemState state) ? state : RuntimeSubsystemState.Unavailable("Subsystem resolver is not implemented.");
	}

	public bool PlayerReady => Get(RuntimeSubsystem.Player).Available;
	public bool AttackReady => Get(RuntimeSubsystem.Entity).Available && Get(RuntimeSubsystem.AttackTransport).Available;
	public bool MovementReady => Get(RuntimeSubsystem.MovementTransport).Available;
	public bool LootReady => Get(RuntimeSubsystem.Ground).Available && Get(RuntimeSubsystem.LootTransport).Available;
	public bool InventoryReady => Get(RuntimeSubsystem.Inventory).Available && Get(RuntimeSubsystem.ItemTable).Available;
	public bool SaleReady => MovementReady && InventoryReady && Get(RuntimeSubsystem.Map).Available && Get(RuntimeSubsystem.Shop).Available && Get(RuntimeSubsystem.RepairTransport).Available && Get(RuntimeSubsystem.ArrangeTransport).Available;
	public bool RepairReady => MovementReady && Get(RuntimeSubsystem.Map).Available && Get(RuntimeSubsystem.Shop).Available && Get(RuntimeSubsystem.RepairTransport).Available;
	public bool SaleRepairReady => SaleReady && RepairReady;

	public string DescribeUnavailable(params RuntimeSubsystem[] required) {
		return string.Join("; ", required.Where(subsystem => ! Get(subsystem).Available).Select(subsystem => $"{subsystem}={Get(subsystem).FailureReason}"));
	}
}
