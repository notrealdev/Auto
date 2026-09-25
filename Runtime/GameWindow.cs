// GameWindow.cs
namespace Auto.Runtime;

using Auto.Attack;
using Auto.DebugTools;
using Auto.Movement;
using Auto.Repair;
using Auto.Sale;
using Auto.Utils;

public class GameWindow {
	public object AutoSync { get; } = new();

	public RuntimeLayout RuntimeLayout { get; set; } = RuntimeLayoutResolver.Unavailable;

	public IntPtr Handle { get; set; }

	public int ProcessId { get; set; }

	public string Title { get; set; } = "";

	public bool Enabled { get; set; }

	public int Level { get; set; }

	public int Hp { get; set; } = -1;

	public int MaxHp { get; set; }

	public int Mp { get; set; } = -1;

	public int MaxMp { get; set; }

	// Sức lực mang đồ theo ô nhớ GỐC của game (InventoryStrengthReader.ReadRaw), để dòng account khớp số trong game.
	// AccountListViewModel.ApplyScanResult ghi mỗi nhịp quét. StrengthCurrent < 0 = chưa đọc được (chưa vào game).
	public int StrengthCurrent { get; set; } = -1;

	public int StrengthMaximum { get; set; }

	// Số tiền trong túi, đơn vị XU (InventoryMoneyReader). < 0 = chưa đọc được. Cùng vòng ghi với sức lực.
	public int Money { get; set; } = -1;

	public int X { get; set; }

	public int Y { get; set; }

	public int MoveTargetX { get; set; }

	public int MoveTargetY { get; set; }

	// Ghi kèm vào DebugLog để mọi dòng log gắn PID có luôn tên nhân vật. Đây là chỗ duy nhất đăng ký tên, nên
	// không thể có nơi nào cập nhật tên mà quên báo cho log.
	public string CharacterName {
		get => characterName;
		set {
			characterName = value;
			DebugLog.SetProcessName(ProcessId, value);
		}
	}

	private string characterName = "";

	public Settings AttackSettings { get; } = new();

	public Engine AttackEngine { get; }

	public Auto.Loot.Settings LootSettings { get; } = new();

	public Auto.Loot.Engine LootEngine { get; }
	public Auto.Support.Settings SupportSettings { get; } = new();
	internal Auto.Support.Engine SupportEngine { get; }
	internal Auto.Support.BuffEngine BuffEngine { get; }
	internal InventorySaleEngine InventorySaleEngine { get; }
	internal AutoFsAttackTransport AutoFsTransport { get; }
	internal AutoFsActionGate AutoFsActionGate { get; }

	public BasicSettings BasicSettings { get; } = new();
	internal LowHpEngine LowHpEngine { get; }
	internal QuickBuyEngine QuickBuyEngine { get; }

	public Auto.Market.Settings MarketSettings { get; } = new();
	internal Auto.Market.ChatEngine ChatEngine { get; }

	public Auto.Quest.Settings QuestSettings { get; } = new();

	public Auto.Quest.ScoutQuestAutomation ScoutQuestAutomation { get; } = new();

	public WeaponRepairMonitor WeaponRepairMonitor { get; } = new();

	public WeaponRepairAutomation WeaponRepairAutomation { get; } = new();

	public ReturnToTrainingAutomation ReturnToTrainingAutomation { get; } = new();

	public ConfiguredTrainingMovementAutomation ConfiguredTrainingMovementAutomation { get; } = new();

	public Dictionary<int, (int RawX, int RawY)> TrainingPositionsByMap { get; } = new();
	public int SavedTrainingMapId { get; set; }

	// Đã khôi phục hồ sơ cấu hình cho cửa sổ này chưa (AccountProfileStore).
	//
	// Chỉ khôi phục ĐÚNG MỘT LẦN, ngay lần đầu đọc được tên nhân vật. Không có cờ này thì vòng quét 1 giây của
	// AccountListViewModel.ApplyScanResult sẽ khôi phục lại mỗi nhịp và ghi đè đúng thứ người dùng vừa sửa tay
	// trên tab. Vòng đời cờ trùng khít vòng đời đối tượng nên không thể rò rỉ như bảng static khoá theo HWND.
	public bool ProfileRestored { get; set; }

	public int LastObservedMapId { get; set; }

	public DateTime NextMapIdentityCheckUtc { get; set; } = DateTime.MinValue;

	public CombatSnapshot Combat { get; set; } = new();

	public GameWindow() {
		AutoFsActionGate = new AutoFsActionGate();
		AutoFsTransport = new AutoFsAttackTransport(AutoFsActionGate);
		AttackEngine = new Engine(AttackSettings, AutoFsTransport, AutoFsActionGate);
		LootEngine = new Auto.Loot.Engine(LootSettings, AutoFsTransport, AutoFsActionGate);
		SupportEngine = new Auto.Support.Engine(SupportSettings, AutoFsTransport);
		BuffEngine = new Auto.Support.BuffEngine(SupportSettings, AutoFsTransport);
		InventorySaleEngine = new InventorySaleEngine(LootSettings, AutoFsTransport);
		LowHpEngine = new LowHpEngine(BasicSettings, AutoFsTransport);
		QuickBuyEngine = new QuickBuyEngine(BasicSettings, AutoFsTransport);
		ChatEngine = new Auto.Market.ChatEngine(MarketSettings, AutoFsTransport);
	}

	public string DisplayName {
		get {
			if (!string.IsNullOrWhiteSpace(CharacterName)) {
				return CharacterName;
			}

			return Title;
		}
	}

	public int HpPercent {
		get {
			if (Hp >= 0 && MaxHp > 0) {
				return Math.Clamp((int)Math.Round(Hp * 100.0 / MaxHp), 0, 100);
			}

			return -1;
		}
	}

	public int MpPercent {
		get {
			if (Mp >= 0 && MaxMp > 0) {
				return Math.Clamp((int)Math.Round(Mp * 100.0 / MaxMp), 0, 100);
			}

			return -1;
		}
	}

	public GameSnapshot ToSnapshot() {
		return new GameSnapshot {
			Status = SnapshotStatus.Success,
			ProcessId = ProcessId,
			CharacterName = CharacterName,
			Level = Level,
			Hp = Hp,
			MaxHp = MaxHp,
			Mp = Mp,
			MaxMp = MaxMp,
			X = X,
			Y = Y,
			MoveTargetX = MoveTargetX,
			MoveTargetY = MoveTargetY,
			Combat = Combat
		};
	}

	public override string ToString() {
		string hpText = HpPercent < 0 ? "HP: ?" : $"HP: {HpPercent}%";
		string mpText = MpPercent < 0 ? "MP: ?" : $"MP: {MpPercent}%";
		string levelText = Level > 0 ? $"Lv {Level}" : "Lv ?";

		return $"{Title} | {levelText} | PID: {ProcessId} | {hpText} | {mpText}";
	}
}
