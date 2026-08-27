namespace Auto.Utils;

public static class GameAddresses {
	public const string ModuleName = "Game.exe";
	public const int ModuleMaximumSize = 0x1F7D000;

	public static class Globals {
		public const int EntityTable = 0x95DF40;
		public const int CurrentTargetIndex = 0x4CB668;
		public const int InventoryRoot = 0xA7DE04;
		public const int ItemTable = 0x53F048;
		public const int AttackManager = 0x4E0640;
		public const int MapCoordinateRoot = 0x9F8060;
		public const int CombatTargetRoot = 0x3A95D8;
		public const int MapId = 0x501AFC;
		public const int MapIdMirror = 0x501B00;
		public const int MapIdRuntimeMirror = 0x516B48;
		public const int ModalState = 0x4ECF68;
		public const int DialogPointer = 0x500BE0;
		public const int ShopState = 0x4ED6B8;
		public const int ReturnToTownModal = 0x4FCD38;
	}

	public static class Entity {
		public const int PlayerIndex = 1;
		public const int Stride = 0xD87C;
		public const int Handle = 0x0000;
		public const int SlotIndex = 0x0004;
		public const int ClassPointer = 0x0008;
		public const int PrimaryPointer = 0x000C;
		public const int SecondaryPointer = 0x0010;
		public const int MirrorIndex = 0x0014;
		public const int ActiveFlag = 0x0018;
		public const int Type = 0x0028;
		public const int Level = 0x0024;
		public const int Hp = 0x27D0;
		public const int MaxHp = 0x27D4;
		public const int Mp = 0x27DC;
		public const int MaxMp = 0x27E0;
		public const int Name = 0x2D70;
		public const int RawX = 0x434C;
		public const int RawY = 0x4350;
		public const int RawXMirror = 0x71EC;
		public const int RawYMirror = 0x71F0;
		public const int PlayerDeathStatus = 0x01E8;
		public const int PlayerDeathState = 0x01EC;
		public const int MoveTargetXCandidate = 0x2CDC;
		public const int MoveTargetYCandidate = 0x2CE0;
		// Đích lệnh di chuyển, xác nhận runtime 27/08/2026: chỉ đổi khi có lệnh mới, giữ nguyên suốt lúc nhân vật đang đi, click giao diện trong game không ghi.
		public const int MovementDestinationX = 0x42D4;
		public const int MovementDestinationY = 0x42D8;
		public const int AnchorXCandidate = 0x42E8;
		public const int AnchorYCandidate = 0x42EC;
	}

	public static class Attack {
		public const int ResetCommand = 0xD794;
		public const int TargetIndexCommand = 0xD798;
		public const int CommandActive = 0xD7A0;
		public const int ColdStartMethodVtable = 0x38;
		public const int PrepareMethodVtable = 0x6C;
	}

	public static class Inventory {
		public const int Object = 0x4B7BC;
		public const int SaleSlotListPointer = 0x0000;
		public const int SaleSlotCount = 35;
		public const int QuickSlotListPointer = 0x0140;
		public const int QuickSlotCount = 4;
		public const int ExtendedSlotListPointer = 0x0208;
		public const int ExtendedSlotCount = 35;
		public const int FirstStrength = 0x0FFC8;
		public const int SecondStrength = 0x0FE88;
		public const int ThirdStrength = 0x10090;
		public const int EquippedWeightItemIndex = 0x0BD78;
		public const int MaximumStrength = 0x21DDC;
		public const int ReceiptDecrement = 0x0FE2C;
		public const int ReceiptFirstIncrement = 0x0FE38;
		public const int ReceiptSecondIncrement = 0x0FE7C;
	}

	public static class Item {
		public const int GroundRecordTablePointer = 0x54BCA0;
		public const int GroundRecordStride = 0x3A4;
		public const int GroundRecordId = 0x14;
		public const int GroundRecordType = 0x18;
		public const int GroundRecordKind = 0x1C;
		public const int GroundName = 0x7C;
		public const int GroundQualityCodeA = 0xA0;
		public const int GroundInternalX = 0xA8;
		public const int GroundInternalY = 0xAC;
		public const int GroundQualityCodeB = 0xB0;
		public const int GroundMapObjectIndex = 0x28;
		public const int GroundMapSegmentIndex = 0x2C;
		public const int GroundTileX = 0x30;
		public const int GroundTileY = 0x34;
		public const int GroundSubTileX = 0x38;
		public const int GroundSubTileY = 0x3C;
		public const int MapObjectStride = 0x260;
		public const int MapSegmentCount = 0x210;
		public const int MapSegmentTable = 0x10;
		public const int MapSegmentStride = 0x1590;
		public const int MapSegmentBaseX = 0x178;
		public const int MapSegmentBaseY = 0x17C;
		public const int InventoryRecordStride = 0x1800;
		public const int InventoryName = 0x0704;
		public const int InventoryType = 0x076E;
		public const int InventoryClassField2A4 = 0x02A4;
		public const int InventoryClassField6D8 = 0x06D8;
		public const int InventoryClassField6DC = 0x06DC;
		public const int InventoryClassField6E0 = 0x06E0;
		public const int InventoryClassField700 = 0x0700;
		public const int InventoryClassFieldAA8 = 0x0AA8;
		public const int Name = 0x0704;
		public const int Weight = 0x06EC;
		public const int Quantity = 0x0754;
		public const int GroundStateA0 = 0xA0;
		public const int GroundStateA8 = 0xA8;
		public const int GroundStateAC = 0xAC;
		public const int GroundStateD8 = 0xD8;
		public const int GroundStateDC = 0xDC;
	}

	public static class Combat {
		public const int HpAt82C = 0x82C;
		public const int CurrentHp = 0xCE4;
		public const int MaximumHp = 0xCE8;
	}
}
