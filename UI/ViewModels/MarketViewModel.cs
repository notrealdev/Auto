namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using Auto.Market;
using Auto.Runtime;
using Auto.UI.Models;

public sealed class MarketViewModel : ViewModelBase {
	public static IReadOnlyList<AdvertiseTemplateOption> Templates { get; } = [
		new("Suc vat", "Địt mẹ thằng súc vật PhiDương, bố mẹ mày ra đường bị ô tô đâm chết, vợ con mày bị hàng xóm hiếp dâm đến chết, Đào Hoa Đảo chứa chấp 1 lũ chó súc vật:@:@:@:@:@:@:@"),
		new("Mua Xu", "Mua xu giá 4v/1 ai bán pm nào :0:0:0"),
		new("Ban PB 7X", "Bán pb 7x sll 299x/1 ai mua pm nhé :$"),
		new("Ban Lieu", "Bán 145K liễu mộc sll 99X/100, ai mua pm :E"),
		new("Mua MHTT", "MUA MHTT, ABPM + giá :B"),
		new("Mua MHTT 55X/1", "M >>>>>>>>>>>> MHTT 55X/1 ==== SLL ==== ABPM :o")
	];

	private readonly Settings settings;
	// Selected client, read when the item list opens; null when no account is selected.
	private readonly GameWindow? game;

	public MarketViewModel() : this(new Settings(), null) { }

	public MarketViewModel(Settings settings, GameWindow? game) {
		this.settings = settings;
		this.game = game;
		InsertTemplateCommand = new RelayCommand(parameter => {
			if (parameter is AdvertiseTemplateOption template) AdvertiseText += template.Content;
		});
		RequestItemOptionsCommand = new RelayCommand(_ => RequestItemOptions());
		InsertItemCommand = new RelayCommand(parameter => {
			if (parameter is ChatItemOption item) AdvertiseText += ChatItemLink.ToPlaceholder(item.UniqueId, item.Name);
		});
		InsertMapCommand = new RelayCommand(parameter => {
			if (parameter is ChatMapOption map) AdvertiseText += ChatItemLink.ToMapPlaceholder(map);
		});
		RequestHistoryOptionsCommand = new RelayCommand(_ => RequestHistoryOptions());
		InsertHistoryCommand = new RelayCommand(parameter => {
			if (parameter is ChatHistoryOption option) InsertHistory(option);
		});
		ClearAdvertiseTextCommand = new RelayCommand(_ => AdvertiseText = "");
	}

	public static IReadOnlyList<ChatMapOption> MapOptions => ChatItemLink.MapOptions;

	public ObservableCollection<ChatItemOption> ItemOptions { get; } = [];

	// Re-read when the list opens: the bag changes all the time.
	private void RequestItemOptions() {
		ItemOptions.Clear();
		if (game == null) return;
		try {
			foreach (ChatItemOption item in ChatItemLink.ListItems(game.ProcessId)) ItemOptions.Add(item);
		} catch {
			// A failed read leaves the list empty; the next opening reads again.
		}
	}

	public bool AutoAdvertise {
		get => settings.AutoAdvertise;
		set {
			if (settings.AutoAdvertise == value) return;
			settings.AutoAdvertise = value;
			OnPropertyChanged();
		}
	}

	public string AdvertiseText {
		get => settings.AdvertiseText;
		set {
			string clamped = value.Length > ChatEngine.MaximumMessageLength ? value[..ChatEngine.MaximumMessageLength] : value;
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
	public RelayCommand RequestItemOptionsCommand { get; }
	public RelayCommand InsertItemCommand { get; }
	public RelayCommand InsertMapCommand { get; }
	public RelayCommand RequestHistoryOptionsCommand { get; }
	public RelayCommand InsertHistoryCommand { get; }
	public RelayCommand ClearAdvertiseTextCommand { get; }

	public ObservableCollection<ChatHistoryOption> HistoryOptions { get; } = [];

	// Re-read when the list opens: every message the player sends changes the client's history.
	private void RequestHistoryOptions() {
		HistoryOptions.Clear();
		if (game == null) return;
		try {
			IReadOnlyList<byte[]> entries = ChatHistoryReader.ReadEntries(game.ProcessId);
			IReadOnlyList<ChatItemOption> items = entries.Count > 0 ? ChatItemLink.ListItems(game.ProcessId) : [];
			for (int index = 0; index < entries.Count; index++) {
				string preview = string.Concat(Describe(entries[index], items).Select(part => part.IsLink ? $"[{part.Label}]" : part.Label));
				HistoryOptions.Add(new ChatHistoryOption(index + 1, $"{index + 1}. {preview}", entries[index]));
			}
		} catch {
			// A failed read leaves the list empty; the next opening reads again.
		}
	}

	// Inserts the whole message: text as editable text, every link as "{link:KEY:label}" whose bytes are kept in
	// CapturedLinks exactly as the client sent them.
	private void InsertHistory(ChatHistoryOption option) {
		IReadOnlyList<ChatItemOption> items = [];
		try {
			if (game != null) items = ChatItemLink.ListItems(game.ProcessId);
		} catch {
			// Without the bag an item link is labelled "Vật phẩm"; its bytes are unaffected.
		}
		Dictionary<string, byte[]> captured = ChatHistoryReader.ParseCaptured(settings.CapturedLinks);
		System.Text.StringBuilder text = new();
		foreach (ChatHistorySegment part in Describe(option.Message, items)) {
			if (! part.IsLink) {
				text.Append(part.Label);
				continue;
			}
			string key = ChatHistoryReader.KeyOf(part.Bytes);
			captured[key] = part.Bytes;
			text.Append(ChatHistoryReader.ToPlaceholder(key, part.Label));
		}
		AdvertiseText += text.ToString();
		// Keep only the links the content still uses, so deleted placeholders do not pile up in the profile.
		HashSet<string> used = ChatItemLink.PlaceholderPattern().Matches(AdvertiseText)
			.Where(match => match.Groups[6].Success)
			.Select(match => match.Groups[6].Value)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		settings.CapturedLinks = ChatHistoryReader.FormatCaptured(captured.Where(pair => used.Contains(pair.Key)).ToDictionary());
	}

	// Splits a history message and names its item links after the bag item with the same id (link bytes 31-34 = record
	// +0xA58, ChatItemLink); the id is not unique, so the name is only a label and never decides what is sent.
	private static IEnumerable<ChatHistorySegment> Describe(byte[] message, IReadOnlyList<ChatItemOption> items) {
		foreach (ChatHistorySegment part in ChatHistoryReader.Split(message)) {
			if (part.IsLink && part.Bytes[0] == 0x0B && part.Bytes.Length == ChatItemLink.Length) {
				uint uniqueId = BitConverter.ToUInt32(part.Bytes, 31);
				ChatItemOption? item = items.FirstOrDefault(candidate => candidate.UniqueId == uniqueId);
				yield return item == null ? part : part with { Label = item.Name };
				continue;
			}
			yield return part;
		}
	}
}
