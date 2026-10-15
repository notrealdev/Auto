namespace Auto.UI.Views;

using System.Windows.Controls;
using Auto.UI.ViewModels;

public partial class MarketView : UserControl {
	public MarketView() {
		InitializeComponent();
	}

	// Read the bag when the list opens, same pattern as SaleCatalogComboBox_DropDownOpened on the Loot tab.
	private void ItemComboBox_DropDownOpened(object sender, EventArgs e) {
		if (DataContext is MarketViewModel viewModel) viewModel.RequestItemOptionsCommand.Execute(null);
	}

	// Read the client's sent-message history (Up arrow) each time the list opens.
	private void HistoryComboBox_DropDownOpened(object sender, EventArgs e) {
		if (DataContext is MarketViewModel viewModel) viewModel.RequestHistoryOptionsCommand.Execute(null);
	}

	private void HistoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
		InsertSelected(sender, e, viewModel => viewModel.InsertHistoryCommand);
	}

	private void TemplateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
		InsertSelected(sender, e, viewModel => viewModel.InsertTemplateCommand);
	}

	private void ItemComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
		InsertSelected(sender, e, viewModel => viewModel.InsertItemCommand);
	}

	private void MapComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
		InsertSelected(sender, e, viewModel => viewModel.InsertMapCommand);
	}

	// Picking a line inserts it and the list goes back to empty, so picking the same line again still fires. Reset
	// through the Dispatcher, as LootView.AddSelectedName does: setting SelectedIndex inside SelectionChanged re-enters it.
	private static void InsertSelected(object sender, SelectionChangedEventArgs e, Func<MarketViewModel, RelayCommand> pickCommand) {
		if (sender is not ComboBox comboBox) return;
		if (e.AddedItems.Count == 0 || e.AddedItems[0] is not { } selected) return;
		if (comboBox.DataContext is MarketViewModel viewModel) pickCommand(viewModel).Execute(selected);
		comboBox.Dispatcher.BeginInvoke(() => comboBox.SelectedIndex = -1);
	}
}
