namespace Auto.UI.ViewModels;

using Auto.Runtime;

public sealed class BasicViewModel : ViewModelBase {
	private const int MinDurabilityThreshold = 1;
	private const int MaxDurabilityThreshold = 1000;
	private const int MinReturnDelayMilliseconds = 0;
	private const int MaxReturnDelayMilliseconds = 60000;
	private const int MinLowHpReturnTalismanThreshold = 1;
	private const int MaxLowHpReturnTalismanThreshold = 100;

	private readonly BasicSettings settings;

	public BasicViewModel() : this(new BasicSettings()) { }

	public BasicViewModel(BasicSettings settings) {
		this.settings = settings;
	}

	public bool EnableLowHpReturnTalisman {
		get => settings.EnableLowHpReturnTalisman;
		set {
			if (settings.EnableLowHpReturnTalisman == value) return;
			settings.EnableLowHpReturnTalisman = value;
			OnPropertyChanged();
		}
	}

	public int LowHpReturnTalismanThreshold {
		get => settings.LowHpReturnTalismanThreshold;
		set {
			int clamped = Math.Clamp(value, MinLowHpReturnTalismanThreshold, MaxLowHpReturnTalismanThreshold);
			if (settings.LowHpReturnTalismanThreshold == clamped) return;
			settings.LowHpReturnTalismanThreshold = clamped;
			OnPropertyChanged();
		}
	}

	public bool EnableWeaponRepair {
		get => settings.EnableWeaponRepair;
		set {
			if (settings.EnableWeaponRepair == value) return;
			settings.EnableWeaponRepair = value;
			OnPropertyChanged();
		}
	}

	public int WeaponDurabilityThreshold {
		get => settings.WeaponDurabilityThreshold;
		set {
			int clamped = Math.Clamp(value, MinDurabilityThreshold, MaxDurabilityThreshold);
			if (settings.WeaponDurabilityThreshold == clamped) return;
			settings.WeaponDurabilityThreshold = clamped;
			OnPropertyChanged();
		}
	}

	public IReadOnlyList<DeathAction> DeathActions { get; } = [DeathAction.StayStill, DeathAction.ReturnToTown, DeathAction.Substitute, DeathAction.Revive];

	public DeathAction DeathAction {
		get => settings.DeathAction;
		set {
			if (settings.DeathAction == value) return;
			settings.DeathAction = value;
			OnPropertyChanged();
		}
	}

	public int ReturnToTownDelayMilliseconds {
		get => settings.ReturnToTownDelayMilliseconds;
		set {
			int clamped = Math.Clamp(value, MinReturnDelayMilliseconds, MaxReturnDelayMilliseconds);
			if (settings.ReturnToTownDelayMilliseconds == clamped) return;
			settings.ReturnToTownDelayMilliseconds = clamped;
			OnPropertyChanged();
		}
	}
}
