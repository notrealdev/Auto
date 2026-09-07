namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using System.Windows;
using Auto.Loot;
using Auto.UI.Views;

public sealed class LootViewModel : ViewModelBase {
	private static readonly string[] BuiltInMaterialItems = ["Đồ Trắng", "Đồ Xanh", "Đồ Lục", "Đồ Vàng", "Đồ Cam", "Đồ Khác", "Mảnh, Ngọc", "Bí Kíp", "Pháp Bảo", "Quẻ", "Lục Đạo", "Tứ Tượng", "Nhãn Vạn Tiên Trận"];
	private static readonly string[] BuiltInExcludedOrSaleItems = ["Đồ Trắng", "Đồ Xanh", "Dược Phẩm"];
	private static readonly string[] PotionNames = ["Tiểu Hồng đơn", "Trung Hồng đơn", "Đại Hồng đơn", "Tiểu Hoàn đơn", "Trung Hoàn đơn", "Đại Hoàn đơn"];

	private readonly Settings settings;
	private string pendingMaterialItem = "";
	private string pendingExcludedItem = "";
	private string pendingSaleItem = "";

	public LootViewModel() : this(new Settings()) { }

	public LootViewModel(Settings settings) {
		this.settings = settings;

		MaterialItems = CreateMaterialItems();
		ExcludedItems = CreateDictionaryItems(BuiltInExcludedOrSaleItems, settings.ExcludedItemSelections);
		SaleItems = CreateDictionaryItems(BuiltInExcludedOrSaleItems, settings.SaleItemSelections);
		PotionNameSelections = CreateDictionaryItems(PotionNames, settings.PotionNameSelections);

		AddMaterialItemCommand = new RelayCommand(_ => {
			if (AddItem(MaterialItems, PendingMaterialItem, name => {
				SetMaterialFlag(name, true);
				return new CheckableItemViewModel(name, true, false, value => SetMaterialFlag(name, value));
			})) PendingMaterialItem = "";
		});
		AddExcludedItemCommand = new RelayCommand(_ => {
			if (AddItem(ExcludedItems, PendingExcludedItem, name => {
				settings.ExcludedItemSelections[name] = true;
				return new CheckableItemViewModel(name, true, false, value => settings.ExcludedItemSelections[name] = value);
			})) PendingExcludedItem = "";
		});
		AddSaleItemCommand = new RelayCommand(_ => {
			if (AddItem(SaleItems, PendingSaleItem, name => {
				settings.SaleItemSelections[name] = true;
				return new CheckableItemViewModel(name, true, false, value => settings.SaleItemSelections[name] = value);
			})) PendingSaleItem = "";
		});
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

	public string PendingMaterialItem {
		get => pendingMaterialItem;
		set => SetField(ref pendingMaterialItem, value);
	}

	public ObservableCollection<CheckableItemViewModel> ExcludedItems { get; }

	public string PendingExcludedItem {
		get => pendingExcludedItem;
		set => SetField(ref pendingExcludedItem, value);
	}

	public ObservableCollection<CheckableItemViewModel> SaleItems { get; }

	public string PendingSaleItem {
		get => pendingSaleItem;
		set => SetField(ref pendingSaleItem, value);
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

	public RelayCommand AddMaterialItemCommand { get; }

	public RelayCommand AddExcludedItemCommand { get; }

	public RelayCommand AddSaleItemCommand { get; }

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

	private static bool AddItem(ObservableCollection<CheckableItemViewModel> items, string pendingName, Func<string, CheckableItemViewModel> createItem) {
		string name = pendingName.Trim();
		if (name.Length == 0) return false;
		CheckableItemViewModel? existing = items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
		if (existing != null) existing.IsChecked = true;
		else items.Add(createItem(name));
		return true;
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
