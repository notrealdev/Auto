namespace Auto.Runtime;

public enum DeathAction {
	StayStill = -1,
	ReturnToTown = 0,
	Substitute = 1,
	Revive = 2
}

public sealed class BasicSettings {
	public bool EnableLowHpReturnTalisman { get; set; }

	public int LowHpReturnTalismanThreshold { get; set; } = 10;

	public bool EnableWeaponRepair { get; set; } = true;

	public int WeaponDurabilityThreshold { get; set; } = 2;

	public DeathAction DeathAction { get; set; } = DeathAction.ReturnToTown;

	public int ReturnToTownDelayMilliseconds { get; set; } = 2000;
}
