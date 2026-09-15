namespace Auto.Attack;

using Auto.Utils;

internal static class AutoFsClientProfile {
	public const int EntityTablePointer = GameAddresses.Globals.EntityTable;
	public const int EntityStride = GameAddresses.Entity.Stride;
	public const int FirstEntityIndex = GameAddresses.Entity.FirstScanIndex;
	public const int LastEntityIndex = GameAddresses.Entity.LastScanIndex;
	public const int PlayerIndex = 1;
	public const int EntityType = GameAddresses.Entity.Type;
	public const int Level = GameAddresses.Entity.Level;
	public const int LifecycleStatus = 0x01E8;
	public const int Name = GameAddresses.Entity.Name;
	public const int Hp = GameAddresses.Entity.Hp;
	public const int RawX = GameAddresses.Entity.RawX;
	public const int RawY = GameAddresses.Entity.RawY;
	public const int MaximumNameLength = 30;
	public const int MonsterType = 0;
	public const int PlayerType = 1;
	public const int FinishedStatus = 6;
}
