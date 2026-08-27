namespace Auto.Loot;

using Auto.Attack;
using Auto.Utils;

public sealed class Engine {
	private const int ScanIntervalMilliseconds = 200;
	private const int RetryIntervalMilliseconds = 100;
	private const int AutoFsApproachCommand = 32;
	private const int AutoFsSelectGroundItemCommand = 9;
	private const int AutoFsPickupCommand = 78;
	private const int FirstGroundItemIndex = 1;
	private const int LastGroundItemIndex = 127;
	private const int NearRawDistance = 200;
	private const int NearPickupAttemptLimit = 3;
	private const int FailedCoordinateCooldownMilliseconds = 30000;
	private readonly Settings settings;
	private readonly Finder finder;
	private readonly AutoFsAttackTransport transport;
	private readonly AutoFsActionGate actionGate;
	private readonly object syncRoot = new();
	private readonly HashSet<int> loggedFilterDecisions = new();
	private readonly Dictionary<(int X, int Y), DateTime> failedCoordinates = new();
	private CancellationTokenSource? workerCancellation;
	private Task? worker;
	private int processId;
	private IntPtr gameWindow;
	private bool manualInputActive;
	private LootSnapshot? pendingCandidate;
	private int pendingItemIndex;
	private (int X, int Y) pendingCoordinate;
	private int pendingPickupAttempts;
	private int pendingNearPickupAttempts;
	private bool approachPrepared;
	private bool startedLogged;
	private string lastError = "";
	private string lastScanSummary = "";
	private string lastScanRejectionSummary = "";
	private string lastCandidateOrderSummary = "";
	private string lastSuspensionState = "";
	private Action<string>? log;
	private Action<string>? dropLog;

	public bool IsBusy {
		get {
			lock (syncRoot) return pendingCandidate != null;
		}
	}

	internal Engine(Settings settings, AutoFsAttackTransport transport, AutoFsActionGate actionGate) {
		this.settings = settings;
		this.transport = transport;
		this.actionGate = actionGate;
		finder = new Finder(settings);
	}

	public string GetDiagnosticState() {
		lock (syncRoot) {
			string pending = pendingCandidate == null ? "NONE" : $"{pendingItemIndex}/{pendingCandidate.ItemNameRaw}";
			string pointer = pendingCandidate == null ? "NONE" : $"0x{pendingCandidate.GroundObjectAddress.ToInt64():X8}";
			return $"AUTOFS_MONITOR_LOOP | Worker={worker != null} | Pending={pending} | GroundPointer={pointer} | PickupAttempts={pendingPickupAttempts} | NearAttempts={pendingNearPickupAttempts} | Cooldowns={failedCoordinates.Count} | Prepared={approachPrepared}";
		}
	}

