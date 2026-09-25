namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using System.Windows.Threading;
using Auto.Login;
using Auto.Runtime;

// Một dòng account trong danh sách. Ô tick khớp cột Checked của AutoFS (StoreOptions.cs:54 —
// nó bỏ tick sau khi đã khởi động account đó).
public sealed class LoginAccountRowViewModel(LoginAccount account) : ViewModelBase {
	private bool isChecked = true;

	public LoginAccount Account { get; } = account;

	public bool IsChecked {
		get => isChecked;
		set => SetField(ref isChecked, value);
	}

	public string User => Account.User;

	// Ẩn tên tài khoản trên UI, chỉ giữ lại 2 ký tự cuối (chủ dự án chốt 2026-09-21) — che bớt khi chụp/chia sẻ
	// màn hình, không phải mã hoá bảo mật.
	public string MaskedUser {
		get {
			string user = Account.User;
			return user.Length <= 2 ? new string('*', user.Length) : new string('*', user.Length - 2) + user[^2..];
		}
	}

	public string Partition => $"{Account.Partition} / {Account.Server}";
}

// Tab Login: đọc Login\Login.json, cho chọn account nào chạy, rồi mở client và gửi chuỗi lệnh đăng nhập.
//
// Tab này KHÔNG gắn với account game đang chọn ở danh sách bên trái — lúc đăng nhập thì chưa có client nào
// để chọn. Nên MainWindowViewModel không dựng lại tab này khi đổi dòng.
public sealed class LoginViewModel : ViewModelBase {
	private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
	private readonly LoginAutomation automation = new();
	private CancellationTokenSource? cancellation;
	private string configPath = Settings.DefaultPath;
	private string gamePath = "";
	private string statusText = "";
	private bool isRunning;

	public LoginViewModel() {
		RunCommand = new RelayCommand(_ => Run(), _ => ! isRunning && Accounts.Count > 0);
		StopCommand = new RelayCommand(_ => Stop(), _ => isRunning);
		BrowseGamePathCommand = new RelayCommand(_ => BrowseGamePath());
		Reload();
	}

	public ObservableCollection<LoginAccountRowViewModel> Accounts { get; } = [];

	// Đường dẫn Game.exe hiện ghi trong Login.json — chỉ hiển thị, sửa qua nút "Chọn đường dẫn game".
	public string GamePath {
		get => gamePath;
		private set => SetField(ref gamePath, value);
	}

	public string StatusText {
		get => statusText;
		private set => SetField(ref statusText, value);
	}

	public bool IsRunning {
		get => isRunning;
		private set => SetField(ref isRunning, value);
	}

	public RelayCommand RunCommand { get; }

	public RelayCommand StopCommand { get; }

	public RelayCommand BrowseGamePathCommand { get; }

	private void Reload() {
		Accounts.Clear();
		if (! Settings.TryLoad(configPath, out Settings settings, out string failure)) {
			StatusText = $"Không đọc được cấu hình: {failure}";
			return;
		}
		foreach (LoginAccount account in settings.Accounts) Accounts.Add(new LoginAccountRowViewModel(account));
		GamePath = settings.ExeLink;
		StatusText = $"Đã đọc {Accounts.Count} account | Speed={settings.Speed} | ChờTrướcĐăngNhập={settings.WaitBeforeLogin}ms | DelayLogin={settings.DelayLogin}ms";
	}

	// Mở hộp thoại chọn Game.exe rồi ghi thẳng vào Login.json (chỉ đổi ExeLink, không đụng Accounts/mật khẩu).
	private void BrowseGamePath() {
		Microsoft.Win32.OpenFileDialog dialog = new() {
			Title = "Chọn đường dẫn Game.exe",
			Filter = "Game.exe|Game.exe|Tệp thực thi (*.exe)|*.exe|Tất cả tệp (*.*)|*.*",
			CheckFileExists = true
		};
		if (dialog.ShowDialog() != true) return;
		if (! Settings.TryLoad(configPath, out Settings settings, out string loadFailure)) {
			StatusText = $"Không đọc được cấu hình: {loadFailure}";
			return;
		}
		settings.ExeLink = dialog.FileName;
		if (! Settings.TrySave(configPath, settings, out string saveFailure)) {
			StatusText = $"Không lưu được cấu hình: {saveFailure}";
			return;
		}
		Reload();
	}

	private void Run() {
		if (isRunning) return;
		if (! Settings.TryLoad(configPath, out Settings settings, out string failure)) {
			StatusText = $"Không đọc được cấu hình: {failure}";
			return;
		}
		// Chỉ chạy những dòng còn tick, theo đúng cách AutoFS lọc bằng cột Checked.
		HashSet<string> selected = new(Accounts.Where(row => row.IsChecked).Select(row => row.User), StringComparer.Ordinal);
		settings.Accounts = settings.Accounts.Where(account => selected.Contains(account.User)).ToList();
		if (settings.Accounts.Count == 0) {
			StatusText = "Không có account nào được tick.";
			return;
		}

		// Giữ khoá đăng nhập độc quyền suốt lượt chạy: luồng tự đăng nhập lại cũng mở Game.exe và bơm cùng chuỗi
		// lệnh 280/281/282, hai bên chạy song song là bắn lệnh lẫn sang cửa sổ của nhau.
		IDisposable? lease = LoginSession.TryEnter("Tab Login");
		if (lease == null) {
			StatusText = $"Đang có luồng đăng nhập khác chạy ({LoginSession.CurrentOwner}), thử lại sau.";
			return;
		}

		cancellation = new CancellationTokenSource();
		CancellationToken token = cancellation.Token;
		IsRunning = true;
		StatusText = $"Đang chạy {settings.Accounts.Count} account...";
		Task.Run(() => automation.Run(settings, Append, token), token).ContinueWith(_ => {
			lease.Dispose();
			dispatcher.Invoke(() => {
				IsRunning = false;
				StatusText = "Đã gửi xong chuỗi lệnh. CHƯA đọc được trạng thái client nên không kết luận đăng nhập thành công.";
			});
		});
	}

	private void Stop() {
		cancellation?.Cancel();
		StatusText = "Đã yêu cầu dừng.";
	}

	// Chỉ ghi ra file — vấn đề gì cũng đã có trong login.log, không cần hiển thị lại trên UI (chủ dự án chốt
	// 2026-09-21).
	private void Append(string line) {
		DebugLog.AddLoginEvent(line);
	}
}
