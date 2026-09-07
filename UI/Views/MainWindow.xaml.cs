namespace Auto.UI.Views;

using System.Windows;
using System.Windows.Input;
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

	private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

	private void Maximize_Click(object sender, RoutedEventArgs e) =>
		WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

	private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