	public IReadOnlyList<string> DiscoverItemOptions(GameSnapshot snapshot) {
		if (! snapshot.Success || snapshot.ProcessId <= 0) return Array.Empty<string>();
		return finder.FindVisibleSpriteItemsForAttackSafety(snapshot)
			.Select(item => ItemGroupClassifier.NormalizeName(item.ItemNameRaw))
			.Where(name => name.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
			.ToArray();
	}

	public void ResetDropJournal() {
		lock (syncRoot) loggedFilterDecisions.Clear();
	}

	public void Stop() {
		CancellationTokenSource? cancellation;
		lock (syncRoot) {
			cancellation = workerCancellation;
			processId = 0;
			gameWindow = IntPtr.Zero;
			manualInputActive = false;
			startedLogged = false;
			lastError = "";
			lastScanSummary = "";
			lastScanRejectionSummary = "";
			lastCandidateOrderSummary = "";
			lastSuspensionState = "";
			log = null;
			dropLog = null;
		}
		cancellation?.Cancel();
	}

	public bool Tick(GameSnapshot snapshot, IntPtr gameWindowHandle, bool manualInputActive, Action<string>? log = null, Action<string>? dropLog = null) {
		if (! settings.Enabled || ! snapshot.Success || snapshot.ProcessId <= 0 || gameWindowHandle == IntPtr.Zero) {
			Stop();
			return false;
		}

		lock (syncRoot) {
			processId = snapshot.ProcessId;
			gameWindow = gameWindowHandle;
			this.manualInputActive = manualInputActive;
			this.log = log;
			this.dropLog = dropLog;
			if (worker == null) {
				workerCancellation = new CancellationTokenSource();
				CancellationToken token = workerCancellation.Token;
				worker = Task.Run(() => RunWorker(token), token);
			}
			return pendingCandidate != null;
		}
	}

	private async Task RunWorker(CancellationToken token) {
		try {
			while (! token.IsCancellationRequested) {
				WorkerContext context = GetWorkerContext();
				if (context.ProcessId <= 0 || context.GameWindow == IntPtr.Zero || context.ManualInputActive || ! actionGate.AutomationEnabled || actionGate.IsLootSuspended) {
					if (actionGate.IsLootSuspended) LogSuspensionState(context.DropLog, context.ProcessId, "BEFORE_SCAN");
					if (! await DelayWorker(ScanIntervalMilliseconds, token).ConfigureAwait(false)) break;
					continue;
				}
				LogSuspensionCleared(context.DropLog, context.ProcessId);

				GameSnapshot snapshot = GameMemory.ReadSnapshot(context.ProcessId);
				if (! snapshot.Success) {
					LogErrorOnce(context.Log, context.ProcessId, snapshot.FailReason);
					if (! await DelayWorker(ScanIntervalMilliseconds, token).ConfigureAwait(false)) break;
					continue;
				}
				if (! startedLogged) {
					context.Log?.Invoke($"Auto Nhặt AutoFS bắt đầu | PID={context.ProcessId} | Worker=DEDICATED | Synchronization=SHARED_MONITOR | Scan={ScanIntervalMilliseconds}ms | Commands={AutoFsSelectGroundItemCommand}/NearItemIndex,{AutoFsApproachCommand}/0,{AutoFsPickupCommand}/ItemIndex | NativeFlow=GROUND_COORDINATE_CONVERTER_MOVE_PICKUP | TablePointer=Game.exe+0x{GameAddresses.Item.GroundRecordTablePointer:X} | Stride=0x{GameAddresses.Item.GroundRecordStride:X}");
					startedLogged = true;
				}
				LootFindResult result = finder.Find(snapshot);
				if (! result.Success) {
					LogErrorOnce(context.Log, context.ProcessId, result.FailReason);
					if (! await DelayWorker(ScanIntervalMilliseconds, token).ConfigureAwait(false)) break;
					continue;
				}

				lastError = "";
				LogScanRejections(result, context.DropLog);
				RemoveExpiredFailedCoordinates(DateTime.UtcNow, context.DropLog, context.ProcessId);
				if (actionGate.IsLootSuspended) {
					LogSuspensionState(context.DropLog, context.ProcessId, "AFTER_SCAN");
					if (! await DelayWorker(ScanIntervalMilliseconds, token).ConfigureAwait(false)) break;
					continue;
				}
				SelectNearbyGroundItems(result.Candidates, snapshot, context.GameWindow, context.Log, context.DropLog);
				LootSnapshot[] candidates = result.Candidates
					.OrderBy(item => GetScanDistance(snapshot, item))
					.ThenBy(item => item.Index)
					.ToArray();
				if (candidates.Length > 0 && ! token.IsCancellationRequested) {
					try {
						actionGate.RunLoot(() => ProcessCandidateBatch(context, candidates, token));
					} catch (Exception ex) {
						LogErrorOnce(context.Log, context.ProcessId, $"{ex.GetType().Name}: {ex.Message}");
						ClearPending();
					}
				}

				if (! await DelayWorker(ScanIntervalMilliseconds, token).ConfigureAwait(false)) break;
			}
		} finally {
			ClearPending();
			lock (syncRoot) {
				if (workerCancellation != null && workerCancellation.Token == token) {
					workerCancellation.Dispose();
					workerCancellation = null;
					worker = null;
					failedCoordinates.Clear();
				}
			}
		}
	}

	private void ProcessCandidateBatch(WorkerContext context, IReadOnlyList<LootSnapshot> candidates, CancellationToken token) {
		LogLootDiagnostic(context.DropLog, $"LOOT_MONITOR_ENTER | PID={context.ProcessId} | CandidateCount={candidates.Count} | Scope=FULL_SCANNED_LIST");
		string exitReason = "COMPLETED";
		try {
			foreach (LootSnapshot candidate in candidates) {
				if (token.IsCancellationRequested) {
					exitReason = "CANCELLED";
					break;
				}
				if (! settings.Enabled) {
					exitReason = "LOOT_DISABLED";
					break;
				}
				if (actionGate.IsLootSuspended) {
					exitReason = $"SUSPENDED_{actionGate.GetLootSuspensionState()}";
					break;
				}
				if (IsManualInputActive()) {
					exitReason = "MANUAL_INPUT_PRIORITY";
					break;
				}
				ProcessCandidate(context, candidate, token);
			}
		} finally {
			LogLootDiagnostic(context.DropLog, $"LOOT_MONITOR_EXIT | PID={context.ProcessId} | CandidateCount={candidates.Count} | Scope=FULL_SCANNED_LIST | Reason={exitReason}");
		}
	}

	private void ProcessCandidate(WorkerContext context, LootSnapshot candidate, CancellationToken token) {
		if (IsCoordinateCoolingDown((candidate.RawX, candidate.RawY))) {
			LogLootDiagnostic(context.DropLog, $"LOOT_CANDIDATE_SKIPPED | PID={context.ProcessId} | Index={candidate.Index} | Name={candidate.ItemNameRaw} | Raw={candidate.RawX}/{candidate.RawY} | Reason=COORDINATE_COOLDOWN_ACTIVE");
			return;
		}
		using MemoryReader reader = new(context.ProcessId);
		IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
		if (moduleBase == IntPtr.Zero) {
			LogErrorOnce(context.Log, context.ProcessId, "Không tìm thấy Game.exe.");
			LogLootDiagnostic(context.DropLog, $"LOOT_CANDIDATE_REJECTED | PID={context.ProcessId} | Index={candidate.Index} | Name={candidate.ItemNameRaw} | Reason=MODULE_NOT_FOUND");
			return;
		}

		IntPtr groundTablePointer = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Item.GroundRecordTablePointer));
		if (groundTablePointer == IntPtr.Zero) {
			LogLootDiagnostic(context.DropLog, $"LOOT_CANDIDATE_REJECTED | PID={context.ProcessId} | Index={candidate.Index} | Name={candidate.ItemNameRaw} | Reason=GROUND_TABLE_POINTER_ZERO");
			return;
		}
		long groundTable = groundTablePointer.ToInt64();
		int itemIndex = GetRecordIndex(candidate, groundTable);
		if (itemIndex < FirstGroundItemIndex || itemIndex > LastGroundItemIndex) {
			LogLootDiagnostic(context.DropLog, $"LOOT_CANDIDATE_REJECTED | PID={context.ProcessId} | Index={candidate.Index} | Name={candidate.ItemNameRaw} | Record=0x{candidate.Address.ToInt64():X8} | Reason=GROUND_INDEX_OUT_OF_RANGE");
			return;
		}
		(int X, int Y) coordinate = (candidate.RawX, candidate.RawY);
		SetPending(candidate, itemIndex, coordinate);
		ItemClassification classification = ItemGroupClassifier.Classify(candidate);
		LogLootDiagnostic(context.DropLog, $"LOOT_CANDIDATE_START | PID={context.ProcessId} | Index={itemIndex} | Name={candidate.ItemNameRaw} | NativeFlow=GROUND_COORDINATE_CONVERTER_MOVE_PICKUP | Record=0x{candidate.Address.ToInt64():X8} | Group={classification.Group} | AutoFsCategory={AutoFsSpecialItemClassifier.Classify(candidate.ItemNameRaw)} | Color={classification.Color} | AttributeClass={classification.AttributeClass} | QualityA={candidate.QualityCodeA} | QualityB={candidate.QualityCodeB} | Raw={coordinate.X}/{coordinate.Y} | Internal={candidate.InternalX}/{candidate.InternalY}");
		string outcome = "LOOP_EXITED";
		bool retryPolicyLogged = false;

