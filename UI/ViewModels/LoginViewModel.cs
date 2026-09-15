namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using System.Windows.Threading;
using Auto.Login;

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
	private string statusText = "";
	private string logText = "";
	private bool isRunning;

	public LoginViewModel() {
		ReloadCommand = new RelayCommand(_ => Reload());
		RunCommand = new RelayCommand(_ => Run(), _ => ! isRunning && Accounts.Count > 0);
		StopCommand = new RelayCommand(_ => Stop(), _ => isRunning);
		ClearLogCommand = new RelayCommand(_ => LogText = "");
		Reload();
	}

	public ObservableCollection<LoginAccountRowViewModel> Accounts { get; } = [];

	public string ConfigPath {
		get => configPath;
		set => SetField(ref configPath, value);
	}

	public string StatusText {
		get => statusText;
		private set => SetField(ref statusText, value);
	}

	public string LogText {
		get => logText;
		private set => SetField(ref logText, value);
	}

	public bool IsRunning {
		get => isRunning;
		private set => SetField(ref isRunning, value);
	}

	public RelayCommand ReloadCommand { get; }

	public RelayCommand RunCommand { get; }

	public RelayCommand StopCommand { get; }

	public RelayCommand ClearLogCommand { get; }

	private void Reload() {
		Accounts.Clear();
		if (! Settings.TryLoad(configPath, out Settings settings, out string failure)) {
			StatusText = $"Không đọc được cấu hình: {failure}";
			return;
		}
		foreach (LoginAccount account in settings.Accounts) Accounts.Add(new LoginAccountRowViewModel(account));
		StatusText = $"Đã đọc {Accounts.Count} account | ExeLink={settings.ExeLink} | Speed={settings.Speed} | ChờTrướcĐăngNhập={settings.WaitBeforeLogin}ms | DelayLogin={settings.DelayLogin}ms";
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

		cancellation = new CancellationTokenSource();
		CancellationToken token = cancellation.Token;
		IsRunning = true;
		StatusText = $"Đang chạy {settings.Accounts.Count} account...";
		Task.Run(() => automation.Run(settings, Append, token), token).ContinueWith(_ => dispatcher.Invoke(() => {
			IsRunning = false;
			StatusText = "Đã gửi xong chuỗi lệnh. CHƯA đọc được trạng thái client nên không kết luận đăng nhập thành công.";
		}));
	}

	private void Stop() {
		cancellation?.Cancel();
		StatusText = "Đã yêu cầu dừng.";
	}

	private void Append(string line) {
		dispatcher.Invoke(() => LogText += $"{DateTime.Now:HH:mm:ss} | {line}\r\n");
	}
}
