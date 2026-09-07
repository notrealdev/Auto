namespace Auto.Runtime;

using System.Collections.Concurrent;
using Auto.Attack;
using Auto.Utils;

// Port từ D:\G\DEV\UI\Accounts.cs (TickAutoEngineCore + helper) — giữ nguyên thứ tự/điều kiện dừng engine.
// Ngoại lệ đồng bộ: DEV không có file non-UI tương đương để diff trực tiếp (logic nằm ngay trong UI/Accounts.cs,
// WinForms). Khi DEV đổi TickAutoEngineCore, phải tự đọc lại D:\G\DEV\UI\Accounts.cs và so tay với file này.
public static class AccountEngineCoordinator {
	// ConcurrentDictionary bắt buộc: TickOne chạy song song nhiều account qua Parallel.ForEach (AccountListViewModel.EngineTick),
	// Dictionary thường không an toàn đa luồng dù các thread ghi khác key nhau, gây InvalidOperationException hỏng state.
	private static readonly ConcurrentDictionary<IntPtr, AttackPositionState> attackPositionByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, DateTime> deathDetectedUtcByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, string> lastRuntimeGateByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, string> lastAutoGateByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, bool> deathReturnLoggedByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, bool> deathReadErrorLoggedByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, string> deathUndetectedLoggedByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, bool> addressAuditRanByWindow = new();
	// Nhớ trạng thái Tự động đánh lượt trước để phát hiện đúng thời điểm chuyển tắt->bật.
	private static readonly ConcurrentDictionary<IntPtr, bool> attackEnabledByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, string> lastReturnRequestFailureByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, DateTime> nextHeartbeatByWindow = new();
	// Chống lặp dòng FAIL của lệnh 38: chỉ ghi lại khi nội dung lỗi đổi.
	private static readonly ConcurrentDictionary<IntPtr, string> deathSendFailureByWindow = new();

	// Ngưỡng đứng im trước khi khôi phục. Hạ từ 20 xuống 10 giây theo yêu cầu phản ứng nhanh khi nhân vật kẹt ngoài bãi.
	private const int StationaryRestartSeconds = 10;

	private readonly record struct AttackPositionState(int X, int Y, DateTime SinceUtc);

