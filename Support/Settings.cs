namespace Auto.Support;

public sealed class Settings {
	// Bật ô này là bật toàn bộ skill hỗ trợ bị động, không tách từng skill. Mặc định bật.
	public bool BuffThreeSystems { get; set; } = true;


	// Buff Chủ/Đệ mặc định bật, ngưỡng chốt với chủ dự án 2026-09-08: Chủ 45%, Đệ 30%.
	public bool HealOwner { get; set; } = true;

	public int HealOwnerHpPercent { get; set; } = 45;

	public bool HealPet { get; set; } = true;

	public int HealPetHpPercent { get; set; } = 30;
}



