namespace Auto.Utils;

public static class GameAddresses {
	public const string ModuleName = "Game.exe";
	public const int ModuleMaximumSize = 0x1FA7000;

	public static class Globals {
		public const int EntityTable = 0x73A0C4;
		public const int CurrentTargetIndex = 0x4F2674;
		public const int InventoryRoot = 0xAA3E84;
		public const int ItemTable = 0x5650C8;
		public const int MagicAttributeDescriptorTable = 0x4F2E10;
		public const int AttackManager = 0x4E0640;
		public const int MapCoordinateRoot = 0xA1E0E0;
		public const int CombatTargetRoot = 0x3A95D8;
		public const int MapId = 0x527B84;
		public const int MapIdMirror = 0x527B88;
		public const int MapIdRuntimeMirror = 0x53CBC8;
		public const int ModalState = 0x512FF0;
		public const int DialogPointer = 0x500BE0;
		public const int ShopState = 0x513740;
		public const int ShopCatalog = 0xABD074;
		public const int ReturnToTownModal = 0x522DC0;
		public const int StrengthRoot = 0x5010D4;
		public const int ChatChannelManager = 0x5191DC;
		public const int ChatChannelCodeTable = 0x519220;
		public const int LoginNoticeDialog = 0x522D7C;
		public const int LoginVersionDialog = 0x512D38;
		public const int LoginServerDialog = 0x5287E4;
		public const int MouseInputObject = 0x5228B8;
		public const int MouseInputClientX = 0x3BACA4;
		public const int MouseInputClientY = 0x3BACA8;
	}

	public static class Functions {
		public const int AttackManagerPointer = 0x5066C8;
		public const int GroundCoordinateConverter = 0x300E10;
		public const int Movement = 0x324240;
		public const int Pickup = 0x3B1510;
		public const int RepairConfirmation = 0x297D60;
		public const int MouseHandler = 0x184530;
		public const int ReturnToTown = 0x1A8C90;
	}

	public static class Vtables {
		public const int AttackManager = 0x499988;
		public const int AttackMethodSlot = 0x40;
		public const int CoordinateDispatcherMethodSlot = 0x10;
		public const int ChatInputControl = 0x479988;
		public const int DoctorConfirmModal = 0x48CF3C;
		public const int MouseInputObject = 0x489F20;
		public const int ReturnToTownObject = 0x48B6E0;
		public const int NpcConfirmModal = 0x469E34;
		public const int RepairConfirmModal = 0x4952BC;
	}

	public static class LegacyClients {
		public const int RecoveryMinimumImageSize = 0x1FC0000;
		public const int RecoveryEntityTable = 0x95DF40;
		public const int RecoveryGroundRecordTablePointer = 0x54BCA0;
		public const int RecoveryAttackManagerPointer = 0x4E0640;
		public const int RecoveryAttackManagerVtable = 0x476618;
		public const int RecoveryGroundCoordinateConverter = 0x2F9E00;
		public const int RecoveryMovement = 0x31CE80;
		public const int RecoveryPickup = 0x3AA0F0;
		public const int OldAttackManagerPointer = 0x4DF600;
		public const int OldAttackManagerVtable = 0x4755F8;
		public const int OldGroundCoordinateConverter = 0x2F9DA0;
		public const int OldMovement = 0x31CDE0;
		public const int OldPickup = 0x3A9C50;
	}

	public static class Entity {
		public const int PlayerIndex = 1;
		public const int ReversedPlayerIndex = 0x353B;
		public const int FirstScanIndex = 2;
		public const int LastScanIndex = 511;
		public const int Stride = 0xD87C;
		public const int ReadSize = 0x70B8;
		public const int Handle = 0x0000;
		public const int SlotIndex = 0x0004;
		public const int ClassPointer = 0x0008;
		public const int OwnerCharacterId = 0x0008;
		public const int PrimaryPointer = 0x000C;
		public const int SecondaryPointer = 0x0010;
		public const int MirrorIndex = 0x0014;
		public const int ActiveFlag = 0x0018;
		public const int Type = 0x0028;
		public const int Level = 0x0024;
		public const int LifecycleStatus = 0x01E8;
		public const int Hp = 0x27D0;
		public const int MaxHp = 0x27D4;
		public const int Mp = 0x27DC;
		public const int MaxMp = 0x27E0;
		public const int Name = 0x2D70;
		public const int PetOwnerCharacterId = 0x429C;
		public const int RawX = 0x434C;
		public const int RawY = 0x4350;
		public const int RawXMirror = 0x71EC;
		public const int RawYMirror = 0x71F0;
		public const int RawXLegacy = 0x75F4;
		public const int RawYLegacy = 0x75F8;
		public const int PlayerDeathStatus = 0x01E8;
		public const int PlayerDeathState = 0x01EC;
		public const int MoveTargetXCandidate = 0x2CDC;
		public const int MoveTargetYCandidate = 0x2CE0;
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
		public const int QuickBuyDailyCount = 0x39CCC;
		public const int QuickBuyBlockFlag = 0x4B79C;
		public const int Object = 0x4B7BC;
		public const int SaleSlotListPointer = 0x0000;
		public const int SaleSlotCount = 35;
		public const int MainBagRoom = 0x21;
		public const int MainBagColumns = 5;
		public const int QuickSlotListPointer = 0x0230;
		public const int QuickSlotCount = 4;
		public const int ExtendedSlotListPointer = 0x02F8;
		public const int ExtendedSlotCount = 35;
		public const int EquippedWeightItemIndex = 0x0BD78;
		public const int CurrentStrength = 0x027C;
		public const int MaximumStrength = 0x0278;
		public const int Money = 0x05F0;
		public const int LoginMaximumStrength = 0x5DE54;
		public const int OpenShopId = 0x4C600;
		public const int LoginMoney = 0x4B7C0;
		public const int ReceiptDecrement = 0x0FE2C;
		public const int ReceiptFirstIncrement = 0x0FE38;
		public const int ReceiptSecondIncrement = 0x0FE7C;
	}

