namespace Auto.Utils;

public static class MemoryLayout {
	public const string ModuleName = GameAddresses.ModuleName;

	public const int GlobalStatsPointerOffset = GameAddresses.Globals.EntityTable;

	// Tạm hardcode theo kết quả reverse hiện tại.
	// Sau này khi tìm được PlayerIndex tự động thì thay phần này.
	public const int PlayerIndex = 0x353B;

	public const int HpOffset = GameAddresses.Entity.Hp;
	public const int MaxHpOffset = GameAddresses.Entity.MaxHp;
	public const int MpOffset = GameAddresses.Entity.Mp;
	public const int MaxMpOffset = GameAddresses.Entity.MaxMp;
}
