namespace Auto.UI.Views;

using System.Windows.Controls;
using Auto.UI.ViewModels;

public partial class AttackView : UserControl {
	public AttackView() {
		InitializeComponent();
	}

	private void MonsterComboBox_DropDownOpened(object sender, EventArgs e) {
		if (DataContext is AttackViewModel viewModel) viewModel.RequestMonsterOptionsCommand.Execute(null);
	}
}
