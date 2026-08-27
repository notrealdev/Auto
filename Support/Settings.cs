namespace Auto.Support;

public sealed class Settings {
	public bool BuffThreeSystems { get; set; }


	public bool HealOwner { get; set; }

	public int HealOwnerHpPercent { get; set; } = 30;

	public bool HealPet { get; set; }

	public int HealPetHpPercent { get; set; } = 30;
}



