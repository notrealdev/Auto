namespace Auto.UI.Views;

using System.Windows.Controls;
using Auto.UI.ViewModels;

public partial class LootView : UserControl {
	public LootView() {
		InitializeComponent();
	}

	// Quét lại đúng lúc mở danh sách, không quét sẵn từ trước: danh sách đồ dưới đất và đồ trong túi đổi liên tục,
	// quét sớm thì lúc mở ra đã cũ. Cùng khuôn với MonsterComboBox_DropDownOpened ở tab Đánh.
	private void MaterialCatalogComboBox_DropDownOpened(object sender, EventArgs e) {
		if (DataContext is LootViewModel viewModel) viewModel.RequestMaterialOptionsCommand.Execute(null);
	}

	private void ExcludedCatalogComboBox_DropDownOpened(object sender, EventArgs e) {
		if (DataContext is LootViewModel viewModel) viewModel.RequestExcludedOptionsCommand.Execute(null);
	}

	private void SaleCatalogComboBox_DropDownOpened(object sender, EventArgs e) {
		if (DataContext is LootViewModel viewModel) viewModel.RequestSaleOptionsCommand.Execute(null);
	}

	private void MaterialCatalogComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
		AddSelectedName(sender, e, viewModel => viewModel.AddMaterialFromCatalogCommand);
	}

	private void ExcludedCatalogComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
		AddSelectedName(sender, e, viewModel => viewModel.AddExcludedFromCatalogCommand);
	}

	private void SaleCatalogComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
		AddSelectedName(sender, e, viewModel => viewModel.AddSaleFromCatalogCommand);
	}

	// Ô xổ xuống KHÔNG giữ lựa chọn: chọn xong là nhả về rỗng, để lần sau chọn lại đúng món đó vẫn bắn sự kiện.
	// Không nhả thì chọn lại cùng một tên sẽ im lặng không làm gì, vì SelectionChanged chỉ bắn khi giá trị đổi.
	//
	// Phải nhả BẰNG Dispatcher: đang ở giữa lượt xử lý SelectionChanged mà gán SelectedIndex ngay thì WPF bắn
	// đệ quy lại chính handler này.
	private static void AddSelectedName(object sender, SelectionChangedEventArgs e, Func<LootViewModel, RelayCommand> pickCommand) {
		if (sender is not ComboBox comboBox) return;
		if (e.AddedItems.Count == 0 || e.AddedItems[0] is not string name) return;
		if (comboBox.DataContext is LootViewModel viewModel) pickCommand(viewModel).Execute(name);
		comboBox.Dispatcher.BeginInvoke(() => comboBox.SelectedIndex = -1);
	}
}