	public static class Shop {
		public const int CatalogRecords = 0x04;
		public const int CatalogPositionsPerRow = 0x08;
		public const int CatalogRowCount = 0x0C;
		public const int CatalogRecordCount = 0x10;
	}

	public static class Item {
		public const int GroundRecordTablePointer = 0x571D20;
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
		public const int SpriteCoordinateBeforeSprite = 0x20;
		public const int SpriteGroundTypeBeforeCoordinate = 0x29C;
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
		public const int InventoryClassField6FC = 0x06FC;
		public const int InventoryClassField700 = 0x0700;
		public const int InventoryClassFieldA78 = 0x0A78;
		public const int InventoryClassFieldA7C = 0x0A7C;
		public const int InventoryClassFieldAA4 = 0x0AA4;
		public const int InventoryClassFieldAA8 = 0x0AA8;
		public const int UniqueId = 0x0A58;
		public const int AttributeLevels = 0x0A5C;
		public const int Seed = 0x17F0;
		public const int MagicAttributeArray = 0x02A4;
		public const int MagicAttributeStride = 0x34;
		public const int MagicAttributeMaximumCount = 6;
		public const int MagicAttributeDescriptorStride = 0xA4;
		public const int MagicAttributeDescriptorTemplate = 0x04;
		public const int Name = 0x0704;
		public const int Weight = 0x06EC;
		public const int Quantity = 0x0754;
		public const int GroundStateA0 = 0xA0;
		public const int GroundStateA8 = 0xA8;
		public const int GroundStateAC = 0xAC;
		public const int GroundStateD8 = 0xD8;
		public const int GroundStateDC = 0xDC;
	}

	public static class Durability {
		public const int EquipmentManager = 0x41D44;
		public const int EquipmentSlotArray = 0x0C;
		public const int EquipmentSlotStride = 0x08;
		public const int Bias = 0x0AA4;
		public const int CharacterStride = 0x35F60;
		public const int CharacterClassRecord = 0xBDDC;
		public const int AlternateEquipmentState = 0x168E4;
		public const int ClassRelationOffset = 0x27B8;
		public const int ClassStateStride = 0x260;
		public const int ClassStateOffset = 0x08;
		public const int LegacyEquippedMirrorDistance = 0x43C;
		public const int LegacyConfirmedEquippedRecordOffsetAddress = 0x1982EC;
		public const int LegacySupportingEquippedRecordOffsetAddress = 0x197EB0;
		public const int LegacyItemRecordStride = 0x17C0;
		public const int LegacyGameRootPointer = 0x40EE1C;
		public const int LegacyEquipmentManager = 0x3BCB4;
		public const int LegacyCharacterStride = 0x2FF40;
		public const int LegacyCharacterClassRecord = 0xBD6C;
		public const int LegacyAlternateEquipmentState = 0x107B8;
		public const int LegacyCharacterClassTablePointer = 0x6611F0;
		public const int LegacyCharacterClassState = 0xB80D30;
		public const int LegacyCharacterClassType = 0x27A4;
		public const int LegacyItemRecordCurrent = 0x0A9C;
		public const int LegacyItemRecordCurrentMirror = 0x0AA0;
		public const int LegacyItemRecordMaximum = 0x0AC8;
		public const int LegacyItemRecordHeader = 0x0A98;
		public const int LegacyItemRecordDurabilityMarker = 0x0AC4;
		public const int LegacyKnownItemRegionSize = 0x21BD000;
	}

	public static class NpcMenu {
		public const int TextOffset = 0x7EC;
		public const int OptionStride = 0x69C;
		public const int AutoFsListPointer = 0x1B4;
		public const int DirectOptionBase = 0x15C;
		public const int AutoFsOptionStride = 0x57C;
		public const int OptionCount = 0x35DC;
		public const int DirectText = 0x454;
		public const int AutoFsText = 0x5B0;
		public const int TextLength = 0x554;
	}

	public static class MouseInput {
		public const int LeftState = 0xFC;
	}

	public static class Chat {
		public const int ChannelCount = 0x8E4C;
		public const int ChannelList = 0x8E50;
		public const int ChannelEntryStride = 0x2818;
		public const int ChannelNameIndex = 0x08;
		public const int ChannelCodeStride = 0x264;
		public const int InputControl = 0x438;
		public const int HistoryEntries = 0x25E;
		public const int HistoryNextSlot = 0x25D;
		public const int HistoryEntryStride = 0x200;
		public const int HistoryLengths = 0x1260;
		public const int InboxDirectList = 0x19DC;
		public static readonly int[] InboxListPath = [0x22C, 0x73C, 0x47C];
		public const int InboxSender = 0x04;
		public const int InboxSenderLength = 0x20;
		public const int InboxLinePrefix = 0x48;
	}

	public static class Combat {
		public const int HpAt82C = 0x82C;
		public const int CurrentHp = 0xCE4;
		public const int MaximumHp = 0xCE8;
	}
}
