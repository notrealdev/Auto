namespace Auto.UI.Views;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Auto.Runtime;
using Auto.UI.ViewModels;

public partial class MainWindow : Window {
	public MainWindow() {
		InitializeComponent();
		MainWindowViewModel viewModel = new();
		DataContext = viewModel;
		// Ô theo dõi vật phẩm không bind Text nữa nên phải tự nghe: xem lý do ở LootFeed_Changed.
		viewModel.PropertyChanged += (_, args) => {
			if (args.PropertyName == nameof(MainWindowViewModel.LootFeedText)) LootFeed_Changed(viewModel.LootFeedText);
		};
		LootFeedBox.Text = viewModel.LootFeedText;
		// Phím tắt Ctrl+A tắt Tự động đánh mà không có tín hiệu nào -> xem ShakeWhenAttackDisabledByHotkey.
		viewModel.AccountList.AttackToggledExternally += ShakeWhenAttackDisabledByHotkey;

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

	// SW_RESTORE: mở lại cửa sổ đang thu nhỏ mà vẫn giữ nguyên kích thước trước đó.
	private const int ShowWindowRestore = 9;

	// Bấm ĐÚP vào một dòng account thì đưa cửa sổ game của nó lên trước. Port nguyên ActivateGameWindow của DEV auto
	// (D:\G\DEV\UI\Accounts.cs:924-939), kể cả việc gắn vào bấm đúp chứ không phải bấm đơn.
	//
	// Grid không phải Control nên không có sẵn sự kiện MouseDoubleClick; đếm ClickCount trên MouseLeftButtonDown là
	// cách đang dùng sẵn trong file này cho thanh tiêu đề.
	// KHÔNG đặt e.Handled: để sự kiện chạy tiếp lên ListBoxItem, nếu không thì bấm đúp sẽ không chọn được dòng đó.
	// Không phải lo bấm vào CheckBox cũng kích hoạt: CheckBox tự đánh dấu sự kiện chuột đã xử lý nên handler này
	// không nhận được — cùng cơ chế đã ghi ở comment cạnh CheckBox trong MainWindow.xaml.
	private void AccountRow_Clicked(object sender, MouseButtonEventArgs e) {
		if (e.ClickCount != 2) return;
		if (sender is not FrameworkElement { DataContext: AccountRowViewModel row }) return;
		IntPtr handle = row.GameWindow.Handle;
		if (handle == IntPtr.Zero) return;
		if (WinApi.IsIconic(handle)) WinApi.ShowWindowAsync(handle, ShowWindowRestore);
		WinApi.SetForegroundWindow(handle);
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

	// Ô theo dõi vật phẩm nhặt được.
	//
	// Trước đây Text bind thẳng vào LootFeedText và chỉ bỏ qua ScrollToEnd khi đang bôi đen. Nhưng mỗi lượt nhặt
	// LootFeed dựng lại TOÀN BỘ chuỗi (Runtime/LootFeed.cs, Render ghép lại từ đầu vì bề rộng cột tên có thể đổi),
	// nên binding vẫn gán đè cả Text — gán Text là TextBox bỏ vùng đang chọn và dựng lại bố cục.
	// Chủ dự án báo "đang copy thì nhảy line height"; tôi CHƯA tự kiểm chứng được bằng pixel, nhưng việc gán đè khi
	// đang bôi đen là có thật và đọc thẳng ra được từ code, nên chặn đúng chỗ đó.
	//
	// Cách làm: giữ lại bản mới nhất, chỉ ghi vào TextBox khi không còn vùng chọn nào.
	private string pendingLootFeedText = "";
	private bool hasPendingLootFeedText;

	// Phải kèm IsKeyboardFocusWithin chứ không chỉ xét SelectionLength: WPF GIỮ NGUYÊN vùng bôi đen sau khi TextBox
	// mất focus, nên nếu chỉ xét vùng chọn thì bôi đen xong bấm đi chỗ khác là ô log đứng im vĩnh viễn.
	private bool IsUserSelectingLootFeed => LootFeedBox.IsKeyboardFocusWithin && LootFeedBox.SelectionLength > 0;

	private void LootFeed_Changed(string text) {
		if (IsUserSelectingLootFeed) {
			pendingLootFeedText = text;
			hasPendingLootFeedText = true;
			return;
		}
		ApplyLootFeedText(text);
	}

	// Vừa bỏ bôi đen (hoặc vừa rời khỏi ô) thì đẩy nốt bản đang chờ vào.
	private void LootFeed_SelectionChanged(object sender, RoutedEventArgs e) => FlushPendingLootFeedText();

	private void LootFeed_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => FlushPendingLootFeedText();

	private void FlushPendingLootFeedText() {
		if (!hasPendingLootFeedText || IsUserSelectingLootFeed) return;
		hasPendingLootFeedText = false;
		ApplyLootFeedText(pendingLootFeedText);
	}

	// Dòng mới luôn nằm dưới cùng, nên tự cuộn xuống đáy để lượt nhặt mới nhất luôn nằm trong tầm mắt.
	private void ApplyLootFeedText(string text) {
		LootFeedBox.Text = text;
		LootFeedBox.ScrollToEnd();
	}

	private void ClearLootFeed_Click(object sender, RoutedEventArgs e) {
		hasPendingLootFeedText = false;
		pendingLootFeedText = "";
		LootFeed.Clear();
	}

	// RUNG CỬA SỔ KHI PHÍM TẮT Ctrl+A TẮT "Tự động đánh".
	//
	// Vì sao cần: bấm Ctrl+A là đổi AttackSettings.Enabled ngay trong AccountListViewModel.EngineTick, không có
	// lấy một tín hiệu nhìn thấy được nào nếu cửa sổ Auto không đang mở đúng tab Đánh của đúng account đó.
	// Ngày 2026-09-17, PID 1604 đứng im 987 giây với heartbeat ghi Attack=False (client-freeze.log 15:10:20
	// "ĐóngBăngTrong=987s", heartbeat.log 15:07:34 "Attack=False | Running=False"), mà không có gì báo là công tắc
	// Đánh đã tắt.
	//
	// CHỈ nghe đường phím tắt: AttackToggledExternally chỉ được bắn ở AccountListViewModel.EngineTick sau khi
	// TryTakeToggle lấy được một yêu cầu từ AttackHotkeyTracker. Tự tay bỏ tick trong giao diện KHÔNG đi qua đây,
	// nên không rung — đúng yêu cầu của chủ dự án.
	//
	// Chỉ rung khi TẮT, không rung khi bật: cờ đã được gán trước lúc bắn sự kiện nên đọc thẳng ra là biết chiều.
	private void ShakeWhenAttackDisabledByHotkey(GameWindow game) {
		if (game.AttackSettings.Enabled) return;
		Dispatcher.BeginInvoke(ShakeWindow);
	}

	// Biên độ rung, đơn vị pixel độc lập thiết bị. Chỉ lệch về BÊN TRÁI (toàn số âm): cửa sổ được ghim sát mép phải
	// vùng làm việc ở SnapToWorkAreaTopRight, đẩy sang phải là lòi ra ngoài màn hình.
	// Nâng 12 -> 30 ngày 2026-09-17 theo yêu cầu của chủ dự án: mức 12 rung quá nhẹ, dễ bỏ sót.
	// Các bước sau giữ nguyên tỉ lệ tắt dần 1 / 0,75 / 0,5 / 0,25.
	private static readonly double[] ShakeOffsets = [-30, 0, -22, 0, -15, 0, -7, 0];
	private const int ShakeStepMilliseconds = 45;
	// Toạ độ trái trước khi rung. Chỉ có giá trị khi đang rung, xem lý do ở ShakeWindow.
	private double? shakeBaseLeft;

	private void ShakeWindow() {
		// Left không có nghĩa khi cửa sổ đang phóng to; thu nhỏ thì không ai nhìn thấy.
		if (WindowState != WindowState.Normal) return;
		// TRẢ Left VỀ TAY, KHÔNG TIN VÀO FillBehavior.Stop.
		//
		// Chủ dự án báo 2026-09-17: rung xong thì mép phải cửa sổ hở ra một khoảng so với mép phải màn hình, tức
		// cửa sổ ở lại bên trái chỗ cũ. Tôi CHƯA xác định được cơ chế chính xác (không tự nhìn được pixel), nên
		// thay vì đoán, chỗ này bỏ hẳn việc phụ thuộc vào cơ chế tự khôi phục của animation:
		//   1. Nhớ Left gốc MỘT LẦN cho mỗi đợt rung. Đọc Left lúc animation đang chạy sẽ ra giá trị đang được
		//      animate chứ không phải giá trị gốc, nên bấm Ctrl+A liên tiếp mà đọc lại sẽ trôi dần sang trái.
		//   2. Xong thì gỡ animation bằng BeginAnimation(..., null) rồi GÁN THẲNG Left = giá trị đã nhớ.
		shakeBaseLeft ??= Left;
		double baseLeft = shakeBaseLeft.Value;
		DoubleAnimationUsingKeyFrames shake = new() { FillBehavior = FillBehavior.Stop };
		for (int step = 0; step < ShakeOffsets.Length; step++) {
			KeyTime keyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds((step + 1) * ShakeStepMilliseconds));
			shake.KeyFrames.Add(new LinearDoubleKeyFrame(baseLeft + ShakeOffsets[step], keyTime));
		}
		shake.Completed += (_, _) => {
			BeginAnimation(LeftProperty, null);
			Left = baseLeft;
			shakeBaseLeft = null;
		};
		BeginAnimation(LeftProperty, shake);
	}

	private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

	private void Maximize_Click(object sender, RoutedEventArgs e) =>
		WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

	private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
