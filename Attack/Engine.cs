namespace Auto.Attack;

using Auto.Movement;
using Auto.Runtime;
using Auto.Utils;

public sealed class Engine {
	private const int ScanIntervalMilliseconds = 20;
	private const int AttackIntervalMilliseconds = 50;
	private const int OutsideAreaWalkIntervalMilliseconds = 700;
	// AutoFS chặn mọi lệnh di chuyển khi ô trạng thái của nhân vật == 3 (WindowQueue.SplitDisk(), WindowQueue.cs:26253,
	// đọc O_Player + 464). Ô 464 đó chính là AutoFsClientProfile.LifecycleStatus của Auto: hàm ngay kế bên,
	// WindowQueue.DisposeTreeNode(index) (WindowQueue.cs:26228), đọc đúng "index * O_Player + 464" rồi lọc "!= 6" —
	// trùng khít bộ lọc "status == FinishedStatus (6) thì bỏ qua" mà AutoFsEntityScanner đang chạy ở cùng vị trí
	// trong cùng vòng quét. Giá trị 7 của ô này là lúc chết, đã thấy trong death.log ("Status=7 | State=7").
	private const int AutoFsBusyLifecycleStatus = 3;
	private readonly Settings settings;
	private readonly AutoFsEntityScanner scanner = new();
	private readonly AutoFsAttackTransport transport;
	private readonly AutoFsActionGate actionGate;
	private readonly object syncRoot = new();
	private CancellationTokenSource? workerCancellation;
	private Task? worker;
	private int processId;
	private IntPtr gameWindow;
	private GameWindow? game;
	private bool manualInputActive;
	private Action<string>? log;
	private Action<string>? targetMovementLog;
	private int lastLoggedTargetIndex = -1;
	private int preferredTargetIndex = -1;
	private long lastAttackPostedUtcTicks;
	private int zeroHpTargetIndex = -1;
	private DateTime zeroHpTargetObservedUtc = DateTime.MinValue;
	private string lastError = "";
	private int outsideAreaCornerIndex;
	private int outsideAreaCornerAttempt;
	private bool outsideAreaAbortLogged;
	private string lastWalkFailure = "";
	private DateTime nextOutsideAreaWalkUtc = DateTime.MinValue;

	internal Engine(Settings settings, AutoFsAttackTransport transport, AutoFsActionGate actionGate) {
		this.settings = settings;
		this.transport = transport;
		this.actionGate = actionGate;
	}

	public bool IsManualOverrideActive => manualInputActive;

