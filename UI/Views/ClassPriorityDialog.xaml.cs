namespace Auto.UI.Views;

using System.Windows;
using Auto.UI.ViewModels;

public partial class ClassPriorityDialog : Window {
	public ClassPriorityDialog() {
		InitializeComponent();
		DataContextChanged += OnDataContextChanged;
	}

	private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) {
		if (e.OldValue is ClassPriorityDialogViewModel oldViewModel) oldViewModel.CloseRequested -= OnCloseRequested;
		if (e.NewValue is ClassPriorityDialogViewModel newViewModel) newViewModel.CloseRequested += OnCloseRequested;
	}

	private void OnCloseRequested(object? sender, bool accepted) {
		DialogResult = accepted;
	}
}
