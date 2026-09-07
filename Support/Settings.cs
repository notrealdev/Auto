namespace Auto.Support;

public sealed class Settings {
	// Bật ô này là bật toàn bộ skill hỗ trợ bị động, không tách từng skill. Mặc định bật.
	public bool BuffThreeSystems { get; set; } = true;


	public bool HealOwner { get; set; }

	public int HealOwnerHpPercent { get; set; } = 30;

	public bool HealPet { get; set; }

	public int HealPetHpPercent { get; set; } = 30;
}