	// Reports whether the worker recently posted an attack command successfully.
	public bool HasRecentAttack(TimeSpan maximumAge) {
		long ticks = Interlocked.Read(ref lastAttackPostedUtcTicks);
		return ticks > 0 && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) <= maximumAge;
	}

	public string GetDiagnosticState() {
		lock (syncRoot) return $"AUTOFS_20MS_PRE_SCAN_50MS_POST_ATTACK | PID={processId} | Target={lastLoggedTargetIndex} | Manual={manualInputActive} | Running={worker != null}";
	}

	public IReadOnlyList<MonsterOption> DiscoverMonsterOptions(GameSnapshot snapshot) {
		if (!snapshot.Success || snapshot.ProcessId <= 0) return Array.Empty<MonsterOption>();
		try {
			// Danh sách chọn quái luôn quét quanh vị trí hiện tại, độc lập với tâm bãi và quái đang chọn
			Settings discoverySettings = new Settings { Range = 1000 };
			return scanner.Scan(snapshot.ProcessId, discoverySettings, true)
				.GroupBy(entity => (entity.Type, Signature: Convert.ToHexString(entity.NameBytes)))
				.Select(group => new MonsterOption(group.First().Name, group.Key.Signature, group.Key.Type))
				.OrderBy(option => option.Name)
				.ToArray();
		} catch {
			return Array.Empty<MonsterOption>();
		}
	}

	public void ResetForMapChange() {
		Stop();
	}

	public void Stop() {
		CancellationTokenSource? cancellation;
		lock (syncRoot) {
			cancellation = workerCancellation;
			workerCancellation = null;
			worker = null;
			processId = 0;
			gameWindow = IntPtr.Zero;
			game = null;
			manualInputActive = false;
			log = null;
			targetMovementLog = null;
			lastLoggedTargetIndex = -1;
			zeroHpTargetIndex = -1;
			zeroHpTargetObservedUtc = DateTime.MinValue;
			lastError = "";
			outsideAreaCornerIndex = 0;
			outsideAreaCornerAttempt = 0;
			outsideAreaAbortLogged = false;
			lastWalkFailure = "";
			nextOutsideAreaWalkUtc = DateTime.MinValue;
		}
		Interlocked.Exchange(ref lastAttackPostedUtcTicks, 0);
		cancellation?.Cancel();
		cancellation?.Dispose();
		transport.Stop();
	}

	public void SetPreferredTargetIndex(int targetIndex) {
		lock (syncRoot) preferredTargetIndex = targetIndex;
	}

	public void ResumeAfterLootInterruption() {
	}

	public void Tick(GameSnapshot snapshot, GameWindow game, bool manualInputActive, Action<string>? log = null, Action<string>? targetLifecycleLog = null, Action<string>? targetViolationLog = null, Action<string>? targetMovementLog = null) {
		IntPtr gameWindowHandle = game.Handle;
		bool enabled = settings.Enabled;
		if (!enabled || !snapshot.Success || snapshot.ProcessId <= 0 || gameWindowHandle == IntPtr.Zero) {
			Stop();
			return;
		}

		lock (syncRoot) {
			processId = snapshot.ProcessId;
			gameWindow = gameWindowHandle;
			this.game = game;
			this.manualInputActive = manualInputActive;
			this.log = log;
			this.targetMovementLog = targetMovementLog;
			if (worker != null) return;
			workerCancellation = new CancellationTokenSource();
			CancellationToken token = workerCancellation.Token;
			worker = Task.Run(() => RunWorker(token), token);
		}
	}

	// Thực hiện đúng chu kỳ AutoFS: chờ 20 ms trước mỗi lần quét và chờ thêm 50 ms sau khi phát lệnh đánh.
	private async Task RunWorker(CancellationToken token) {
		MemoryReader? reader = null;
		int readerProcessId = 0;
		while (!token.IsCancellationRequested) {
			try {
				await Task.Delay(ScanIntervalMilliseconds, token).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			bool attackPosted = false;
			int currentProcessId;
			IntPtr currentWindow;
			GameWindow? currentGame;
			bool currentManualInput;
			Action<string>? currentLog;
			Action<string>? currentTargetMovementLog;
			lock (syncRoot) {
				currentProcessId = processId;
				currentWindow = gameWindow;
				currentGame = game;
				currentManualInput = manualInputActive;
				currentLog = log;
				currentTargetMovementLog = targetMovementLog;
			}

			if (!currentManualInput && currentProcessId > 0 && currentWindow != IntPtr.Zero && currentGame != null) {
				try {
					actionGate.TryRunAttack(() => {
						if (reader == null || readerProcessId != currentProcessId) {
							reader?.Dispose();
							reader = new MemoryReader(currentProcessId);
							readerProcessId = currentProcessId;
						}
						IReadOnlyList<AutoFsEntity> candidates = scanner.Scan(reader, settings, out int playerX, out int playerY, out int playerLifecycleStatus, preferredTargetIndex: preferredTargetIndex);
						if (preferredTargetIndex >= 0 && candidates.Any(candidate => candidate.IsCurrentTarget && candidate.Index != preferredTargetIndex)) preferredTargetIndex = -1;
						AutoFsEntity? target = candidates.FirstOrDefault();
						if (target == null) {
							lastLoggedTargetIndex = -1;
							TryWanderTrainingCorners(currentGame, currentProcessId, playerX, playerY, playerLifecycleStatus, currentTargetMovementLog);
						} else if (!transport.TrySend(currentWindow, target.Index, out string error)) {
							LogErrorOnce(currentLog, currentProcessId, error);
						} else {
							attackPosted = true;
							Interlocked.Exchange(ref lastAttackPostedUtcTicks, DateTime.UtcNow.Ticks);
							lastError = "";
							if (target.IsCurrentTarget && target.Hp <= 0 && zeroHpTargetIndex != target.Index) {
								zeroHpTargetIndex = target.Index;
								zeroHpTargetObservedUtc = DateTime.UtcNow;
								currentTargetMovementLog?.Invoke($"CURRENT_TARGET_ZERO_HP_OBSERVED | PID={currentProcessId} | Index={target.Index} | Name={target.Name} | Status={target.Status} | HP={target.Hp} | Action=OBSERVE_ONLY");
							}
							if (lastLoggedTargetIndex != target.Index) {
								bool configuredTrainingMode = settings.TrainingEnabled || settings.TeachingEnabled || settings.ContinueEnabled;
								bool effectiveAroundPoint = configuredTrainingMode || settings.UseCenterPosition;
								string zeroHpTransition = zeroHpTargetObservedUtc == DateTime.MinValue
									? "PreviousZeroHpTarget=NONE"
									: $"PreviousZeroHpTarget={zeroHpTargetIndex} | MillisecondsSinceZeroHp={(int)Math.Min(int.MaxValue, (DateTime.UtcNow - zeroHpTargetObservedUtc).TotalMilliseconds)}";
								lastLoggedTargetIndex = target.Index;
								// Một dòng duy nhất cho cả việc chọn mục tiêu lẫn việc gửi lệnh; trước đây hai dòng này
								// được ghi cùng lúc vào hai file khác nhau với nội dung gần trùng hết.
								currentLog?.Invoke($"Auto Đánh AutoFS | PID={currentProcessId} | Index={target.Index} | Status={target.Status} | HP={target.Hp} | Type={target.Type} | Name={target.Name} | Selection={(target.IsCurrentTarget ? "CURRENT_TARGET" : "NEAREST")} | AroundPoint={effectiveAroundPoint} | CenterSource={(configuredTrainingMode ? "CONFIGURED_TRAINING_MODE" : "GENERAL_ATTACK")} | Target={target.RawX}/{target.RawY} | Center={target.CenterX}/{target.CenterY} | PlayerDistance={target.Distance:F2} | CenterDistance={target.DistanceToCenter:F2} | Range={Math.Max(settings.Range, 1)} | {zeroHpTransition} | Delivery=POSTED | Acceptance=UNVERIFIED");
								if (zeroHpTargetIndex != target.Index) {
									zeroHpTargetIndex = -1;
									zeroHpTargetObservedUtc = DateTime.MinValue;
								}
							}
						}
					});
				} catch (Exception ex) {
					LogErrorOnce(currentLog, currentProcessId, $"{ex.GetType().Name}: {ex.Message}");
				}
			}

			if (attackPosted) {
				try {
					await Task.Delay(AttackIntervalMilliseconds, token).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					break;
				}
			}
		}
		reader?.Dispose();
	}

	// Port khối "Quanh điểm, không còn ứng viên" của AutoFS (VectorFactory.cs:4523-4640, bản đã bóc lớp làm rối):
	//   if (!QuanhĐiểm) break;
	//   if (flag8) {
	//       if (num4 > 3) num4 = 0;
	//       num5/num6 = XTrain ± TầmĐánh/3, YTrain ± TầmĐánh/3 theo num4 = 0:(-,-) 1:(+,+) 2:(-,+) 3:(+,-)
	//       if (SplitDisk()) break;
	//       if (NavigateEmulator(num5, num6)) { num4++; num3++; break; }
	//       UncheckDomain(num5, num6); Thread.Sleep(700);
	//       num7++; if (num7 <= 2) break; num7 = 0; num4++;
	//   }
	// flag8 = "chỉ có một điểm bãi" (list4.Count == 1) hoặc "không có danh sách điểm bãi" — Auto luôn dùng đúng một
	// tâm bãi nên luôn rơi vào nhánh này.
	// Bắt buộc dùng AutoFsMovementCommand.TryWalkTo (port UncheckDomain, chỉ gửi lệnh 0 và 5) — đúng cách di chuyển
	// của vòng đánh. KHÔNG được dùng TryMoveTo ở đây: đó là port SaveDevice, tức di chuyển có gắn cờ, chỉ dành cho
	// luồng đi sửa đồ và tự lên bãi.
	private void TryWanderTrainingCorners(GameWindow currentGame, int currentProcessId, int playerX, int playerY, int playerLifecycleStatus, Action<string>? currentTargetMovementLog) {
		bool aroundPoint = settings.TrainingEnabled || settings.TeachingEnabled || settings.ContinueEnabled || settings.UseCenterPosition;
		if (!aroundPoint || playerX <= 0 || playerY <= 0) return;
		if (currentGame.ReturnToTrainingAutomation.IsBusy || currentGame.ConfiguredTrainingMovementAutomation.IsBusy || currentGame.WeaponRepairAutomation.IsBusy) return;
		DateTime now = DateTime.UtcNow;
		if (now < nextOutsideAreaWalkUtc) return;
		(int centerX, int centerY) = AutoFsEntityScanner.GetCenter(settings, playerX, playerY);
		if (centerX <= 0 || centerY <= 0) return;
		int range = Math.Max(settings.Range, 1);
		int corner = outsideAreaCornerIndex;
		int step = range / 3;
		int destinationX = centerX + (corner is 1 or 3 ? step : -step);
		int destinationY = centerY + (corner is 1 or 2 ? step : -step);
		// AutoFS "if (SplitDisk()) break;": bỏ lượt đi khi ô trạng thái của nhân vật đang mang giá trị bận.
		// Không đặt mốc chờ 700ms ở nhánh này — AutoFS chỉ Thread.Sleep(700) sau khi thật sự gửi lệnh đi.
		// Runtime beta 2026-09-06: 928/928 mẫu đều đọc ra 7 kể cả lúc đang chạy, nên chốt này hiện KHÔNG bao giờ kích
		// hoạt. Giữ lại vì ánh xạ offset đã xác minh, còn hằng số 3 là của client đời cũ, chưa tìm được giá trị tương ứng.
		if (playerLifecycleStatus == AutoFsBusyLifecycleStatus) return;
		// Chốt an toàn KHÔNG có trong AutoFS, thêm sau sự cố runtime beta 21:36: nhân vật bị đẩy tới 3840 khỏi tâm bãi
		// trong khi mọi góc đích đều nằm trong bán kính Range/3*1.42. Nếu nhân vật đã ở ngoài gấp đôi bán kính bãi thì
		// vòng đi 4 góc không còn kéo về được nữa; im lặng còn hơn tiếp tục đẩy đi sai hướng.
		long driftX = (long)playerX - centerX;
		long driftY = (long)playerY - centerY;
		if (driftX * driftX + driftY * driftY > (long)range * range * 4) {
			if (outsideAreaAbortLogged) return;
			outsideAreaAbortLogged = true;
			currentTargetMovementLog?.Invoke($"NO_TARGET_CORNER_ABORT | PID={currentProcessId} | Player={playerX}/{playerY} | Center={centerX}/{centerY} | Distance={Math.Sqrt(driftX * driftX + driftY * driftY):F0} | Range={range} | Lý do=Ngoài gấp đôi bán kính bãi, dừng đi 4 góc");
			return;
		}
		outsideAreaAbortLogged = false;
		// AutoFS bỏ hẳn bước đi khi đã tới góc đó và chuyển sang góc kế ngay, thay vì gửi thêm lệnh thừa.
		if (HasReachedTrainingCorner(playerX, playerY, destinationX, destinationY)) {
			outsideAreaCornerAttempt = 0;
			outsideAreaCornerIndex = (corner + 1) % 4;
			currentTargetMovementLog?.Invoke($"NO_TARGET_CORNER_REACHED | PID={currentProcessId} | Player={playerX}/{playerY} | Center={centerX}/{centerY} | Corner={corner} | PlayerStatus={playerLifecycleStatus} | Destination={destinationX}/{destinationY} | Range={range}");
			return;
		}
		// AutoFS bám cùng một góc 3 lượt (num7 = 0,1,2) rồi mới sang góc kế. Bản trước đổi góc mỗi lượt nên cứ 700ms
		// lại đảo sang góc đối diện, nhân vật giằng qua giằng lại và trôi xa dần tâm bãi.
		int attempt = outsideAreaCornerAttempt + 1;
		if (attempt > 2) {
			outsideAreaCornerAttempt = 0;
			outsideAreaCornerIndex = (corner + 1) % 4;
		} else {
			outsideAreaCornerAttempt = attempt;
		}
		// Trước đây nhánh thất bại chỉ "return", nuốt luôn lý do native từ chối. Ghi lại, dedupe để không spam mỗi 700ms.
		if (!AutoFsMovementCommand.TryWalkTo(currentGame, destinationX, destinationY, out string walkResult)) {
			if (!string.Equals(lastWalkFailure, walkResult, StringComparison.Ordinal)) {
				lastWalkFailure = walkResult;
				currentTargetMovementLog?.Invoke($"NO_TARGET_CORNER_WALK FAIL | PID={currentProcessId} | Player={playerX}/{playerY} | Corner={corner} | Destination={destinationX}/{destinationY} | {walkResult}");
			}
			return;
		}
		lastWalkFailure = "";
		nextOutsideAreaWalkUtc = now.AddMilliseconds(OutsideAreaWalkIntervalMilliseconds);
		currentTargetMovementLog?.Invoke($"NO_TARGET_CORNER_WALK | PID={currentProcessId} | Player={playerX}/{playerY} | Center={centerX}/{centerY} | Corner={corner} | Attempt={attempt} | PlayerStatus={playerLifecycleStatus} | Destination={destinationX}/{destinationY} | Range={range} | {walkResult}");
	}

	// Port AutoFS WindowQueue.NavigateEmulator(x, y) (WindowQueue.cs:26907-26955): so ô lưới của vị trí hiện tại với
	// ô lưới của đích, chứ không so toạ độ tuyệt đối.
	//   toạ độ > 9999: so SaveDevice() với (x/32/8) + "," + (y/32/16)
	//   ngược lại    : so SaveDevice() với (x/8) + "," + (y/16)
	// SaveDevice() đọc chuỗi toạ độ mà client tự hiển thị. Ở đây thay bằng phép chia nguyên trên toạ độ raw đọc từ
	// entity, tức coi chuỗi hiển thị = raw/32/8 và raw/32/16 — GIẢ ĐỊNH, chưa đối chiếu được với chuỗi thật của client.
	// Dù giả định lệch thì hệ quả cũng chỉ là sai lệch một ô (256 raw trục X, 512 raw trục Y), không đổi bản chất.
	private static bool HasReachedTrainingCorner(int playerX, int playerY, int destinationX, int destinationY) {
		if (destinationX > 9999 && destinationY > 9999) return playerX / 32 / 8 == destinationX / 32 / 8 && playerY / 32 / 16 == destinationY / 32 / 16;
		return playerX / 8 == destinationX / 8 && playerY / 16 == destinationY / 16;
	}

	// Ghi lỗi duy nhất một lần cho cùng nội dung.
	private void LogErrorOnce(Action<string>? currentLog, int currentProcessId, string error) {
		if (string.Equals(lastError, error, StringComparison.Ordinal)) return;
		lastError = error;
		currentLog?.Invoke($"Auto Đánh AutoFS FAIL | PID={currentProcessId} | {error}");
	}
}
