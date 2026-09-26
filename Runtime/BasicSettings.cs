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

	// Khối "Hồi phục" (dựa AutoFS): dùng thuốc đã chọn ở khối mua nhanh khi HP/MP dưới ngưỡng. AutoFS tính ngưỡng theo phần
	// trăm; chủ dự án chốt 2026-09-26 Auto tính theo ĐIỂM. Giá trị mặc định là giả định, chưa được chủ dự án chốt.
	public bool EnableRecoverHp { get; set; } = true;

	public int RecoverHpThreshold { get; set; } = 300;

	public bool EnableRecoverMp { get; set; } = true;

	public int RecoverMpThreshold { get; set; } = 150;

	// Khối "Mua item hồi phục" (dựa AutoFS): số bình mỗi lần mua nhanh, mặc định 1 để thử trước khi tăng.
	public bool EnableQuickBuyHp { get; set; } = true;

	public int QuickBuyHpQuantity { get; set; } = 1;

	public bool EnableQuickBuyMp { get; set; } = true;

	public int QuickBuyMpQuantity { get; set; } = 1;

	// Mã chi tiết của thuốc sẽ mua (chủ dự án chốt 2026-09-25): máu mặc định Trung Hồng đơn (1), mana mặc định Tiểu Hoàn đơn (3).
	public int QuickBuyHpPotionCode { get; set; } = 1;

	public int QuickBuyMpPotionCode { get; set; } = 3;

	public bool EnableQuickBuyAtDoctor { get; set; }
}
