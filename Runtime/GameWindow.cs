// GameWindow.cs
namespace Auto.Runtime;

using Auto.Attack;
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

	public int X { get; set; }

	public int Y { get; set; }

	public int MoveTargetX { get; set; }

	public int MoveTargetY { get; set; }

	public string CharacterName { get; set; } = "";

	public Settings AttackSettings { get; } = new();

	public Engine AttackEngine { get; }

	public Auto.Loot.Settings LootSettings { get; } = new();

	public Auto.Loot.Engine LootEngine { get; }
	public Auto.Support.Settings SupportSettings { get; } = new();
	internal Auto.Support.Engine SupportEngine { get; }
	internal InventorySaleEngine InventorySaleEngine { get; }
	internal AutoFsAttackTransport AutoFsTransport { get; }
	internal AutoFsActionGate AutoFsActionGate { get; }

	public BasicSettings BasicSettings { get; } = new();
	internal LowHpReturnTalismanEngine LowHpReturnTalismanEngine { get; }

	public Auto.Market.Settings MarketSettings { get; } = new();
	internal Auto.Market.AutoAdvertiseEngine AutoAdvertiseEngine { get; }

	public WeaponRepairMonitor WeaponRepairMonitor { get; } = new();

	public WeaponRepairAutomation WeaponRepairAutomation { get; } = new();

	public ReturnToTrainingAutomation ReturnToTrainingAutomation { get; } = new();

	public ConfiguredTrainingMovementAutomation ConfiguredTrainingMovementAutomation { get; } = new();

	public Dictionary<int, (int RawX, int RawY)> TrainingPositionsByMap { get; } = new();
	public int SavedTrainingMapId { get; set; }

	public int LastObservedMapId { get; set; }

	public DateTime NextMapIdentityCheckUtc { get; set; } = DateTime.MinValue;

	public CombatSnapshot Combat { get; set; } = new();

	public GameWindow() {
		AutoFsActionGate = new AutoFsActionGate();
		AutoFsTransport = new AutoFsAttackTransport(AutoFsActionGate);
		AttackEngine = new Engine(AttackSettings, AutoFsTransport, AutoFsActionGate);
		LootEngine = new Auto.Loot.Engine(LootSettings, AutoFsTransport, AutoFsActionGate);
		SupportEngine = new Auto.Support.Engine(SupportSettings, AutoFsTransport);
		InventorySaleEngine = new InventorySaleEngine(LootSettings, AutoFsTransport);
		LowHpReturnTalismanEngine = new LowHpReturnTalismanEngine(BasicSettings, AutoFsTransport);
		AutoAdvertiseEngine = new Auto.Market.AutoAdvertiseEngine(MarketSettings, AutoFsTransport);
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
