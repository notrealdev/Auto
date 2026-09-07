namespace Auto.UI.Views;

using System.Windows.Controls;
using System.Windows.Input;
using Auto.UI.ViewModels;

public partial class AttackView : UserControl {
	public AttackView() {
		InitializeComponent();
	}

	private void MonsterComboBox_DropDownOpened(object sender, EventArgs e) {
		if (DataContext is AttackViewModel viewModel) viewModel.RequestMonsterOptionsCommand.Execute(null);
	}

	// MỖI lần bấm chuột vào ô Tâm là chụp lại tâm bãi. Cố tình dùng PreviewMouseLeftButtonDown thay vì GotFocus:
	// GotFocus chỉ bắn ở lần chuyển trạng thái mất-focus -> có-focus, nên bấm tiếp lúc ô đang giữ focus sẽ không
	// bắn gì và người dùng thấy tâm chỉ cập nhật đúng một lần. Sự kiện chuột thì bấm bao nhiêu lần bắn bấy nhiêu.
	private void CenterTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
		if (DataContext is AttackViewModel viewModel) viewModel.CaptureCenterPositionCommand.Execute(null);
	}
}
