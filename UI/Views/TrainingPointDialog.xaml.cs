namespace Auto.UI.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Auto.UI.ViewModels;

public partial class TrainingPointDialog : Window {
	public TrainingPointDialog() {
		InitializeComponent();
		DataContextChanged += OnDataContextChanged;
	}

	private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) {
		if (e.OldValue is TrainingPointDialogViewModel oldViewModel) oldViewModel.CloseRequested -= OnCloseRequested;
		if (e.NewValue is TrainingPointDialogViewModel newViewModel) newViewModel.CloseRequested += OnCloseRequested;
	}

	private void OnCloseRequested(object? sender, bool accepted) {
		DialogResult = accepted;
	}

	// Double-click vào một dòng là ÁP DỤNG điểm đó (chủ dự án chốt 2026-09-22) — xem ghi chú lý do lệch so với
	// AutoFS ở TrainingPointDialogViewModel.ApplySelectedCommand. Xoá đã có nút "Xoá" riêng.
	//
	// Cùng khuôn với AttackView.xaml.cs: code-behind chỉ bắc cầu sự kiện WPF thô sang RelayCommand trên DataContext.
	//
	// Kiểm nguồn click: MouseDoubleClick của ListBox bắn cả khi double-click vào vùng TRỐNG dưới danh sách, lúc đó
	// dòng đang chọn vẫn là dòng cũ nên sẽ áp dụng một điểm người dùng không hề nhắm tới.
	private void PointList_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
		if (DataContext is not TrainingPointDialogViewModel viewModel) return;
		if (e.OriginalSource is not DependencyObject source) return;
		if (ItemsControl.ContainerFromElement((ListBox)sender, source) is not ListBoxItem) return;
		if (viewModel.ApplySelectedCommand.CanExecute(null)) viewModel.ApplySelectedCommand.Execute(null);
	}
}
