namespace Auto.Attack;

using Auto.Movement;
using Auto.Runtime;
using Auto.Utils;

public sealed class Engine {
	// Nới 20ms -> 40ms ngày 2026-09-14 theo quyết định của chủ dự án, để giảm tải CPU.
	//
	// Vì sao đây là chốt đắt nhất: mỗi lượt quét đọc 510 entity (GameAddresses.Entity.FirstScanIndex..LastScanIndex)
	// và mỗi entity tốn vài lần ReadProcessMemory, nên số lượt quét nhân thẳng vào số syscall mỗi giây — với 6
	// account chạy song song, mỗi worker một luồng riêng, đó là phần lớn CPU mà Auto tiêu thụ.
	//
	// 20ms là nhịp chép từ AutoFS. Đổi nhịp này CÓ đổi hành vi: phản ứng chọn mục tiêu chậm hơn tối đa 20ms mỗi lượt.
	private const int ScanIntervalMilliseconds = 40;
	private const int AttackIntervalMilliseconds = 50;
	private const int OutsideAreaWalkIntervalMilliseconds = 700;
	// Đứng im bao lâu thì mới coi là kẹt và gửi lại lệnh tránh thủ lĩnh. Cùng ngưỡng với nhánh qua cổng của hai
	// hàng đợi di chuyển, vì cùng một lý do: đang đi thì để yên, đừng phát lại lệnh.
	private const int EliteWalkStallMilliseconds = 2000;
	// Trốn xong phải dư ra bao nhiêu so với bán kính cấm, để boss nhích nhẹ là chưa phải chạy lại ngay. 1 ô = 256 raw.
	private const int EliteRetreatMarginRaw = 256;
	// Cách đích đang giữ trong chừng này thì coi như đã tới nơi. Nửa ô trục X (1 ô = 256 raw): client tự tìm đường
	// nên hiếm khi dừng đúng ô yêu cầu, đòi trùng khít thì không bao giờ tính là tới.
	private const int EliteRetreatArrivalRaw = 128;
	// AutoFS chặn mọi lệnh di chuyển khi ô trạng thái của nhân vật == 3 (WindowQueue.SplitDisk(), WindowQueue.cs:26253,
	// đọc O_Player + 464). Ô 464 đó chính là AutoFsClientProfile.LifecycleStatus của Auto: hàm ngay kế bên,
	// WindowQueue.DisposeTreeNode(index) (WindowQueue.cs:26228), đọc đúng "index * O_Player + 464" rồi lọc "!= 6" —
	// trùng khít bộ lọc "status == FinishedStatus (6) thì bỏ qua" mà AutoFsEntityScanner đang chạy ở cùng vị trí
	// trong cùng vòng quét. Giá trị 7 của ô này là lúc chết, đã thấy trong death.log ("Status=7 | State=7").
	private const int AutoFsBusyLifecycleStatus = 3;
	// Nhịp quét của IsInsideEliteZoneNow. Coordinator gọi mỗi 100ms; worker luồng đánh vốn đã quét mỗi
	// ScanIntervalMilliseconds nên đường phụ này cố tình thưa hơn hẳn để không nhân đôi tải đọc bộ nhớ.
	private const int EliteProbeIntervalMilliseconds = 300;
	// Không tiến thêm được bao lâu thì coi là không tới được.
	//
	// Chọn theo tốc độ đi ĐO ĐƯỢC, không phải đoán. Lấy 77 cặp mẫu ELITE_RETREAT liên tiếp cùng map trong
	// Release/Diagnostics/movement.log ngày 2026-09-10 (đã loại các bước nhảy đổi map): trung vị 174 raw/giây,
	// p90 653 raw/giây. Đi bình thường 1,5 giây là được ~260 raw, gấp hơn 4 lần mức 60 raw yêu cầu ở dưới —
	// còn dư khoảng an toàn kể cả khi chậm hơn trung vị 4 lần.
	//
	// Cố tình để ngắn: nhận nhầm thì chỉ mất con quái đó trong 20 giây rồi đánh con khác, rẻ hơn nhiều so với
	// đứng loay hoay. Trước đây đặt 4000ms là tôi đoán, không có số đo nào chống lưng.
	private const int StuckTargetMilliseconds = 1500;
	// Bỏ qua con đó bao lâu trước khi cho chọn lại. Quái có thể tự đi ra chỗ tới được nên không cấm vĩnh viễn.
	private const int UnreachableTargetCooldownMilliseconds = 20000;
	// true = phát hiện kẹt thì bỏ con đó UnreachableTargetCooldownMilliseconds rồi chọn con khác; hết ứng viên thì
	// nơi gọi rơi vào TryWanderTrainingCorners, tức nhân vật tự đi chỗ khác — đó là lối thoát khi bị tường ghim.
	//
	// LỊCH SỬ: bật hành động ngay từ đầu là sai lầm của tôi và nó đã phá gameplay thật. Bản release build 20:11:42
	// ngày 2026-09-10 bắn 4840 dòng TARGET_UNREACHABLE_SKIPPED, trung vị Distance=107 raw (~0,4 ô), nhỏ nhất 1 raw —
	// toàn là lúc đang áp sát đánh bình thường. Hậu quả: con quái sát bên bị cấm 20 giây nên Auto quay sang con xa
	// hơn, mục tiêu bị đổi giữa chừng khi quái chưa chết. Phải tắt đi, kèm điều kiện "chỉ bật lại sau khi có một
	// phiên log sạch chứng minh ngưỡng phân biệt đúng kẹt thật với đang đánh".
	//
	// BẬT LẠI 2026-09-15, điều kiện đó đã đạt. Đo trên Release/Diagnostics (6 account), 235 dòng:
	//   Distance trung vị 1087 raw (~4,2 ô), KHÔNG còn dòng nào <= 300 raw (chốt StuckArrivalDistanceRaw loại hết
	//   đúng nhóm báo nhầm cũ), và 183/211 = 87% các lần kẹt liên tiếp trong 60 giây xảy ra ở ĐÚNG MỘT CHỖ (lệch
	//   không quá 1 ô) — dấu vân tay của nhân vật bị tường ghim, không phải nhiễu.
	// Tần suất ~3 lần/account/giờ, cách xa mức 4840 dòng của phiên hỏng, nên mỗi lần cấm 20 giây một con là rẻ.
	//
	// NẾU GAMEPLAY HỎNG LẠI (quái sát bên bị bỏ, đổi mục tiêu khi quái chưa chết): đặt lại false là quay về chế độ
	// chỉ ghi log, không cần sửa gì thêm.
	// static readonly chứ không const: const làm trình biên dịch cắt hẳn nhánh kia và sinh cảnh báo CS0162.
	private static readonly bool BlacklistUnreachableTargets = true;
	// Khoảng cách tới quái phải rút ngắn ít nhất chừng này mới tính là ĐANG TIẾN TỚI.
	//
	// Vì sao đo khoảng cách chứ không đo toạ độ nhân vật (chủ dự án chỉ ra 2026-09-10): bị vật cản chặn thì nhân vật
	// xoay ngang xoay dọc liên tục tại chỗ, toạ độ đổi luôn nên mọi phép đo dựa trên toạ độ đều tưởng là đang đi.
	// Khoảng cách tới con quái thì đứng yên — đó mới là thứ phân biệt được "đang tới gần" với "loay hoay".
	// Hạ cùng lúc với StuckTargetMilliseconds: 60 raw trong 1,5 giây tương đương 40 raw/giây, thấp hơn trung vị đo
	// được (174 raw/giây) hơn 4 lần nên không cắt nhầm nhân vật đang đi thật.
	private const int StuckDistanceProgressRaw = 60;
	// Trong khoảng này coi như đã tới nơi, không xét kẹt. 300 raw ~ 1,2 ô theo trục X. Lấy từ phân bố đo được:
	// 62/75 dòng báo nhầm nằm dưới mức này, 13 dòng còn lại từ 556 tới 823 raw mới là lúc đang thật sự đi tới.
	private const int StuckArrivalDistanceRaw = 300;
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
	private int eliteWalkDestinationX;
	private int eliteWalkDestinationY;
	private int eliteWalkObservedX;
	private int eliteWalkObservedY;
	private DateTime eliteWalkProgressUtc = DateTime.MinValue;
	// Dùng lại một danh sách cho mọi lượt quét thay vì cấp phát mới mỗi 20ms.
	private readonly List<AutoFsEntity> eliteBuffer = new();
	// Mỗi tên quái thủ lĩnh chỉ ghi log một lần cho tới khi tắt Đánh, nếu không thì mỗi 20ms lại một dòng.
	private readonly HashSet<string> loggedEliteNames = new(StringComparer.Ordinal);
	// Đọc từ AccountEngineCoordinator (luồng khác) nên phải volatile.
	private volatile bool retreatingFromElite;
	// Chỉ worker luồng đánh đụng vào bốn field này và cái Dictionary, không cần khoá.
	private readonly Dictionary<int, DateTime> unreachableTargetsUntilUtc = new();
	private int stuckTargetIndex = -1;
	private int stuckTargetHp;
	private double stuckBestDistance = double.MaxValue;
	// Chống lặp dòng TARGET_ALL_BLACKLISTED: worker quét mỗi ScanIntervalMilliseconds nên không chốt là ngập log.
	private bool allCandidatesBlacklistedLogged;
	private int lastScannedPlayerX;
	private int lastScannedPlayerY;
	private long lastScannedPlayerUtcTicks;
	private DateTime stuckSinceUtc = DateTime.MinValue;
	private readonly object eliteProbeSyncRoot = new();
	private DateTime nextEliteProbeUtc = DateTime.MinValue;
	private bool lastEliteProbeResult;

