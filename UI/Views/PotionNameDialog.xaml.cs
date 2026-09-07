namespace Auto.UI.Views;

using System.Windows;
using Auto.UI.ViewModels;

public partial class PotionNameDialog : Window {
	public PotionNameDialog() {
		InitializeComponent();
		DataContextChanged += OnDataContextChanged;
	}

	private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) {
		if (e.OldValue is PotionNameDialogViewModel oldViewModel) oldViewModel.CloseRequested -= OnCloseRequested;
		if (e.NewValue is PotionNameDialogViewModel newViewModel) newViewModel.CloseRequested += OnCloseRequested;
	}

	private void OnCloseRequested(object? sender, bool accepted) {
		DialogResult = accepted;
	}
}
