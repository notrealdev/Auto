namespace Auto.Runtime;

public enum DeathAction {
	StayStill = -1,
	ReturnToTown = 0,
	Substitute = 1,
	Revive = 2
}

public sealed class BasicSettings {
	// Mặc định bật, ngưỡng 15% chốt với chủ dự án 2026-09-08.
	public bool EnableLowHpReturnTalisman { get; set; } = true;

	public int LowHpReturnTalismanThreshold { get; set; } = 15;

	public bool EnableWeaponRepair { get; set; } = true;

	public int WeaponDurabilityThreshold { get; set; } = 2;

	public DeathAction DeathAction { get; set; } = DeathAction.ReturnToTown;

	public int ReturnToTownDelayMilliseconds { get; set; } = 2000;
}
