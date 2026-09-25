namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using System.Windows;
using Auto.Loot;
using Auto.Runtime;
using Auto.UI.Views;
using Auto.Utils;

public sealed class LootViewModel : ViewModelBase {
	private static readonly string[] BuiltInMaterialItems = ["Đồ Trắng", "Đồ Xanh", Finder.GreenWeaponSelectionName, "Đồ Lục", "Đồ Vàng", "Đồ Cam", "Đồ Khác", "Thảo Dược", "Mảnh, Ngọc", "Bí Kíp", "Pháp Bảo", "Quẻ", "Lục Đạo", "Tứ Tượng", "Nhãn Vạn Tiên Trận", Finder.MountUpgradeSelectionName];
	private static readonly string[] BuiltInExcludedOrSaleItems = ["Đồ Trắng", "Đồ Xanh", "Dược Phẩm"];
	private static readonly string[] PotionNames = ["Tiểu Hồng đơn", "Trung Hồng đơn", "Đại Hồng đơn", "Tiểu Hoàn đơn", "Trung Hoàn đơn", "Đại Hoàn đơn"];

	// Nhãn trên mỗi ô xổ xuống. Sau mỗi lượt quét, nhãn nói rõ quét được bao nhiêu món hoặc vì sao không có món nào.
	//
	// Vì sao cần: ô xổ xuống rỗng có thể là "không có đồ nào dưới đất" (bình thường) hoặc "bị chặn ở cổng
	// readiness" (hỏng), mà nhìn vào thì hai ca giống hệt nhau — đúng chỗ làm chủ dự án báo "dropdown chưa hoạt
	// động" 2026-09-23 trong khi đo bằng scratchpad thì hàm quét đất trả về đúng 0 món vì lúc đó bãi sạch đồ.
	private const string GroundHintDefault = "Chọn thêm từ đồ dưới đất";
	private const string SaleHintDefault = "Chọn thêm từ đồ trong túi";

	private readonly Settings settings;
	// Client đang chọn, để quét được đồ thật lúc mở danh sách xổ xuống. null khi chưa chọn account nào — lúc đó
	// danh sách chỉ hiện những mục đã lưu, không quét gì (cùng cách AttackViewModel xử lý game == null).
	private readonly GameWindow? game;
	private string materialCatalogHint = GroundHintDefault;
	private string excludedCatalogHint = GroundHintDefault;
	private string saleCatalogHint = SaleHintDefault;

	public LootViewModel() : this(new Settings(), null) { }

	public LootViewModel(Settings settings, GameWindow? game) {
		this.settings = settings;
		this.game = game;

		MaterialItems = CreateMaterialItems();
		ExcludedItems = CreateDictionaryItems(BuiltInExcludedOrSaleItems, settings.ExcludedItemSelections);
		SaleItems = CreateDictionaryItems(BuiltInExcludedOrSaleItems, settings.SaleItemSelections);
		PotionNameSelections = CreateDictionaryItems(PotionNames, settings.PotionNameSelections);

		RequestMaterialOptionsCommand = new RelayCommand(_ => RequestGroundOptions(MaterialCatalog, BuiltInMaterialItems, hint => MaterialCatalogHint = hint));
		RequestExcludedOptionsCommand = new RelayCommand(_ => RequestGroundOptions(ExcludedCatalog, BuiltInExcludedOrSaleItems, hint => ExcludedCatalogHint = hint));
		RequestSaleOptionsCommand = new RelayCommand(_ => RequestSaleOptions());
		AddMaterialFromCatalogCommand = new RelayCommand(parameter => AddFromCatalog(parameter as string, MaterialItems, ApplyMaterialSelection));
		AddExcludedFromCatalogCommand = new RelayCommand(parameter => AddFromCatalog(parameter as string, ExcludedItems, ApplyExcludedSelection));
		AddSaleFromCatalogCommand = new RelayCommand(parameter => AddFromCatalog(parameter as string, SaleItems, ApplySaleSelection));
		RemoveItemCommand = new RelayCommand(parameter => {
			if (parameter is not CheckableItemViewModel { IsBuiltIn: false } item) return;
			if (MaterialItems.Remove(item)) { settings.ItemSelections.Remove(item.Name); return; }
			if (ExcludedItems.Remove(item)) { settings.ExcludedItemSelections.Remove(item.Name); return; }
			if (SaleItems.Remove(item)) settings.SaleItemSelections.Remove(item.Name);
		});
		OpenPotionNamesCommand = new RelayCommand(_ => OpenPotionNamesDialog());
	}