	public static void TickOne(GameWindow game) {
		lock (game.AutoSync) {
			bool masterEnabled = game.Enabled;
			game.AutoFsActionGate.SetAutomationEnabled(masterEnabled);
			DebugLog.SetProcessLoggingEnabled(game.ProcessId, masterEnabled);
			Action<string> accountLog = text => DebugLog.AddForProcess(game.ProcessId, text);
			Action<string> accountDropLog = text => DebugLog.AddLootDropForProcess(game.ProcessId, text);
			Action<string> accountTargetLifecycleLog = text => DebugLog.AddTargetLifecycleForProcess(game.ProcessId, text);
			Action<string> accountTargetViolationLog = text => DebugLog.AddTargetViolationForProcess(game.ProcessId, text);
			Action<string> accountTargetMovementLog = text => DebugLog.AddTargetMovementForProcess(game.ProcessId, text);
			RefreshMapState(game, accountLog);

			bool attackEnabled = game.AttackSettings.Enabled;
			bool lootEnabled = game.LootSettings.Enabled;
			bool repairConfigured = game.BasicSettings.EnableWeaponRepair;
			bool repairEnabled = repairConfigured;
			bool saleEnabled = game.InventorySaleEngine.IsAutomaticSaleEnabled;
			bool returnTalismanConfigured = game.BasicSettings.EnableLowHpReturnTalisman;
			bool autoAdvertiseEnabled = game.AutoAdvertiseEngine.IsConfigured;

			RuntimeLayout layout = RuntimeLayoutResolver.Resolve(game.ProcessId);
			game.RuntimeLayout = layout;
			LogRuntimeGate(game, layout, attackEnabled, lootEnabled, repairEnabled, saleEnabled, accountLog);
			RunAddressAuditOnce(game);
			attackEnabled = attackEnabled && layout.AttackReady;
			lootEnabled = lootEnabled && layout.LootReady;
			repairEnabled = repairEnabled && layout.RepairReady;
			saleEnabled = saleEnabled && layout.SaleReady;
			bool returnTalismanEnabled = returnTalismanConfigured && layout.PlayerReady && layout.InventoryReady;
			bool returnToTownEnabled = game.BasicSettings.DeathAction != DeathAction.StayStill && layout.PlayerReady;

			GameSnapshot snapshot = GameMemory.ReadSnapshot(game.ProcessId);
			if (!snapshot.Success) {
				LogHeartbeat(game, snapshot, masterEnabled, attackEnabled, lootEnabled, repairConfigured, repairEnabled, saleEnabled, accountLog);
				game.AutoFsActionGate.SetRuntimeSuspended(true);
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}
			game.AutoFsActionGate.SetRuntimeSuspended(false);

			if (masterEnabled && returnTalismanEnabled && game.LowHpReturnTalismanEngine.Tick(game.ProcessId, game.Handle, snapshot, game.LastObservedMapId, accountLog)) {
				game.ReturnToTrainingAutomation.Cancel(accountLog, "Hồi thành phù giữ quyền điều khiển");
				game.ConfiguredTrainingMovementAutomation.Cancel(accountLog, "Hồi thành phù giữ quyền điều khiển");
				game.WeaponRepairAutomation.Cancel(game);
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}
			if (!returnTalismanEnabled) game.LowHpReturnTalismanEngine.Reset();

			if (game.LowHpReturnTalismanEngine.ConsumeReturnToTrainingRequest()) {
				if (!game.AttackSettings.EnableReturnToTraining) {
					accountLog($"LOW_HP_RETURN_TO_TRAINING_SKIPPED | Reason=EnableReturnToTraining=False | Hp={snapshot.Hp}/{snapshot.MaxHp}");
				} else if (game.ReturnToTrainingAutomation.IsBusy) {
					accountLog($"LOW_HP_RETURN_TO_TRAINING_SKIPPED | Reason=ReturnToTrainingAutomation.IsBusy | Hp={snapshot.Hp}/{snapshot.MaxHp}");
				} else {
					accountLog($"LOW_HP_RETURN_TO_TRAINING_REQUESTED | Hp={snapshot.Hp}/{snapshot.MaxHp} | MapId={game.LastObservedMapId}");
					game.ReturnToTrainingAutomation.Prepare(game, snapshot, accountLog);
				}
			}

			game.WeaponRepairMonitor.SetEnabled(masterEnabled && repairConfigured);
			if (!game.WeaponRepairAutomation.IsBusy) game.WeaponRepairMonitor.Tick(game.ProcessId, game.BasicSettings.WeaponDurabilityThreshold, masterEnabled && (attackEnabled || lootEnabled), accountLog);
			if (repairConfigured && !layout.RepairReady) game.WeaponRepairMonitor.ReportUnavailableTransport(layout.DescribeUnavailable(RuntimeSubsystem.MovementTransport, RuntimeSubsystem.Map, RuntimeSubsystem.Shop, RuntimeSubsystem.RepairTransport), accountLog);

			if (!masterEnabled || (!attackEnabled && !lootEnabled && !repairEnabled && !saleEnabled && !returnToTownEnabled && !returnTalismanEnabled && !autoAdvertiseEnabled)) {
				LogAutoGate(game, masterEnabled, attackEnabled, lootEnabled, repairEnabled, saleEnabled);
				game.WeaponRepairAutomation.Cancel(game);
				game.ReturnToTrainingAutomation.Cancel();
				game.ConfiguredTrainingMovementAutomation.Cancel();
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}
			lastAutoGateByWindow.TryRemove(game.Handle, out _);

			if (game.WeaponRepairMonitor.RequiresExclusiveControl) {
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}
			if (game.WeaponRepairMonitor.HasPendingRepairRequest && !layout.RepairReady) {
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}

			ApplySnapshot(game, snapshot);
			game.LootSettings.CenterX = game.AttackSettings.CenterX;
			game.LootSettings.CenterY = game.AttackSettings.CenterY;
			game.LootSettings.UseCenterPosition = game.AttackSettings.UseCenterPosition;

			// Bật Tự động đánh mà đang ở ngoài bãi thì đi đúng một chuyến lên bãi đã lưu rồi mới train.
			// Chỉ xét đúng lúc chuyển tắt->bật; đang đứng trong bãi thì bỏ qua hoàn toàn, không bắn lệnh di chuyển nào.
			bool attackWasEnabled = attackEnabledByWindow.TryGetValue(game.Handle, out bool previousAttackEnabled) && previousAttackEnabled;
			attackEnabledByWindow[game.Handle] = attackEnabled;
			if (attackEnabled && !attackWasEnabled && IsOutsideTrainingArea(game, snapshot)) {
				RequestReturnToTrainingCenter(game, snapshot, accountLog, "Bật Tự động đánh khi đang ở ngoài bãi");
			}

			// AutoFS gốc không theo dõi chuột, nên Auto cũng không tạm dừng tự động đánh theo click trái.
			bool manualInputActive = false;

			LogHeartbeat(game, snapshot, masterEnabled, attackEnabled, lootEnabled, repairConfigured, repairEnabled, saleEnabled, accountLog);
			if (HandleDeathPopup(game, snapshot, accountLog)) {
				game.WeaponRepairAutomation.Cancel(game);
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}

			// Skill hỗ trợ bị động chỉ gửi gói bật lại, không chiếm quyền điều khiển nên chạy trước và không chặn luồng khác.
			game.PassiveBuffEngine.Tick(game.ProcessId, game.Handle, attackEnabled, accountLog);

			if (game.SupportEngine.Tick(game.ProcessId, game.Handle, snapshot, attackEnabled, accountLog)) {
				game.ReturnToTrainingAutomation.Cancel(accountLog, "Buff hỗ trợ giữ quyền điều khiển");
				game.ConfiguredTrainingMovementAutomation.Cancel(accountLog, "Buff hỗ trợ giữ quyền điều khiển");
				game.WeaponRepairAutomation.Cancel(game);
				game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.ReturnMovementOwner, true);
				game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.TrainingMovementOwner, true);
				game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.SaleRepairOwner, true);
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}
			game.AutoAdvertiseEngine.Tick(game.ProcessId, game.Handle, accountLog);

			game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.ReturnMovementOwner, game.ReturnToTrainingAutomation.IsBusy);
			game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.TrainingMovementOwner, game.ConfiguredTrainingMovementAutomation.IsBusy);
			game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.SaleRepairOwner, game.WeaponRepairAutomation.IsBusy);
			bool repairPriority = game.WeaponRepairAutomation.IsBusy || (game.WeaponRepairMonitor.HasPendingRepairRequest && layout.RepairReady);
			if (repairPriority) {
				attackPositionByWindow.TryRemove(game.Handle, out _);
				game.ReturnToTrainingAutomation.Cancel(accountLog, "Sửa đồ giữ quyền điều khiển");
				game.ConfiguredTrainingMovementAutomation.Cancel(accountLog, "Sửa đồ giữ quyền điều khiển");
			}

			bool movementRecoveryPending = game.ReturnToTrainingAutomation.IsBusy || game.ConfiguredTrainingMovementAutomation.IsBusy;
			bool recentAttack = game.AttackEngine.HasRecentAttack(TimeSpan.FromSeconds(5));
			if (attackEnabled && !manualInputActive && !repairPriority && (movementRecoveryPending || !recentAttack) && ShouldRestartStationaryAttack(game.Handle, snapshot, out int stationaryMilliseconds)) {
				string reason = $"ANTI_AFK_POSITION_UNCHANGED_{StationaryRestartSeconds}_SECONDS | StationaryMilliseconds={stationaryMilliseconds} | Position={snapshot.X}/{snapshot.Y} | RecentAttack={recentAttack} | MovementBusy={movementRecoveryPending}";
				string recoveredFlow;
				if (game.ReturnToTrainingAutomation.IsBusy) {
					game.ReturnToTrainingAutomation.Recover(accountLog, reason);
					recoveredFlow = "RETURN_TO_TRAINING";
				} else if (game.ConfiguredTrainingMovementAutomation.IsBusy) {
					game.ConfiguredTrainingMovementAutomation.Recover(accountLog, reason);
					recoveredFlow = "CONFIGURED_TRAINING_MOVEMENT";
				} else if (IsOutsideTrainingArea(game, snapshot)) {
					// Đứng im ngoài phạm vi bãi thì tạo lại worker đánh là vô ích vì không quái nào lọt bộ lọc quanh tâm.
					// Phải giao cho ReturnToTrainingAutomation đưa nhân vật về đúng bãi: nó đi một chuyến rồi tự dừng
					// (Prepare chặn bởi !IsBusy, Tick xong gọi Reset), và đi được xuyên map qua cổng.
					RequestReturnToTrainingCenter(game, snapshot, accountLog, "Đứng im ngoài phạm vi bãi");
					game.AttackEngine.Stop();
					recoveredFlow = "RETURN_TO_TRAINING_CENTER";
				} else {
					game.AttackEngine.Stop();
					recoveredFlow = "AUTO_ATTACK";
				}
				accountLog($"ANTI_AFK_COORDINATION_RECOVERY | Reason={reason} | Flow={recoveredFlow} | AttackWorker=RECREATE_ON_TICK");
			}

			// Công tắc Tự động đánh khóa thực thi các luồng Tân thủ, Thành thị và Mê cung nhưng không sửa cấu hình của chúng.
			if (!attackEnabled) {
				game.ReturnToTrainingAutomation.Cancel();
				game.ConfiguredTrainingMovementAutomation.Cancel();
			}

			bool returnToTrainingBusy = attackEnabled && !repairPriority && game.ReturnToTrainingAutomation.Tick(game, snapshot, manualInputActive, accountLog);
			game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.ReturnMovementOwner, returnToTrainingBusy);
			if (returnToTrainingBusy) {
				game.ConfiguredTrainingMovementAutomation.Cancel(accountLog, "Tự lên bãi giữ quyền điều khiển");
				game.WeaponRepairAutomation.Cancel(game);
				game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.SaleRepairOwner, false);
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}

			bool configuredTrainingMovementBusy = attackEnabled && !repairPriority && game.ConfiguredTrainingMovementAutomation.Tick(game, snapshot, manualInputActive, accountLog);
			game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.TrainingMovementOwner, configuredTrainingMovementBusy);
			if (configuredTrainingMovementBusy) {
				game.WeaponRepairAutomation.Cancel(game);
				game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.SaleRepairOwner, false);
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}

			bool repairBusy = game.WeaponRepairAutomation.Tick(game, snapshot, accountLog);
			game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.SaleRepairOwner, repairBusy);
			if (repairBusy) {
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}

			if (lootEnabled) game.LootEngine.Tick(snapshot, game, manualInputActive, accountLog, accountDropLog);
			else game.LootEngine.Stop();

			if (attackEnabled) {
				game.AttackEngine.Tick(snapshot, game, manualInputActive, accountLog, accountTargetLifecycleLog, accountTargetViolationLog, accountTargetMovementLog);
			} else {
				attackPositionByWindow.TryRemove(game.Handle, out _);
				game.AttackEngine.Stop();
			}
		}
	}

	private static void ApplySnapshot(GameWindow game, GameSnapshot snapshot) {
		game.CharacterName = snapshot.CharacterName;
		game.Level = snapshot.Level;
		game.Hp = snapshot.Hp;
		game.MaxHp = snapshot.MaxHp;
		game.Mp = snapshot.Mp;
		game.MaxMp = snapshot.MaxMp;
		game.X = snapshot.X;
		game.Y = snapshot.Y;
		game.MoveTargetX = snapshot.MoveTargetX;
		game.MoveTargetY = snapshot.MoveTargetY;
		game.Combat = snapshot.Combat;
	}

	private static void RefreshMapState(GameWindow game, Action<string>? log) {
		DateTime now = DateTime.UtcNow;
		if (now < game.NextMapIdentityCheckUtc) return;
		game.NextMapIdentityCheckUtc = now.AddSeconds(1);
		GameMapInfo map = GameMapReader.Read(game.ProcessId);
		if (!map.Success) return;
		if (game.LastObservedMapId <= 0) {
			game.LastObservedMapId = map.MapId;
			return;
		}
		if (game.LastObservedMapId == map.MapId) return;

		int previousMapId = game.LastObservedMapId;
		game.LastObservedMapId = map.MapId;
		game.AttackEngine.ResetForMapChange();
		game.LootEngine.Stop();
		game.WeaponRepairMonitor.ResetCache();
		game.WeaponRepairMonitor.ScheduleImmediateCheck("sau đổi map");
		log?.Invoke($"Đổi map | Map={previousMapId}->{map.MapId} | Giữ quái đã chọn và làm mới danh sách quái cùng cache độ bền.");
	}

	// Port từ D:\G\DEV\UI\Accounts.cs:653 (LogHeartbeat) — mỗi account ghi một dòng trạng thái tổng mỗi phút.
	private static void LogHeartbeat(GameWindow game, GameSnapshot snapshot, bool masterEnabled, bool attackEnabled, bool lootEnabled, bool repairConfigured, bool repairReady, bool saleEnabled, Action<string> log) {
		DateTime now = DateTime.UtcNow;
		if (nextHeartbeatByWindow.TryGetValue(game.Handle, out DateTime nextUtc) && now < nextUtc) return;
		nextHeartbeatByWindow[game.Handle] = now.AddMinutes(1);
		EntitySnapshot target = snapshot.Success ? EntitySnapshot.ReadCurrentTarget(game.ProcessId) : new EntitySnapshot();
		log(
			$"HEARTBEAT_AUTO | Master={masterEnabled} | Attack={attackEnabled} | Loot={lootEnabled} | RepairConfigured={repairConfigured} | RepairReady={repairReady} | Sale={saleEnabled} | " +
			$"Snapshot={snapshot.Success}/{snapshot.Status} | Map={game.LastObservedMapId} | Player={snapshot.X}/{snapshot.Y} | HP={snapshot.Hp}/{snapshot.MaxHp} | " +
			$"CombatDiagnostic={(snapshot.Combat.Success ? $"CONFIRMED/{snapshot.Combat.CurrentHp}/{snapshot.Combat.MaxHpCandidate}" : "UNAVAILABLE")} | " +
			$"Target={target.Success}/{target.Index}/{target.Handle}/Active={target.ActiveFlag}/HP={target.Hp}/{target.MaxHp} | " +
			$"AttackState={game.AttackEngine.GetDiagnosticState()} | LootState={game.LootEngine.GetDiagnosticState()} | " +
			$"TrainingMovementBusy={game.ConfiguredTrainingMovementAutomation.IsBusy} | ReturnToTrainingBusy={game.ReturnToTrainingAutomation.IsBusy} | RepairBusy={game.WeaponRepairAutomation.IsBusy}"
		);
	}

	private static void LogRuntimeGate(GameWindow game, RuntimeLayout layout, bool attackEnabled, bool lootEnabled, bool repairEnabled, bool saleEnabled, Action<string> log) {
		List<string> unavailable = new();
		if (attackEnabled && !layout.AttackReady) unavailable.Add("Attack=" + layout.DescribeUnavailable(RuntimeSubsystem.Entity, RuntimeSubsystem.AttackTransport));
		if (lootEnabled && !layout.LootReady) unavailable.Add("Loot=" + layout.DescribeUnavailable(RuntimeSubsystem.Ground, RuntimeSubsystem.LootTransport));
		if (saleEnabled && !layout.SaleReady) unavailable.Add("Sale=" + layout.DescribeUnavailable(RuntimeSubsystem.MovementTransport, RuntimeSubsystem.Inventory, RuntimeSubsystem.ItemTable, RuntimeSubsystem.Map, RuntimeSubsystem.Shop, RuntimeSubsystem.RepairTransport, RuntimeSubsystem.ArrangeTransport));
		if (repairEnabled && !layout.RepairReady) unavailable.Add("Repair=" + layout.DescribeUnavailable(RuntimeSubsystem.MovementTransport, RuntimeSubsystem.Map, RuntimeSubsystem.Shop, RuntimeSubsystem.RepairTransport));
		string state = unavailable.Count == 0 ? "READY" : string.Join(" | ", unavailable);
		// Log của tiến trình chỉ bật khi Auto tổng bật; nếu ghi nhớ trạng thái lúc log đang tắt thì dòng đầu tiên bị nuốt và không bao giờ ghi lại được.
		if (!DebugLog.IsProcessLoggingEnabled(game.ProcessId)) return;
		if (lastRuntimeGateByWindow.TryGetValue(game.Handle, out string? previous) && string.Equals(previous, state, StringComparison.Ordinal)) return;
		lastRuntimeGateByWindow[game.Handle] = state;
		log($"HEARTBEAT_RUNTIME_LAYOUT | Fingerprint={layout.Fingerprint} | Player={layout.PlayerReady} | Attack={layout.AttackReady} | Movement={layout.MovementReady} | Loot={layout.LootReady} | Sale={layout.SaleReady} | Repair={layout.RepairReady} | Gate={state}");
	}

	private static void LogAutoGate(GameWindow game, bool masterEnabled, bool attackEnabled, bool lootEnabled, bool repairEnabled, bool saleEnabled) {
		string gate = $"Master={masterEnabled} | Attack={attackEnabled} | Loot={lootEnabled} | Repair={repairEnabled} | Sale={saleEnabled}";
		if (lastAutoGateByWindow.TryGetValue(game.Handle, out string? previousGate) && previousGate == gate) return;
		lastAutoGateByWindow[game.Handle] = gate;
		DebugLog.AddForProcess(game.ProcessId, "Auto chưa chạy | " + gate + " | Cần bật Auto tổng và ít nhất một module.");
	}

	private static bool ShouldRestartStationaryAttack(IntPtr gameWindow, GameSnapshot snapshot, out int stationaryMilliseconds) {
		stationaryMilliseconds = 0;
		DateTime now = DateTime.UtcNow;
		if (!attackPositionByWindow.TryGetValue(gameWindow, out AttackPositionState state) || state.X != snapshot.X || state.Y != snapshot.Y) {
			attackPositionByWindow[gameWindow] = new AttackPositionState(snapshot.X, snapshot.Y, now);
			return false;
		}
		TimeSpan stationary = now - state.SinceUtc;
		if (stationary < TimeSpan.FromSeconds(StationaryRestartSeconds)) return false;
		stationaryMilliseconds = (int)Math.Min(int.MaxValue, stationary.TotalMilliseconds);
		attackPositionByWindow[gameWindow] = new AttackPositionState(snapshot.X, snapshot.Y, now);
		return true;
	}

	// Kiểm tra toàn bộ địa chỉ client một lần cho mỗi cửa sổ game, để một bản cập nhật client lộ ra hết trong một khối log
	// thay vì lộ dần qua từng lần tính năng hỏng. Chạy ngoài luồng tick vì mỗi mục là một SendMessageTimeout riêng.
	private static void RunAddressAuditOnce(GameWindow game) {
		// Log của tiến trình chưa bật thì mọi dòng sẽ bị bỏ, nên chưa đánh dấu đã chạy để lần tick sau còn ghi được.
		if (! DebugLog.IsProcessLoggingEnabled(game.ProcessId)) return;
		if (! addressAuditRanByWindow.TryAdd(game.Handle, true)) return;
		int processId = game.ProcessId;
		Task.Run(() => {
			try {
				foreach (string line in ClientAddressAudit.Run(game)) DebugLog.AddDebugForProcess(processId, line);
			} catch (Exception ex) {
				DebugLog.AddDebugForProcess(processId, $"ADDRESS_AUDIT_FAILED | {ex.GetType().Name}: {ex.Message}");
			}
		});
	}

	// Nhân vật nằm ngoài bán kính quanh tâm đã cấu hình thì mọi quái đều bị bộ lọc loại, nên nó không thể tự đánh trở lại.
	// Giao cho ReturnToTrainingAutomation đưa nhân vật về đúng bãi đã lưu: nó đi một chuyến rồi tự dừng và đi được xuyên map.
	// Prepare tự chặn khi đang bận nên không chồng lệnh. Nếu không giải được đích thì ghi rõ lý do (chỉ một lần cho tới khi
	// nội dung đổi) thay vì im lặng để nhân vật kẹt như trước.
	private static void RequestReturnToTrainingCenter(GameWindow game, GameSnapshot snapshot, Action<string> log, string reason) {
		if (game.ReturnToTrainingAutomation.IsBusy) return;
		game.ReturnToTrainingAutomation.Prepare(game, snapshot, log);
		if (game.ReturnToTrainingAutomation.IsBusy) {
			lastReturnRequestFailureByWindow.TryRemove(game.Handle, out _);
			return;
		}
		Settings settings = game.AttackSettings;
		string line = $"Tự lên bãi AutoFS không khởi động được | PID={game.ProcessId} | YêuCầu={reason} | TựLênBãi={settings.EnableReturnToTraining} | QuanhĐiểm={settings.UseCenterPosition} | Tâm={settings.CenterX}/{settings.CenterY} | MapTâm={settings.CenterMapId} | MapHiệnTại={game.LastObservedMapId}";
		if (lastReturnRequestFailureByWindow.TryGetValue(game.Handle, out string? previous) && string.Equals(previous, line, StringComparison.Ordinal)) return;
		lastReturnRequestFailureByWindow[game.Handle] = line;
		log(line);
	}

	private static bool IsOutsideTrainingArea(GameWindow game, GameSnapshot snapshot) {
		Settings settings = game.AttackSettings;
		if (! settings.UseCenterPosition || settings.CenterX <= 0 || settings.CenterY <= 0 || snapshot.X <= 0 || snapshot.Y <= 0) return false;
		// Khác map bãi là chắc chắn ngoài bãi. Không xét map thì toạ độ của hai map khác nhau bị đem trừ nhau, cho kết quả vô nghĩa.
		if (settings.CenterMapId > 0 && game.LastObservedMapId > 0 && game.LastObservedMapId != settings.CenterMapId) return true;
		long deltaX = (long)snapshot.X - settings.CenterX;
		long deltaY = (long)snapshot.Y - settings.CenterY;
		long range = Math.Max(settings.Range, 1);
		return deltaX * deltaX + deltaY * deltaY > range * range;
	}

	private static bool HandleDeathPopup(GameWindow game, GameSnapshot snapshot, Action<string> log) {
		bool deathStateRead = TryReadAutoFsDeathState(game, out int deathStatus, out int deathState, out uint deathModal, out uint expectedDeathModal, out string readError);
		bool playerDead = deathStateRead && (deathStatus == 6 || deathState == 15 || deathModal == expectedDeathModal);
		if (!playerDead) {
			if (deathReturnLoggedByWindow.TryRemove(game.Handle, out _)) log("Nhân vật đã rời trạng thái chết; Auto tiếp tục.");
			deathDetectedUtcByWindow.TryRemove(game.Handle, out _);
			deathSendFailureByWindow.TryRemove(game.Handle, out _);
			if (! string.IsNullOrEmpty(readError)) {
				if (deathReadErrorLoggedByWindow.TryAdd(game.Handle, true)) log($"Về thành AutoFS đọc trạng thái FAIL | PID={game.ProcessId} | {readError}");
			} else {
				deathReadErrorLoggedByWindow.TryRemove(game.Handle, out _);
			}
			// Runtime 2026-09-04 ghi nhận HP=0 suốt hơn 8 phút mà không dòng log nào, vì nhánh "chưa chết" hoàn toàn
			// không có log. In giá trị thật của cả ba điều kiện nhận diện khi HP đã bằng 0 để biết trường nào lệch.
			LogDeathStateUndetected(game, snapshot, deathStateRead, deathStatus, deathState, deathModal, expectedDeathModal, log);
			return false;
		}
		deathReadErrorLoggedByWindow.TryRemove(game.Handle, out _);
		deathUndetectedLoggedByWindow.TryRemove(game.Handle, out _);
		game.ReturnToTrainingAutomation.Prepare(game, snapshot, log);
		if (game.BasicSettings.DeathAction == DeathAction.StayStill) {
			if (deathReturnLoggedByWindow.TryAdd(game.Handle, true)) log($"Nhân vật chết AutoFS | PID={game.ProcessId} | Status={deathStatus} | State={deathState} | Modal=0x{deathModal:X8} | Về thành đã tắt");
			return true;
		}

		int returnDelayMilliseconds = game.BasicSettings.ReturnToTownDelayMilliseconds;
		DateTime detectedUtc = deathDetectedUtcByWindow.GetOrAdd(game.Handle, DateTime.UtcNow);
		if ((DateTime.UtcNow - detectedUtc).TotalMilliseconds < returnDelayMilliseconds) {
			if (deathReturnLoggedByWindow.TryAdd(game.Handle, true)) log($"Nhân vật chết AutoFS | PID={game.ProcessId} | Status={deathStatus} | State={deathState} | Modal=0x{deathModal:X8} | Chờ={returnDelayMilliseconds}ms");
			return true;
		}

		int deathActionPayload = (int)game.BasicSettings.DeathAction;
		// Đặt lại mốc trước khi gửi để nhánh lỗi cũng bị giãn đúng ReturnToTownDelayMilliseconds. Đặt sau lệnh gửi thì khi
		// native safe-reject, mốc không bao giờ được cập nhật nên mỗi tick lại gửi lại: runtime 18:05 ghi 519 dòng FAIL
		// liên tiếp cách nhau ~100ms cho một lần chết.
		deathDetectedUtcByWindow[game.Handle] = DateTime.UtcNow;
		// Phải dùng lệnh có xác nhận: TrySendCommand chỉ PostMessageA rồi báo thành công ngay, nên khi native
		// TryDispatchReturnToTown safe-reject (vtable/chữ ký lệch sau bản cập nhật client) log vẫn ghi thành công giả.
		if (!game.AutoFsTransport.TrySendConfirmedCommand(game.Handle, 38, deathActionPayload, out string sendError)) {
			string failure = $"Xử lý khi chết AutoFS FAIL | PID={game.ProcessId} | Command=38 | Payload={deathActionPayload} | {sendError}";
			if (!deathSendFailureByWindow.TryGetValue(game.Handle, out string? previousFailure) || !string.Equals(previousFailure, failure, StringComparison.Ordinal)) {
				deathSendFailureByWindow[game.Handle] = failure;
				log(failure);
			}
			return true;
		}
		deathSendFailureByWindow.TryRemove(game.Handle, out _);
		log($"Xử lý khi chết AutoFS | PID={game.ProcessId} | Command=38 | Payload={deathActionPayload} | Action={game.BasicSettings.DeathAction} | Delay={returnDelayMilliseconds}ms");
		return true;
	}

	// Chỉ ghi khi HP đã bằng 0 mà vẫn không nhận diện được là chết, và chỉ ghi lại khi bộ giá trị đổi.
	// Cụm "Về thành" giữ cho dòng này vào đúng death.log theo bộ định tuyến hiện hành.
	private static void LogDeathStateUndetected(GameWindow game, GameSnapshot snapshot, bool deathStateRead, int status, int state, uint modal, uint expectedModal, Action<string> log) {
		if (! deathStateRead || ! snapshot.Success || snapshot.MaxHp <= 0 || snapshot.Hp > 0) {
			deathUndetectedLoggedByWindow.TryRemove(game.Handle, out _);
			return;
		}
		string line = $"Về thành AutoFS chưa nhận diện được trạng thái chết | PID={game.ProcessId} | Hp={snapshot.Hp}/{snapshot.MaxHp} | " +
			$"Status={status}/ExpectedStatus=6 | State={state}/ExpectedState=15 | Modal=0x{modal:X8}/Expected=0x{expectedModal:X8} | " +
			$"EntityTableRva=0x{game.RuntimeLayout.EntityTableRva:X} | PlayerRecordOffset=0x{game.RuntimeLayout.PlayerRecordOffset:X} | " +
			$"StatusOffset=0x{GameAddresses.Entity.PlayerDeathStatus:X} | StateOffset=0x{GameAddresses.Entity.PlayerDeathState:X} | ModalStateRva=0x{GameAddresses.Globals.ModalState:X}";
		if (deathUndetectedLoggedByWindow.TryGetValue(game.Handle, out string? previous) && string.Equals(previous, line, StringComparison.Ordinal)) return;
		deathUndetectedLoggedByWindow[game.Handle] = line;
		log(line);
	}

	private static bool TryReadAutoFsDeathState(GameWindow game, out int status, out int state, out uint modal, out uint expectedModal, out string error) {
		status = 0;
		state = 0;
		modal = 0;
		expectedModal = 0;
		error = "";
		RuntimeLayout layout = game.RuntimeLayout;
		if (!layout.PlayerReady || layout.EntityTableRva <= 0 || layout.PlayerRecordOffset <= 0) {
			error = "Runtime player layout chưa sẵn sàng.";
			return false;
		}
		try {
			using MemoryReader reader = new(game.ProcessId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) {
				error = "ModuleBase bằng 0.";
				return false;
			}
			IntPtr entityTable = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			if (entityTable == IntPtr.Zero) {
				error = "EntityTable bằng 0.";
				return false;
			}
			IntPtr player = IntPtr.Add(entityTable, layout.PlayerRecordOffset);
			status = reader.ReadInt32(IntPtr.Add(player, GameAddresses.Entity.PlayerDeathStatus));
			state = reader.ReadInt32(IntPtr.Add(player, GameAddresses.Entity.PlayerDeathState));
			modal = unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState)));
			expectedModal = unchecked((uint)IntPtr.Add(moduleBase, GameAddresses.Globals.ReturnToTownModal).ToInt64());
			return true;
		} catch (Exception ex) {
			error = $"{ex.GetType().Name}: {ex.Message}";
			return false;
		}
	}
}