		try {
			while (! token.IsCancellationRequested && settings.Enabled && ! IsManualInputActive()) {
				GroundSlotReading slot = ReadGroundSlot(reader, moduleBase, groundTable, itemIndex, candidate);
				LogLootDiagnostic(context.DropLog, $"LOOT_SLOT_RECHECK | PID={context.ProcessId} | Index={itemIndex} | Name={candidate.ItemNameRaw} | AttemptNext={pendingPickupAttempts + 1} | ExpectedGroundId={candidate.GroundId} | CurrentGroundId={slot.GroundId} | ExpectedRaw={candidate.RawX}/{candidate.RawY} | CurrentRaw={slot.RawX}/{slot.RawY} | Matches={slot.Matches} | Reason={slot.Reason}");
				if (! slot.Matches) {
					LogLootDiagnostic(context.DropLog, $"LOOT_SLOT_LEFT | PID={context.ProcessId} | Index={itemIndex} | Name={candidate.ItemNameRaw} | Attempts={pendingPickupAttempts} | Reason={slot.Reason}");
					outcome = $"SLOT_LEFT_{slot.Reason}";
					break;
				}
				GameSnapshot snapshot = GameMemory.ReadSnapshot(context.ProcessId);
				if (! snapshot.Success) {
					outcome = "PLAYER_SNAPSHOT_FAILED";
					break;
				}
				int rawDistance = GetRawDistance(snapshot, candidate);
				if (rawDistance >= NearRawDistance && ! approachPrepared) {
					if (! transport.TrySendCommand(context.GameWindow, AutoFsApproachCommand, 0, out string prepareError)) {
						LogLootDiagnostic(context.DropLog, $"LOOT_COMMAND_FAILED | PID={context.ProcessId} | Command={AutoFsApproachCommand} | Payload=0 | Index={itemIndex} | Name={candidate.ItemNameRaw} | Reason={prepareError}");
						outcome = "COMMAND_32_POST_FAILED";
						break;
					}
					approachPrepared = true;
					LogLootDiagnostic(context.DropLog, $"LOOT_COMMAND_POSTED | PID={context.ProcessId} | Command={AutoFsApproachCommand} | Payload=0 | Index={itemIndex} | Name={candidate.ItemNameRaw} | Phase=PREPARE_DISTANT_PICKUP | PlayerRaw={snapshot.X}/{snapshot.Y} | ItemRaw={coordinate.X}/{coordinate.Y} | Distance={rawDistance}");
				}

				if (! transport.TrySendCommand(context.GameWindow, AutoFsPickupCommand, itemIndex, out string pickupError)) {
					LogErrorOnce(context.Log, context.ProcessId, pickupError);
					LogLootDiagnostic(context.DropLog, $"LOOT_COMMAND_FAILED | PID={context.ProcessId} | Command={AutoFsPickupCommand} | Payload={itemIndex} | Index={itemIndex} | Name={candidate.ItemNameRaw} | Reason={pickupError}");
					outcome = "COMMAND_78_POST_FAILED";
					break;
				}
				pendingPickupAttempts++;
				if (rawDistance < NearRawDistance) pendingNearPickupAttempts++;
				LogLootDiagnostic(context.DropLog, $"LOOT_COMMAND_POSTED | PID={context.ProcessId} | Command={AutoFsPickupCommand} | Payload={itemIndex} | Index={itemIndex} | Name={candidate.ItemNameRaw} | Attempt={pendingPickupAttempts} | NearAttempt={pendingNearPickupAttempts} | Delivery=POSTED | Acceptance=UNVERIFIED | Synchronization=SHARED_MONITOR | NativeFlow=GROUND_COORDINATE_CONVERTER_MOVE_PICKUP | PlayerRaw={snapshot.X}/{snapshot.Y} | ItemRaw={coordinate.X}/{coordinate.Y} | Distance={rawDistance}");
				if (token.WaitHandle.WaitOne(RetryIntervalMilliseconds)) break;
				bool persistentRetry = IsPersistentNearRetry(candidate);
				if (rawDistance < NearRawDistance && pendingNearPickupAttempts >= NearPickupAttemptLimit && persistentRetry && ! retryPolicyLogged) {
					retryPolicyLogged = true;
					LogLootDiagnostic(context.DropLog, $"LOOT_RETRY_POLICY | PID={context.ProcessId} | Index={itemIndex} | Name={candidate.ItemNameRaw} | Persistent=True | Reason=VALUABLE_ITEM_EXCEPTION");
				}
				if (rawDistance < NearRawDistance && pendingNearPickupAttempts >= NearPickupAttemptLimit && ! persistentRetry) {
					AddFailedCoordinate(coordinate, DateTime.UtcNow.AddMilliseconds(FailedCoordinateCooldownMilliseconds));
					outcome = "NEAR_RETRY_LIMIT_COOLDOWN";
					break;
				}
			}
		} finally {
			if (token.IsCancellationRequested) outcome = "CANCELLED";
			else if (! settings.Enabled) outcome = "LOOT_DISABLED";
			else if (IsManualInputActive()) outcome = "MANUAL_INPUT_PRIORITY";
			LogLootDiagnostic(context.DropLog, $"LOOT_CANDIDATE_END | PID={context.ProcessId} | Index={itemIndex} | Name={candidate.ItemNameRaw} | Outcome={outcome} | Attempts={pendingPickupAttempts} | NearAttempts={pendingNearPickupAttempts} | Prepared={approachPrepared} | NativeFlow=GROUND_COORDINATE_CONVERTER_MOVE_PICKUP | Raw={coordinate.X}/{coordinate.Y}");
			ClearPending();
		}
	}

	private WorkerContext GetWorkerContext() {
		lock (syncRoot) return new(processId, gameWindow, manualInputActive, log, dropLog);
	}

	private bool IsManualInputActive() {
		lock (syncRoot) return manualInputActive;
	}

	private void SetPending(LootSnapshot candidate, int itemIndex, (int X, int Y) coordinate) {
		lock (syncRoot) {
			pendingCandidate = candidate;
			pendingItemIndex = itemIndex;
			pendingCoordinate = coordinate;
			pendingPickupAttempts = 0;
			pendingNearPickupAttempts = 0;
			approachPrepared = false;
		}
	}

	private void ClearPending() {
		lock (syncRoot) {
			pendingCandidate = null;
			pendingItemIndex = 0;
			pendingCoordinate = default;
			pendingPickupAttempts = 0;
			pendingNearPickupAttempts = 0;
			approachPrepared = false;
		}
	}

	private static async Task<bool> DelayWorker(int milliseconds, CancellationToken token) {
		try {
			await Task.Delay(milliseconds, token).ConfigureAwait(false);
			return true;
		} catch (OperationCanceledException) {
			return false;
		}
	}

	private static int GetRecordIndex(LootSnapshot item, long groundTable) {
		long delta = item.Address.ToInt64() - groundTable;
		if (delta < 0 || delta % GameAddresses.Item.GroundRecordStride != 0 || delta / GameAddresses.Item.GroundRecordStride > int.MaxValue) return -1;
		return (int)(delta / GameAddresses.Item.GroundRecordStride);
	}

	private void SelectNearbyGroundItems(IEnumerable<LootSnapshot> candidates, GameSnapshot snapshot, IntPtr currentGameWindow, Action<string>? currentLog, Action<string>? currentDropLog) {
		foreach (LootSnapshot candidate in candidates) {
			if (GetScanDistance(snapshot, candidate) >= NearRawDistance) continue;
			if (! transport.TrySendCommand(currentGameWindow, AutoFsSelectGroundItemCommand, candidate.Index, out string error)) {
				LogErrorOnce(currentLog, snapshot.ProcessId, error);
				LogLootDiagnostic(currentDropLog, $"LOOT_COMMAND_FAILED | PID={snapshot.ProcessId} | Command={AutoFsSelectGroundItemCommand} | Payload={candidate.Index} | Index={candidate.Index} | Name={candidate.ItemNameRaw} | Phase=SCAN_NEAR | Reason={error}");
				continue;
			}
			LogLootDiagnostic(currentDropLog, $"LOOT_COMMAND_POSTED | PID={snapshot.ProcessId} | Command={AutoFsSelectGroundItemCommand} | Payload={candidate.Index} | Index={candidate.Index} | Name={candidate.ItemNameRaw} | Phase=SCAN_NEAR | PlayerRaw={snapshot.X}/{snapshot.Y} | ItemRaw={candidate.RawX}/{candidate.RawY} | Distance={GetScanDistance(snapshot, candidate):F0}");
		}
	}

	// Xác nhận ground slot vẫn chứa đúng item trước khi gửi command 78
	private static GroundSlotReading ReadGroundSlot(MemoryReader reader, IntPtr moduleBase, long groundTable, int itemIndex, LootSnapshot expected) {
		if (itemIndex < FirstGroundItemIndex || itemIndex > LastGroundItemIndex) return new(false, 0, 0, 0, "INDEX_OUT_OF_RANGE");
		IntPtr record = new(groundTable + (long)itemIndex * GameAddresses.Item.GroundRecordStride);
		byte[] recordBytes = reader.ReadBytes(record, GameAddresses.Item.GroundRecordStride);
		if (recordBytes.Length != GameAddresses.Item.GroundRecordStride) return new(false, 0, 0, 0, "RECORD_READ_FAILED");
		int groundId    = BitConverter.ToInt32(recordBytes, GameAddresses.Item.GroundRecordId);
		int groundState = BitConverter.ToInt32(recordBytes, GameAddresses.Item.GroundRecordKind);
		if (groundId <= 0) return new(false, groundId, 0, 0, "GROUND_ID_ZERO");
		if (groundState != 3) return new(false, groundId, 0, 0, $"GROUND_STATE_{groundState}");
		if (groundId != expected.GroundId) return new(false, groundId, 0, 0, "GROUND_ID_CHANGED");
		byte[] currentNameBytes = ReadGroundName(recordBytes);
		if (! currentNameBytes.AsSpan().SequenceEqual(expected.ItemNameBytes)) return new(false, groundId, 0, 0, "GROUND_NAME_CHANGED");
		IntPtr mapCoordinateRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.MapCoordinateRoot));
		Dictionary<(int MapObjectIndex, int SegmentIndex), (int BaseX, int BaseY)> segmentCoordinates = new();
		if (mapCoordinateRoot == IntPtr.Zero) return new(false, groundId, 0, 0, "MAP_COORDINATE_ROOT_ZERO");
		if (! AutoFsGroundItemScanner.TryReadRawCoordinate(reader, recordBytes, 0, mapCoordinateRoot, segmentCoordinates, out int rawX, out int rawY, out string failure)) return new(false, groundId, 0, 0, "RAW_COORDINATE_READ_FAILED_" + failure);
		bool matches = rawX == expected.RawX && rawY == expected.RawY;
		return new(matches, groundId, rawX, rawY, matches ? "MATCH" : "RAW_COORDINATE_CHANGED");
	}

	// Đọc tên item trực tiếp từ ground record hiện hành
	private static byte[] ReadGroundName(byte[] recordBytes) {
		int offset = GameAddresses.Item.GroundName;
		int length = 0;
		while (length < 64 && offset + length < recordBytes.Length && recordBytes[offset + length] != 0) length++;
		return length == 0 ? Array.Empty<byte>() : recordBytes.AsSpan(offset, length).ToArray();
	}

	private void LogSuspensionState(Action<string>? currentDropLog, int currentProcessId, string phase) {
		if (! settings.LogCandidates || currentDropLog == null) return;
		string owners = actionGate.GetLootSuspensionState();
		string state = $"{phase}/{owners}";
		if (string.Equals(lastSuspensionState, state, StringComparison.Ordinal)) return;
		lastSuspensionState = state;
		currentDropLog($"LOOT_SCAN_SUSPENDED | PID={currentProcessId} | Owners={owners} | Phase={phase}");
	}

	private void LogSuspensionCleared(Action<string>? currentDropLog, int currentProcessId) {
		if (lastSuspensionState.Length == 0) return;
		lastSuspensionState = "";
		LogLootDiagnostic(currentDropLog, $"LOOT_SCAN_RESUMED | PID={currentProcessId} | Owners=NONE | Phase=BEFORE_SCAN");
	}

	private void LogCandidateOrder(IReadOnlyList<LootSnapshot> candidates, Action<string>? currentDropLog, int currentProcessId) {
		if (! settings.LogCandidates || currentDropLog == null) return;
		string summary = string.Join(",", candidates.Select(item => $"{item.Index}:{item.MemoryFingerprint:X8}:{item.DistanceToPlayer:F0}"));
		if (string.Equals(lastCandidateOrderSummary, summary, StringComparison.Ordinal)) return;
		lastCandidateOrderSummary = summary;
		currentDropLog($"LOOT_CANDIDATE_ORDER | PID={currentProcessId} | Count={candidates.Count} | Format=Index:Fingerprint:EuclideanDistance | Ordering=NEAREST_FIRST | Items=[{summary}]");
	}

	private void LogFilterDecisions(LootFindResult result, Action<string>? currentDropLog) {
		if (! settings.LogCandidates || currentDropLog == null) return;
		foreach (LootSnapshot item in result.ObservedItems) {
			lock (syncRoot) {
				if (! loggedFilterDecisions.Add(item.MemoryFingerprint)) continue;
			}
			LootFilterDecision decision = Finder.EvaluateFilter(item, settings);
			currentDropLog($"LOOT_FILTER | PID={item.ProcessId} | Index={item.Index} | Name={item.ItemNameRaw} | Record=0x{item.Address.ToInt64():X8} | GroundType={item.GroundType} | GroundKind={item.GroundKind} | Group={decision.Classification.Group} | AutoFsCategory={AutoFsSpecialItemClassifier.Classify(item.ItemNameRaw)} | Color={decision.Classification.Color} | AttributeClass={decision.Classification.AttributeClass} | QualityA={item.QualityCodeA} | QualityB={item.QualityCodeB} | Accepted={decision.Accepted} | Reason={decision.Reason} | Raw={item.RawX}/{item.RawY} | Internal={item.InternalX}/{item.InternalY} | EuclideanDistance={item.DistanceToPlayer:F0}");
		}
	}

	private void LogScanSummary(LootFindResult result, Action<string>? currentDropLog) {
		if (! settings.LogCandidates || currentDropLog == null) return;
		string summary = result.ToSummary();
		if (string.Equals(lastScanSummary, summary, StringComparison.Ordinal)) return;
		lastScanSummary = summary;
		currentDropLog($"LOOT_SCAN | PID={result.ProcessId} | {summary}");
	}

	private void LogScanRejections(LootFindResult result, Action<string>? currentDropLog) {
		if (! settings.LogCandidates || currentDropLog == null) return;
		string summary = string.Join(" || ", result.SpriteRejectedDetails);
		if (string.Equals(lastScanRejectionSummary, summary, StringComparison.Ordinal)) return;
		bool cleared = summary.Length == 0 && lastScanRejectionSummary.Length > 0;
		lastScanRejectionSummary = summary;
		if (cleared) currentDropLog($"LOOT_SCAN_REJECTIONS_CLEARED | PID={result.ProcessId}");
		foreach (string rejection in result.SpriteRejectedDetails) currentDropLog($"LOOT_SCAN_REJECTED | PID={result.ProcessId} | {rejection}");
	}

	// Chỉ ghi sự kiện lỗi, từ chối, bỏ qua hoặc retry bất thường; không ghi vòng scan thành công.
	private void LogLootDiagnostic(Action<string>? currentDropLog, string message) {
		if (!settings.LogCandidates || currentDropLog == null) return;
		if (!message.Contains("FAIL", StringComparison.OrdinalIgnoreCase) &&
			!message.Contains("REJECTED", StringComparison.OrdinalIgnoreCase) &&
			!message.Contains("SKIPPED", StringComparison.OrdinalIgnoreCase) &&
			!message.Contains("SLOT_LEFT", StringComparison.OrdinalIgnoreCase) &&
			!message.Contains("RETRY", StringComparison.OrdinalIgnoreCase) &&
			!message.Contains("COOLDOWN", StringComparison.OrdinalIgnoreCase)) return;
		currentDropLog(message);
	}

	private static int GetRawDistance(GameSnapshot snapshot, LootSnapshot item) {
		long deltaX = (long)snapshot.X - item.RawX;
		long deltaY = (long)snapshot.Y - item.RawY;
		return (int)Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	private static double GetScanDistance(GameSnapshot snapshot, LootSnapshot item) {
		long deltaX = snapshot.X - item.RawX;
		long deltaY = snapshot.Y - item.RawY;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	private static bool IsPersistentNearRetry(LootSnapshot item) {
		ItemClassification classification = ItemGroupClassifier.Classify(item);
		if (classification.AttributeClass == 3) return true;
		if (classification.Color is ItemColor.Green or ItemColor.Yellow) return true;
		return AutoFsSpecialItemClassifier.Classify(item.ItemNameRaw) == AutoFsSpecialItemCategory.SkillBook;
	}

	private bool IsCoordinateCoolingDown((int X, int Y) coordinate) {
		lock (syncRoot) return failedCoordinates.ContainsKey(coordinate);
	}

	private void AddFailedCoordinate((int X, int Y) coordinate, DateTime untilUtc) {
		lock (syncRoot) failedCoordinates[coordinate] = untilUtc;
	}

	private void RemoveExpiredFailedCoordinates(DateTime now, Action<string>? currentDropLog, int currentProcessId) {
		(int X, int Y)[] expired;
		lock (syncRoot) {
			expired = failedCoordinates.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray();
			foreach ((int X, int Y) coordinate in expired) failedCoordinates.Remove(coordinate);
		}
		foreach ((int X, int Y) coordinate in expired) LogLootDiagnostic(currentDropLog, $"LOOT_COORDINATE_COOLDOWN_EXPIRED | PID={currentProcessId} | Raw={coordinate.X}/{coordinate.Y}");
	}

	private void LogErrorOnce(Action<string>? currentLog, int currentProcessId, string error) {
		if (string.Equals(lastError, error, StringComparison.Ordinal)) return;
		lastError = error;
		currentLog?.Invoke($"Auto Nhặt AutoFS FAIL | PID={currentProcessId} | {error}");
	}

	private readonly record struct WorkerContext(int ProcessId, IntPtr GameWindow, bool ManualInputActive, Action<string>? Log, Action<string>? DropLog);
	private readonly record struct GroundSlotReading(bool Matches, int GroundId, int RawX, int RawY, string Reason);
}