	internal Engine(Settings settings, AutoFsAttackTransport transport, AutoFsActionGate actionGate) {
		this.settings = settings;
		this.transport = transport;
		this.actionGate = actionGate;
	}

	public bool IsManualOverrideActive => manualInputActive;

	// Nhân vật đang trong vùng cấm quanh quái thủ lĩnh và đang tự chạy ra. Coordinator đọc cờ này để KHÔNG cho Buff
	// giành quyền điều khiển: tránh boss là ưu tiên số 1, chạy ra xa rồi mới heal (chủ dự án chốt 2026-09-10).
	public bool IsRetreatingFromElite => retreatingFromElite;

	// Toạ độ người chơi mà WORKER ĐÁNH vừa đọc, kèm mốc thời gian đọc.
	//
	// Phơi ra để ClientFreezeWatch đối chiếu với toạ độ của GameMemory.ReadSnapshot — hai đường đọc khác gốc, khác
	// offset, và chúng ĐÃ TỪNG bất đồng suốt 38 phút trên PID=28500 ngày 2026-09-15 đúng lúc client bị treo (chủ dự
	// án xác nhận). Chênh lệch giữa hai nguồn chính là đồng hồ bắt sự cố đó.
	// Đọc/ghi qua Volatile vì worker chạy luồng riêng còn coordinator đọc từ luồng khác.
	public (int X, int Y, DateTime ReadUtc) LastScannedPlayerPosition {
		get => (Volatile.Read(ref lastScannedPlayerX), Volatile.Read(ref lastScannedPlayerY), new DateTime(Interlocked.Read(ref lastScannedPlayerUtcTicks)));
	}

