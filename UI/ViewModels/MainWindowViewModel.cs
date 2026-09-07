namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using Auto.Runtime;

public sealed class MainWindowViewModel : ViewModelBase {
	private readonly TabItemViewModel attackTabItem;
	private readonly TabItemViewModel lootTabItem;
	private readonly TabItemViewModel basicTabItem;
	private readonly TabItemViewModel supportTabItem;
	private readonly TabItemViewModel marketTabItem;
	private readonly TabItemViewModel debugTabItem;
	private ViewModelBase selectedTabContent;

	public MainWindowViewModel() {
		AttackViewModel attackTab = new();
		LootViewModel lootTab = new();
		BasicViewModel basicTab = new();
		SupportViewModel supportTab = new();
		MarketViewModel marketTab = new();
		InfoViewModel infoTab = new();
		DebugViewModel debugTab = new();

		attackTabItem = new TabItemViewModel("Đánh", attackTab) { IsSelected = true };
		lootTabItem = new TabItemViewModel("Nhặt", lootTab);
		basicTabItem = new TabItemViewModel("Cơ bản", basicTab);
		supportTabItem = new TabItemViewModel("Buff", supportTab);
		marketTabItem = new TabItemViewModel("Chat", marketTab);
		debugTabItem = new TabItemViewModel("Debug", debugTab);

		Tabs = [
			attackTabItem,
			lootTabItem,
			supportTabItem,
			marketTabItem,
			basicTabItem,
			new TabItemViewModel("Thông tin", infoTab),
			debugTabItem
		];

		selectedTabContent = attackTab;

		foreach (TabItemViewModel tab in Tabs) tab.PropertyChanged += OnTabPropertyChanged;
		AccountList.PropertyChanged += OnAccountListPropertyChanged;
		AccountList.AttackToggledExternally += OnAttackToggledExternally;
	}

	public AccountListViewModel AccountList { get; } = new();

	public ObservableCollection<TabItemViewModel> Tabs { get; }

	public ViewModelBase SelectedTabContent {
		get => selectedTabContent;
		private set => SetField(ref selectedTabContent, value);
	}

	private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e) {
		if (e.PropertyName != nameof(TabItemViewModel.IsSelected)) return;
		if (sender is not TabItemViewModel { IsSelected: true } selectedTab) return;

		foreach (TabItemViewModel tab in Tabs) {
			if (tab != selectedTab) tab.IsSelected = false;
		}

		SelectedTabContent = selectedTab.Content;
	}

	private void OnAttackToggledExternally(GameWindow game) {
		if (game != AccountList.SelectedRow?.GameWindow) return;
		if (attackTabItem.Content is AttackViewModel attackViewModel) attackViewModel.RefreshEnabled();
	}

	private void OnAccountListPropertyChanged(object? sender, PropertyChangedEventArgs e) {
		if (e.PropertyName != nameof(AccountListViewModel.SelectedRow)) return;

		GameWindow? selected = AccountList.SelectedRow?.GameWindow;

		bool wasAttackSelected = attackTabItem.Content == SelectedTabContent;
		bool wasLootSelected = lootTabItem.Content == SelectedTabContent;
		bool wasBasicSelected = basicTabItem.Content == SelectedTabContent;
		bool wasSupportSelected = supportTabItem.Content == SelectedTabContent;
		bool wasMarketSelected = marketTabItem.Content == SelectedTabContent;
		bool wasDebugSelected = debugTabItem.Content == SelectedTabContent;

		AttackViewModel newAttackTab = selected != null ? new AttackViewModel(selected.AttackSettings, selected) : new AttackViewModel();
		LootViewModel newLootTab = selected != null ? new LootViewModel(selected.LootSettings) : new LootViewModel();
		BasicViewModel newBasicTab = selected != null ? new BasicViewModel(selected.BasicSettings) : new BasicViewModel();
		SupportViewModel newSupportTab = selected != null ? new SupportViewModel(selected.SupportSettings) : new SupportViewModel();
		MarketViewModel newMarketTab = selected != null ? new MarketViewModel(selected.MarketSettings) : new MarketViewModel();
		DebugViewModel newDebugTab = new(selected);

		attackTabItem.Content = newAttackTab;
		lootTabItem.Content = newLootTab;
		basicTabItem.Content = newBasicTab;
		supportTabItem.Content = newSupportTab;
		marketTabItem.Content = newMarketTab;
		debugTabItem.Content = newDebugTab;

		if (wasAttackSelected) SelectedTabContent = newAttackTab;
		else if (wasLootSelected) SelectedTabContent = newLootTab;
		else if (wasBasicSelected) SelectedTabContent = newBasicTab;
		else if (wasSupportSelected) SelectedTabContent = newSupportTab;
		else if (wasMarketSelected) SelectedTabContent = newMarketTab;
		else if (wasDebugSelected) SelectedTabContent = newDebugTab;
	}
}