	public Settings Settings => settings;

	public bool Enabled {
		get => settings.Enabled;
		set {
			if (settings.Enabled == value) return;
			settings.Enabled = value;
			OnPropertyChanged();
		}
	}

	public bool PickPotion {
		get => settings.ItemSelections.TryGetValue("Dược Phẩm", out bool enabled) && enabled;
		set {
			if (PickPotion == value) return;
			settings.ItemSelections["Dược Phẩm"] = value;
			OnPropertyChanged();
		}
	}

	public ObservableCollection<CheckableItemViewModel> MaterialItems { get; }

	public ObservableCollection<CheckableItemViewModel> ExcludedItems { get; }

	public ObservableCollection<CheckableItemViewModel> SaleItems { get; }

	// Ba danh sách nguồn cho ô xổ xuống. Chỉ là TÊN, không có checkbox (chủ dự án chốt 2026-09-23): chọn một dòng
	// là món đó được thêm thẳng xuống danh sách bên dưới, nên checkbox trong ô xổ xuống chỉ là bước thừa.
	// Dựng lại TỪ ĐẦU mỗi lần mở (xem RebuildCatalog) nên không giữ trạng thái cũ.
	public ObservableCollection<string> MaterialCatalog { get; } = [];

	public ObservableCollection<string> ExcludedCatalog { get; } = [];

	public ObservableCollection<string> SaleCatalog { get; } = [];

	public string MaterialCatalogHint {
		get => materialCatalogHint;
		private set => SetField(ref materialCatalogHint, value);
	}

	public string ExcludedCatalogHint {
		get => excludedCatalogHint;
		private set => SetField(ref excludedCatalogHint, value);
	}

	public string SaleCatalogHint {
		get => saleCatalogHint;
		private set => SetField(ref saleCatalogHint, value);
	}

	public bool SaleQuantityThresholdEnabled {
		get => settings.EnableSaleQuantityThreshold;
		set {
			if (settings.EnableSaleQuantityThreshold == value) return;
			settings.EnableSaleQuantityThreshold = value;
			OnPropertyChanged();
		}
	}

	public int SaleQuantityThreshold {
		get => settings.SaleQuantityThreshold;
		set {
			int clamped = Math.Clamp(value, 0, 200);
			if (settings.SaleQuantityThreshold == clamped) return;
			settings.SaleQuantityThreshold = clamped;
			OnPropertyChanged();
		}
	}

	public bool SaleRemainingStrengthThresholdEnabled {
		get => settings.EnableSaleRemainingStrengthThreshold;
		set {
			if (settings.EnableSaleRemainingStrengthThreshold == value) return;
			settings.EnableSaleRemainingStrengthThreshold = value;
			OnPropertyChanged();
		}
	}

	public int SaleRemainingStrengthThreshold {
		get => settings.SaleRemainingStrengthThreshold;
		set {
			int clamped = Math.Clamp(value, 0, 999);
			if (settings.SaleRemainingStrengthThreshold == clamped) return;
			settings.SaleRemainingStrengthThreshold = clamped;
			OnPropertyChanged();
		}
	}

	public ObservableCollection<CheckableItemViewModel> PotionNameSelections { get; }

	public RelayCommand RequestMaterialOptionsCommand { get; }

	public RelayCommand RequestExcludedOptionsCommand { get; }

	public RelayCommand RequestSaleOptionsCommand { get; }

	public RelayCommand AddMaterialFromCatalogCommand { get; }

	public RelayCommand AddExcludedFromCatalogCommand { get; }

	public RelayCommand AddSaleFromCatalogCommand { get; }

	public RelayCommand RemoveItemCommand { get; }

	public RelayCommand OpenPotionNamesCommand { get; }

	// Màu sắc (Đồ Trắng/Xanh/Lục/Vàng/Cam/Khác) được Loot.Finder đọc qua Pick* bool riêng, không qua ItemSelections dict.
	private bool GetMaterialFlag(string name) => name switch {
		"Đồ Trắng" => settings.PickWhite,
		"Đồ Xanh" => settings.PickBlue,
		"Đồ Lục" => settings.PickGreen,
		"Đồ Vàng" => settings.PickYellow,
		"Đồ Cam" => settings.PickOrange,
		"Đồ Khác" => settings.PickOtherColor,
		_ => settings.ItemSelections.TryGetValue(name, out bool enabled) && enabled
	};

