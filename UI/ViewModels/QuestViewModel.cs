namespace Auto.UI.ViewModels;

using Auto.Quest;

// Tab "Nhiệm vụ". Các ComboBox bind SelectedIndex (int) để khớp đúng cách AutoFS lưu trong NhiệmVụ.json,
// nhờ đó khi port engine sau này không phải dịch qua enum trung gian.
public sealed class QuestViewModel : ViewModelBase {
	private const int QuestTypeCount = 2;
	private const int CultivationTypeCount = 2;
	private const int RoundTripTalismanTypeCount = 2;
	private const int ReceiveMapCount = 2;
	private const int CamelTypeCount = 2;

	private readonly Settings settings;

	public QuestViewModel() : this(new Settings()) { }

	public QuestViewModel(Settings settings) {
		this.settings = settings;
	}

	// ===== Thám Quân =====

	public bool ScoutEnabled {
		get => settings.ScoutEnabled;
		set {
			if (settings.ScoutEnabled == value) return;
			settings.ScoutEnabled = value;
			OnPropertyChanged();
		}
	}

	public int ScoutQuestType {
		get => settings.ScoutQuestType;
		set {
			int clamped = Math.Clamp(value, 0, QuestTypeCount - 1);
			if (settings.ScoutQuestType == clamped) return;
			settings.ScoutQuestType = clamped;
			OnPropertyChanged();
		}
	}

	public int ScoutCultivationType {
		get => settings.ScoutCultivationType;
		set {
			int clamped = Math.Clamp(value, 0, CultivationTypeCount - 1);
			if (settings.ScoutCultivationType == clamped) return;
			settings.ScoutCultivationType = clamped;
			OnPropertyChanged();
		}
	}

	public bool ScoutUseGoldenCard {
		get => settings.ScoutUseGoldenCard;
		set {
			if (settings.ScoutUseGoldenCard == value) return;
			settings.ScoutUseGoldenCard = value;
			OnPropertyChanged();
		}
	}

	public bool ScoutOutboundTravelTalisman {
		get => settings.ScoutOutboundTravelTalisman;
		set {
			if (settings.ScoutOutboundTravelTalisman == value) return;
			settings.ScoutOutboundTravelTalisman = value;
			OnPropertyChanged();
		}
	}

	public bool ScoutReturnRoundTripTalisman {
		get => settings.ScoutReturnRoundTripTalisman;
		set {
			if (settings.ScoutReturnRoundTripTalisman == value) return;
			settings.ScoutReturnRoundTripTalisman = value;
			OnPropertyChanged();
		}
	}

	public int ScoutReturnRoundTripTalismanType {
		get => settings.ScoutReturnRoundTripTalismanType;
		set {
			int clamped = Math.Clamp(value, 0, RoundTripTalismanTypeCount - 1);
			if (settings.ScoutReturnRoundTripTalismanType == clamped) return;
			settings.ScoutReturnRoundTripTalismanType = clamped;
			OnPropertyChanged();
		}
	}

	public bool ScoutReturnTownTalisman {
		get => settings.ScoutReturnTownTalisman;
		set {
			if (settings.ScoutReturnTownTalisman == value) return;
			settings.ScoutReturnTownTalisman = value;
			OnPropertyChanged();
		}
	}

}
