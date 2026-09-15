namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using Auto.Runtime;

public sealed class MainWindowViewModel : ViewModelBase {
	private readonly TabItemViewModel attackTabItem;
	private readonly TabItemViewModel lootTabItem;
	private readonly TabItemViewModel questTabItem;
	private readonly TabItemViewModel basicTabItem;
	private readonly TabItemViewModel supportTabItem;
	private readonly TabItemViewModel marketTabItem;
	private readonly TabItemViewModel infoTabItem;
	private readonly TabItemViewModel debugTabItem;
	private readonly DispatcherTimer statusBarTimer;
	private ViewModelBase selectedTabContent;
	private string statusBarText = "Loading...";

	public MainWindowViewModel() {
		AttackViewModel  attackTab  = new();
		LootViewModel    lootTab    = new();
		QuestViewModel   questTab   = new();
		BasicViewModel   basicTab   = new();
		SupportViewModel supportTab = new();
		MarketViewModel  marketTab  = new();
		InfoViewModel    infoTab    = new();
		// Tab Login KHÔNG dựng lại theo account đang chọn: lúc đăng nhập chưa có client nào tồn tại để chọn.
		LoginViewModel loginTab = new();
		DebugViewModel debugTab = new();

		attackTabItem  = new TabItemViewModel("Attack", attackTab) { IsSelected = true };
		lootTabItem    = new TabItemViewModel("Loot", lootTab);
		basicTabItem   = new TabItemViewModel("Base", basicTab);
		supportTabItem = new TabItemViewModel("Buff", supportTab);
		marketTabItem  = new TabItemViewModel("Chat", marketTab);
		questTabItem   = new TabItemViewModel("Quest", questTab);
		infoTabItem    = new TabItemViewModel("Info", infoTab);
		debugTabItem   = new TabItemViewModel("Debug", debugTab);

		Tabs = [
			attackTabItem,
			lootTabItem,
			questTabItem,
			supportTabItem,
			marketTabItem,
			basicTabItem,
			new TabItemViewModel("Login", loginTab),
			infoTabItem,
			debugTabItem
		];

		selectedTabContent = attackTab;

		foreach (TabItemViewModel tab in Tabs) tab.PropertyChanged += OnTabPropertyChanged;
		AccountList.PropertyChanged += OnAccountListPropertyChanged;
		AccountList.AttackToggledExternally += OnAttackToggledExternally;

		// 2 giây một nhịp: đủ mượt để nhìn, và đủ thưa để phép chia thời gian CPU không bị nhiễu.
		statusBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
		statusBarTimer.Tick += (_, _) => RefreshStatusBar();
		statusBarTimer.Start();
		ProcessLoadMonitor.Sample();
	}

	// Tải của chính tiến trình Auto.
	//
	// "Mức tải" là nhãn Auto TỰ TÍNH từ %CPU đo được, KHÔNG phải cột "Power usage" của Windows: giá trị đó Task
	// Manager tính từ CPU + GPU + đĩa bằng cơ chế nội bộ và không có API công khai nào đọc ra (chưa tìm thấy, tính
	// tới 2026-09-15). Ghi rõ để không ai đọc nhầm hai thứ là một.
	public string StatusBarText {
		get => statusBarText;
		private set => SetField(ref statusBarText, value);
	}

	private void RefreshStatusBar() {
		ProcessLoadMonitor.Sample();
		double percent = ProcessLoadMonitor.CpuPercent;
		StatusBarText = $"CPU: {percent:F1}% | RAM: {ProcessLoadMonitor.WorkingSetMegabytes:F0}MB | Power: {ProcessLoadMonitor.DescribeLoad(percent)} | Account: {AccountList.Accounts.Count}";
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

		bool wasAttackSelected  = attackTabItem.Content  == SelectedTabContent;
		bool wasLootSelected    = lootTabItem.Content    == SelectedTabContent;
		bool wasQuestSelected   = questTabItem.Content   == SelectedTabContent;
		bool wasBasicSelected   = basicTabItem.Content   == SelectedTabContent;
		bool wasSupportSelected = supportTabItem.Content == SelectedTabContent;
		bool wasMarketSelected  = marketTabItem.Content  == SelectedTabContent;
		bool wasInfoSelected    = infoTabItem.Content    == SelectedTabContent;
		bool wasDebugSelected   = debugTabItem.Content   == SelectedTabContent;

		AttackViewModel  newAttackTab  = selected != null ? new AttackViewModel(selected.AttackSettings, selected) : new AttackViewModel();
		LootViewModel    newLootTab    = selected != null ? new LootViewModel(selected.LootSettings) : new LootViewModel();
		QuestViewModel   newQuestTab   = selected != null ? new QuestViewModel(selected.QuestSettings) : new QuestViewModel();
		BasicViewModel   newBasicTab   = selected != null ? new BasicViewModel(selected.BasicSettings) : new BasicViewModel();
		SupportViewModel newSupportTab = selected != null ? new SupportViewModel(selected.SupportSettings) : new SupportViewModel();
		MarketViewModel  newMarketTab  = selected != null ? new MarketViewModel(selected.MarketSettings) : new MarketViewModel();
		InfoViewModel    newInfoTab    = new(selected);
		DebugViewModel   newDebugTab   = new(selected);

		attackTabItem.Content  = newAttackTab;
		lootTabItem.Content    = newLootTab;
		questTabItem.Content   = newQuestTab;
		basicTabItem.Content   = newBasicTab;
		supportTabItem.Content = newSupportTab;
		marketTabItem.Content  = newMarketTab;
		infoTabItem.Content    = newInfoTab;
		debugTabItem.Content   = newDebugTab;

		if (wasAttackSelected) SelectedTabContent = newAttackTab;
		else if (wasLootSelected) SelectedTabContent = newLootTab;
		else if (wasQuestSelected) SelectedTabContent = newQuestTab;
		else if (wasBasicSelected) SelectedTabContent = newBasicTab;
		else if (wasSupportSelected) SelectedTabContent = newSupportTab;
		else if (wasMarketSelected) SelectedTabContent = newMarketTab;
		else if (wasInfoSelected) SelectedTabContent = newInfoTab;
		else if (wasDebugSelected) SelectedTabContent = newDebugTab;
	}
}