	private void SetMaterialFlag(string name, bool value) {
		switch (name) {
			case "Đồ Trắng": settings.PickWhite = value; break;
			case "Đồ Xanh": settings.PickBlue = value; break;
			case "Đồ Lục": settings.PickGreen = value; break;
			case "Đồ Vàng": settings.PickYellow = value; break;
			case "Đồ Cam": settings.PickOrange = value; break;
			case "Đồ Khác": settings.PickOtherColor = value; break;
			default: settings.ItemSelections[name] = value; break;
		}
	}

	private ObservableCollection<CheckableItemViewModel> CreateMaterialItems() {
		ObservableCollection<CheckableItemViewModel> items = new(
			BuiltInMaterialItems.Select(name => new CheckableItemViewModel(name, GetMaterialFlag(name), true, value => SetMaterialFlag(name, value))));
		foreach ((string name, bool enabled) in settings.ItemSelections) {
			if (BuiltInMaterialItems.Contains(name) || name == "Dược Phẩm") continue;
			items.Add(new CheckableItemViewModel(name, enabled, false, value => SetMaterialFlag(name, value)));
		}
		return items;
	}

	private static ObservableCollection<CheckableItemViewModel> CreateDictionaryItems(IReadOnlyCollection<string> builtInNames, Dictionary<string, bool> dictionary) {
		ObservableCollection<CheckableItemViewModel> items = new(
			builtInNames.Select(name => new CheckableItemViewModel(name, dictionary.TryGetValue(name, out bool enabled) && enabled, true, value => dictionary[name] = value)));
		foreach ((string name, bool enabled) in dictionary) {
			if (builtInNames.Contains(name)) continue;
			items.Add(new CheckableItemViewModel(name, enabled, false, value => dictionary[name] = value));
		}
		return items;
	}

	// Bỏ tick thì XOÁ HẲN khoá khỏi dictionary chứ không để lại false: tên món tự chọn là dữ liệu do người dùng
	// thêm vào, giữ lại giá trị false chỉ làm Profiles.json phình ra theo từng món từng đi ngang qua bãi.
	// Ba mục dựng sẵn không đi qua đây (chúng bị loại khỏi catalog) nên giá trị false của chúng vẫn được giữ.
	private void ApplyMaterialSelection(string name, bool value) {
		if (value) SetMaterialFlag(name, true);
		else settings.ItemSelections.Remove(name);
	}

	private void ApplyExcludedSelection(string name, bool value) {
		if (value) settings.ExcludedItemSelections[name] = true;
		else settings.ExcludedItemSelections.Remove(name);
	}

	private void ApplySaleSelection(string name, bool value) {
		if (value) settings.SaleItemSelections[name] = true;
		else settings.SaleItemSelections.Remove(name);
	}

	// Quét đồ DƯỚI ĐẤT cho hai mục Vật phẩm / Không nhặt.
	private void RequestGroundOptions(ObservableCollection<string> catalog, IReadOnlyCollection<string> builtInNames, Action<string> setHint) {
		if (game == null) {
			catalog.Clear();
			setHint($"{GroundHintDefault} — chưa chọn tài khoản nào");
			return;
		}
		IReadOnlyList<string> names;
		// lock(game.AutoSync) bắt buộc: EngineTick chạy Parallel.ForEach(TickOne) trên thread pool và TickOne giữ
		// đúng khoá này, nên hai bên chạy song song thật. Cùng lý do với AttackViewModel.RequestMonsterOptions.
		lock (game.AutoSync) {
			game.RuntimeLayout = RuntimeLayoutResolver.Resolve(game.ProcessId);
			if (! game.RuntimeLayout.LootReady) {
				catalog.Clear();
				setHint($"{GroundHintDefault} — client chưa sẵn sàng (Ground/LootTransport)");
				return;
			}
			names = game.LootEngine.DiscoverItemOptions(GameMemory.ReadSnapshot(game.ProcessId));
		}
		int shown = RebuildCatalog(catalog, names, builtInNames);
		// Tầm quét bằng đúng ô Range của Nhặt, quy đổi sang raw ở Finder.FindVisibleSpriteItemsForAttackSafety.
		// Ghi rõ con số để biết bãi sạch đồ hay là đứng quá xa chỗ đồ rơi.
		setHint(shown > 0
			? $"{GroundHintDefault} — thấy {shown} món trong {Math.Max(settings.Range, 10)} ô"
			: $"{GroundHintDefault} — không có món nào trong {Math.Max(settings.Range, 10)} ô");
	}

