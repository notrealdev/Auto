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
	// Hồ sơ đã đọc được cho account mở app lần đầu (không phải do Auto tự relogin) nhưng CHƯA áp dụng vào UI —
	// chờ người dùng bấm nút Áp dụng. Khoá theo ProcessId vì đó là thứ ổn định trong một phiên chạy của cửa sổ.
	private readonly Dictionary<int, AccountProfile> pendingManualApply = new();

	public AccountListViewModel() {
		AttackHotkeyTracker.ResetAll();
		// Đọc hồ sơ TRƯỚC lần quét đầu tiên: ScanTick có thể đọc được tên nhân vật ngay nhịp đầu, mà lúc đó kho
		// hồ sơ chưa nạp thì account đó mất lượt khôi phục (ProfileRestored chỉ chạy đúng một lần).
		AccountProfileStore.LoadAll();
		// Danh sách điểm train dùng chung cho mọi account, nạp cùng lúc với hồ sơ.
		TrainingPointStore.Load();

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

	// Raised sau khi hồ sơ cấu hình vừa được khôi phục cho một account. Các tab đọc Settings qua getter nên WPF
	// không biết giá trị vừa đổi từ code — MainWindowViewModel nghe sự kiện này để dựng lại tab đang mở.
	public event Action<GameWindow>? ProfileRestored;

	public AccountRowViewModel? SelectedRow {
		get => selectedRow;
		set => SetField(ref selectedRow, value);
	}

	private async Task ScanTick() {
		if (isScanTicking) return;
		isScanTicking = true;

		try {
			int[] knownProcessIds = knownByProcessId.Keys.ToArray();
			// Chụp sẵn handle + tên ở luồng UI: knownByProcessId là Dictionary thường, không được chạm từ Task.Run.
			(int ProcessId, IntPtr Handle, string Name)[] knownWindows = knownByProcessId.Values
				.Select(game => (game.ProcessId, game.Handle, game.CharacterName))
				.ToArray();

			(List<GameWindow> scanned, Dictionary<int, GameSnapshot> snapshots, Dictionary<int, InventoryStrengthReading> strengths, Dictionary<int, int> moneys) = await Task.Run(() => {
				List<GameWindow> scannedWindows = WindowScanner.FindGameWindows();
				Dictionary<int, GameSnapshot> snapshotByProcessId = new();
				Dictionary<int, InventoryStrengthReading> strengthByProcessId = new();
				Dictionary<int, int> moneyByProcessId = new();
				foreach (int processId in knownProcessIds) {
					GameSnapshot snapshot = GameMemory.ReadSnapshot(processId);
					snapshotByProcessId[processId] = snapshot;
					// Chỉ đọc sức lực và tiền khi đã vào game: chưa đăng nhập thì con trỏ gốc bằng 0, đọc chỉ tốn thêm một lần mở tiến trình.
					// Read (số lớn hơn giữa ô game và tổng trọng lượng túi), KHÔNG dùng ReadRaw: ô game +0x27C trễ so với số
					// trong túi đồ game hiển thị. Chủ dự án quan sát 2026-09-25: dòng account 2x/335 trong khi mở túi trong
					// game thấy 7x/335, vài giây sau dòng account mới nhảy lên. Đo cùng ngày: Read thêm ~0,9ms/lượt cho 6 client.
					strengthByProcessId[processId] = snapshot.Success ? InventoryStrengthReader.Read(processId) : InventoryStrengthReading.Fail("Chưa vào game.");
					moneyByProcessId[processId] = snapshot.Success ? InventoryMoneyReader.Read(processId) : -1;
				}
				// Dò treo ở đây chứ không ở luồng UI: IsHungAppWindow gần như miễn phí, nhưng nhánh đo WM_NULL bên
				// trong Probe là một chuyến đồng bộ sang luồng UI của client, không được để nó chắn giao diện Auto.
				foreach ((int processId, IntPtr handle, string name) in knownWindows) WindowResponsiveness.Probe(processId, handle, name);
				return (scannedWindows, snapshotByProcessId, strengthByProcessId, moneyByProcessId);
			});

			ApplyScanResult(scanned, snapshots, strengths, moneys);
		} finally {
			isScanTicking = false;
		}
	}

	// Ghi ĐỒNG BỘ xuống Profiles.json — chỉ chạy khi người dùng bấm nút Lưu (chủ dự án chốt 2026-09-22: không còn
	// tự lưu định kỳ, không tự lưu khi đóng app; chưa bấm Lưu thì thay đổi trong phiên mất khi tắt Auto).
	public void SaveProfilesNow() {
		AccountProfile[] captured = CaptureProfiles();
		if (captured.Length == 0) return;
		AccountProfileStore.SaveIfChanged(captured);
	}

	// lock(game.AutoSync) là bắt buộc: hàm này chạy luồng UI, còn EngineTick chạy Parallel.ForEach(TickOne) trên
	// thread pool và TickOne giữ đúng khoá đó (AccountEngineCoordinator.cs:81). Hai bên chạy song song thật.
	private AccountProfile[] CaptureProfiles() {
		List<AccountProfile> captured = [];
		foreach (AccountRowViewModel row in Accounts) {
			GameWindow game = row.GameWindow;
			// Chưa đọc được tên nhân vật thì chưa biết lưu vào khoá nào.
			if (game.CharacterName.Length == 0) continue;
			lock (game.AutoSync) captured.Add(AccountProfileStore.Capture(game));
		}
		return [.. captured];
	}

	private void ApplyScanResult(List<GameWindow> scanned, Dictionary<int, GameSnapshot> snapshots, Dictionary<int, InventoryStrengthReading> strengths, Dictionary<int, int> moneys) {
		HashSet<int> scannedIds = scanned.Select(game => game.ProcessId).ToHashSet();

		foreach (int deadId in knownByProcessId.Keys.Where(id => !scannedIds.Contains(id)).ToList()) {
			// GHI LẠI TRƯỚC KHI XOÁ. Trước đây cửa sổ game biến mất thì Auto lặng lẽ bỏ dòng account, log chỉ đơn giản
			// im bặt — không phân biệt được "client crash" với "đang chạy bình thường mà chưa tới nhịp ghi tiếp".
			//
			// Đúng lỗ hổng này làm tôi suýt kết luận sai hôm 2026-09-15: PID 16116 im từ 15:12:59 nên tôi tưởng nó
			// chết, trong khi Get-Process cho thấy tiến trình vẫn sống và vẫn nhịp tim đều — nó chỉ chưa tới nhịp kế.
			// Dùng AddClientEvent: tiến trình đã biến mất nên cổng lọc theo PID có thể đã chặn, và dòng này thuộc nhóm bằng
			// chứng vòng đời client nên phải nằm cùng client-freeze.log với CLIENT_FREEZE_*.
			GameWindow deadWindow = knownByProcessId[deadId];
			string lastState = snapshots.TryGetValue(deadId, out GameSnapshot? lastSnapshot) && lastSnapshot != null
				? $"SnapshotCuối={lastSnapshot.Success} | HP={lastSnapshot.Hp}/{lastSnapshot.MaxHp} | ViTri={lastSnapshot.X}/{lastSnapshot.Y}"
				: "SnapshotCuối=KHÔNG_ĐỌC_ĐƯỢC";
			DebugLog.AddClientEvent($"GAME_WINDOW_LOST | PID={deadId} | {deadWindow.CharacterName} | {lastState} | Map={deadWindow.LastObservedMapId} | SốAccountCònLại={scannedIds.Count}");
			knownByProcessId.Remove(deadId);
			pendingManualApply.Remove(deadId);
			WindowResponsiveness.Forget(deadId);
			ClientFreezeWatch.Forget(deadId);
			// Hai bảng khoá theo HWND, trước đây không ai dọn nên rò một mục cho mỗi client đã đóng.
			AccountEngineCoordinator.Forget(deadWindow.Handle);
			AttackHotkeyTracker.Untrack(deadWindow.Handle);
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
			// Ghi TRƯỚC nhánh snapshot hỏng bên dưới: client thoát ra màn chọn nhân vật thì phải xoá số cũ, không để
			// dòng account treo mãi sức lực của phiên trước. strengths và snapshots cùng khoá (dựng chung một vòng lặp).
			InventoryStrengthReading strength = strengths[processId];
			game.StrengthCurrent = strength.Success ? strength.Current : -1;
			game.StrengthMaximum = strength.Success ? strength.Maximum : 0;
			game.Money = moneys[processId];
			if (!snapshot.Success) continue;
			game.CharacterName = snapshot.CharacterName;
			RestoreProfileOnce(game);
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

		// Luôn giữ MỘT dòng được chọn khi còn account.
		//
		// Không có dòng nào được chọn thì SelectedRow đứng yên ở null, MainWindowViewModel.OnAccountListPropertyChanged
		// không bao giờ chạy, và mọi tab vẫn treo ở viewmodel dựng bằng constructor rỗng — tức ghi vào Settings rác
		// không thuộc account nào. Người dùng tick "Làm nhiệm vụ" hay "Tự động đánh" thì engine không thấy gì.
		//
		// Bằng chứng: quest.log rỗng và heartbeat.log 2026-09-09 18:57/18:58 ghi "Quest=False" trong khi ô đã được
		// tick. Ô "Auto tổng" nằm trong chính dòng account (MainWindow.xaml:70), click vào CheckBox thì WPF không
		// chọn ListBoxItem, nên bật Auto tổng KHÔNG kéo theo chọn dòng.
		if (SelectedRow == null) SelectedRow = Accounts.FirstOrDefault();
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

	// Khôi phục hồ sơ cấu hình ĐÚNG MỘT LẦN cho mỗi cửa sổ, ngay lần đầu đọc được tên nhân vật.
	//
	// Đặt ở đây vì đây là chỗ duy nhất trong app mà CharacterName lần đầu có giá trị. Không kiểm cờ ProfileRestored
	// thì mỗi nhịp quét 1 giây lại nạp đè lên đúng thứ người dùng vừa sửa tay trên tab.
	private void RestoreProfileOnce(GameWindow game) {
		if (game.ProfileRestored || game.CharacterName.Length == 0) return;
		game.ProfileRestored = true;
		if (! AccountProfileStore.TryGet(game.CharacterName, out AccountProfile profile)) {
			DebugLog.AddProfileEvent($"PROFILE_NOT_FOUND | PID={game.ProcessId} | NhânVật={game.CharacterName} | Chưa có hồ sơ, giữ nguyên mặc định.");
			return;
		}
		// Client do CHÍNH Auto đăng nhập lại thì bật lại đúng trạng thái trước khi rớt — không thì cứu xong account
		// vẫn nằm im, vô nghĩa. Consume: lấy ra và xoá, nên chỉ có tác dụng đúng một lần.
		bool autoLaunched = ReloginSupervisor.ConsumeAutoLaunched(game.CharacterName);
		if (autoLaunched) {
			lock (game.AutoSync) AccountProfileStore.Apply(game, profile, autoEnableMaster: true);
			ProfileRestored?.Invoke(game);
			return;
		}
		// Client chủ dự án tự mở tay (không phải Auto tự relogin): KHÔNG tự áp dụng nữa (chốt 2026-09-22) — chỉ
		// giữ lại hồ sơ, chờ bấm nút "Áp dụng cấu hình đã lưu" ở ApplyAllProfilesNow.
		pendingManualApply[game.ProcessId] = profile;
		DebugLog.AddProfileEvent($"PROFILE_PENDING_MANUAL_APPLY | PID={game.ProcessId} | NhânVật={game.CharacterName} | Chờ bấm nút Áp dụng.");
	}

	// Áp dụng TẤT CẢ hồ sơ đang chờ (mở app lần đầu, chưa qua relogin tự động) vào UI/engine cùng lúc — người dùng
	// bấm nút "Áp dụng cấu hình đã lưu" (chốt 2026-09-22: không còn tự áp dụng khi mở app).
	public void ApplyAllProfilesNow() {
		foreach ((int processId, AccountProfile profile) in pendingManualApply) {
			if (! knownByProcessId.TryGetValue(processId, out GameWindow? game)) continue;
			lock (game.AutoSync) AccountProfileStore.Apply(game, profile, autoEnableMaster: false);
			ProfileRestored?.Invoke(game);
		}
		pendingManualApply.Clear();
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