	// Quét NGAY để biết nhân vật có đang đứng trong vùng cấm quanh thủ lĩnh không.
	//
	// Vì sao cần dù đã có IsRetreatingFromElite: cờ đó do worker luồng đánh cập nhật. Worker bị Stop() (Buff, sửa đồ,
	// anti-AFK) hoặc bị AutoFsActionGate.TryRunAttack chặn vì loot đang suspend thì cờ đóng băng ở giá trị cũ; boss
	// đi tới sau đó không ai phát hiện, và đó đúng là lúc nguy hiểm nhất.
	//
	// Lặp lại đúng hai điều kiện CẤU TRÚC của TryRetreatFromElitesCore (bật "Không đánh Boss", có bán kính, đánh
	// quanh một điểm). Cố tình BỎ các điều kiện tạm thời (đang lên bãi/đang sửa đồ) vì chúng tự hết sau vài nhịp,
	// còn ở đây mà chặn nhầm thì Buff bị treo.
	//
	// Có chốt giãn nhịp: coordinator gọi mỗi 100ms, quét đầy đủ từng nhịp là thừa.
	public bool IsInsideEliteZoneNow(int checkProcessId) {
		if (checkProcessId <= 0 || !settings.DoNotAttackBoss || settings.ElitePlayerRetreatRadius <= 0) return false;
		if (!(settings.TrainingEnabled || settings.TeachingEnabled || settings.ContinueEnabled || settings.UseCenterPosition)) return false;
		DateTime now = DateTime.UtcNow;
		lock (eliteProbeSyncRoot) {
			if (now < nextEliteProbeUtc) return lastEliteProbeResult;
			nextEliteProbeUtc = now.AddMilliseconds(EliteProbeIntervalMilliseconds);
		}
		bool inside = false;
		try {
			using MemoryReader probeReader = new(checkProcessId);
			List<AutoFsEntity> probeElites = [];
			scanner.Scan(probeReader, settings, out int playerX, out int playerY, out _, eliteCollector: probeElites);
			if (playerX > 0 && playerY > 0 && probeElites.Count > 0) {
				inside = EliteAvoidance.IsNearAnyElite(playerX, playerY, probeElites.Select(elite => (elite.RawX, elite.RawY)).ToList(), settings.ElitePlayerRetreatRadius);
			}
		} catch {
			inside = false;
		}
		lock (eliteProbeSyncRoot) lastEliteProbeResult = inside;
		return inside;
	}

