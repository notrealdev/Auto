namespace Auto.UI.ViewModels;

using Auto.Support;

public sealed class SupportViewModel : ViewModelBase {
	private readonly Settings settings;

	public SupportViewModel() : this(new Settings()) { }

	public SupportViewModel(Settings settings) {
		this.settings = settings;
	}

	public bool BuffThreeSystems {
		get => settings.BuffThreeSystems;
		set {
			if (settings.BuffThreeSystems == value) return;
			settings.BuffThreeSystems = value;
			OnPropertyChanged();
		}
	}

	public bool HealOwner {
		get => settings.HealOwner;
		set {
			if (settings.HealOwner == value) return;
			settings.HealOwner = value;
			OnPropertyChanged();
		}
	}

	public int HealOwnerHpPercent {
		get => settings.HealOwnerHpPercent;
		set {
			int clamped = Math.Clamp(value, 1, 100);
			if (settings.HealOwnerHpPercent == clamped) return;
			settings.HealOwnerHpPercent = clamped;
			OnPropertyChanged();
		}
	}

	public bool HealPet {
		get => settings.HealPet;
		set {
			if (settings.HealPet == value) return;
			settings.HealPet = value;
			OnPropertyChanged();
		}
	}

	public int HealPetHpPercent {
		get => settings.HealPetHpPercent;
		set {
			int clamped = Math.Clamp(value, 1, 100);
			if (settings.HealPetHpPercent == clamped) return;
			settings.HealPetHpPercent = clamped;
			OnPropertyChanged();
		}
	}
}
