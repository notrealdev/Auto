namespace Auto.UI.Views;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Auto.Runtime;
using Auto.UI.ViewModels;

public partial class MainWindow : Window {
	public MainWindow() {
		InitializeComponent();
		DataContext = new MainWindowViewModel();

		Rect workArea = SystemParameters.WorkArea;
		MaxWidth = workArea.Width;
		MaxHeight = workArea.Height;
		// Kích thước chỉ khai báo ở MainWindow.xaml. Trước đây chỗ này gán đè Width/Height rồi tính
		// Left = workArea.Right - Width, nên khi sửa Width trong XAML thì code-behind vẫn ghi đè giá trị cũ,
		// và Left lại được tính từ con số vừa gán chứ không phải bề rộng thật sau khi bị MinWidth kẹp lại.
		// Dời sang Loaded để đọc ActualWidth, tức bề rộng đã render xong.
		Loaded += SnapToWorkAreaTopRight;
	}

	// Ghim mép phải cửa sổ vào mép phải vùng làm việc, mép trên vào mép trên.
	private void SnapToWorkAreaTopRight(object sender, RoutedEventArgs e) {
		Loaded -= SnapToWorkAreaTopRight;
		Rect workArea = SystemParameters.WorkArea;
		Left = workArea.Right - ActualWidth;
		Top = workArea.Top;
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
		if (e.ClickCount == 2) {
			WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
		} else {
			DragMove();
		}
	}

	// Bật/tắt Auto của một account thì chọn luôn dòng đó, để các tab bên dưới nhảy sang đúng account vừa thao tác.
	// Xem lý do đầy đủ ở comment cạnh CheckBox trong MainWindow.xaml.
	private void AccountAutoEnabled_Toggled(object sender, RoutedEventArgs e) {
		if (sender is not DependencyObject source) return;
		ListBoxItem? row = FindAncestor<ListBoxItem>(source);
		if (row != null) row.IsSelected = true;
	}

	// Đóng cửa sổ game của đúng dòng vừa bấm chuột phải.
	//
	// Dùng Process.Kill chứ KHÔNG gửi WM_CLOSE: ca cần tới nút này là lúc client đã treo cứng (chủ dự án báo
	// 2026-09-15), mà cửa sổ treo thì không bơm message nên WM_CLOSE rơi vào hư không. Kill là đúng thứ Task Manager
	// vẫn làm, chỉ là khỏi phải tự dò PID.
	private void CloseGameProcess_Click(object sender, RoutedEventArgs e) {
		if (sender is not MenuItem { DataContext: AccountRowViewModel row }) return;
		int processId = row.GameWindow.ProcessId;
		string label = row.IsIdentified ? row.DisplayName : $"PID {processId}";
		MessageBoxResult answer = MessageBox.Show(
			this,
			$"Đóng cửa sổ game của {label}?\n\nTiến trình {processId} bị kết thúc ngay, nhân vật KHÔNG thoát game đúng cách.",
			"Đóng cửa sổ game",
			MessageBoxButton.YesNo,
			MessageBoxImage.Warning,
			MessageBoxResult.No);
		if (answer != MessageBoxResult.Yes) return;

		// Tắt Auto của account này trước: các engine đang đọc bộ nhớ tiến trình đó mỗi 100ms, giết ngay giữa chừng
		// là để chúng đọc vào tiến trình đã chết. Dòng account tự biến mất ở nhịp quét sau, không cần xoá tay.
		row.AutoEnabled = false;
		try {
			using Process process = Process.GetProcessById(processId);
			process.Kill();
			DebugLog.AddClientEvent($"GAME_PROCESS_KILLED_BY_USER | PID={processId} | {row.GameWindow.CharacterName}");
		} catch (Exception ex) {
			DebugLog.AddClientEvent($"GAME_PROCESS_KILL_FAILED | PID={processId} | {ex.GetType().Name}: {ex.Message}");
			MessageBox.Show(this, $"Không đóng được tiến trình {processId}.\n\n{ex.Message}", "Đóng cửa sổ game", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject {
		for (DependencyObject? current = source; current != null; current = VisualTreeHelper.GetParent(current)) {
			if (current is T match) return match;
		}
		return null;
	}

	private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

	private void Maximize_Click(object sender, RoutedEventArgs e) =>
		WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

	private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
