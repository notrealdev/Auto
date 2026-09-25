namespace Auto.UI.ViewModels;

using Auto.Runtime;
using Auto.UI.Models;

public sealed class BasicViewModel : ViewModelBase {
	private const int MinDurabilityThreshold = 1;
	private const int MaxDurabilityThreshold = 1000;
	private const int MinReturnDelayMilliseconds = 0;
	private const int MaxReturnDelayMilliseconds = 60000;
	private const int MinLowHpReturnTalismanThreshold = 1;
	private const int MaxLowHpReturnTalismanThreshold = 100;
	// Khớp giới hạn 1..100 của AutoFS cho số bình mua nhanh và QuickBuyMaximumQuantity bên native.
	private const int MinQuickBuyQuantity = 1;
	private const int MaxQuickBuyQuantity = 100;

	private readonly BasicSettings settings;

	public BasicViewModel() : this(new BasicSettings()) { }

	public BasicViewModel(BasicSettings settings) {
		this.settings = settings;
	}

	public bool EnableQuickBuyHp {
		get => settings.EnableQuickBuyHp;
		set {
			if (settings.EnableQuickBuyHp == value) return;
			settings.EnableQuickBuyHp = value;
			OnPropertyChanged();
		}
	}

	public int QuickBuyHpQuantity {
		get => settings.QuickBuyHpQuantity;
		set {
			int clamped = Math.Clamp(value, MinQuickBuyQuantity, MaxQuickBuyQuantity);
			if (settings.QuickBuyHpQuantity == clamped) return;
			settings.QuickBuyHpQuantity = clamped;
			OnPropertyChanged();
		}
	}

	public bool EnableQuickBuyMp {
		get => settings.EnableQuickBuyMp;
		set {
			if (settings.EnableQuickBuyMp == value) return;
			settings.EnableQuickBuyMp = value;
			OnPropertyChanged();
		}
	}

	public int QuickBuyMpQuantity {
		get => settings.QuickBuyMpQuantity;
		set {
			int clamped = Math.Clamp(value, MinQuickBuyQuantity, MaxQuickBuyQuantity);
			if (settings.QuickBuyMpQuantity == clamped) return;
			settings.QuickBuyMpQuantity = clamped;
			OnPropertyChanged();
		}
	}

	// Danh sách lấy từ QuickBuyPotions để UI và engine mua nhanh không thể lệch nhau.
	public IReadOnlyList<PotionOption> QuickBuyHpPotions { get; } = [.. QuickBuyPotions.Hp.Select(potion => new PotionOption(potion.Name, potion.Code))];

	public IReadOnlyList<PotionOption> QuickBuyMpPotions { get; } = [.. QuickBuyPotions.Mp.Select(potion => new PotionOption(potion.Name, potion.Code))];

	public int QuickBuyHpPotionCode {
		get => settings.QuickBuyHpPotionCode;
		set {
			if (settings.QuickBuyHpPotionCode == value) return;
			settings.QuickBuyHpPotionCode = value;
			OnPropertyChanged();
		}
	}

	public int QuickBuyMpPotionCode {
		get => settings.QuickBuyMpPotionCode;
		set {
			if (settings.QuickBuyMpPotionCode == value) return;
			settings.QuickBuyMpPotionCode = value;
			OnPropertyChanged();
		}
	}

	public bool EnableQuickBuyAtDoctor {
		get => settings.EnableQuickBuyAtDoctor;
		set {
			if (settings.EnableQuickBuyAtDoctor == value) return;
			settings.EnableQuickBuyAtDoctor = value;
			OnPropertyChanged();
		}
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