	// Quét đồ TRONG TÚI cho mục Bán — nguồn khác hẳn hai mục trên, và cổng readiness cũng khác (SaleReady).
	private void RequestSaleOptions() {
		if (game == null) {
			SaleCatalog.Clear();
			SaleCatalogHint = $"{SaleHintDefault} — chưa chọn tài khoản nào";
			return;
		}
		IReadOnlyList<string> names;
		lock (game.AutoSync) {
			game.RuntimeLayout = RuntimeLayoutResolver.Resolve(game.ProcessId);
			if (! game.RuntimeLayout.SaleReady) {
				SaleCatalog.Clear();
				SaleCatalogHint = $"{SaleHintDefault} — client chưa sẵn sàng (Inventory/Shop)";
				return;
			}
			names = game.InventorySaleEngine.DiscoverItemOptions(game.ProcessId);
		}
		int shown = RebuildCatalog(SaleCatalog, names, BuiltInExcludedOrSaleItems);
		SaleCatalogHint = shown > 0 ? $"{SaleHintDefault} — thấy {shown} món" : $"{SaleHintDefault} — túi trống";
	}

	// Chỉ đổ TÊN vào ô xổ xuống; việc chọn được xử lý ở AddFromCatalog. Mục dựng sẵn bị loại vì chúng đã có sẵn
	// checkbox riêng ở danh sách bên dưới.
	private static int RebuildCatalog(ObservableCollection<string> catalog, IReadOnlyList<string> discoveredNames, IReadOnlyCollection<string> builtInNames) {
		catalog.Clear();
		foreach (string name in discoveredNames) {
			if (builtInNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
			catalog.Add(name);
		}
		return catalog.Count;
	}

	// Chọn một dòng trong ô xổ xuống = thêm món đó xuống danh sách bên dưới, ở trạng thái ĐÃ tick.
	//
	// Khác với bản trước: hồi đó ô xổ xuống liệt kê cả những món chưa chọn nên bắt buộc phải để chúng CHƯA tick,
	// không thì mở ra là bán sạch túi. Giờ chỉ món người dùng chủ động chọn mới đi xuống danh sách, nên tick sẵn
	// là đúng ý định của thao tác đó.
	private static void AddFromCatalog(string? name, ObservableCollection<CheckableItemViewModel> selectedList, Action<string, bool> applySelection) {
		if (string.IsNullOrWhiteSpace(name)) return;
		applySelection(name, true);
		SyncSelectedList(selectedList, name, true, applySelection);
	}

	// Giữ danh sách hiển thị bên trên khớp với thao tác tick trong ô xổ xuống (chủ dự án chốt 2026-09-23: danh
	// sách bên dưới ở lại, ô xổ xuống chỉ để chọn thêm).
	private static void SyncSelectedList(ObservableCollection<CheckableItemViewModel> selectedList, string name, bool value, Action<string, bool> applySelection) {
		CheckableItemViewModel? existing = selectedList.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
		if (value) {
			if (existing == null) selectedList.Add(new CheckableItemViewModel(name, true, false, next => applySelection(name, next)));
			else existing.IsChecked = true;
			return;
		}
		// Chỉ gỡ món tự chọn; mục dựng sẵn luôn ở lại danh sách kể cả khi bỏ tick.
		if (existing is { IsBuiltIn: false }) selectedList.Remove(existing);
	}

	private void OpenPotionNamesDialog() {
		ObservableCollection<CheckableItemViewModel> copy = new(PotionNameSelections.Select(item => new CheckableItemViewModel(item.Name, item.IsChecked, true)));
		PotionNameDialogViewModel dialogViewModel = new(copy, settings.PotionQuantityLimit);
		PotionNameDialog dialog = new() { DataContext = dialogViewModel, Owner = Application.Current.MainWindow };

		if (dialog.ShowDialog() != true) return;

		foreach (CheckableItemViewModel updated in copy) {
			CheckableItemViewModel? target = PotionNameSelections.FirstOrDefault(item => item.Name == updated.Name);
			if (target != null) target.IsChecked = updated.IsChecked;
		}
		settings.PotionQuantityLimit = dialogViewModel.QuantityLimit;
	}
}