	// Reports whether the worker recently posted an attack command successfully.
	public bool HasRecentAttack(TimeSpan maximumAge) {
		long ticks = Interlocked.Read(ref lastAttackPostedUtcTicks);
		return ticks > 0 && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) <= maximumAge;
	}

	// Giai đoạn A của tính năng tránh quái thủ lĩnh: mới chỉ PHÁT HIỆN và ghi log, chưa đổi hành vi đánh hay đi.
	// In kèm Hp và Level để đối chiếu giả thuyết "boss có thanh máu rất to" — AutoFS không dùng thanh máu để
	// nhận diện (grep MaxHp toàn AutoSource cho 0 kết quả) nên đây là dữ liệu mới, chưa có gì để so.
	private void LogDetectedElites(int currentProcessId, Action<string>? currentTargetMovementLog) {
		if (currentTargetMovementLog == null || eliteBuffer.Count == 0) return;
		foreach (AutoFsEntity elite in eliteBuffer) {
			if (!loggedEliteNames.Add(elite.Name)) continue;
			currentTargetMovementLog($"ELITE_DETECTED | PID={currentProcessId} | Name={elite.Name} | Hp={elite.Hp} | Level={elite.Level} | Raw={elite.RawX}/{elite.RawY} | ToPlayer={elite.Distance:F0} | ToCenter={elite.DistanceToCenter:F0} | Range={settings.Range} | AvoidRadius={settings.EliteAvoidRadius} | RetreatRadius={settings.ElitePlayerRetreatRadius}");
		}
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
		// Worker dừng thì không còn ai cập nhật cờ này nữa. Bỏ quên nó ở true là Buff bị chặn vĩnh viễn, nhân vật
		// không bao giờ được heal — hỏng ngược lại đúng thứ mà thứ tự ưu tiên mới đang bảo vệ.
		retreatingFromElite = false;
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
			eliteWalkDestinationX = 0;
			eliteWalkDestinationY = 0;
			eliteWalkObservedX = 0;
			eliteWalkObservedY = 0;
			eliteWalkProgressUtc = DateTime.MinValue;
			eliteBuffer.Clear();
			loggedEliteNames.Clear();
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

	// Chờ ScanIntervalMilliseconds trước mỗi lần quét và chờ thêm 50 ms sau khi phát lệnh đánh (nhịp sau vẫn đúng AutoFS).
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

			// Stop() chỉ Cancel() chứ không Wait() worker, mà thân vòng lặp dưới đây không kiểm token lần nào.
			// Thiếu chốt này thì lượt đang chạy vẫn gửi được lệnh đi bộ tới góc quanh tâm bãi, và vì client tự đi hết
			// đường nên nhân vật còn đi ngược về tâm một đoạn sau khi người dùng đã tắt Đánh rồi mới dừng.
			if (token.IsCancellationRequested) break;

			if (!currentManualInput && currentProcessId > 0 && currentWindow != IntPtr.Zero && currentGame != null) {
				try {
					actionGate.TryRunAttack(() => {
						if (reader == null || readerProcessId != currentProcessId) {
							reader?.Dispose();
							reader = new MemoryReader(currentProcessId);
							readerProcessId = currentProcessId;
						}
						eliteBuffer.Clear();
						IReadOnlyList<AutoFsEntity> candidates = scanner.Scan(reader, settings, out int playerX, out int playerY, out int playerLifecycleStatus, preferredTargetIndex: preferredTargetIndex, eliteCollector: eliteBuffer);
						// Ghi lại cho ClientFreezeWatch đối chiếu với nguồn toạ độ của GameMemory.ReadSnapshot.
						if (playerX > 0 && playerY > 0) {
							Volatile.Write(ref lastScannedPlayerX, playerX);
							Volatile.Write(ref lastScannedPlayerY, playerY);
							Interlocked.Exchange(ref lastScannedPlayerUtcTicks, DateTime.UtcNow.Ticks);
						}
						LogDetectedElites(currentProcessId, currentTargetMovementLog);
						// BÁM MỤC TIÊU bằng chỉ số Auto TỰ NHỚ, không dựa vào ô CurrentTargetIndex của client.
						//
						// Vì sao: audit ngày 2026-09-14 cho thấy ô đó (GameAddresses.Globals.CurrentTargetIndex = 0x4CB668)
						// đọc ra rác — Value=-1251073949, ngoài dải -1..511, sai ở cả 6 account và cả bản release cũ.
						// Hệ quả đo được trong attack.log 2 tiếng: 10.849/10.849 dòng mang Selection=NEAREST, KHÔNG một
						// dòng nào CURRENT_TARGET, tức tầng ưu tiên "giữ con đang đánh" chưa bao giờ chạy.
						// Khoảng cách gánh thay được phần lớn (trung bình 3,98 giây mỗi lần đổi mục tiêu), nhưng
						// 1.630/12.092 lần (13,5%) đổi trong chưa tới 1 giây — đó là lúc con khác lại gần hơn con đang
						// đánh và chen lên đầu bảng.
						//
						// Chỉ nhả khi con đó KHÔNG CÒN trong danh sách ứng viên: chết, ra khỏi tầm, hoặc bị vùng cấm
						// quanh thủ lĩnh loại. Không cần địa chỉ mới nào của client.
						if (preferredTargetIndex >= 0 && ! candidates.Any(candidate => candidate.Index == preferredTargetIndex)) preferredTargetIndex = -1;
						AutoFsEntity? target = PickReachableTarget(candidates, currentProcessId, playerX, playerY, currentTargetMovementLog);
						if (TryRetreatFromElites(currentGame, currentProcessId, playerX, playerY, playerLifecycleStatus, currentTargetMovementLog)) {
							lastLoggedTargetIndex = -1;
						} else if (target == null) {
							lastLoggedTargetIndex = -1;
							TryWanderTrainingCorners(currentGame, currentProcessId, playerX, playerY, playerLifecycleStatus, currentTargetMovementLog);
						} else if (!transport.TrySend(currentWindow, target.Index, out string error)) {
							LogErrorOnce(currentLog, currentProcessId, error);
						} else {
							attackPosted = true;
							// Ghi nhớ con vừa đánh để lượt quét sau ưu tiên nó, thay cho ô CurrentTargetIndex đã hỏng.
							preferredTargetIndex = target.Index;
							Interlocked.Exchange(ref lastAttackPostedUtcTicks, DateTime.UtcNow.Ticks);
							lastError = "";
							if (target.IsCurrentTarget && target.Hp <= 0 && zeroHpTargetIndex != target.Index) {
								zeroHpTargetIndex = target.Index;
								zeroHpTargetObservedUtc = DateTime.UtcNow;
								// Sau khi AutoFsEntityScanner lọc hp <= 0 (2026-09-11) thì nhánh này lẽ ra không còn nổ.
								// Giữ lại làm chuông báo: nếu vẫn thấy dòng này thì bộ lọc kia đã hụt một đường nào đó.
								currentTargetMovementLog?.Invoke($"CURRENT_TARGET_ZERO_HP_OBSERVED | PID={currentProcessId} | Index={target.Index} | Name={target.Name} | Status={target.Status} | HP={target.Hp} | Action=BẤT_THƯỜNG_LẼ_RA_ĐÃ_BỊ_LỌC");
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

	// Giai đoạn C: nhân vật đang đứng trong vùng cấm quanh thủ lĩnh thì BỎ đánh lượt này và đi ra góc sạch nhất.
	// Trả về true nghĩa là lượt này do luồng lùi chiếm, nơi gọi không được gửi lệnh đánh.
	//
	// Đi 4 góc quanh tâm bãi chứ không tính vector ngược hướng: 4 góc luôn nằm trong range/3*1.42 quanh tâm nên không
	// bao giờ đẩy nhân vật ra khỏi bãi rồi giằng co với chốt NO_TARGET_CORNER_ABORT, và tái dùng đúng đường đi đã chạy ổn.
	// BẮT BUỘC dùng AutoFsMovementCommand.TryWalkTo (lệnh đi bộ không gắn cờ) — TryMoveTo chỉ dành cho sửa đồ và lên bãi.
	//
	// Không cần nhớ vị trí thủ lĩnh đã khuất tầm: mẫu PID 22292 ngày 2026-09-07 cho thấy client stream entity vào khi
	// còn cách 1598 raw và chưa vào khi cách 2410 raw, tức ngưỡng stream xa hơn hẳn bán kính vùng cấm 500. Một con nằm
	// trong vùng cấm thì luôn đang được stream, không có chuyện chớp tắt gây giằng co.
	// Bỏ qua những con đã bị đánh dấu là không tới được, và tự đánh dấu con đang kẹt.
	//
	// Vấn đề: client tự tìm đường tới quái, gặp vật cản địa hình thì nó đứng nguyên tại chỗ mà không báo gì. Auto vẫn
	// bắn lệnh đánh mỗi 50ms nên tự nó không biết là đang loay hoay.
	//
	// Anti-AFK của coordinator KHÔNG cứu được ca này: điều kiện của nó là "(movementRecoveryPending || !recentAttack)"
	// (AccountEngineCoordinator.cs), mà đang đuổi quái thì recentAttack luôn true và không luồng di chuyển nào bận.
	// Bằng chứng: cả 14 dòng ANTI_AFK Flow=AUTO_ATTACK trong anti-afk.log ngày 2026-09-10 đều mang RecentAttack=False,
	// không một dòng nào nổ trong lúc đang đánh.
	//
	// Ba dấu hiệu phải cùng lúc mới coi là kẹt, thiếu một cái là nhận nhầm:
	//   - vẫn đúng con đó
	//   - toạ độ nhân vật không đổi   (đứng yên)
	//   - máu con đó không đổi        (đứng yên mà máu tụt là đang đánh bình thường, không phải kẹt)
	private AutoFsEntity? PickReachableTarget(IReadOnlyList<AutoFsEntity> candidates, int currentProcessId, int playerX, int playerY, Action<string>? currentTargetMovementLog) {
		DateTime now = DateTime.UtcNow;
		AutoFsEntity? target = null;
		foreach (AutoFsEntity candidate in candidates) {
			if (unreachableTargetsUntilUtc.TryGetValue(candidate.Index, out DateTime until)) {
				if (now >= until) unreachableTargetsUntilUtc.Remove(candidate.Index);
				// Ở chế độ chỉ quan sát, bảng này chỉ để KHỎI ghi lặp log, không dùng để loại mục tiêu.
				else if (BlacklistUnreachableTargets) continue;
			}
			target = candidate;
			break;
		}
		if (target == null) {
			stuckTargetIndex = -1;
			stuckBestDistance = double.MaxValue;
			// Phân biệt hai ca mà nơi gọi xử lý y hệt nhau (đều rơi xuống TryWanderTrainingCorners):
			//   candidates rỗng              -> quanh đây hết quái, hoàn toàn bình thường
			//   candidates CÒN mà bị cấm hết -> đúng triệu chứng phiên hỏng 2026-09-10: "mọi con quái đều bị cấm 20
			//                                   giây, hết ứng viên, nhân vật đứng im"
			// Vì sao bắt buộc phải có dòng này: nhìn bằng mắt trong game thì "Auto bỏ qua con quái sát bên" trông Y
			// HỆT ca bỏ ĐÚNG vì giữa hai bên có tường phải đi vòng — chủ dự án chỉ ra 2026-09-15 là không thể tự phân
			// biệt được. Không đo được ca này thì không có cách nào biết BlacklistUnreachableTargets đang cứu hay
			// đang phá. Đây là đồng hồ đo cho chính công tắc đó.
			if (BlacklistUnreachableTargets && candidates.Count > 0 && !allCandidatesBlacklistedLogged) {
				allCandidatesBlacklistedLogged = true;
				currentTargetMovementLog?.Invoke($"TARGET_ALL_BLACKLISTED | PID={currentProcessId} | Player={playerX}/{playerY} | ỨngViên={candidates.Count} | SốConĐangBịCấm={unreachableTargetsUntilUtc.Count} | CooldownMs={UnreachableTargetCooldownMilliseconds} | Action=Không còn con nào để đánh, chuyển sang đi tuần góc bãi");
			}
			return null;
		}
		// Chọn được con để đánh thì mở lại chuông báo, để đợt "cấm hết" lần sau vẫn được ghi đúng một dòng.
		allCandidatesBlacklistedLogged = false;
		// Ba đường reset đồng hồ, mỗi đường ứng với một kiểu "vẫn ổn":
		//   đổi con           -> đợt đuổi mới
		//   máu con đó đổi    -> đang đánh được nó, đứng yên là bình thường
		//   tới gần thêm      -> đường đi vẫn thông
		// Đã tới nơi thì KHÔNG xét kẹt nữa. Đứng cạnh quái thì khoảng cách không rút ngắn thêm được là chuyện đương
		// nhiên, không phải dấu hiệu bị chặn.
		//
		// Bằng chứng vì sao phải có chốt này: bản đầu thiếu nó và tự bắn 70 dòng TARGET_UNREACHABLE_SKIPPED trong
		// ~3 phút trên cả 6 account (Release/Diagnostics/auto-runtime.log 2026-09-10 20:12-20:15), trung vị
		// Distance=103 raw, nhỏ nhất 2 raw — 62/75 dòng nằm trong 300 raw, tức đang áp sát đánh bình thường. Hậu quả:
		// mọi con quái đều bị cấm 20 giây, hết ứng viên, nhân vật đứng im.
		if (target.Distance <= StuckArrivalDistanceRaw) {
			stuckTargetIndex = target.Index;
			stuckTargetHp = target.Hp;
			stuckBestDistance = target.Distance;
			stuckSinceUtc = now;
			return target;
		}
		bool closingIn = target.Distance <= stuckBestDistance - StuckDistanceProgressRaw;
		if (target.Index != stuckTargetIndex || target.Hp != stuckTargetHp || closingIn) {
			// Chỉ hạ mốc gần nhất, không nâng: quái tự chạy ra xa rồi quay lại thì không được coi là tiến độ mới.
			stuckBestDistance = target.Index != stuckTargetIndex ? target.Distance : Math.Min(stuckBestDistance, target.Distance);
			stuckTargetIndex = target.Index;
			stuckTargetHp = target.Hp;
			stuckSinceUtc = now;
			return target;
		}
		if ((now - stuckSinceUtc).TotalMilliseconds < StuckTargetMilliseconds) return target;

		unreachableTargetsUntilUtc[target.Index] = now.AddMilliseconds(UnreachableTargetCooldownMilliseconds);
		string stuckAction = BlacklistUnreachableTargets ? "Bỏ qua con này và chọn con khác" : "CHỈ GHI LOG, vẫn đánh con này";
		currentTargetMovementLog?.Invoke($"TARGET_UNREACHABLE_SKIPPED | PID={currentProcessId} | Index={target.Index} | Name={target.Name} | Player={playerX}/{playerY} | Target={target.RawX}/{target.RawY} | Distance={target.Distance:F0} | GầnNhấtĐạtĐược={stuckBestDistance:F0} | TargetHp={target.Hp} | StuckMs={(int)(now - stuckSinceUtc).TotalMilliseconds} | CooldownMs={UnreachableTargetCooldownMilliseconds} | Action={stuckAction}");
		stuckTargetIndex = -1;
		stuckBestDistance = double.MaxValue;
		// Chế độ chỉ quan sát: giữ nguyên mục tiêu, chỉ ghi lại một dòng để về sau còn chỉnh ngưỡng bằng số thật.
		if (!BlacklistUnreachableTargets) return target;
		// Hết ứng viên thì nơi gọi rơi vào nhánh target == null -> TryWanderTrainingCorners đưa nhân vật đi chỗ khác.
		// Đó chính là bước "lùi lại", tái dùng đường đi sẵn có chứ không thêm luồng di chuyển mới.
		return PickReachableTarget(candidates, currentProcessId, playerX, playerY, currentTargetMovementLog);
	}

	private bool TryRetreatFromElites(GameWindow currentGame, int currentProcessId, int playerX, int playerY, int playerLifecycleStatus, Action<string>? currentTargetMovementLog) {
		bool retreating = TryRetreatFromElitesCore(currentGame, currentProcessId, playerX, playerY, playerLifecycleStatus, currentTargetMovementLog);
		retreatingFromElite = retreating;
		return retreating;
	}

	private bool TryRetreatFromElitesCore(GameWindow currentGame, int currentProcessId, int playerX, int playerY, int playerLifecycleStatus, Action<string>? currentTargetMovementLog) {
		if (!settings.DoNotAttackBoss || settings.ElitePlayerRetreatRadius <= 0 || eliteBuffer.Count == 0) return false;
		if (playerX <= 0 || playerY <= 0) return false;
		bool aroundPoint = settings.TrainingEnabled || settings.TeachingEnabled || settings.ContinueEnabled || settings.UseCenterPosition;
		if (!aroundPoint) return false;
		if (currentGame.ReturnToTrainingAutomation.IsBusy || currentGame.ConfiguredTrainingMovementAutomation.IsBusy || currentGame.WeaponRepairAutomation.IsBusy) return false;

		List<(int X, int Y)> elites = eliteBuffer.Select(elite => (elite.RawX, elite.RawY)).ToList();
		if (!EliteAvoidance.IsNearAnyElite(playerX, playerY, elites, settings.ElitePlayerRetreatRadius)) {
			return false;
		}

		// Đã trong vùng cấm: từ đây trở đi luôn chiếm lượt, kể cả khi chưa tới hạn đi hay không tìm được góc sạch.
		// Nếu trả false ở các nhánh dưới thì nơi gọi sẽ quay ra đánh chính con quái cạnh thủ lĩnh.
		(int centerX, int centerY) = AutoFsEntityScanner.GetCenter(settings, playerX, playerY);
		if (centerX <= 0 || centerY <= 0) return true;
		if (playerLifecycleStatus == AutoFsBusyLifecycleStatus) return true;

		int range = Math.Max(settings.Range, 1);
		// BỎ HẲN 4 góc cố định cho luồng tránh boss (chủ dự án chốt 2026-09-11). Đích trốn giờ tính theo KHOẢNG CÁCH:
		// đẩy nhân vật ra tới khi cách mọi thủ lĩnh quá radius + margin, hướng đẩy là tổng vector đẩy từ từng con.
		//
		// 4 góc nằm ở tâm ± Range/3, chỉ cách tâm 745 raw và không liên quan gì tới chỗ boss đứng, nên "chạy trốn"
		// hoá ra là đi tới 1 trong 4 chỗ có sẵn — có thể còn gần boss hơn chỗ đang đứng. Boss đứng gần tâm thì cả 4
		// góc đều bẩn và nhân vật đứng chết: movement.log 2026-09-11 PID=34032 có bốn dòng ELITE_NO_SAFE_CORNER lúc
		// 14:50:38 / 14:50:48 / 14:50:58 / 14:51:09 đều cùng một toạ độ Player=63631/90321.
		// Luật mới không có trạng thái "không tìm được chỗ": boss đuổi tới thì lượt quét sau cho ra đích xa hơn.
		if (!EliteAvoidance.TryGetRetreatDestination(playerX, playerY, centerX, centerY, range, elites,
			settings.ElitePlayerRetreatRadius, EliteRetreatMarginRaw, out int destinationX, out int destinationY)) {
			// Chỉ xảy ra khi không tính được hướng đẩy (trùng toạ độ mọi con). Giữ lượt để khỏi quay ra đánh con quái
			// cạnh thủ lĩnh, lượt sau boss nhích một chút là có hướng.
			return true;
		}

		DateTime now = DateTime.UtcNow;
		if (now < nextOutsideAreaWalkUtc) return true;
		// CHỐT ĐÍCH: đã gửi một lệnh trốn thì đi cho hết chỗ đó, KHÔNG tính lại đích chỉ vì nhân vật đã nhích.
		//
		// Vì sao phải đổi cách so (đo trên Release/Diagnostics/movement.log, phiên 10 tiếng ngày 2026-09-15, 11837
		// dòng ELITE_RETREAT): bản cũ so đích MỚI với đích CŨ rồi mới quyết gửi lại. Nhưng đích mới luôn tính từ
		// TOẠ ĐỘ HIỆN TẠI của nhân vật (EliteAvoidance.TryGetRetreatDestination: playerX + hướng đẩy * needed), nên
		// nhân vật càng đi thì đích càng trôi theo — phép so đó gần như luôn "đổi đáng kể" và lệnh lại được bắn.
		// Số đo: gom các lệnh cách nhau dưới 5 giây thành một lần trốn thì chỉ 309 lần trốn gửi đúng MỘT lệnh, còn
		// 1000+ lần gửi từ 2 lệnh trở lên (225 lần đúng 2 lệnh, có lần tới 15). Trong các lần gửi lại đó, 57,6% lọt
		// qua vì đích lệch hơn 192 raw, 42,4% lọt qua vì hết hạn đứng im 2 giây. Mỗi lần bắn lại là client khởi động
		// lại đường đi — đúng hiện tượng nhân vật giật lại rồi đi lần hai mà chủ dự án nhìn thấy.
		//
		// Luật mới chỉ gửi lại khi có LÝ DO THẬT, không gửi lại vì đích trôi:
		//   1. Chưa có đích nào đang giữ.
		//   2. Đích đang giữ đã bẩn — boss đã đi tới gần chỗ đó, tới nơi cũng vẫn nằm trong vùng cấm.
		//   3. Đã tới nơi mà vẫn còn trong vùng cấm (boss bám theo) -> cần chặng kế tiếp.
		//   4. Nhân vật đứng im quá EliteWalkStallMilliseconds -> lệnh trước không ăn, phải bắn lại.
		if (eliteWalkDestinationX > 0 && eliteWalkDestinationY > 0) {
			if (playerX != eliteWalkObservedX || playerY != eliteWalkObservedY) {
				eliteWalkObservedX = playerX;
				eliteWalkObservedY = playerY;
				eliteWalkProgressUtc = now;
			}
			bool committedDestinationDirty = EliteAvoidance.IsNearAnyElite(eliteWalkDestinationX, eliteWalkDestinationY, elites, settings.ElitePlayerRetreatRadius);
			bool arrived = EliteAvoidance.Distance(playerX, playerY, eliteWalkDestinationX, eliteWalkDestinationY) <= EliteRetreatArrivalRaw;
			bool stalled = now - eliteWalkProgressUtc >= TimeSpan.FromMilliseconds(EliteWalkStallMilliseconds);
			if (!committedDestinationDirty && !arrived && !stalled) return true;
			// Còn đi dở tới một đích vẫn sạch mà phải bắn lại vì đứng im: giữ nguyên đích cũ thay vì lấy đích mới đã
			// trôi theo nhân vật, để lệnh thứ hai là đi tiếp đúng chỗ cũ chứ không phải đổi hướng giữa đường.
			if (!committedDestinationDirty && !arrived) {
				destinationX = eliteWalkDestinationX;
				destinationY = eliteWalkDestinationY;
			}
		}
		if (!AutoFsMovementCommand.TryWalkTo(currentGame, destinationX, destinationY, out string walkResult)) {
			if (!string.Equals(lastWalkFailure, walkResult, StringComparison.Ordinal)) {
				lastWalkFailure = walkResult;
				currentTargetMovementLog?.Invoke($"ELITE_RETREAT FAIL | PID={currentProcessId} | Player={playerX}/{playerY} | Destination={destinationX}/{destinationY} | {walkResult}");
			}
			return true;
		}
		lastWalkFailure = "";
		nextOutsideAreaWalkUtc = now.AddMilliseconds(OutsideAreaWalkIntervalMilliseconds);
		eliteWalkDestinationX = destinationX;
		eliteWalkDestinationY = destinationY;
		eliteWalkObservedX = playerX;
		eliteWalkObservedY = playerY;
		eliteWalkProgressUtc = now;
		(int nearestX, int nearestY) = elites.OrderBy(elite => EliteAvoidance.Distance(playerX, playerY, elite.X, elite.Y)).First();
		currentTargetMovementLog?.Invoke($"ELITE_RETREAT | PID={currentProcessId} | Player={playerX}/{playerY} | Elite={nearestX}/{nearestY} | Distance={EliteAvoidance.Distance(playerX, playerY, nearestX, nearestY):F0} | Radius={settings.ElitePlayerRetreatRadius} | SauKhiTới={EliteAvoidance.NearestEliteDistance(destinationX, destinationY, elites):F0} | Destination={destinationX}/{destinationY} | {walkResult}");
		return true;
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
