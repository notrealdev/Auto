namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using System.Windows.Threading;
using Auto.Attack;
using Auto.Runtime;
using Auto.Utils;

public sealed class AccountListViewModel : ViewModelBase {
	private readonly Dictionary<int, GameWindow> knownByProcessId = new();
	private readonly DispatcherTimer scanTimer;
	private readonly DispatcherTimer engineTimer;
	private AccountRowViewModel? selectedRow;
	private bool isScanTicking;
	private bool isEngineTicking;
	private bool autoLogSessionActive;

	public AccountListViewModel() {
		AttackHotkeyTracker.ResetAll();

		scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		scanTimer.Tick += async (_, _) => await ScanTick();
		scanTimer.Start();
		_ = ScanTick();

		engineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
		engineTimer.Tick += async (_, _) => await EngineTick();
		engineTimer.Start();
	}

	public ObservableCollection<AccountRowViewModel> Accounts { get; } = [];

	// Raised khi hotkey Ctrl+A đổi AttackSettings.Enabled từ bên ngoài, để tab Đánh đang mở tự cập nhật lại.
	public event Action<GameWindow>? AttackToggledExternally;

	public AccountRowViewModel? SelectedRow {
		get => selectedRow;
		set => SetField(ref selectedRow, value);
	}

	private async Task ScanTick() {
		if (isScanTicking) return;
		isScanTicking = true;

		try {
			int[] knownProcessIds = knownByProcessId.Keys.ToArray();

			(List<GameWindow> scanned, Dictionary<int, GameSnapshot> snapshots) = await Task.Run(() => {
				List<GameWindow> scannedWindows = WindowScanner.FindGameWindows();
				Dictionary<int, GameSnapshot> snapshotByProcessId = new();
				foreach (int processId in knownProcessIds) snapshotByProcessId[processId] = GameMemory.ReadSnapshot(processId);
				return (scannedWindows, snapshotByProcessId);
			});

			ApplyScanResult(scanned, snapshots);
		} finally {
			isScanTicking = false;
		}
	}

	private void ApplyScanResult(List<GameWindow> scanned, Dictionary<int, GameSnapshot> snapshots) {
		HashSet<int> scannedIds = scanned.Select(game => game.ProcessId).ToHashSet();

		foreach (int deadId in knownByProcessId.Keys.Where(id => !scannedIds.Contains(id)).ToList()) {
			knownByProcessId.Remove(deadId);
			AccountRowViewModel? deadRow = Accounts.FirstOrDefault(row => row.GameWindow.ProcessId == deadId);
			if (deadRow == null) continue;
			Accounts.Remove(deadRow);
			if (SelectedRow == deadRow) SelectedRow = null;
		}

		foreach (GameWindow scannedWindow in scanned) {
			if (knownByProcessId.ContainsKey(scannedWindow.ProcessId)) continue;
			knownByProcessId[scannedWindow.ProcessId] = scannedWindow;
			Accounts.Add(new AccountRowViewModel(scannedWindow));
			AttackHotkeyTracker.TrackWindow(scannedWindow.Handle);
		}

		// Đọc/hiển thị HP/MP độc lập với trạng thái Auto tổng — AccountEngineCoordinator chỉ ApplySnapshot
		// khi Auto tổng bật, nên không thể dùng chung 1 nguồn snapshot cho hiển thị cơ bản.
		foreach ((int processId, GameSnapshot snapshot) in snapshots) {
			if (!knownByProcessId.TryGetValue(processId, out GameWindow? game)) continue;
			if (!snapshot.Success) continue;
			game.CharacterName = snapshot.CharacterName;
			game.Level = snapshot.Level;
			game.Hp = snapshot.Hp;
			game.MaxHp = snapshot.MaxHp;
			game.Mp = snapshot.Mp;
			game.MaxMp = snapshot.MaxMp;
			// Toạ độ chỉ được ghi ở đây khi account chưa bật Auto tổng (khớp quy tắc DEV\UI\Accounts.cs:747).
			// Account đang chạy để AccountEngineCoordinator.ApplySnapshot ghi trong tick đã khoá: AutoFsOrderQueue.ShouldRetry
			// và AutoFsTrainingOrderQueue.ShouldRetry dùng chính game.X/game.Y làm bộ dò tiến độ, nếu bị vòng quét 1 giây
			// ghi đè giá trị cũ thì bộ dò nhiễu và hàng đợi gửi lại lệnh di chuyển thừa.
			if (game.Enabled) continue;
			game.X = snapshot.X;
			game.Y = snapshot.Y;
			game.MoveTargetX = snapshot.MoveTargetX;
			game.MoveTargetY = snapshot.MoveTargetY;
			game.Combat = snapshot.Combat;
		}

		foreach (AccountRowViewModel row in Accounts) row.Refresh();
	}

	private async Task EngineTick() {
		while (AttackHotkeyTracker.TryTakeToggle(out IntPtr handle)) {
			GameWindow? game = Accounts.FirstOrDefault(row => row.GameWindow.Handle == handle)?.GameWindow;
			if (game == null) continue;
			game.AttackSettings.Enabled = !game.AttackSettings.Enabled;
			AttackToggledExternally?.Invoke(game);
		}

		UpdateAutoLogSessionState();

		if (isEngineTicking) return;
		isEngineTicking = true;

		try {
			GameWindow[] snapshot = Accounts.Select(row => row.GameWindow).ToArray();
			await Task.Run(() => Parallel.ForEach(snapshot, new ParallelOptions { MaxDegreeOfParallelism = 3 }, AccountEngineCoordinator.TickOne));
		} finally {
			isEngineTicking = false;
		}
	}

	// Chỉ khi có session Auto đang chạy thì DebugLog mới thực sự ghi file (xem DebugLog.autoLoggingEnabled).
	private void UpdateAutoLogSessionState() {
		bool anyMasterEnabled = Accounts.Any(row => row.GameWindow.Enabled);
		if (anyMasterEnabled == autoLogSessionActive) return;
		autoLogSessionActive = anyMasterEnabled;
		if (anyMasterEnabled) DebugLog.BeginAutoSession(DateTime.Now);
		else DebugLog.EndAutoSession("đã tắt Auto tổng trên tất cả tài khoản");
	}
}
