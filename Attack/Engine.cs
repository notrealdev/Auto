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
	// Boss-only patrol: resend the corner walk only after this long without moving, and give up on a corner after
	// BossPatrolCornerTimeoutMilliseconds (a blocked corner still counts as patrolled so the round can finish).
	private const int BossPatrolStallMilliseconds = 2000;
	private const int BossPatrolCornerTimeoutMilliseconds = 30000;
	// Đứng im bao lâu thì mới coi là kẹt và gửi lại lệnh tránh thủ lĩnh. Cùng ngưỡng với nhánh qua cổng của hai
	// hàng đợi di chuyển, vì cùng một lý do: đang đi thì để yên, đừng phát lại lệnh.
	private const int EliteWalkStallMilliseconds = 2000;
	// Đích trốn phải được giữ tối thiểu chừng này trước khi cho phép huỷ vì "bẩn" (một thủ lĩnh lọt vào bán kính
	// cấm quanh đích). Không có ngưỡng này thì nhiều thủ lĩnh xoay quanh sát đích sẽ làm đích bị coi là bẩn ở HẦU
	// HẾT mỗi lượt gửi lại (OutsideAreaWalkIntervalMilliseconds = 700ms), khiến TryWalkTo bị bắn lại liên tục và
	// không lệnh nào kịp chạy hết trước khi bị lệnh sau ghi đè — nhân vật đứng nguyên một chỗ dù log báo
	// "Delivery=CONFIRMED" mỗi lần. Cùng ngưỡng với EliteWalkStallMilliseconds vì cùng bản chất: "vừa chốt đích thì
	// để yên, đừng huỷ ngay".
	//
	// Bằng chứng (movement.log 2026-09-22, PID=19860): 12:02:20.625-12:02:52.944, Player kẹt cứng đúng
	// 61551/91208 suốt ~32 giây, 30+ dòng ELITE_RETREAT phần lớn LýDoBắn=ĐÍCH_BẨN cách nhau ~700ms, do 3 thủ lĩnh
	// Băng Linh xoay quanh gần đích trốn.
	private const int EliteRetreatMinimumHoldMilliseconds = 2000;
	// Trốn xong phải dư ra bao nhiêu so với bán kính cấm, để boss nhích nhẹ là chưa phải chạy lại ngay. 1 ô = 256 raw.
	// Shared with the monster filter in AutoFsEntityScanner, see EliteAvoidance.RetreatMarginRaw.
	private const int EliteRetreatMarginRaw = EliteAvoidance.RetreatMarginRaw;
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
	// Wall bypass (owner request 2026-10-11, plan simulated 2026-10-09): when the stuck check above fires, step sideways
	// from the stuck point instead of dropping the monster at once, then try the monster again. Sides alternate
	// left 1, right 1, left 2, right 2, left 3, right 3 cells, measured perpendicular to the player->monster line from the
	// stuck point. The simulation reached the monster past a short wall only with alternating sides; committing to one
	// side failed there. A long wall is not passed: after the 6 steps the monster is dropped as before.
	// The simulation also showed the outer loop (give up -> 20 s cooldown -> same monster -> bypass again) never ends on a
	// long wall, so episodes are capped per account. All numbers below are the simulated plan, not measured in game.
	private const int WallBypassSteps = 6;
	private const int WallBypassWalkTimeoutMilliseconds = 2000;
	private const int WallBypassArrivalRaw = 128;
	private const int WallBypassMaximumEpisodes = 3;
	// Without a training area ("Quanh điểm" dropped while bosses surround it) the character roams wider and meets more
	// walls, so the cap is doubled (plan approved 2026-10-13).
	private const int WallBypassMaximumEpisodesRoaming = 6;
	private int CurrentWallBypassMaximumEpisodes => settings.TrainingEnabled || settings.TeachingEnabled || settings.ContinueEnabled || settings.UseCenterPosition
		? WallBypassMaximumEpisodes
		: WallBypassMaximumEpisodesRoaming;
	// Monsters standing within one cell of a monster given up as unreachable are skipped as well for the same cooldown:
	// they stand behind the same wall. Before, only that one Index was skipped and the next pick was its neighbour.
	private readonly List<(int X, int Y, int Radius, DateTime UntilUtc)> unreachableZones = [];
	private static readonly TimeSpan WallBypassEpisodeWindow = TimeSpan.FromMinutes(2);
	// Game cell scale: 256 raw on X, 512 raw on Y.
	private const int CellRawX = 256;
	private const int CellRawY = 512;
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
	// "Chỉ đánh Boss" patrol: one bit per corner reached since a boss was last targeted, plus the current leg's timing.
	private int bossPatrolVisitedCorners;
	private DateTime bossPatrolLegStartedUtc = DateTime.MinValue;
	private DateTime bossPatrolProgressUtc = DateTime.MinValue;
	private int bossPatrolObservedX;
	private int bossPatrolObservedY;
	// Set by the worker when the patrol flips "Chỉ đánh Boss" -> "Không đánh Boss"; the UI tick takes it to refresh the tab.
	private int bossModeSwitchPending;
	private bool outsideAreaAbortLogged;
	private string lastWalkFailure = "";
	private DateTime nextOutsideAreaWalkUtc = DateTime.MinValue;
	private int eliteWalkDestinationX;
	private int eliteWalkDestinationY;
	private int eliteWalkObservedX;
	private int eliteWalkObservedY;
	private DateTime eliteWalkProgressUtc = DateTime.MinValue;
	// Thời điểm đích hiện tại được CHỐT (gán cùng lúc với eliteWalkDestinationX/Y). Dùng để chặn vòng lặp bắn lại
	// liên tục khi nhiều thủ lĩnh xoay quanh sát đích trốn — xem EliteRetreatMinimumHoldMilliseconds bên dưới.
	private DateTime eliteWalkCommittedUtc = DateTime.MinValue;
	// Directions (scaled-Y space) a retreat could not walk along, and the side steps tried once every direction was
	// blocked; both cleared when the retreat ends. movement.log 2026-10-13: 910 ĐỨNG_IM resends always went to the same
	// held destination, e.g. 1đêm2nháy 16:32:35-16:32:40 stood at 61508/88906 while three resends asked for 62561/91167.
	// Owner 2026-10-13: never stand still near a boss, so one stall is enough to turn away (no second resend).
	private readonly List<(double X, double Y)> eliteBlockedDirections = [];
	private int eliteSideStepCount;
	// Where the last blocked direction was recorded; ELITE_RETREAT_UNSTUCK fires two cells away from it.
	private int eliteBlockedAtX;
	private int eliteBlockedAtY;
	private DateTime eliteBlockedAtUtc = DateTime.MinValue;
	// Walls do not move, but the character may come back to the same spot much later with the boss elsewhere.
	private static readonly TimeSpan EliteBlockedDirectionLifetime = TimeSpan.FromSeconds(60);
	private const int EliteMaximumBlockedDirections = 6;
	// Side steps 1, 1, 2, 2, 3, 3 cells (left/right alternating), then the blocked list is cleared and tried again.
	private const int EliteMaximumSideSteps = 6;
	// Tên cổng làm TryRetreatFromElitesCore trả false. Chỉ dùng để ghi log chẩn đoán ở TryRetreatFromElites; đọc và
	// ghi đều trong cùng một lượt quét của worker nên không cần đồng bộ.
	private string retreatExitGate = "";
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
	// Wall bypass state, worker thread only. bypassTargetIndex < 0 = no episode running.
	private enum WallBypassPhase { NeedWalk, Walking, Probing }
	private int bypassTargetIndex = -1;
	private int bypassStep;
	private WallBypassPhase bypassPhase;
	private int bypassAnchorX;
	private int bypassAnchorY;
	private int bypassStepX;
	private int bypassStepY;
	private DateTime bypassPhaseUntilUtc;
	private double bypassProbeStartDistance;
	private int bypassProbeStartHp;
	// Start times of recent episodes; kept across Stop on purpose, like bossPatrolVisitedCorners, because the worker is
	// recreated often (anti-AFK) and the cap must still hold.
	private readonly Queue<DateTime> bypassEpisodeStartsUtc = new();
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
		// No "around point" condition any more: the retreat also runs while "Quanh điểm" is dropped (2026-10-13).
		if (checkProcessId <= 0 || !settings.DoNotAttackBoss || settings.ElitePlayerRetreatRadius <= 0) return false;
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
				inside = EliteAvoidance.IsNearAnyElite(playerX, playerY, EliteZones(probeElites));
			}
		} catch {
			inside = false;
		}
		lock (eliteProbeSyncRoot) lastEliteProbeResult = inside;
		return inside;
	}

	// Elites with the radius the character runs from: 3 cells for a "<c=" boss, 1 cell for a "(" enhanced monster.
	private List<(int X, int Y, int Radius)> EliteZones(IEnumerable<AutoFsEntity> elites)
		=> elites.Select(elite => (elite.RawX, elite.RawY, AutoFsEntityScanner.GetPlayerRetreatRadius(settings, elite.NameBytes))).ToList();

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
			currentTargetMovementLog($"ELITE_DETECTED | PID={currentProcessId} | Name={elite.Name} | Hp={elite.Hp} | Level={elite.Level} | Raw={elite.RawX}/{elite.RawY} | ToPlayer={elite.Distance:F0} | ToCenter={elite.DistanceToCenter:F0} | Range={settings.Range} | Loại={(EliteAvoidance.IsColorTagBoss(elite.NameBytes) ? "BOSS" : "CƯỜNG_HOÁ")} | BánKínhTránh={AutoFsEntityScanner.GetPlayerRetreatRadius(settings, elite.NameBytes)}");
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

	// True once after the patrol switched the boss mode, so the UI thread can refresh the two boss checkboxes.
	public bool TakeBossModeSwitched() => Interlocked.Exchange(ref bossModeSwitchPending, 0) == 1;

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
			// bossPatrolVisitedCorners survives Stop on purpose: the worker is stopped whenever another flow takes over
			// (auto-runtime.log 2026-10-08 11:38:35 PID=20808: pet approach walk, then "Auto Nhặt AutoFS bắt đầu"), and
			// clearing it there restarted the round at 3/4 — the "patrols twice" the owner saw.
			bossPatrolLegStartedUtc = DateTime.MinValue;
			outsideAreaAbortLogged = false;
			lastWalkFailure = "";
			nextOutsideAreaWalkUtc = DateTime.MinValue;
			eliteWalkDestinationX = 0;
			eliteWalkDestinationY = 0;
			eliteWalkObservedX = 0;
			eliteWalkObservedY = 0;
			eliteWalkProgressUtc = DateTime.MinValue;
			eliteWalkCommittedUtc = DateTime.MinValue;
			// Blocked directions survive Stop() too (anti-AFK and Buff restart the worker while it is stuck); see the
			// retreat end in TryRetreatFromElites.
			eliteBuffer.Clear();
			loggedEliteNames.Clear();
			bypassTargetIndex = -1;
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
						AutoFsEntity? target = PickReachableTarget(candidates, currentGame.LastObservedMapId, currentProcessId, playerX, playerY, currentTargetMovementLog);
						if (TryRetreatFromElites(currentGame, currentProcessId, playerX, playerY, playerLifecycleStatus, currentTargetMovementLog)) {
							lastLoggedTargetIndex = -1;
							if (bypassTargetIndex >= 0) EndWallBypass(currentProcessId, playerX, playerY, "TRỐN_BOSS", currentTargetMovementLog);
						} else if (target != null && target.Index == bypassTargetIndex && HoldForWallBypass(currentGame, currentProcessId, target, playerX, playerY, currentTargetMovementLog)) {
							lastLoggedTargetIndex = -1;
						} else if (target == null) {
							lastLoggedTargetIndex = -1;
							TryWanderTrainingCorners(currentGame, currentProcessId, playerX, playerY, playerLifecycleStatus, currentTargetMovementLog);
						} else if (TryLeavePetLagCluster(currentGame, currentProcessId, target, playerX, playerY, currentTargetMovementLog)) {
							lastLoggedTargetIndex = -1;
						} else if (!transport.TrySend(currentWindow, target.Index, out string error)) {
							LogErrorOnce(currentLog, currentProcessId, error);
						} else {
							attackPosted = true;
							// A boss was found: the "patrol 4 corners without a boss" round starts over.
							if (settings.OnlyAttackBoss) {
								bossPatrolVisitedCorners = 0;
								bossPatrolLegStartedUtc = DateTime.MinValue;
							}
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
	private AutoFsEntity? PickReachableTarget(IReadOnlyList<AutoFsEntity> candidates, int mapId, int currentProcessId, int playerX, int playerY, Action<string>? currentTargetMovementLog) {
		DateTime now = DateTime.UtcNow;
		// A bypass episode keeps its monster; the stuck check below does not run on it until the episode ends.
		if (bypassTargetIndex >= 0) {
			AutoFsEntity? held = candidates.FirstOrDefault(candidate => candidate.Index == bypassTargetIndex);
			if (held != null) return held;
			EndWallBypass(currentProcessId, playerX, playerY, "QUÁI_MẤT_HOẶC_CHẾT", currentTargetMovementLog);
		}
		AutoFsEntity? target = null;
		unreachableZones.RemoveAll(zone => now >= zone.UntilUtc);
		foreach (AutoFsEntity candidate in candidates) {
			if (unreachableTargetsUntilUtc.TryGetValue(candidate.Index, out DateTime until)) {
				if (now >= until) unreachableTargetsUntilUtc.Remove(candidate.Index);
				// Ở chế độ chỉ quan sát, bảng này chỉ để KHỎI ghi lặp log, không dùng để loại mục tiêu.
				else if (BlacklistUnreachableTargets) continue;
			}
			if (BlacklistUnreachableTargets && unreachableZones.Any(zone => EliteAvoidance.Distance(candidate.RawX, candidate.RawY, zone.X, zone.Y) <= zone.Radius)) continue;
			if (BlacklistUnreachableTargets && StuckSpotStore.IsNearStoredSpot(mapId, candidate.RawX, candidate.RawY)) continue;
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
		// Chế độ "Chỉ đánh Boss": TẮT hẳn nhánh "kẹt -> đổi" bên dưới, theo yêu cầu chủ dự án 2026-09-29. Boss có kỹ
		// năng né/lùi/dịch chuyển nên không rút ngắn khoảng cách kịp trong StuckTargetMilliseconds dù không hề bị vật
		// cản — cơ chế này vốn thiết kế cho quái đứng sau tường lại hiểu nhầm thành "không tới được" và bỏ sang con
		// khác. Bằng chứng (auto-runtime.log 2026-09-29 11:36-11:40): nhiều dòng TARGET_UNREACHABLE_SKIPPED liên tiếp
		// cùng tên "Băng Linh" ở các Index khác nhau, TargetHp không đổi giữa các lần — chưa đánh trúng phát nào đã bị
		// đổi. Bám đúng 1 boss tới khi nó rời candidates (chết/ra khỏi tầm quét) chứ không tự bỏ vì "kẹt" nữa.
		if (settings.OnlyAttackBoss) {
			stuckTargetIndex = target.Index;
			stuckTargetHp = target.Hp;
			stuckBestDistance = target.Distance;
			stuckSinceUtc = now;
			return target;
		}
		// Inside the elite zone the retreat takes over right after this call (RunWorker), so standing still is not a sign
		// of a wall. Without this the stuck check started a wall bypass that the retreat aborted in the same millisecond:
		// 563 of 1106 WALL_BYPASS_START were ABORTED "TRỐN_BOSS" (auto-runtime.log 2026-10-13), each one using one of the
		// three episodes per 2 minutes, followed by 970 WALL_BYPASS_SKIPPED.
		if (settings.DoNotAttackBoss && settings.ElitePlayerRetreatRadius > 0 && eliteBuffer.Count > 0
			&& EliteAvoidance.IsNearAnyElite(playerX, playerY, EliteZones(eliteBuffer))) {
			stuckTargetIndex = -1;
			stuckBestDistance = double.MaxValue;
			return target;
		}
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

		if (BlacklistUnreachableTargets && TryStartWallBypass(target, currentProcessId, playerX, playerY, now, currentTargetMovementLog)) {
			stuckTargetIndex = -1;
			stuckBestDistance = double.MaxValue;
			return target;
		}
		unreachableTargetsUntilUtc[target.Index] = now.AddMilliseconds(UnreachableTargetCooldownMilliseconds);
		unreachableZones.Add((target.RawX, target.RawY, CellRawX, now.AddMilliseconds(UnreachableTargetCooldownMilliseconds)));
		string stuckAction = BlacklistUnreachableTargets ? "Bỏ qua con này và chọn con khác" : "CHỈ GHI LOG, vẫn đánh con này";
		currentTargetMovementLog?.Invoke($"TARGET_UNREACHABLE_SKIPPED | PID={currentProcessId} | Index={target.Index} | Name={target.Name} | Player={playerX}/{playerY} | Target={target.RawX}/{target.RawY} | Distance={target.Distance:F0} | GầnNhấtĐạtĐược={stuckBestDistance:F0} | TargetHp={target.Hp} | StuckMs={(int)(now - stuckSinceUtc).TotalMilliseconds} | CooldownMs={UnreachableTargetCooldownMilliseconds} | Action={stuckAction}");
		stuckTargetIndex = -1;
		stuckBestDistance = double.MaxValue;
		// Chế độ chỉ quan sát: giữ nguyên mục tiêu, chỉ ghi lại một dòng để về sau còn chỉnh ngưỡng bằng số thật.
		if (!BlacklistUnreachableTargets) return target;
		// Hết ứng viên thì nơi gọi rơi vào nhánh target == null -> TryWanderTrainingCorners đưa nhân vật đi chỗ khác.
		// Đó chính là bước "lùi lại", tái dùng đường đi sẵn có chứ không thêm luồng di chuyển mới.
		return PickReachableTarget(candidates, mapId, currentProcessId, playerX, playerY, currentTargetMovementLog);
	}

	// Starts a bypass episode on a monster the stuck check just flagged, unless the per-account cap is used up.
	private bool TryStartWallBypass(AutoFsEntity target, int currentProcessId, int playerX, int playerY, DateTime now, Action<string>? currentTargetMovementLog) {
		if (playerX <= 0 || playerY <= 0) return false;
		while (bypassEpisodeStartsUtc.Count > 0 && now - bypassEpisodeStartsUtc.Peek() > WallBypassEpisodeWindow) bypassEpisodeStartsUtc.Dequeue();
		if (bypassEpisodeStartsUtc.Count >= CurrentWallBypassMaximumEpisodes) {
			currentTargetMovementLog?.Invoke($"WALL_BYPASS_SKIPPED | PID={currentProcessId} | Index={target.Index} | Name={target.Name} | Player={playerX}/{playerY} | Target={target.RawX}/{target.RawY} | SốĐợt={bypassEpisodeStartsUtc.Count}/{CurrentWallBypassMaximumEpisodes} trong {WallBypassEpisodeWindow.TotalMinutes:F0} phút | Action=Không lách, bỏ qua con này như cũ");
			return false;
		}
		bypassEpisodeStartsUtc.Enqueue(now);
		bypassTargetIndex = target.Index;
		bypassStep = 0;
		bypassPhase = WallBypassPhase.NeedWalk;
		bypassAnchorX = playerX;
		bypassAnchorY = playerY;
		currentTargetMovementLog?.Invoke($"WALL_BYPASS_START | PID={currentProcessId} | Index={target.Index} | Name={target.Name} | Player={playerX}/{playerY} | Target={target.RawX}/{target.RawY} | Distance={target.Distance:F0} | Đợt={bypassEpisodeStartsUtc.Count}/{CurrentWallBypassMaximumEpisodes}");
		return true;
	}

	// Runs the episode on the worker's scan. Returns true while the character is walking a side step (no attack command
	// this scan); false while probing, so the caller sends the normal attack command at the monster.
	private bool HoldForWallBypass(GameWindow currentGame, int currentProcessId, AutoFsEntity target, int playerX, int playerY, Action<string>? currentTargetMovementLog) {
		DateTime now = DateTime.UtcNow;
		if (bypassPhase == WallBypassPhase.NeedWalk) return StartWallBypassStep(currentGame, currentProcessId, target, playerX, playerY, now, currentTargetMovementLog);
		if (bypassPhase == WallBypassPhase.Walking) {
			double toStep = EliteAvoidance.Distance(playerX, playerY, bypassStepX, bypassStepY);
			if (toStep > WallBypassArrivalRaw && now < bypassPhaseUntilUtc) return true;
			bypassPhase = WallBypassPhase.Probing;
			bypassPhaseUntilUtc = now.AddMilliseconds(StuckTargetMilliseconds);
			bypassProbeStartDistance = target.Distance;
			bypassProbeStartHp = target.Hp;
			currentTargetMovementLog?.Invoke($"WALL_BYPASS_STEP_DONE | PID={currentProcessId} | Bước={bypassStep + 1}/{WallBypassSteps} | {(toStep <= WallBypassArrivalRaw ? "TỚI_NƠI" : "HẾT_GIỜ")} | Player={playerX}/{playerY} | Đích={bypassStepX}/{bypassStepY} | CònCáchĐích={toStep:F0} | CáchQuái={target.Distance:F0} | Action=Thử đánh lại con này");
			return false;
		}
		// Probing: the attack command goes out as usual. Only reaching the monster or its HP moving means the way is open.
		// Closing in by 60 raw used to count as well, but walking back from a side step always closes in that much before
		// the same wall stops it again: XinLỗiEm 2026-10-13 23:50:19-23:50:33 had two such "SUCCEEDED" (192->159,
		// 475->413) on one monster behind a wall, each restarting the 1.5 s stuck check, 14 s in all. Closing in now only
		// extends the probe.
		if (target.Distance <= bypassProbeStartDistance - StuckDistanceProgressRaw && target.Distance > StuckArrivalDistanceRaw) {
			bypassProbeStartDistance = target.Distance;
			bypassPhaseUntilUtc = now.AddMilliseconds(StuckTargetMilliseconds);
			return false;
		}
		bool progressed = target.Distance <= StuckArrivalDistanceRaw || target.Hp != bypassProbeStartHp;
		if (progressed) {
			currentTargetMovementLog?.Invoke($"WALL_BYPASS_SUCCEEDED | PID={currentProcessId} | Index={target.Index} | Name={target.Name} | Bước={bypassStep + 1}/{WallBypassSteps} | Player={playerX}/{playerY} | CáchQuái={bypassProbeStartDistance:F0}->{target.Distance:F0} | HP={bypassProbeStartHp}->{target.Hp}");
			bypassTargetIndex = -1;
			// Fresh baseline for the normal stuck check.
			stuckTargetIndex = target.Index;
			stuckTargetHp = target.Hp;
			stuckBestDistance = target.Distance;
			stuckSinceUtc = now;
			return false;
		}
		if (now < bypassPhaseUntilUtc) return false;
		bypassStep++;
		if (bypassStep >= WallBypassSteps) {
			unreachableTargetsUntilUtc[target.Index] = now.AddMilliseconds(UnreachableTargetCooldownMilliseconds);
			unreachableZones.Add((target.RawX, target.RawY, CellRawX, now.AddMilliseconds(UnreachableTargetCooldownMilliseconds)));
			int mapId = currentGame.LastObservedMapId;
			string stored = StuckSpotStore.Add(mapId, target.RawX, target.RawY) ? $"ĐÃ_LƯU config.ini Map{mapId}" : mapId <= 0 ? "KHÔNG_LƯU(chưa biết map)" : $"ĐÃ_CÓ_TRONG config.ini Map{mapId}";
			currentTargetMovementLog?.Invoke($"WALL_BYPASS_GAVE_UP | PID={currentProcessId} | Index={target.Index} | Name={target.Name} | Player={playerX}/{playerY} | ĐiểmKẹt={bypassAnchorX}/{bypassAnchorY} | Quái={target.RawX}/{target.RawY} | CáchQuái={target.Distance:F0} | ChỗKẹt={stored} | Action=Hết {WallBypassSteps} bước, bỏ qua con này và mọi con quanh chỗ này");
			bypassTargetIndex = -1;
			stuckTargetIndex = -1;
			stuckBestDistance = double.MaxValue;
			return true;
		}
		return StartWallBypassStep(currentGame, currentProcessId, target, playerX, playerY, now, currentTargetMovementLog);
	}

	// One side step: left/right alternating, 1..3 cells from the stuck point, perpendicular to the line to the monster.
	// Same move as the boss retreat: switch the client's own attack order off first (it overrides a walk), then the
	// unflagged walk 0/321. Never the flagged route inside the training area (owner rule 2026-10-11).
	private bool StartWallBypassStep(GameWindow currentGame, int currentProcessId, AutoFsEntity target, int playerX, int playerY, DateTime now, Action<string>? currentTargetMovementLog) {
		bool left = bypassStep % 2 == 0;
		int cells = bypassStep / 2 + 1;
		double towardX = (double)(target.RawX - bypassAnchorX) / CellRawX;
		double towardY = (double)(target.RawY - bypassAnchorY) / CellRawY;
		double length = Math.Sqrt(towardX * towardX + towardY * towardY);
		if (length <= 0) {
			EndWallBypass(currentProcessId, playerX, playerY, "TRÙNG_TOẠ_ĐỘ_QUÁI", currentTargetMovementLog);
			return false;
		}
		double sideX = left ? -towardY / length : towardY / length;
		double sideY = left ? towardX / length : -towardX / length;
		bypassStepX = bypassAnchorX + (int)Math.Round(sideX * cells * CellRawX);
		bypassStepY = bypassAnchorY + (int)Math.Round(sideY * cells * CellRawY);
		string clientMoveState = ReadClientMoveState(currentGame);
		string cancelAttack = ClientAttackCanceller.Cancel(currentGame);
		bool walked = AutoFsMovementCommand.TryWalkTo(currentGame, bypassStepX, bypassStepY, out string walkResult);
		bypassPhase = WallBypassPhase.Walking;
		bypassPhaseUntilUtc = now.AddMilliseconds(WallBypassWalkTimeoutMilliseconds);
		currentTargetMovementLog?.Invoke($"WALL_BYPASS_STEP | PID={currentProcessId} | Bước={bypassStep + 1}/{WallBypassSteps} | Hướng={(left ? "TRÁI" : "PHẢI")} | Ô={cells} | Player={playerX}/{playerY} | ĐiểmKẹt={bypassAnchorX}/{bypassAnchorY} | Đích={bypassStepX}/{bypassStepY} | Quái={target.RawX}/{target.RawY} | {clientMoveState} | {cancelAttack} | Walked={walked} | {walkResult}");
		return true;
	}

	// Pet position lag (owner 2026-10-13): the pet's damage stops reaching the monster when its position desyncs, and the
	// owner's own damage is small. The other causes the owner named: the pet is dead, or a wall stands between the pet
	// and the monster. A monster still alive PetLagAttackMilliseconds after the character reached it means the spot is
	// bad, not the monster (owner 2026-10-14: a whole cluster there can have the position error), so every monster within
	// PetLagClusterRadiusRaw of it is skipped for a while and the next pick lies elsewhere: the character walks there.
	// Fixed 10 s instead of the per-account learned threshold: that one sank to its 3000 ms floor on most accounts
	// (auto-runtime.log 2026-10-13 23:37-00:03), because slow kills were stepped away from and so never sampled, and it
	// then fired on monsters that were losing HP normally (TiểuHồngĐơn Index=6: 3053 HP at 3.1 s, 1006 at 7.2 s).
	private const int PetLagAttackMilliseconds = 10000;
	// Three X cells (1.5 Y cells) around the monster, for the same 20 s as an unreachable monster.
	private const int PetLagClusterRadiusRaw = 3 * CellRawX;
	private const int PetLagKillSamples = 30;
	// A kill counts only when the target was still being fought this recently before the next target came up; longer gaps
	// mean a boss retreat, a wall bypass or no target in between, so the time would not be a clean kill time.
	private const int PetLagKillGapMilliseconds = 1500;
	private const int PetLagStatsEveryKills = 20;
	private int petLagTargetIndex = -1;
	private DateTime petLagEngagedUtc = DateTime.MinValue;
	private DateTime petLagLastSeenUtc = DateTime.MinValue;
	private readonly Queue<int> petLagKillMilliseconds = new();
	private int petLagKillCount;

	// Called when the target changes: logs this account's kill times, for reference only.
	private void NotePetLagKill(int currentProcessId, DateTime now, Action<string>? currentTargetMovementLog) {
		if (petLagTargetIndex < 0 || petLagEngagedUtc == DateTime.MinValue) return;
		if ((now - petLagLastSeenUtc).TotalMilliseconds > PetLagKillGapMilliseconds) return;
		if (petLagKillMilliseconds.Count >= PetLagKillSamples) petLagKillMilliseconds.Dequeue();
		petLagKillMilliseconds.Enqueue((int)(petLagLastSeenUtc - petLagEngagedUtc).TotalMilliseconds);
		petLagKillCount++;
		if (petLagKillCount % PetLagStatsEveryKills != 0) return;
		int[] sorted = petLagKillMilliseconds.Order().ToArray();
		currentTargetMovementLog?.Invoke($"PET_LAG_KILL_STATS | PID={currentProcessId} | SốConĐãTính={petLagKillCount} | Mẫu={sorted.Length} | NhanhNhất={sorted[0]} | TrungVị={sorted[sorted.Length / 2]} | ChậmNhất={sorted[^1]}");
	}

	// true = the cluster was given up this tick; no attack order is sent.
	private bool TryLeavePetLagCluster(GameWindow currentGame, int currentProcessId, AutoFsEntity target, int playerX, int playerY, Action<string>? currentTargetMovementLog) {
		DateTime now = DateTime.UtcNow;
		if (target.Index != petLagTargetIndex) {
			NotePetLagKill(currentProcessId, now, currentTargetMovementLog);
			petLagTargetIndex = target.Index;
			petLagEngagedUtc = DateTime.MinValue;
		}
		if (settings.OnlyAttackBoss || target.Hp <= 0 || playerX <= 0 || playerY <= 0) return false;
		// The clock starts once the character stands next to the monster, so walking there does not count.
		if (petLagEngagedUtc == DateTime.MinValue) {
			if (target.Distance <= StuckArrivalDistanceRaw) petLagEngagedUtc = now;
			petLagLastSeenUtc = now;
			return false;
		}
		petLagLastSeenUtc = now;
		if ((now - petLagEngagedUtc).TotalMilliseconds < PetLagAttackMilliseconds) return false;
		if (settings.DoNotAttackBoss && settings.ElitePlayerRetreatRadius > 0 && eliteBuffer.Count > 0
			&& EliteAvoidance.IsNearAnyElite(playerX, playerY, EliteZones(eliteBuffer))) return false;
		Auto.Support.PetHealthReading pet = Auto.Support.PetHealthReader.Read(currentProcessId);
		string petState = ! pet.Success ? "ĐệHp=?" : ! pet.Present ? "ĐệHp=KHÔNG_CÓ" : $"ĐệHp={pet.CurrentHp}/{pet.MaximumHp} | ĐệRaw={pet.RawX}/{pet.RawY}";
		DateTime until = now.AddMilliseconds(UnreachableTargetCooldownMilliseconds);
		unreachableTargetsUntilUtc[target.Index] = until;
		unreachableZones.Add((target.RawX, target.RawY, PetLagClusterRadiusRaw, until));
		string cancelAttack = ClientAttackCanceller.Cancel(currentGame);
		currentTargetMovementLog?.Invoke($"PET_LAG_LEAVE_CLUSTER | PID={currentProcessId} | Index={target.Index} | Name={target.Name} | TargetHp={target.Hp} | ĐánhMs={(int)(now - petLagEngagedUtc).TotalMilliseconds} | Player={playerX}/{playerY} | Quái={target.RawX}/{target.RawY} | BánKính={PetLagClusterRadiusRaw} | {petState} | {cancelAttack} | Action=Bỏ cụm quái quanh đây {UnreachableTargetCooldownMilliseconds}ms, sang chỗ khác");
		// Forget the monster: when it comes back after the cooldown its clock starts over.
		petLagTargetIndex = -1;
		return true;
	}

	private void EndWallBypass(int currentProcessId, int playerX, int playerY, string reason, Action<string>? currentTargetMovementLog) {
		currentTargetMovementLog?.Invoke($"WALL_BYPASS_ABORTED | PID={currentProcessId} | Index={bypassTargetIndex} | Bước={bypassStep + 1}/{WallBypassSteps} | Player={playerX}/{playerY} | LýDo={reason}");
		bypassTargetIndex = -1;
	}

	private bool TryRetreatFromElites(GameWindow currentGame, int currentProcessId, int playerX, int playerY, int playerLifecycleStatus, Action<string>? currentTargetMovementLog) {
		retreatExitGate = "";
		bool retreating = TryRetreatFromElitesCore(currentGame, currentProcessId, playerX, playerY, playerLifecycleStatus, currentTargetMovementLog);
		// CHẨN ĐOÁN 2026-09-22: movement.log PID=24236 MaiAnhNhe 09:21-09:22 có MỌI dòng ELITE_RETREAT mang
		// CònCáchĐíchCũ="chưa có đích", tức đích chốt ở dòng "eliteWalkDestinationX = destinationX" bên dưới bị xoá
		// giữa hai lần trốn cách nhau chỉ 3-6 giây (nhịp quét 100ms nên có ~35 lượt ở giữa). Hệ quả: luật "chốt đích,
		// đi cho hết chỗ đó" chưa bao giờ chạy, mỗi lượt lại bắn một đích mới tính từ chỗ đang đứng, và nhân vật
		// đứng nguyên ở ~62265/91411 trong suốt đoạn log.
		//
		// Chỉ ghi khi ĐANG GIỮ một đích rồi mới bị xoá — đó mới là trường hợp làm mất luật chốt đích. Ghi một dòng
		// cho mỗi lần như vậy để biết CỔNG NÀO của Core trả false, thay vì tiếp tục đoán.
		if (! retreating && eliteWalkDestinationX > 0 && eliteWalkDestinationY > 0) {
			currentTargetMovementLog?.Invoke($"ELITE_RETREAT_DESTINATION_DROPPED | PID={currentProcessId} | Player={playerX}/{playerY} | ĐíchĐangGiữ={eliteWalkDestinationX}/{eliteWalkDestinationY} | CònCách={EliteAvoidance.Distance(playerX, playerY, eliteWalkDestinationX, eliteWalkDestinationY):F0} | CổngThoát={retreatExitGate}");
		}
		// HẾT ĐỢT TRỐN THÌ PHẢI BỎ ĐÍCH ĐANG GIỮ.
		//
		// Đích trốn chỉ được xoá ở khối khởi động lại worker, không bao giờ xoá khi đợt trốn kết thúc bình thường
		// (nhánh "không còn đứng gần thủ lĩnh nào" của TryRetreatFromElitesCore trả false). Nên đợt trốn KẾ TIẾP —
		// có khi nhiều phút sau, từ một chỗ hoàn toàn khác — vẫn rơi vào nhánh "giữ đích" và còn ghi đè đích vừa
		// tính bằng cái đích cũ đó.
		//
		// Đo trên Release/Diagnostics/movement.log, phiên 11:48-15:00 ngày 2026-09-17, 3198 dòng ELITE_RETREAT:
		//   1728 cặp liên tiếp dùng CÙNG một đích; 824 cặp (48%) trong số đó cách nhau hơn 10 giây, tức là đợt trốn
		//   mới dùng lại đích của đợt cũ. Cặp xa nhất cách 586 giây: PID 36320, 12:09:22 -> 12:19:08, đích giữ
		//   nguyên 62717/91215 trong khi nhân vật đã đi từ 63487/90156 sang 62893/90722.
		//   Hậu quả: 0/1728 lần nhân vật vào được trong 128 raw của đích (ngưỡng EliteRetreatArrivalRaw), và 57%
		//   số lần nó còn XA đích hơn lần trước (lệch trung bình +315 raw mỗi lần gửi lại).
		if (! retreating) {
			eliteWalkDestinationX = 0;
			eliteWalkDestinationY = 0;
			eliteWalkObservedX = 0;
			eliteWalkObservedY = 0;
			eliteWalkProgressUtc = DateTime.MinValue;
			eliteWalkCommittedUtc = DateTime.MinValue;
			// The blocked directions are kept: they belong to the stuck spot, not to this retreat. A boss stepping past the
			// exit radius for a moment ended the retreat and wiped them, and the next retreat hit the same wall again
			// (PhâyKer 2026-10-13 22:18:10 dropped at 900 > 896). They go once the character is two cells away or after
			// EliteBlockedDirectionLifetime (TryRetreatFromElitesCore).
		}
		retreatingFromElite = retreating;
		return retreating;
	}

	private bool TryRetreatFromElitesCore(GameWindow currentGame, int currentProcessId, int playerX, int playerY, int playerLifecycleStatus, Action<string>? currentTargetMovementLog) {
		// retreatExitGate: tên cổng làm hàm này trả false, để TryRetreatFromElites ghi được lý do đích bị xoá.
		if (!settings.DoNotAttackBoss || settings.ElitePlayerRetreatRadius <= 0 || eliteBuffer.Count == 0) {
			retreatExitGate = $"CÀI_ĐẶT_HOẶC_BẢNG_RỖNG (KhôngĐánhBoss={settings.DoNotAttackBoss}, BánKính={settings.ElitePlayerRetreatRadius}, SốThủLĩnhQuétĐược={eliteBuffer.Count})";
			return false;
		}
		if (playerX <= 0 || playerY <= 0) {
			retreatExitGate = $"KHÔNG_ĐỌC_ĐƯỢC_VỊ_TRÍ ({playerX}/{playerY})";
			return false;
		}
		// No "around point" gate any more: with "Quanh điểm" dropped because bosses surround the area (owner 2026-10-13) the
		// character still has to run from them. Without a training area the destination is not held inside any field.
		bool aroundPoint = settings.TrainingEnabled || settings.TeachingEnabled || settings.ContinueEnabled || settings.UseCenterPosition;
		if (currentGame.ReturnToTrainingAutomation.IsBusy || currentGame.ConfiguredTrainingMovementAutomation.IsBusy || currentGame.WeaponRepairAutomation.IsBusy) {
			retreatExitGate = $"LUỒNG_KHÁC_ĐANG_CHẠY (LênBãi={currentGame.ReturnToTrainingAutomation.IsBusy}, ĐiBãiCấuHình={currentGame.ConfiguredTrainingMovementAutomation.IsBusy}, SửaĐồ={currentGame.WeaponRepairAutomation.IsBusy})";
			return false;
		}

		List<(int X, int Y, int Radius)> elites = EliteZones(eliteBuffer);
		// Exit with a margin while a destination is held: leave only at radius + half the margin, or once the held
		// destination is reached outside the radius. With one threshold for entering and leaving, 1đêm2nháy 2026-10-13
		// 18:20:07-18:20:19 dropped the destination at 769-776 and was back inside 0.7-1.5 s later, six times in a row;
		// 1283 of 1737 re-entries (74%) after the 16:46 build came within 5 s.
		// Clearance = distance minus that elite's own radius (3 cells for a boss, 1 cell for an enhanced monster).
		double nearestClearance = EliteAvoidance.NearestEliteClearance(playerX, playerY, elites);
		bool holdingDestination = eliteWalkDestinationX > 0 && eliteWalkDestinationY > 0;
		bool reachedHeldDestination = holdingDestination
			&& EliteAvoidance.Distance(playerX, playerY, eliteWalkDestinationX, eliteWalkDestinationY) <= EliteRetreatArrivalRaw;
		int exitClearance = holdingDestination && ! reachedHeldDestination ? EliteRetreatMarginRaw / 2 : 0;
		if (nearestClearance > exitClearance) {
			retreatExitGate = $"ĐÃ_RA_KHỎI_VÙNG_CẤM (khoảngHở={nearestClearance:F0} > ngưỡngThoát={exitClearance})";
			return false;
		}

		// Đã trong vùng cấm: từ đây trở đi luôn chiếm lượt, kể cả khi chưa tới hạn đi hay không tìm được góc sạch.
		// Nếu trả false ở các nhánh dưới thì nơi gọi sẽ quay ra đánh chính con quái cạnh thủ lĩnh.
		(int centerX, int centerY) = AutoFsEntityScanner.GetCenter(settings, playerX, playerY);
		if (centerX <= 0 || centerY <= 0) return true;
		if (playerLifecycleStatus == AutoFsBusyLifecycleStatus) return true;

		// Range 0 = no field to stay inside (EliteAvoidance never calls a candidate "leaving the field").
		int range = aroundPoint ? Math.Max(settings.Range, 1) : 0;
		// BỎ HẲN 4 góc cố định cho luồng tránh boss (chủ dự án chốt 2026-09-11). Đích trốn giờ tính theo KHOẢNG CÁCH:
		// đẩy nhân vật ra tới khi cách mọi thủ lĩnh quá radius + margin, hướng đẩy là tổng vector đẩy từ từng con.
		//
		// 4 góc nằm ở tâm ± Range/3, chỉ cách tâm 745 raw và không liên quan gì tới chỗ boss đứng, nên "chạy trốn"
		// hoá ra là đi tới 1 trong 4 chỗ có sẵn — có thể còn gần boss hơn chỗ đang đứng. Boss đứng gần tâm thì cả 4
		// góc đều bẩn và nhân vật đứng chết: movement.log 2026-09-11 PID=34032 có bốn dòng ELITE_NO_SAFE_CORNER lúc
		// 14:50:38 / 14:50:48 / 14:50:58 / 14:51:09 đều cùng một toạ độ Player=63631/90321.
		// Luật mới không có trạng thái "không tìm được chỗ": boss đuổi tới thì lượt quét sau cho ra đích xa hơn.
		// Two cells away from where the last wall was met the character is out of that pocket (owner 2026-10-13: once unstuck,
		// move on elsewhere): the blocked directions were measured from the old spot, so drop them and retreat normally.
		bool blockedExpired = DateTime.UtcNow - eliteBlockedAtUtc > EliteBlockedDirectionLifetime;
		if (eliteBlockedDirections.Count > 0 && (blockedExpired || EliteAvoidance.Distance(playerX, playerY, eliteBlockedAtX, eliteBlockedAtY) >= 2 * CellRawX)) {
			currentTargetMovementLog?.Invoke($"ELITE_RETREAT_UNSTUCK | PID={currentProcessId} | Player={playerX}/{playerY} | ChỗBịKẹt={eliteBlockedAtX}/{eliteBlockedAtY} | SốHướngĐãXoá={eliteBlockedDirections.Count} | LýDo={(blockedExpired ? "HẾT_HẠN" : "ĐÃ_RA_XA_2_Ô")}");
			ClearEliteBlockedDirection();
		}
		if (!EliteAvoidance.TryGetRetreatDestination(playerX, playerY, centerX, centerY, range, elites,
			EliteRetreatMarginRaw, out int destinationX, out int destinationY, out _, eliteBlockedDirections)) {
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
		// Lý do bắn lại + còn cách đích bao nhiêu. Thiếu đúng hai số này nên đêm 2026-09-18 không kết luận được vì sao
		// 5615 lệnh trốn chỉ 73 lần (1,3%) vào được trong 128 raw của đích, ở MỌI quãng đường: 0-199 raw đạt 8,3%,
		// 200-399 raw đạt 1,4%, 1800-1999 raw đạt 3,4% — không hề có tương quan với quãng đường, nên không phải
		// giới hạn tầm của lệnh 321. Log hiện tại không ghi gì giữa hai lệnh trốn nên không thấy được cái gì kéo
		// nhân vật đi: lúc 10:15:00 gửi đích 62187/89939 (X giảm), 4,6 giây sau nhân vật ở 62746/89763 tức đã đi
		// NGƯỢC 410 raw. Hai trường này cho biết lần bắn lại là do đích bẩn, do tới nơi, hay do đứng im.
		string retreatReason = "ĐÍCH_MỚI";
		double retreatRemaining = -1;
		if (eliteWalkDestinationX > 0 && eliteWalkDestinationY > 0) {
			retreatRemaining = EliteAvoidance.Distance(playerX, playerY, eliteWalkDestinationX, eliteWalkDestinationY);
			// Progress = moved StuckDistanceProgressRaw from the last progress point, as for monsters. Any coordinate change
			// used to count, and a character turning in place against a wall changes coordinates all the time.
			if (EliteAvoidance.Distance(playerX, playerY, eliteWalkObservedX, eliteWalkObservedY) >= StuckDistanceProgressRaw) {
				eliteWalkObservedX = playerX;
				eliteWalkObservedY = playerY;
				eliteWalkProgressUtc = now;
			}
			bool committedDestinationDirty = EliteAvoidance.IsNearAnyElite(eliteWalkDestinationX, eliteWalkDestinationY, elites);
			bool arrived = EliteAvoidance.Distance(playerX, playerY, eliteWalkDestinationX, eliteWalkDestinationY) <= EliteRetreatArrivalRaw;
			bool stalled = now - eliteWalkProgressUtc >= TimeSpan.FromMilliseconds(EliteWalkStallMilliseconds);
			// Chỉ huỷ vì "bẩn" nếu đích đã được giữ đủ EliteRetreatMinimumHoldMilliseconds — chặn vòng lặp bắn lại
			// liên tục khi nhiều thủ lĩnh xoay quanh sát đích (mỗi lượt gửi lại cách nhau 700ms đều thấy dirty=true).
			bool destinationDirtyAndReady = committedDestinationDirty
				&& now - eliteWalkCommittedUtc >= TimeSpan.FromMilliseconds(EliteRetreatMinimumHoldMilliseconds);
			if (!destinationDirtyAndReady && !arrived && !stalled) return true;
			// Stalled is checked first: with the boss closing in every resend was "ĐÍCH_BẨN", which skipped the wall handling
			// below, and PhâyKer 2026-10-13 22:18:42-52 got four destinations into the same south-west wall without moving
			// from 62881/100134 while the boss came from 750 to 381.
			retreatReason = stalled ? "ĐỨNG_IM" : destinationDirtyAndReady ? "ĐÍCH_BẨN" : "ĐÃ_TỚI";
			// Còn đi dở tới một đích vẫn sạch (hoặc mới bẩn nhưng chưa đủ thời gian giữ) mà phải bắn lại vì đứng im:
			// giữ nguyên đích cũ thay vì lấy đích mới đã trôi theo nhân vật, để lệnh thứ hai là đi tiếp đúng chỗ cũ
			// chứ không phải đổi hướng giữa đường.
			if (retreatReason == "ĐỨNG_IM") {
				// The held destination did not move the character: that way is walled off. Turn away at once (owner
				// 2026-10-13: never stand still near a boss) instead of resending the same destination.
				(destinationX, destinationY) = PickRetreatAroundWall(currentProcessId, playerX, playerY, centerX, centerY, range, elites, currentTargetMovementLog);
			} else if (!destinationDirtyAndReady && !arrived) {
				destinationX = eliteWalkDestinationX;
				destinationY = eliteWalkDestinationY;
			}
		}
		// A fresh destination that points into a known wall means EliteAvoidance found no opening and fell back: keep stepping
		// out through the most open side (boss side included) instead of walking back into the wall and stalling next to the
		// boss again.
		if (retreatReason != "ĐỨNG_IM" && eliteBlockedDirections.Count > 0 && IsTowardBlockedDirection(playerX, playerY, destinationX, destinationY)) {
			(destinationX, destinationY) = StepTowardOpenDirection(currentProcessId, playerX, playerY, destinationX, destinationY, elites, currentTargetMovementLog);
		}
		// Read BEFORE resending: shows what the client did with the previous retreat command. Equal to the held destination
		// = client accepted it but is not walking; 135/-1 = the client is casting skill 135 at its attack target (measured
		// 2026-10-06, not an idle sentinel as noted on 2026-10-01); another point = something cancelled or overwrote it.
		string clientMoveState = ReadClientMoveState(currentGame);
		// The client keeps hitting the monster command 300 handed it and the walk alone does not stop that; switch the
		// attack order off first so the retreat starts at once, whatever the character was doing.
		string cancelAttack = ClientAttackCanceller.Cancel(currentGame);
		if (!AutoFsMovementCommand.TryWalkTo(currentGame, destinationX, destinationY, out string walkResult)) {
			if (!string.Equals(lastWalkFailure, walkResult, StringComparison.Ordinal)) {
				lastWalkFailure = walkResult;
				currentTargetMovementLog?.Invoke($"ELITE_RETREAT FAIL | PID={currentProcessId} | Player={playerX}/{playerY} | Destination={destinationX}/{destinationY} | {walkResult}");
			}
			return true;
		}
		lastWalkFailure = "";
		nextOutsideAreaWalkUtc = now.AddMilliseconds(OutsideAreaWalkIntervalMilliseconds);
		// Chỉ khởi động lại đồng hồ giữ đích khi đích THẬT SỰ đổi toạ độ — resend cùng một đích (nhánh ĐỨNG_IM) thì
		// đồng hồ giữ nguyên, không thì mỗi lần bắn lại vì đứng im lại tự gia hạn thời gian miễn nhiễm với "bẩn".
		if (destinationX != eliteWalkDestinationX || destinationY != eliteWalkDestinationY) eliteWalkCommittedUtc = now;
		eliteWalkDestinationX = destinationX;
		eliteWalkDestinationY = destinationY;
		// A "dirty" or "arrived" resend keeps the stall clock running: restarting it on every resend is what let the boss
		// keep the character against the wall, since a dirty resend came every 2-4 s and never reached the stall limit.
		if (retreatReason is "ĐÍCH_MỚI" or "ĐỨNG_IM") {
			eliteWalkObservedX = playerX;
			eliteWalkObservedY = playerY;
			eliteWalkProgressUtc = now;
		}
		(int nearestX, int nearestY, int nearestRadius) = elites.OrderBy(elite => EliteAvoidance.Distance(playerX, playerY, elite.X, elite.Y) - elite.Radius).First();
		currentTargetMovementLog?.Invoke($"ELITE_RETREAT | PID={currentProcessId} | Player={playerX}/{playerY} | Elite={nearestX}/{nearestY} | Distance={EliteAvoidance.Distance(playerX, playerY, nearestX, nearestY):F0} | Radius={nearestRadius} | KhoảngHở={EliteAvoidance.NearestEliteClearance(playerX, playerY, elites):F0} | SauKhiTới={EliteAvoidance.NearestEliteDistance(destinationX, destinationY, elites):F0} | KhoảngHởSauKhiTới={EliteAvoidance.NearestEliteClearance(destinationX, destinationY, elites):F0} | Destination={destinationX}/{destinationY} | LýDoBắn={retreatReason} | CònCáchĐíchCũ={(retreatRemaining < 0 ? "chưa có đích" : retreatRemaining.ToString("F0"))} | {clientMoveState} | {cancelAttack} | {walkResult}");
		// Diagnostic only. XinLỗiEm 2026-10-14 00:28:42-00:30:27 stood on 62355/88219 for 100 s next to a boss and died:
		// every retreat walk was CONFIRMED, D7A0 was already 0 (HuỷĐánh=KHÔNG_CẦN), yet the client destination stayed on
		// 135/-1 then 454/-1 (skill cast state) and the position did not change by 1 raw. Which client field holds that
		// lock is unknown, so dump the player record around the attack and movement fields: once on a fresh retreat as the
		// baseline, and on every stalled resend while the destination shows the cast state.
		if (retreatReason == "ĐÍCH_MỚI" || (retreatReason == "ĐỨNG_IM" && clientMoveState.EndsWith("/-1", StringComparison.Ordinal))) {
			currentTargetMovementLog?.Invoke($"CLIENT_CAST_LOCK_DUMP | PID={currentProcessId} | LýDoBắn={retreatReason} | Player={playerX}/{playerY} | {clientMoveState} | {DumpPlayerLockFields(currentGame)}");
		}
		return true;
	}

	private static string DumpPlayerLockFields(GameWindow game) {
		try {
			RuntimeLayout layout = game.RuntimeLayout;
			using MemoryReader reader = new(game.ProcessId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr entityTable = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			if (entityTable == IntPtr.Zero) return "Dump=KHÔNG_ĐỌC_ĐƯỢC";
			IntPtr player = IntPtr.Add(entityTable, layout.PlayerRecordOffset);
			string Hex(int offset, int size) => $"+0x{offset:X}={Convert.ToHexString(reader.ReadBytes(IntPtr.Add(player, offset), size))}";
			return $"{Hex(0x42C0, 0x40)} | {Hex(0xD780, 0x40)} | {Hex(0x2CD0, 0x20)}";
		} catch (Exception ex) {
			return $"Dump=LỖI({ex.GetType().Name})";
		}
	}

	// Adds the walled-off direction to the blocked list and asks EliteAvoidance for a destination outside 45 degrees of every
	// blocked direction. When every direction is blocked it steps 1, 2, 3, 3, 3, 3 cells toward the most open direction,
	// boss side included, then clears the list and starts over.
	private (int X, int Y) PickRetreatAroundWall(int currentProcessId, int playerX, int playerY, int centerX, int centerY, int range,
		List<(int X, int Y, int Radius)> elites, Action<string>? currentTargetMovementLog) {
		int blockedX = eliteWalkDestinationX;
		int blockedY = eliteWalkDestinationY;
		if (eliteBlockedDirections.Count >= EliteMaximumBlockedDirections) eliteBlockedDirections.RemoveAt(0);
		eliteBlockedDirections.Add((blockedX - (double)playerX, (blockedY - (double)playerY) * EliteAvoidance.RawYToRawXScale));
		eliteBlockedAtX = playerX;
		eliteBlockedAtY = playerY;
		eliteBlockedAtUtc = DateTime.UtcNow;
		if (EliteAvoidance.TryGetRetreatDestination(playerX, playerY, centerX, centerY, range, elites,
				EliteRetreatMarginRaw, out int otherX, out int otherY, out _, eliteBlockedDirections)
			&& ! IsTowardBlockedDirection(playerX, playerY, otherX, otherY)) {
			currentTargetMovementLog?.Invoke($"ELITE_RETREAT_BLOCKED_DIRECTION | PID={currentProcessId} | Player={playerX}/{playerY} | ĐíchBịChặn={blockedX}/{blockedY} | ĐíchMới={otherX}/{otherY} | SốHướngBịChặn={eliteBlockedDirections.Count}");
			return (otherX, otherY);
		}
		return StepTowardOpenDirection(currentProcessId, playerX, playerY, blockedX, blockedY, elites, currentTargetMovementLog);
	}

	// Getting unstuck comes before avoiding the boss (owner 2026-10-13): walls on three sides and the boss on the fourth
	// means walking out past the boss. Pick the one of 16 directions farthest from every blocked direction, even when it
	// points at the boss; the next stall adds it to the list, so each step tries a fresh opening.
	private (int X, int Y) StepTowardOpenDirection(int currentProcessId, int playerX, int playerY, int blockedX, int blockedY,
		List<(int X, int Y, int Radius)> elites, Action<string>? currentTargetMovementLog) {
		double openX = 1;
		double openY = 0;
		double openWorstCosine = double.PositiveInfinity;
		for (int index = 0; index < 16; index++) {
			double angle = index * Math.PI / 8;
			double unitX = Math.Cos(angle);
			double unitY = Math.Sin(angle);
			double worstCosine = -1;
			foreach ((double directionX, double directionY) in eliteBlockedDirections) {
				double directionLength = Math.Sqrt(directionX * directionX + directionY * directionY);
				if (directionLength >= 1e-6) worstCosine = Math.Max(worstCosine, (unitX * directionX + unitY * directionY) / directionLength);
			}
			if (worstCosine < openWorstCosine) {
				openWorstCosine = worstCosine;
				openX = unitX;
				openY = unitY;
			}
		}
		int cells = Math.Min(eliteSideStepCount + 1, 3);
		// Scaled space; one cell is 256 scaled units on both axes (256 raw X, 512 raw Y).
		int stepX = playerX + (int)Math.Round(openX * cells * CellRawX);
		int stepY = playerY + (int)Math.Round(openY * cells * CellRawX / EliteAvoidance.RawYToRawXScale);
		eliteSideStepCount++;
		currentTargetMovementLog?.Invoke($"ELITE_RETREAT_SIDE_STEP | PID={currentProcessId} | Player={playerX}/{playerY} | ĐíchBịChặn={blockedX}/{blockedY} | Ô={cells} | Góc={Math.Atan2(openY, openX) * 180 / Math.PI:F0} | KhoảngCáchBossTạiĐích={EliteAvoidance.NearestEliteDistance(stepX, stepY, elites):F0} | Đích={stepX}/{stepY} | SốHướngBịChặn={eliteBlockedDirections.Count} | Lần={eliteSideStepCount}/{EliteMaximumSideSteps}");
		if (eliteSideStepCount >= EliteMaximumSideSteps) ClearEliteBlockedDirection();
		return (stepX, stepY);
	}

	private bool IsTowardBlockedDirection(int playerX, int playerY, int destinationX, int destinationY) {
		double towardX = destinationX - (double)playerX;
		double towardY = (destinationY - (double)playerY) * EliteAvoidance.RawYToRawXScale;
		double towardLength = Math.Sqrt(towardX * towardX + towardY * towardY);
		if (towardLength < 1) return true;
		foreach ((double blockedX, double blockedY) in eliteBlockedDirections) {
			double blockedLength = Math.Sqrt(blockedX * blockedX + blockedY * blockedY);
			if (blockedLength < 1e-6) continue;
			if ((towardX * blockedX + towardY * blockedY) / (towardLength * blockedLength) > 0.7071) return true;
		}
		return false;
	}

	private void ClearEliteBlockedDirection() {
		eliteBlockedDirections.Clear();
		eliteSideStepCount = 0;
	}

	private static string ReadClientMoveState(GameWindow game) {
		try {
			RuntimeLayout layout = game.RuntimeLayout;
			using MemoryReader reader = new(game.ProcessId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr entityTable = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			if (entityTable == IntPtr.Zero) return "ĐíchClient=KHÔNG_ĐỌC_ĐƯỢC";
			IntPtr player = IntPtr.Add(entityTable, layout.PlayerRecordOffset);
			int x = reader.ReadInt32(IntPtr.Add(player, GameAddresses.Entity.MovementDestinationX));
			int y = reader.ReadInt32(IntPtr.Add(player, GameAddresses.Entity.MovementDestinationY));
			return $"ĐíchClient={x}/{y}";
		} catch (Exception ex) {
			return $"ĐíchClient=LỖI({ex.GetType().Name})";
		}
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
		// Boss-only: skip corners already patrolled this round (Stop resets the index to 0 but keeps the round).
		if (settings.OnlyAttackBoss && bossPatrolVisitedCorners != 0b1111) {
			while ((bossPatrolVisitedCorners & (1 << outsideAreaCornerIndex)) != 0) outsideAreaCornerIndex = (outsideAreaCornerIndex + 1) % 4;
		}
		int corner = outsideAreaCornerIndex;
		// "Chỉ đánh Boss": patrol at Range/2 instead of AutoFS's Range/3. The client only refreshes entities roughly one
		// screen around the player (cross-checked live on 6 clients 2026-10-01: a boss 1674/2254 raw away was visible, one
		// 1956/1601 and one 2706/317 away were not), while bosses on map 32 spawned 2205-2976 from the center (Range 3000).
		// From Range/3 corners the rim stays out of view. Boss-only patrols the rim itself (owner decision 2026-10-08): the four
		// diagonal points exactly Range from the center, i.e. Range/sqrt(2) per axis (2121 at Range 3000), since the training
		// area is the circle distanceToCenter <= Range (AutoFsEntityScanner) and anything beyond it is never targeted.
		int step = settings.OnlyAttackBoss ? (int)(range / Math.Sqrt(2)) : range / 3;
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
		if (settings.OnlyAttackBoss) {
			PatrolBossCorner(currentGame, currentProcessId, playerX, playerY, corner, destinationX, destinationY, range, now, currentTargetMovementLog);
			return;
		}
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

	// "Chỉ đánh Boss" with no boss in view (owner decision 2026-10-08): walk each corner all the way, then the next, until
	// all 4 are patrolled; if no boss turned up, switch the account to "Không đánh Boss". The generic patrol moves on after
	// 3 sends 700 ms apart (~2 s), far too short for a rim corner (3000 raw from the center at Range 3000; a walk
	// measured 2026-10-06 covered ~16 raw per 60 ms), so the character turned back before ever reaching one.
	private void PatrolBossCorner(GameWindow currentGame, int currentProcessId, int playerX, int playerY, int corner, int destinationX, int destinationY, int range, DateTime now, Action<string>? currentTargetMovementLog) {
		bool legActive = bossPatrolLegStartedUtc != DateTime.MinValue;
		bool reached = HasReachedTrainingCorner(playerX, playerY, destinationX, destinationY);
		bool timedOut = legActive && now - bossPatrolLegStartedUtc >= TimeSpan.FromMilliseconds(BossPatrolCornerTimeoutMilliseconds);
		if (reached || timedOut) {
			bossPatrolVisitedCorners |= 1 << corner;
			bossPatrolLegStartedUtc = DateTime.MinValue;
			outsideAreaCornerIndex = (corner + 1) % 4;
			int visited = System.Numerics.BitOperations.PopCount((uint)bossPatrolVisitedCorners);
			currentTargetMovementLog?.Invoke($"BOSS_PATROL_CORNER | PID={currentProcessId} | Player={playerX}/{playerY} | Corner={corner} | Result={(reached ? "TỚI_NƠI" : "QUÁ_30S")} | ĐãTuần={visited}/4 | Destination={destinationX}/{destinationY} | Range={range}");
			if (bossPatrolVisitedCorners != 0b1111) return;
			bossPatrolVisitedCorners = 0;
			settings.OnlyAttackBoss = false;
			settings.DoNotAttackBoss = true;
			Interlocked.Exchange(ref bossModeSwitchPending, 1);
			currentTargetMovementLog?.Invoke($"BOSS_PATROL_DONE | PID={currentProcessId} | Player={playerX}/{playerY} | Action=Đã tuần đủ 4 góc không thấy boss, chuyển sang Không đánh Boss");
			return;
		}
		if (playerX != bossPatrolObservedX || playerY != bossPatrolObservedY) {
			bossPatrolObservedX = playerX;
			bossPatrolObservedY = playerY;
			bossPatrolProgressUtc = now;
		}
		bool stalled = legActive && now - bossPatrolProgressUtc >= TimeSpan.FromMilliseconds(BossPatrolStallMilliseconds);
		if (legActive && !stalled) return;
		if (!AutoFsMovementCommand.TryWalkTo(currentGame, destinationX, destinationY, out string walkResult)) {
			if (!string.Equals(lastWalkFailure, walkResult, StringComparison.Ordinal)) {
				lastWalkFailure = walkResult;
				currentTargetMovementLog?.Invoke($"BOSS_PATROL_WALK FAIL | PID={currentProcessId} | Player={playerX}/{playerY} | Corner={corner} | Destination={destinationX}/{destinationY} | {walkResult}");
			}
			return;
		}
		lastWalkFailure = "";
		if (!legActive) bossPatrolLegStartedUtc = now;
		bossPatrolProgressUtc = now;
		nextOutsideAreaWalkUtc = now.AddMilliseconds(OutsideAreaWalkIntervalMilliseconds);
		currentTargetMovementLog?.Invoke($"BOSS_PATROL_WALK | PID={currentProcessId} | Player={playerX}/{playerY} | Corner={corner} | LýDo={(legActive ? "ĐỨNG_IM" : "GÓC_MỚI")} | ĐãTuần={System.Numerics.BitOperations.PopCount((uint)bossPatrolVisitedCorners)}/4 | Destination={destinationX}/{destinationY} | Range={range} | {walkResult}");
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
