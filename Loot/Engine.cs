namespace Auto.Loot;

using Auto.Attack;
using Auto.Movement;
using Auto.Runtime;
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
	// Khoá = MemoryFingerprint của món, giá trị = phán quyết lần ghi gần nhất. Xem LogFilterDecisions.
	private readonly Dictionary<int, string> loggedFilterDecisions = new();
	private readonly Dictionary<(int X, int Y), DateTime> failedCoordinates = new();
	private CancellationTokenSource? workerCancellation;
	private Task? worker;
	private int processId;
	private IntPtr gameWindow;
	private GameWindow? game;
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
			game = null;
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
		finder.InvalidatePotionCounts();
		cancellation?.Cancel();
	}

	public bool Tick(GameSnapshot snapshot, GameWindow game, bool manualInputActive, Action<string>? log = null, Action<string>? dropLog = null) {
		if (! settings.Enabled || ! snapshot.Success || snapshot.ProcessId <= 0 || game.Handle == IntPtr.Zero) {
			Stop();
			return false;
		}

		lock (syncRoot) {
			processId = snapshot.ProcessId;
			gameWindow = game.Handle;
			this.game = game;
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
				LogScanSummary(result, context.DropLog);
				LogScanRejections(result, context.DropLog);
				// LogFilterDecisions viết ra từ lâu nhưng KHÔNG NƠI NÀO GỌI, nên dòng LOOT_FILTER — dòng duy nhất ghi
				// lại vì sao một món được nhận hay bị loại — chưa bao giờ xuất hiện trong log. Hậu quả đã gặp thật
				// 2026-09-11: một account nhặt nhầm đồ trắng 'Vũ Khúc Chiến Ngoa' mà grep cả Diagnostics lẫn
				// DiagnosticsBeta ra 0 dòng, không truy được do tick màu, do ô 'Vật phẩm' khớp chuỗi con, hay do
				// byte màu rơi vào nhánh 'Đồ Khác'.
				// Không đi qua LogLootDiagnostic: bộ lọc ở đó chỉ giữ dòng có FAIL/REJECTED/SKIPPED/SLOT_LEFT/RETRY/
				// COOLDOWN nên sẽ vứt luôn dòng này. Số dòng vẫn có trần vì loggedFilterDecisions lọc trùng theo
				// MemoryFingerprint, mỗi item chỉ ghi một lần.
				LogFilterDecisions(result, context.DropLog);
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
			finder.InvalidatePotionCounts();
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
				// Kiểm lại GIỮA CHỪNG batch, không chỉ một lần lúc Find() dựng danh sách — một batch có thể gom nhiều
				// món, nhặt hết cả loạt rồi mới quay lại Find() để kiểm tiếp là đã trễ (chủ dự án chỉ ra 2026-09-24).
				if (finder.IsCarryingCapacityExhausted(context.ProcessId)) {
					exitReason = "CARRYING_CAPACITY_EXHAUSTED";
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
		// Chụp TOÀN BỘ túi TRƯỚC khi gửi lệnh nhặt. Không có nó thì không tài nào phân biệt "mình nhặt được" với
		// "người khác nhặt mất": cả hai đều kết thúc bằng SLOT_LEFT_GROUND_ID_ZERO y hệt nhau.
		//
		// Trước đây chỉ đếm ĐÚNG MỘT tên (tên món đang chờ). Đó là chỗ hở: nếu client nhặt trúng món KHÁC thì số
		// lượng của tên đang chờ không tăng, và cả hai nhánh ghi log bên dưới đều không nhận — không một dòng nào
		// được ghi. Đo được ngày 2026-09-17: PID 43596 có "Đấu Trận Chiến Ngoa" (đồ trắng) nằm trong túi, trong khi
		// loot-scan.log ghi 29/29 lần lọc đều Accepted=False và không có dòng nhặt nào ở bất kỳ file log nào.
		// Chụp cả bảng thì lấy hiệu hai lần chụp là ra tên món THẬT SỰ vào túi.
		string candidateKey = Finder.ToInventoryKey(candidate.ItemNameRaw);
		bool inventoryCountReadable = finder.TrySnapshotInventory(context.ProcessId, out Dictionary<string, int> inventoryBefore);
		int inventoryCountBefore = inventoryCountReadable && inventoryBefore.TryGetValue(candidateKey, out int beforeCount) ? beforeCount : 0;
		// Lý do ô đất ngừng khớp, giữ lại để dòng log cuối chỉ được ra nguồn: GROUND_ID_ZERO là món biến mất,
		// còn các lý do khác nghĩa là ô đã bị món khác chiếm — đúng kịch bản đua tranh nghi ngờ ở SelectNearbyGroundItems.
		string lastSlotReason = "KHÔNG_CÓ";
		string lastSlotCurrentName = "";

		try {
			while (! token.IsCancellationRequested && settings.Enabled && ! IsManualInputActive()) {
				GroundSlotReading slot = ReadGroundSlot(reader, moduleBase, groundTable, itemIndex, candidate);
				LogLootDiagnostic(context.DropLog, $"LOOT_SLOT_RECHECK | PID={context.ProcessId} | Index={itemIndex} | Name={candidate.ItemNameRaw} | AttemptNext={pendingPickupAttempts + 1} | ExpectedGroundId={candidate.GroundId} | CurrentGroundId={slot.GroundId} | ExpectedRaw={candidate.RawX}/{candidate.RawY} | CurrentRaw={slot.RawX}/{slot.RawY} | Matches={slot.Matches} | Reason={slot.Reason}");
				if (! slot.Matches) {
					lastSlotReason = slot.Reason;
					lastSlotCurrentName = slot.CurrentName;
					LogLootDiagnostic(context.DropLog, $"LOOT_SLOT_LEFT | PID={context.ProcessId} | Index={itemIndex} | Name={candidate.ItemNameRaw} | TênỞÔĐất={slot.CurrentName} | Attempts={pendingPickupAttempts} | Reason={slot.Reason}");
					outcome = $"SLOT_LEFT_{slot.Reason}";
					break;
				}
				GameSnapshot snapshot = GameMemory.ReadSnapshot(context.ProcessId);
				if (! snapshot.Success) {
					outcome = "PLAYER_SNAPSHOT_FAILED";
					break;
				}
				int rawDistance = GetRawDistance(snapshot, candidate);
				// Chốt chặn sống: item là vật tĩnh nên nhân vật đi tới thì khoảng cách phải GIẢM. Vượt bán kính quét
				// nghĩa là đang bị kéo đi sai chỗ, phải bỏ ngay. Bộ lọc trong AutoFsGroundItemScanner chỉ chặn lúc
				// quét, còn chỗ này chặn đúng lúc lệnh 78 đang được gửi lại mỗi RetryIntervalMilliseconds.
				// Cần thiết vì pendingNearPickupAttempts bên dưới chỉ tăng khi rawDistance < NearRawDistance, nên
				// giới hạn NearPickupAttemptLimit không bao giờ áp được cho item ở xa: vòng lặp sẽ chạy vô hạn.
				if (rawDistance > Finder.PlayerScanRadius) {
					LogLootDiagnostic(context.DropLog, $"LOOT_OUT_OF_SCAN_RADIUS | PID={context.ProcessId} | Index={itemIndex} | Name={candidate.ItemNameRaw} | Distance={rawDistance} | ScanRadius={Finder.PlayerScanRadius} | PlayerRaw={snapshot.X}/{snapshot.Y} | ItemRaw={coordinate.X}/{coordinate.Y}");
					outcome = "OUT_OF_SCAN_RADIUS";
					break;
				}
				// Nhánh dưới chỉ chạy khi rawDistance >= NearRawDistance (200), mà chốt chặn ngay trên đã cắt ở
				// PlayerScanRadius (150) nên nó KHÔNG còn với tới được. Giữ nguyên vì đây là lệnh 32 thuộc luồng
				// AutoFS đã xác nhận; xoá là đổi hành vi luồng gốc. Nếu sau này nới PlayerScanRadius vượt 200 thì
				// nhánh này sống lại đúng như thiết kế cũ.
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
			// Item rời khỏi ô dưới đất nghĩa là số lượng trong túi có thể đã đổi: xoá cache để lượt lọc sau đọc lại.
			// Cố ý không cộng thẳng vào cache: SLOT_LEFT cũng xảy ra khi người khác nhặt mất, cộng thẳng sẽ đếm thừa.
			if (outcome.StartsWith("SLOT_LEFT", StringComparison.Ordinal)) finder.InvalidatePotionCounts();
			if (token.IsCancellationRequested) outcome = "CANCELLED";
			else if (! settings.Enabled) outcome = "LOOT_DISABLED";
			else if (IsManualInputActive()) outcome = "MANUAL_INPUT_PRIORITY";
			// Món rời ô đất: đếm lại túi để biết nó vào túi AI. Tăng thì là mình, không tăng thì người khác nhặt mất.
			// Chủ dự án chốt 2026-09-14: chỉ account thật sự nhặt được mới được ghi dòng nhặt, nên nhánh
			// TAKEN_BY_OTHER cố ý im lặng — đó chính là 645 dòng SLOT_LEFT_GROUND_ID_ZERO gây hiểu nhầm "nhân vật
			// B, C nhặt món của A" trong log ngày 14/09.
			if (outcome.StartsWith("SLOT_LEFT", StringComparison.Ordinal)) {
				bool afterReadable = finder.TrySnapshotInventory(context.ProcessId, out Dictionary<string, int> inventoryAfter);
				int inventoryCountAfter = afterReadable && inventoryAfter.TryGetValue(candidateKey, out int afterCount) ? afterCount : 0;
				string inventoryEvidence = inventoryCountReadable && afterReadable
					? $"TúiTrước={inventoryCountBefore} | TúiSau={inventoryCountAfter}"
					: $"TúiTrước={(inventoryCountReadable ? inventoryCountBefore.ToString() : "không đọc được")} | TúiSau={(afterReadable ? inventoryCountAfter.ToString() : "không đọc được")}";
				if (inventoryCountReadable && afterReadable && inventoryCountAfter > inventoryCountBefore) {
					// Dòng DUY NHẤT khẳng định một món đã vào túi account này. Tên lấy từ ô đất lúc lọc, còn số lượng
					// lấy từ túi, nên nếu client nhặt nhầm món khác thì TúiSau của tên này sẽ KHÔNG tăng.
					//
					// Nhóm tiêu hao/nguyên liệu nhặt liên tục thì đổi sang nhãn LOOT_ROUTINE_PICKUP để rơi sang
					// loot-scan.log, giữ loot-drops.log chỉ còn món đáng chú ý (chủ dự án chốt 2026-09-15).
					// Đo trên chính loot-drops.log 2 tiếng ngày 14/09: 81/94 dòng là dược phẩm, 12/94 là Tứ Tượng.
					// KHÔNG xoá hẳn — vẫn cần để truy khi nghi nhặt sai hoặc kiểm giới hạn số lượng dược phẩm.
					AutoFsSpecialItemCategory category = AutoFsSpecialItemClassifier.Classify(candidate.ItemNameRaw);
					bool routinePickup = IsRoutinePickup(classification, category);
					string marker = routinePickup ? "LOOT_ROUTINE_PICKUP" : "LOOT_PICKED_UP";
					// Đẩy lên ô theo dõi trên giao diện ĐÚNG những lượt vào loot-drops.log, tức bỏ nhóm nhặt thường xuyên.
					// Đồ Trắng/Đồ Xanh nhặt liên tục sau khi bật cả hai màu (chủ dự án chốt) nên bị bỏ khỏi ô hiển thị
					// UI riêng — "Vũ khí xanh" nằm ở ItemColor.Green (Finder.cs:141), không phải Blue, nên không bị lọc.
					bool suppressUiFeed = classification.Color is ItemColor.White or ItemColor.Blue;
					if (! routinePickup && ! suppressUiFeed) LootFeed.Add(context.ProcessId, candidate.ItemNameRaw);
					context.DropLog?.Invoke($"{marker} | PID={context.ProcessId} | Index={itemIndex} | Name={candidate.ItemNameRaw} | Group={classification.Group} | AutoFsCategory={category} | Color={classification.Color} | {inventoryEvidence} | Attempts={pendingPickupAttempts} | Raw={coordinate.X}/{coordinate.Y}");
				} else if (! inventoryCountReadable || ! afterReadable) {
					// Không đọc được túi thì KHÔNG được im lặng: im lặng ở đây sẽ giấu luôn cả lượt nhặt thật.
					LogLootDiagnostic(context.DropLog, $"LOOT_PICKUP_UNVERIFIED | PID={context.ProcessId} | Index={itemIndex} | Name={candidate.ItemNameRaw} | Outcome={outcome} | {inventoryEvidence} | Reason=INVENTORY_READ_FAILED");
				}

				// SO CẢ BẢNG TÚI Ở NGOÀI BA NHÁNH TRÊN, KHÔNG NẰM TRONG NHÁNH "MÓN CHỜ KHÔNG TĂNG" NỮA.
				//
				// Bản trước đặt phép so này trong nhánh else, tức chỉ chạy khi món đang chờ KHÔNG vào túi. Nên ca
				// "client nhặt đúng món chờ VÀ kèm thêm một món khác" lọt hoàn toàn: nhánh đầu ghi LOOT_ROUTINE_PICKUP
				// rồi thoát, không xét gì thêm.
				// Bằng chứng (chủ dự án báo 2026-09-17, tôi đọc bộ nhớ PID 36320 xác nhận): "Tham Lang Hộ Giáp" nằm ở
				// ô túi chính [3] id=9, trong khi cả 36 dòng "Hộ Giáp" ở loot-scan.log đều là
				// Accepted=False | Reason=COLOR_AND_EXACT_ITEM_DISABLED — Auto chưa bao giờ nhắm tới nó — và
				// LOOT_PICKED_WRONG_ITEM không nổ lấy một lần trong toàn bộ log.
				//
				// Vì sao ca này có thật: bảng đất 127 ô bị tái sử dụng rất nhanh, mà lệnh 78 nhận CHỈ SỐ Ô chứ không
				// nhận mã món. Riêng ô 14 của PID 36320 trong loot-scan.log: 16:30:22 "Tham Lang Yêu Đái" ->
				// 16:31:33 "Tham Lang Hộ Giáp" -> 16:32:46 "Đấu Trận Giáp" -> 16:37:35 "Tiểu Hoàn đơnx1" (được nhặt).
				//
				// Bỏ đúng tên đang chờ ra khỏi phép so, nên nhặt trúng món mình muốn KHÔNG sinh dòng này.
				if (inventoryCountReadable && afterReadable) {
					string unexpected = DescribeInventoryGain(inventoryBefore, inventoryAfter, candidateKey);
					if (unexpected.Length > 0) {
						// Ghi vào loot-drops.log chứ không phải loot-scan.log: nhặt sai món là lỗi cần thấy ngay.
						// Các trường ở đây chọn để chỉ thẳng ra NGUỒN, không phải chỉ để biết là có lỗi:
						//   MónChờCũngVào — True là nhặt kèm, False là nhặt trượt sang món khác. Hai kịch bản khác nhau.
						//   LýDoRờiÔ  — GROUND_ID_ZERO là món tự biến mất; lý do khác nghĩa là ô đất đã bị món khác
						//               chiếm giữa lúc lọc và lúc gửi lệnh, tức đúng kịch bản đua tranh nghi ngờ.
						//   ChỉSốÔĐất — để dò ngược LOOT_SLOT_RECHECK và LOOT_COMMAND_POSTED cùng chỉ số.
						//   SốLầnGửi78 — 0 nghĩa là chưa từng gửi lệnh nhặt trong lượt này, tức món vào túi bằng đường KHÁC.
						context.DropLog?.Invoke($"LOOT_PICKED_WRONG_ITEM | PID={context.ProcessId} | Index={itemIndex} | TênChờ={candidate.ItemNameRaw} | TênVàoTúi={unexpected} | MónChờCũngVào={inventoryCountAfter > inventoryCountBefore} | Group={classification.Group} | Color={classification.Color} | {inventoryEvidence} | LýDoRờiÔ={lastSlotReason} | TênỞÔĐấtLúcRời={lastSlotCurrentName} | ChỉSốÔĐất={itemIndex} | SốLầnGửi78={pendingPickupAttempts} | ĐãGửi32={approachPrepared} | Outcome={outcome} | Raw={coordinate.X}/{coordinate.Y}");
					}
				}
			} else {
				LogLootDiagnostic(context.DropLog, $"LOOT_CANDIDATE_END | PID={context.ProcessId} | Index={itemIndex} | Name={candidate.ItemNameRaw} | Outcome={outcome} | Attempts={pendingPickupAttempts} | NearAttempts={pendingNearPickupAttempts} | Prepared={approachPrepared} | NativeFlow=GROUND_COORDINATE_CONVERTER_MOVE_PICKUP | Raw={coordinate.X}/{coordinate.Y}");
			}
			ClearPending();
		}
	}

	private WorkerContext GetWorkerContext() {
		lock (syncRoot) return new(processId, gameWindow, game, manualInputActive, log, dropLog);
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

	// Liệt kê những tên CÓ THÊM giữa hai lần chụp túi, dạng "TÊN+n". Chuỗi rỗng nghĩa là túi không nhận thêm gì.
	//
	// Tên ở đây là khoá đã chuẩn hoá của InventoryPotionCounter (bỏ dấu, viết hoa) chứ không phải tên hiển thị —
	// đủ để nhận ra món và tra ngược vào log, mà không phải giữ thêm một bảng tên thứ hai.
	// excludeKey: tên đang được nhặt có chủ đích, bỏ ra để lượt nhặt đúng không bị báo là nhặt nhầm.
	private static string DescribeInventoryGain(Dictionary<string, int> before, Dictionary<string, int> after, string? excludeKey = null) {
		List<string> gains = [];
		foreach ((string name, int afterCount) in after) {
			if (excludeKey != null && string.Equals(name, excludeKey, StringComparison.Ordinal)) continue;
			before.TryGetValue(name, out int beforeCount);
			if (afterCount > beforeCount) gains.Add($"{name}+{afterCount - beforeCount}");
		}
		gains.Sort(StringComparer.Ordinal);
		return string.Join(",", gains);
	}

	private static int GetRecordIndex(LootSnapshot item, long groundTable) {
		long delta = item.Address.ToInt64() - groundTable;
		if (delta < 0 || delta % GameAddresses.Item.GroundRecordStride != 0 || delta / GameAddresses.Item.GroundRecordStride > int.MaxValue) return -1;
		return (int)(delta / GameAddresses.Item.GroundRecordStride);
	}

	// Kiểm lại ô đất TRƯỚC khi gửi lệnh 9, dùng chung đúng ReadGroundSlot mà đường lệnh 78 đang dùng.
	//
	// Vì sao thêm: đường lệnh 78 kiểm GroundId + trạng thái + nguyên byte tên + toạ độ mỗi vòng lặp, còn đường này
	// bắn thẳng candidate.Index lấy từ lượt quét trước, không đọc lại gì. Bảng đất 127 ô được tái sử dụng, nên nếu
	// món hợp lệ ở ô N biến mất và client thả món khác vào đúng ô N trong khoảng giữa lúc đọc bảng và lúc gửi lệnh
	// thì lệnh 9 trúng món mới. Vòng quét chạy mỗi 200ms trên nhiều account, cửa sổ race hẹp nhưng không bằng 0.
	//
	// GIẢ THUYẾT, CHƯA VERIFY: đây là nguyên nhân vụ một account nhặt nhầm đồ trắng 'Vũ Khúc Chiến Ngoa'
	// (2026-09-11, đúng một lần trong cả phiên). Dòng LOOT_SELECT_SKIPPED_SLOT_CHANGED bên dưới là thứ sẽ xác nhận hoặc bác
	// bỏ. Phép kiểm này đúng bất kể giả thuyết có đúng hay không: gửi lệnh vào ô không còn giữ món đã lọc là sai.
	private void SelectNearbyGroundItems(IEnumerable<LootSnapshot> candidates, GameSnapshot snapshot, IntPtr currentGameWindow, Action<string>? currentLog, Action<string>? currentDropLog) {
		using MemoryReader reader = new(snapshot.ProcessId);
		IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
		if (moduleBase == IntPtr.Zero) return;
		IntPtr groundTablePointer = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Item.GroundRecordTablePointer));
		if (groundTablePointer == IntPtr.Zero) return;
		long groundTable = groundTablePointer.ToInt64();
		foreach (LootSnapshot candidate in candidates) {
			if (GetScanDistance(snapshot, candidate) >= NearRawDistance) continue;
			int itemIndex = GetRecordIndex(candidate, groundTable);
			GroundSlotReading slot = ReadGroundSlot(reader, moduleBase, groundTable, itemIndex, candidate);
			if (! slot.Matches) {
				LogLootDiagnostic(currentDropLog, $"LOOT_SELECT_SKIPPED_SLOT_CHANGED | PID={snapshot.ProcessId} | Command={AutoFsSelectGroundItemCommand} | Index={itemIndex} | ScanIndex={candidate.Index} | Name={candidate.ItemNameRaw} | TênỞÔĐất={slot.CurrentName} | ExpectedGroundId={candidate.GroundId} | CurrentGroundId={slot.GroundId} | ExpectedRaw={candidate.RawX}/{candidate.RawY} | CurrentRaw={slot.RawX}/{slot.RawY} | Reason={slot.Reason}");
				continue;
			}
			// Gửi ĐÚNG số ô vừa được kiểm, không phải candidate.Index. Trước 2026-09-14 chỗ này kiểm ô itemIndex
			// (suy từ địa chỉ record) nhưng lại bắn candidate.Index (số thứ tự lúc quét) — hai giá trị khác nhau về
			// bản chất, chính dòng log cũ cũng in chúng thành hai trường Index và ScanIndex. Đường lệnh 78 bên dưới
			// luôn dùng itemIndex, nên chỉ đường này lệch.
			if (itemIndex != candidate.Index) {
				LogLootDiagnostic(currentDropLog, $"LOOT_SELECT_INDEX_MISMATCH | PID={snapshot.ProcessId} | Name={candidate.ItemNameRaw} | RecordIndex={itemIndex} | ScanIndex={candidate.Index} | Record=0x{candidate.Address.ToInt64():X8} | Action=GỬI_THEO_RecordIndex");
			}
			if (! transport.TrySendCommand(currentGameWindow, AutoFsSelectGroundItemCommand, itemIndex, out string error)) {
				LogErrorOnce(currentLog, snapshot.ProcessId, error);
				LogLootDiagnostic(currentDropLog, $"LOOT_COMMAND_FAILED | PID={snapshot.ProcessId} | Command={AutoFsSelectGroundItemCommand} | Payload={itemIndex} | Index={itemIndex} | ScanIndex={candidate.Index} | Name={candidate.ItemNameRaw} | Phase=SCAN_NEAR | Reason={error}");
				continue;
			}
			LogLootDiagnostic(currentDropLog, $"LOOT_COMMAND_POSTED | PID={snapshot.ProcessId} | Command={AutoFsSelectGroundItemCommand} | Payload={itemIndex} | Index={itemIndex} | ScanIndex={candidate.Index} | Name={candidate.ItemNameRaw} | Phase=SCAN_NEAR | PlayerRaw={snapshot.X}/{snapshot.Y} | ItemRaw={candidate.RawX}/{candidate.RawY} | Distance={GetScanDistance(snapshot, candidate):F0}");
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
		// Đọc tên NGAY, trước mọi nhánh thoát, để dòng log nào cũng nói được ô đất đang chứa món gì.
		// Nhưng CHỈ giải mã ở các nhánh hỏng — tức lúc sắp ghi log. Nhánh khớp là đường nóng, chạy mỗi vòng lặp trên
		// mọi account, giải mã ở đó là thêm một chuỗi rác mỗi lượt mà không ai đọc tới.
		byte[] currentNameBytes = ReadGroundName(recordBytes);
		string DecodeCurrentName() => currentNameBytes.Length == 0 ? "" : LegacyVietnameseText.Decode(currentNameBytes).Trim();
		if (groundId <= 0) return new(false, groundId, 0, 0, "GROUND_ID_ZERO", DecodeCurrentName());
		if (groundState != 3) return new(false, groundId, 0, 0, $"GROUND_STATE_{groundState}", DecodeCurrentName());
		if (groundId != expected.GroundId) return new(false, groundId, 0, 0, "GROUND_ID_CHANGED", DecodeCurrentName());
		if (! currentNameBytes.AsSpan().SequenceEqual(expected.ItemNameBytes)) return new(false, groundId, 0, 0, "GROUND_NAME_CHANGED", DecodeCurrentName());
		IntPtr mapCoordinateRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.MapCoordinateRoot));
		Dictionary<(int MapObjectIndex, int SegmentIndex), (int BaseX, int BaseY)> segmentCoordinates = new();
		if (mapCoordinateRoot == IntPtr.Zero) return new(false, groundId, 0, 0, "MAP_COORDINATE_ROOT_ZERO", DecodeCurrentName());
		if (! AutoFsGroundItemScanner.TryReadRawCoordinate(reader, recordBytes, 0, mapCoordinateRoot, segmentCoordinates, out int rawX, out int rawY, out string failure)) return new(false, groundId, 0, 0, "RAW_COORDINATE_READ_FAILED_" + failure, DecodeCurrentName());
		bool matches = rawX == expected.RawX && rawY == expected.RawY;
		return matches
			? new(true, groundId, rawX, rawY, "MATCH")
			: new(false, groundId, rawX, rawY, "RAW_COORDINATE_CHANGED", DecodeCurrentName());
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
			LootFilterDecision decision = finder.EvaluateFilter(item);
			// Phân loại lại từ tên ĐÃ CHUẨN HOÁ, đúng thứ Finder.ShouldPick dùng. Trước 2026-09-17 chỗ này truyền
			// item.ItemNameRaw nên cột AutoFsCategory in ra KHÔNG phải nhóm mà bộ lọc thật sự nhìn thấy.
			string category = AutoFsSpecialItemClassifier.Classify(ItemGroupClassifier.NormalizeName(item.ItemNameRaw)).ToString();
			// GHI LẠI KHI PHÁN QUYẾT ĐỔI, không khoá một dòng vĩnh viễn cho mỗi món.
			//
			// Bản cũ dùng HashSet: món nào đã ghi một lần thì thôi. Nên nếu một món bị từ chối lúc mới thấy rồi sau đó
			// được chấp nhận (người dùng đổi ô tick, ngưỡng dược phẩm tụt xuống, màu đọc lại ra khác) thì nó được nhặt
			// mà log vẫn đứng nguyên ở dòng Accepted=False cũ. Ngày 2026-09-17 chính chỗ này làm việc truy vụ
			// "Tham Lang Hộ Giáp" trong túi PID 36320 bế tắc: mọi dòng log đều Accepted=False mà món vẫn nằm trong túi.
			// Khoá theo (món, phán quyết) nên trạng thái ổn định vẫn chỉ ghi một dòng, đổi mới ghi thêm.
			string state = $"{decision.Accepted}|{decision.Reason}|{decision.Classification.Color}|{category}";
			lock (syncRoot) {
				if (loggedFilterDecisions.TryGetValue(item.MemoryFingerprint, out string? previous) && string.Equals(previous, state, StringComparison.Ordinal)) continue;
				loggedFilterDecisions[item.MemoryFingerprint] = state;
			}
			foreach (string diagnostic in finder.ConsumePotionCountDiagnostics()) currentDropLog(diagnostic);
			currentDropLog($"LOOT_FILTER | PID={item.ProcessId} | Index={item.Index} | Name={item.ItemNameRaw} | Record=0x{item.Address.ToInt64():X8} | GroundType={item.GroundType} | GroundKind={item.GroundKind} | Group={decision.Classification.Group} | AutoFsCategory={category} | Color={decision.Classification.Color} | AttributeClass={decision.Classification.AttributeClass} | QualityA={item.QualityCodeA} | QualityB={item.QualityCodeB} | Accepted={decision.Accepted} | Reason={decision.Reason} | Raw={item.RawX}/{item.RawY} | Internal={item.InternalX}/{item.InternalY} | EuclideanDistance={item.DistanceToPlayer:F0}");
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

	// Bốn nhóm chủ dự án chốt 2026-09-15 là "nhặt thường xuyên, không cần nằm trong log nhặt".
	// Dược Phẩm nhận diện bằng AttributeClass == 1 — cùng phép thử mà Finder.cs:122 dùng để quyết định có nhặt hay
	// không, nên hai chỗ không thể lệch nhau. Ba nhóm còn lại đã có sẵn mã riêng trong AutoFsSpecialItemCategory.
	private static bool IsRoutinePickup(ItemClassification classification, AutoFsSpecialItemCategory category) {
		if (classification.AttributeClass == 1) return true;                       // Dược Phẩm
		if (classification.Group == ItemGroup.Herbal) return true;                 // Thảo Dược theo GroundKind
		return category is AutoFsSpecialItemCategory.Herb                          // Thảo Dược theo tên
			or AutoFsSpecialItemCategory.FourSymbols                               // Tứ Tượng
			or AutoFsSpecialItemCategory.SixPaths;                                 // Lục Đạo
	}

	// Chỉ ghi sự kiện lỗi, từ chối, bỏ qua hoặc retry bất thường; không ghi vòng scan thành công.
	private void LogLootDiagnostic(Action<string>? currentDropLog, string message) {
		if (!settings.LogCandidates || currentDropLog == null) return;
		// Dòng nhặt THÀNH CÔNG không đi qua đây: nó gọi thẳng context.DropLog vì không mang từ khoá lỗi nào.
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

	private readonly record struct WorkerContext(int ProcessId, IntPtr GameWindow, GameWindow? Game, bool ManualInputActive, Action<string>? Log, Action<string>? DropLog);
	// CurrentName = tên món ĐANG nằm ở ô đất lúc kiểm, không phải tên món đang chờ.
	//
	// Trước đây ReadGroundSlot có đọc tên hiện tại nhưng chỉ dùng để so rồi vứt đi, nên log chỉ nói được "ô đã đổi"
	// mà không nói "đổi thành món gì". Đúng thứ đó mới chỉ ra được ô đất có bị món khác chiếm hay không — tức phân
	// biệt "món tự biến mất" với kịch bản đua tranh tái sử dụng ô.
	private readonly record struct GroundSlotReading(bool Matches, int GroundId, int RawX, int RawY, string Reason, string CurrentName = "");
}
