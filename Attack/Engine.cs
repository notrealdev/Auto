namespace Auto.Attack;

using Auto.Movement;
using Auto.Runtime;
using Auto.Utils;

public sealed class Engine {
	private const int ScanIntervalMilliseconds = 20;
	private const int AttackIntervalMilliseconds = 50;
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
						IReadOnlyList<AutoFsEntity> candidates = scanner.Scan(reader, settings, out int playerX, out int playerY, preferredTargetIndex: preferredTargetIndex);
						if (preferredTargetIndex >= 0 && candidates.Any(candidate => candidate.IsCurrentTarget && candidate.Index != preferredTargetIndex)) preferredTargetIndex = -1;
						AutoFsEntity? target = candidates.FirstOrDefault();
						if (target == null) {
							lastLoggedTargetIndex = -1;
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
								currentLog?.Invoke($"Auto Đánh AutoFS | PID={currentProcessId} | Index={target.Index} | Status={target.Status} | HP={target.Hp} | Type={target.Type} | Name={target.Name} | Selection={(target.IsCurrentTarget ? "CURRENT_TARGET" : "NEAREST")} | AroundPoint={effectiveAroundPoint} | CenterSource={(configuredTrainingMode ? "CONFIGURED_TRAINING_MODE" : "GENERAL_ATTACK")} | PlayerDistance={target.Distance:F2} | CenterDistance={target.DistanceToCenter:F2} | Center={target.CenterX}/{target.CenterY} | Range={Math.Max(settings.Range, 1)} | Delivery=POSTED | Acceptance=UNVERIFIED");
								currentTargetMovementLog?.Invoke($"TARGET_SELECTED | PID={currentProcessId} | Index={target.Index} | Name={target.Name} | Status={target.Status} | HP={target.Hp} | Selection={(target.IsCurrentTarget ? "CURRENT_TARGET" : "NEAREST")} | AroundPoint={effectiveAroundPoint} | CenterSource={(configuredTrainingMode ? "CONFIGURED_TRAINING_MODE" : "GENERAL_ATTACK")} | Target={target.RawX}/{target.RawY} | Center={target.CenterX}/{target.CenterY} | PlayerDistance={target.Distance:F2} | CenterDistance={target.DistanceToCenter:F2} | Range={Math.Max(settings.Range, 1)} | {zeroHpTransition}");
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

	// Ghi lỗi duy nhất một lần cho cùng nội dung.
	private void LogErrorOnce(Action<string>? currentLog, int currentProcessId, string error) {
		if (string.Equals(lastError, error, StringComparison.Ordinal)) return;
		lastError = error;
		currentLog?.Invoke($"Auto Đánh AutoFS FAIL | PID={currentProcessId} | {error}");
	}
}
