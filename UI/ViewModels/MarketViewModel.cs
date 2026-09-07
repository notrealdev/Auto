namespace Auto.UI.ViewModels;

using Auto.Market;
using Auto.UI.Models;

public sealed class MarketViewModel : ViewModelBase {
	public static IReadOnlyList<AdvertiseTemplateOption> Templates { get; } = [
		new("Mua xu", "Mua xu"),
		new("Bán xu", "Bán xu"),
		new("Mua máu", "Mua máu"),
		new("Bán máu", "Bán máu")
	];

	private readonly Settings settings;
	private AdvertiseTemplateOption selectedTemplate = Templates[0];

	public MarketViewModel() : this(new Settings()) { }

	public MarketViewModel(Settings settings) {
		this.settings = settings;
		InsertTemplateCommand = new RelayCommand(_ => AdvertiseText += SelectedTemplate.Content);
	}

	public bool AutoAdvertise {
		get => settings.AutoAdvertise;
		set {
			if (settings.AutoAdvertise == value) return;
			settings.AutoAdvertise = value;
			OnPropertyChanged();
		}
	}

	public AdvertiseTemplateOption SelectedTemplate {
		get => selectedTemplate;
		set => SetField(ref selectedTemplate, value);
	}

	public string AdvertiseText {
		get => settings.AdvertiseText;
		set {
			string clamped = value.Length > 180 ? value[..180] : value;
			if (settings.AdvertiseText == clamped) return;
			settings.AdvertiseText = clamped;
			OnPropertyChanged();
		}
	}

	public bool NearbyEnabled {
		get => settings.NearbyEnabled;
		set {
			if (settings.NearbyEnabled == value) return;
			settings.NearbyEnabled = value;
			OnPropertyChanged();
		}
	}

	public int NearbyDelaySeconds {
		get => settings.NearbyDelaySeconds;
		set {
			int clamped = Math.Clamp(value, 1, 3600);
			if (settings.NearbyDelaySeconds == clamped) return;
			settings.NearbyDelaySeconds = clamped;
			OnPropertyChanged();
		}
	}

	public bool AreaEnabled {
		get => settings.AreaEnabled;
		set {
			if (settings.AreaEnabled == value) return;
			settings.AreaEnabled = value;
			OnPropertyChanged();
		}
	}

	public int AreaDelaySeconds {
		get => settings.AreaDelaySeconds;
		set {
			int clamped = Math.Clamp(value, 1, 3600);
			if (settings.AreaDelaySeconds == clamped) return;
			settings.AreaDelaySeconds = clamped;
			OnPropertyChanged();
		}
	}

	public bool TradeEnabled {
		get => settings.TradeEnabled;
		set {
			if (settings.TradeEnabled == value) return;
			settings.TradeEnabled = value;
			OnPropertyChanged();
		}
	}

	public int TradeDelaySeconds {
		get => settings.TradeDelaySeconds;
		set {
			int clamped = Math.Clamp(value, 1, 3600);
			if (settings.TradeDelaySeconds == clamped) return;
			settings.TradeDelaySeconds = clamped;
			OnPropertyChanged();
		}
	}

	public bool TerritoryEnabled {
		get => settings.TerritoryEnabled;
		set {
			if (settings.TerritoryEnabled == value) return;
			settings.TerritoryEnabled = value;
			OnPropertyChanged();
		}
	}

	public int TerritoryDelaySeconds {
		get => settings.TerritoryDelaySeconds;
		set {
			int clamped = Math.Clamp(value, 1, 3600);
			if (settings.TerritoryDelaySeconds == clamped) return;
			settings.TerritoryDelaySeconds = clamped;
			OnPropertyChanged();
		}
	}

	public RelayCommand InsertTemplateCommand { get; }
}
