namespace Auto.Runtime;

using System.Collections.Concurrent;
using Auto.Attack;
using Auto.DebugTools;
using Auto.Movement;
using Auto.Repair;
using Auto.Utils;

// Port từ D:\G\DEV\UI\Accounts.cs (TickAutoEngineCore + helper) — giữ nguyên thứ tự/điều kiện dừng engine.
// Ngoại lệ đồng bộ: DEV không có file non-UI tương đương để diff trực tiếp (logic nằm ngay trong UI/Accounts.cs,
// WinForms). Khi DEV đổi TickAutoEngineCore, phải tự đọc lại D:\G\DEV\UI\Accounts.cs và so tay với file này.
public static class AccountEngineCoordinator {
	// ConcurrentDictionary bắt buộc: TickOne chạy song song nhiều account qua Parallel.ForEach (AccountListViewModel.EngineTick),
	// Dictionary thường không an toàn đa luồng dù các thread ghi khác key nhau, gây InvalidOperationException hỏng state.
	private static readonly ConcurrentDictionary<IntPtr, AttackPositionState> attackPositionByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, OutOfWorldState> outOfWorldByWindow = new();
	// Rơi khỏi game bao lâu thì bắt đầu kêu, và kêu lại mỗi bao lâu.
	// 3 phút: đo được đêm 2026-09-15 -> 16, 5/6 account tự vào lại trong khoảng 5 nhịp heartbeat (~5 phút), nên
	// ngưỡng dưới mốc đó để bắt được ca KHÔNG tự vào lại mà vẫn không kêu oan mỗi lần đổi map.
	private const double OutOfWorldWarnAfterMinutes = 3;
	private const double OutOfWorldRepeatMinutes = 10;

	private sealed class OutOfWorldState {
		public DateTime SinceUtc;
		public DateTime NextReportUtc;
		// Đã yêu cầu đăng nhập lại cho ĐỢT rớt này chưa. Một đợt chỉ yêu cầu một lần, không thì nhịp 100ms sẽ
		// bắn yêu cầu liên tục.
		public bool ReloginRequested;
	}
	private static readonly ConcurrentDictionary<IntPtr, DateTime> deathDetectedUtcByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, string> lastRuntimeGateByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, string> lastAutoGateByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, string> lastQuestGateByWindow = new();
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

	// ===== Lớp giám sát đứng im, đứng TRÊN mọi engine =====
	//
	// Khác hẳn ShouldRestartStationaryAttack: nhánh đó nằm SAU bốn điều kiện (attackEnabled, !manualInputActive,
	// !repairPriority, movementRecoveryPending||!recentAttack) nên nó tự tắt đúng lúc một engine đang ôm quyền mà
	// không làm gì — tức đúng ca kẹt nguy hiểm nhất. Lớp này không hỏi engine nào đang giữ quyền.
	//
	// Bằng chứng vì sao cần (2026-09-12, PID=22824): kẹt cạnh Đại Phu 3 giờ 39 phút, heartbeat.log ghi 219 dòng liên
	// tiếp cùng toạ độ và RepairBusy=True ở cả 219 dòng, còn anti-afk.log không có lấy một dòng nào của PID này.
	//
	// NGƯỠNG ĐO ĐƯỢC, không đoán: quét 141 quãng đứng yên trong heartbeat.log (Master=True). Mọi quãng của phiên chạy
	// bình thường đều dưới 1 phút. Chỉ 8 quãng đạt 2 phút trở lên, và 5 trong số đó là Player=0/0 tức lỗi đọc
	// snapshot. Hai quãng đứng im thật là 135 phút và 89 phút, đều của chính vụ kẹt này. Lấy 5 phút: trên xa mọi
	// quãng hợp lệ đo được, dưới xa mọi quãng kẹt thật.
	private const int StuckSupervisorMinutes = 5;
	// Coi là "vẫn đứng nguyên chỗ" nếu chưa rời khỏi bán kính này. Không so khớp toạ độ tuyệt đối như
	// ShouldRestartStationaryAttack: trong vụ 22824 nhân vật trôi 14 raw (0,05 ô) nên phép so tuyệt đối cắt quãng kẹt
	// 219 phút thành hai mảnh 135 và 89 — đủ để một bộ đếm dựa trên so khớp tuyệt đối bị reset oan.
	private const int StuckSupervisorRadiusRaw = 256;
	// Bỏ hẳn yêu cầu sửa trong quãng này khi phải cứu kẹt, để HasPendingRepairRequest về false và repairPriority nhả.
	private const int StuckSupervisorRepairCooldownSeconds = 600;
	// Ngoài bãi liên tục quá chừng này thì kéo về, KHÔNG đòi đứng im. Lưới an toàn cho ca nhân vật kẹt trong hốc cạnh NPC
	// mà cứ dao động trái-phải: dao động rộng hơn StuckSupervisorRadiusRaw nên mốc đứng im (10 giây, bán kính 256) luôn bị
	// reset, và ShouldRestartStationaryAttack còn đòi !recentAttack trong khi luồng Đánh vẫn bắn lệnh vào từng con quái
	// "không tới được". Bằng chứng (Release/Diagnostics PID=22612 MaiAnhNhe 2026-09-24): xong chuyến bán 22:40:05 nhân vật
	// dao động trong x 59199-59870, y 92585-93114 (cách tâm bãi ~4990 raw, Range=3000) suốt tới 22:43:15 mới có
	// ANTI_AFK_COORDINATION_RECOVERY, rồi "Tự lên bãi" đi từ chính chỗ đó về tâm bãi trong 20 giây — tức đường về thông,
	// chỉ là không ai gọi nó sớm. 20 giây dài hơn mọi lần đuổi quái sát mép bãi bình thường.
	private const int OutsideAreaReturnSeconds = 20;
	private static readonly ConcurrentDictionary<IntPtr, DateTime> outsideAreaSinceByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, StuckAnchor> stuckAnchorByWindow = new();
	// Máu của nhịp trước, để phân biệt "đang hồi máu" với "đang bị đánh" — xem chỗ dùng trong lớp giám sát đứng im.
	private static readonly ConcurrentDictionary<IntPtr, int> lastStuckHpByWindow = new();

	// Trốn boss bị KẸT: đang trốn mà vị trí xê dịch không quá EliteRetreatStuckRadius trong EliteRetreatStuckSeconds.
	// Chỉ lúc đó Buff mới được heal trong vùng cấm (chủ dự án chốt 2026-09-24). Số đo từ movement.log PID=2228: chạy
	// bình thường ~495 raw trong 1,7 giây (23:14:27 -> 23:14:28), kẹt thật chỉ xê dịch ~16 raw trong 5 giây
	// (23:14:52 -> 23:14:57, 63941/92762 -> 63939/92746) — 64 raw trong 3 giây tách được hai ca.
	private const int EliteRetreatStuckSeconds = 3;
	private const int EliteRetreatStuckRadius = 64;
	private static readonly ConcurrentDictionary<IntPtr, AttackPositionState> eliteRetreatAnchorByWindow = new();
	private static readonly ConcurrentDictionary<IntPtr, DateTime> eliteRetreatStuckLoggedByWindow = new();
	// Mốc bắt đầu đợt trốn boss hiện tại, dùng RIÊNG cho việc chặn ANTI_AFK.
	private static readonly ConcurrentDictionary<IntPtr, DateTime> eliteRetreatAntiAfkSinceByWindow = new();
	// Trốn boss được miễn ANTI_AFK trong bấy nhiêu lâu. Dài hơn hẳn StationaryRestartSeconds (10s) vì một chặng trốn
	// bình thường đã tốn hơn thế; nhưng vẫn có trần để ca trốn treo thật không kẹt vĩnh viễn — hết trần thì ANTI_AFK
	// được đập worker như cũ.
	private const int EliteRetreatAntiAfkGraceSeconds = 60;

	private readonly record struct AttackPositionState(int X, int Y, DateTime SinceUtc);

	// Dọn toàn bộ trạng thái theo cửa sổ khi cửa sổ game biến mất.
	//
	// Trước lượt này KHÔNG ai dọn: ApplyScanResult chỉ gọi WindowResponsiveness.Forget và ClientFreezeWatch.Forget,
	// nên 17 bảng dưới đây giữ lại một mục cho mỗi HWND đã chết suốt vòng đời app. Hôm nay chỉ rò khi client crash;
	// từ lúc có tính năng tự đăng nhập lại thì mỗi lượt cứu account sinh một HWND mới, tức rò theo nhịp.
	//
	// THÊM BẢNG MỚI Ở TRÊN THÌ PHẢI THÊM MỘT DÒNG Ở ĐÂY.
	//
	// Nhận Handle chứ không nhận processId như hai Forget sẵn có — khác kiểu tham số là cố ý, vì các bảng này
	// khoá theo HWND.
	public static void Forget(IntPtr handle) {
		attackPositionByWindow.TryRemove(handle, out _);
		outOfWorldByWindow.TryRemove(handle, out _);
		deathDetectedUtcByWindow.TryRemove(handle, out _);
		lastRuntimeGateByWindow.TryRemove(handle, out _);
		lastAutoGateByWindow.TryRemove(handle, out _);
		lastQuestGateByWindow.TryRemove(handle, out _);
		deathReturnLoggedByWindow.TryRemove(handle, out _);
		deathReadErrorLoggedByWindow.TryRemove(handle, out _);
		deathUndetectedLoggedByWindow.TryRemove(handle, out _);
		addressAuditRanByWindow.TryRemove(handle, out _);
		attackEnabledByWindow.TryRemove(handle, out _);
		lastReturnRequestFailureByWindow.TryRemove(handle, out _);
		nextHeartbeatByWindow.TryRemove(handle, out _);
		deathSendFailureByWindow.TryRemove(handle, out _);
		stuckAnchorByWindow.TryRemove(handle, out _);
		lastStuckHpByWindow.TryRemove(handle, out _);
		eliteRetreatAntiAfkSinceByWindow.TryRemove(handle, out _);
		eliteRetreatAnchorByWindow.TryRemove(handle, out _);
		eliteRetreatStuckLoggedByWindow.TryRemove(handle, out _);
		outsideAreaSinceByWindow.TryRemove(handle, out _);
	}

	public static void TickOne(GameWindow game) {
		long profilerStart = HotPathProfiler.Begin();
		try {
		lock (game.AutoSync) {
			bool masterEnabled = game.Enabled;
			// Công cụ Debug chạy tay phải chạy được KỂ CẢ khi Auto tổng đang tắt (chủ dự án chốt 2026-09-10).
			// Bắt bật Auto tổng lên chỉ để chẩn đoán thì các engine khác cùng chen lệnh vào và làm bẩn phép đo — đúng
			// thứ mà công cụ chẩn đoán phải tránh. Mở cổng ở đây an toàn vì khi Auto tổng tắt thì không engine nào
			// khác được tick, chỉ luồng sửa đồ chạy tay là gửi lệnh.
			bool debugRepairRun = game.WeaponRepairAutomation.IsDebugRun;
			game.AutoFsActionGate.SetAutomationEnabled(masterEnabled || debugRepairRun);
			DebugLog.SetProcessLoggingEnabled(game.ProcessId, masterEnabled);
			// Auto tổng tắt (và không có chuyến sửa/bán chạy tay) thì account này KHÔNG chạy luồng nào (chủ dự án chốt
			// 2026-09-24): chỉ dọn đúng những gì cổng tắt bên dưới vẫn dọn rồi thoát, không đọc bộ nhớ game. Trước đây
			// account tắt vẫn đi qua ReadSnapshot/RefreshMapState/ClientFreezeWatch/WeaponRepairMonitor mỗi 100ms.
			// Bằng chứng (perf.log bản 16:50, bật 6/21 account): 6 client ĐọcNhânVật=88 lần/s, 24,5ms/s; 21 client
			// ĐọcNhânVật=209 lần/s (= 21 account x 10 nhịp), 549ms/s, MộtNhịpAccount=759ms/s, CPU 50-90% của 1 lõi.
			// Dòng account trên UI vẫn có HP/MP/tên vì AccountListViewModel.ScanTick tự đọc riêng mỗi giây.
			// Giữ lại DUY NHẤT RefreshMapState (tự giới hạn 1 lần/giây): công cụ Debug chạy khi Auto tổng tắt đọc
			// LastObservedMapId (ReturnTalismanProbe), và để map cũ thì lúc bật lại Auto bị tính nhầm là đổi map.
			if (! masterEnabled && ! debugRepairRun && ! game.InventorySaleEngine.IsDebugRun) {
				RefreshMapState(game, null);
				LogAutoGate(game, masterEnabled, game.AttackSettings.Enabled, game.LootSettings.Enabled, game.BasicSettings.EnableWeaponRepair, game.InventorySaleEngine.IsAutomaticSaleEnabled);
				game.WeaponRepairMonitor.SetEnabled(false);
				stuckAnchorByWindow.TryRemove(game.Handle, out _);
				attackEnabledByWindow.TryRemove(game.Handle, out _);
				game.ReturnToTrainingAutomation.Cancel();
				game.ConfiguredTrainingMovementAutomation.Cancel();
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}
			Action<string> accountLog = text => DebugLog.AddForProcess(game.ProcessId, text);
			Action<string> accountDropLog = text => DebugLog.AddLootDropForProcess(game.ProcessId, text);
			Action<string> accountTargetLifecycleLog = text => DebugLog.AddTargetLifecycleForProcess(game.ProcessId, text);
			Action<string> accountTargetViolationLog = text => DebugLog.AddTargetViolationForProcess(game.ProcessId, text);
			Action<string> accountTargetMovementLog = text => DebugLog.AddTargetMovementForProcess(game.ProcessId, text);
			RefreshMapState(game, accountLog);

			bool attackEnabled = game.AttackSettings.Enabled;
			// Giữ riêng giá trị ô checkbox: attackEnabled bên dưới bị AND thêm layout.AttackReady, nên dùng nó để
			// khoá Sửa đồ là sai — Attack rớt readiness một nhịp sẽ kéo Sửa đồ chết theo dù người dùng vẫn bật Đánh.
			// Sửa đồ đã có cổng readiness riêng là layout.RepairReady.
			bool attackConfigured = attackEnabled;
			bool lootEnabled = game.LootSettings.Enabled;
			bool repairConfigured = game.BasicSettings.EnableWeaponRepair;
			bool repairEnabled = repairConfigured;
			bool saleEnabled = game.InventorySaleEngine.IsAutomaticSaleEnabled;
			bool returnTalismanConfigured = game.BasicSettings.EnableLowHpReturnTalisman;
			bool autoAdvertiseEnabled = game.ChatEngine.IsConfigured;

			// Nhiệm vụ đi khắp map và tự chọn NPC riêng nên không chia được quyền điều khiển với Đánh và Sửa đồ:
			// bật "Làm nhiệm vụ" ở BẤT KỲ nhiệm vụ nào là khoá cứng cả hai (chủ dự án chốt 2026-09-09).
			// Chỉ khoá thực thi, không sửa cấu hình — bỏ tick nhiệm vụ thì hai chức năng kia trở lại đúng ô đã đặt.
			// Phải khoá cả attackConfigured chứ không riêng attackEnabled, vì attackConfigured mới là thứ cho phép
			// WeaponRepairAutomation chạy tiếp (repairAllowed bên dưới) và cho WeaponRepairMonitor xếp yêu cầu sửa.
			// Nhặt KHÔNG bị khoá: nó không tự phát sinh di chuyển.
			// Chưa có engine nhiệm vụ nên hiện tại bật ô này chỉ dừng Đánh/Sửa đồ chứ chưa có gì chạy thay.
			// Bào thương đã bị gỡ khỏi UI (2026-09-11) nên KHÔNG được đưa CaravanEnabled vào cổng này: file cấu hình cũ
			// có thể còn lưu true, mà không còn ô nào để bỏ tick — Đánh và Sửa đồ sẽ bị khoá vĩnh viễn.
			bool questEnabled = game.QuestSettings.ScoutEnabled;
			// Ghi ngay khi trạng thái ô tick đổi. HEARTBEAT_AUTO chỉ ghi mỗi phút một lần nên một phiên chạy ngắn
			// không đủ để biết ô "Làm nhiệm vụ" có tới được engine hay không (quest.log rỗng ngày 2026-09-09 18:57).
			string questGate = $"QUEST_GATE | ThámQuân={game.QuestSettings.ScoutEnabled} | Khoá Đánh/Sửa đồ={questEnabled}";
			if (!lastQuestGateByWindow.TryGetValue(game.Handle, out string? previousQuestGate) || !string.Equals(previousQuestGate, questGate, StringComparison.Ordinal)) {
				lastQuestGateByWindow[game.Handle] = questGate;
				accountLog(questGate);
			}
			if (questEnabled) {
				attackEnabled = false;
				attackConfigured = false;
				repairConfigured = false;
				repairEnabled = false;
			}

			// CHỈ resolve khi có lý do dùng tới kết quả. RuntimeLayoutResolver.Resolve tự cache VĨNH VIỄN một khi
			// PlayerReady, nhưng lúc CHƯA sẵn sàng (chưa login, đang ở màn chọn nhân vật...) nó dump lại TOÀN BỘ ảnh
			// Game.exe (module.ModuleMemorySize, cỡ 33MB) rồi quét chữ ký mỗi 2 giây (RuntimeLayoutResolver.cs:35,
			// unavailableByProcess retry). LogRuntimeGate/RunAddressAuditOnce đều tự no-op khi log tiến trình đang tắt
			// (DebugLog.IsProcessLoggingEnabled = masterEnabled, đặt ở trên), và mọi nơi đọc game.RuntimeLayout khác
			// (ScoutQuestAutomation, WeaponRepairAutomation, AutoFsMovementCommand) chỉ chạy sau cổng masterEnabled ở
			// dưới — nên kết quả Resolve() hoàn toàn không dùng tới khi Auto tổng tắt. Trước đây gọi vô điều kiện nên
			// mỗi client mở sẵn nhưng CHƯA login/CHƯA bật Auto tổng vẫn tự dump+quét 33MB mỗi 2 giây, cộng dồn theo số
			// client mở — đúng hiện tượng CPU/RAM tăng theo số client dù chưa bật gì (chủ dự án báo 2026-09-21).
			RuntimeLayout layout = masterEnabled || debugRepairRun ? RuntimeLayoutResolver.Resolve(game.ProcessId) : game.RuntimeLayout;
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
			// Đồng hồ bắt client treo. Đặt NGAY sau khi đọc snapshot và TRƯỚC mọi cổng bật/tắt bên dưới: client treo
			// thì mọi tính năng đều lệch theo, nên phải đo cả lúc Auto tổng đang tắt. Chỉ quan sát, không đổi hành vi.
			ClientFreezeWatch.Observe(game, snapshot);
			if (!snapshot.Success) {
				LogHeartbeat(game, snapshot, masterEnabled, attackEnabled, lootEnabled, repairConfigured, repairEnabled, saleEnabled, questEnabled, accountLog);
				ReportAccountOutOfWorld(game, snapshot);
				game.AutoFsActionGate.SetRuntimeSuspended(true);
				// Cùng lý do như ở cổng Auto tổng: worker đánh vừa bị Stop nên trạng thái nhớ phải về "chưa chạy",
				// không thì lượt đọc được snapshot trở lại sẽ không tính là chuyển tắt->bật.
				attackEnabledByWindow.TryRemove(game.Handle, out _);
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}
			ReportAccountBackInWorld(game);
			game.AutoFsActionGate.SetRuntimeSuspended(false);

			// Mua nhanh thuốc luôn chạy khi Auto tổng bật (chủ dự án chốt 2026-09-25) nên đặt TRƯỚC mọi nhánh return bên dưới, để
			// Hồi thành phù, Về thành, Sửa đồ, Buff... đều không chặn được nó. Chỉ gửi một lệnh mua, không giữ quyền điều khiển.
			// Hồi phục (dùng thuốc khi HP/MP dưới ngưỡng) cùng vị trí và cùng lý do với mua nhanh: không luồng nào được chặn nó.
			if (masterEnabled && layout.PlayerReady && layout.InventoryReady) game.RecoveryEngine.Tick(game.ProcessId, game.Handle, snapshot, accountLog);
			if (masterEnabled && layout.PlayerReady && layout.InventoryReady) game.QuickBuyEngine.Tick(game.ProcessId, game.Handle, accountLog);

			// Đặt NGAY ĐÂY, trước mọi nhánh return của từng luồng. Mọi nhánh bên dưới đều có đường thoát sớm
			// (Hồi thành phù, tắt Auto tổng, chết, Buff, Tự lên bãi, Di chuyển bãi), nên đặt sau bất kỳ nhánh nào
			// là lại đẻ ra đúng lỗ hổng cũ: luồng nào ôm quyền thì lớp giám sát tắt theo.
			SuperviseStuckAccount(game, snapshot, masterEnabled, accountLog);

			// "Đang train" để Hồi thành phù bật lại sau khi về thành: đã tới bãi, không còn luồng di chuyển nào đang kéo nhân vật đi.
			bool isTraining = game.LastObservedMapId > 0 && !game.ReturnToTrainingAutomation.IsBusy && !game.ConfiguredTrainingMovementAutomation.IsBusy && !IsOutsideTrainingArea(game, snapshot);
			if (masterEnabled && returnTalismanEnabled && game.LowHpEngine.Tick(game.ProcessId, game.Handle, snapshot, game.LastObservedMapId, isTraining, accountLog)) {
				game.ReturnToTrainingAutomation.Cancel(accountLog, "Hồi thành phù giữ quyền điều khiển");
				game.ConfiguredTrainingMovementAutomation.Cancel(accountLog, "Hồi thành phù giữ quyền điều khiển");
				game.WeaponRepairAutomation.Cancel(game, accountLog, "Hồi thành phù giữ quyền điều khiển");
				// Cùng lý do như ở cổng Auto tổng. Nhánh này chính là "phù về thành": nó vừa huỷ Tự lên bãi và Stop
				// worker đánh, nên phải quên trạng thái nhớ. Yêu cầu lên bãi trùng không gây hại — RequestReturnToTrainingCenter
				// và Prepare đều tự chặn khi ReturnToTrainingAutomation đang bận.
				attackEnabledByWindow.TryRemove(game.Handle, out _);
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}
			if (!returnTalismanEnabled) game.LowHpEngine.Reset();

			if (game.LowHpEngine.ConsumeReturnToTrainingRequest()) {
				if (!game.AttackSettings.EnableReturnToTraining) {
					accountLog($"LOW_HP_RETURN_TO_TRAINING_SKIPPED | Reason=EnableReturnToTraining=False | Hp={snapshot.Hp}/{snapshot.MaxHp}");
				} else if (game.ReturnToTrainingAutomation.IsBusy) {
					accountLog($"LOW_HP_RETURN_TO_TRAINING_SKIPPED | Reason=ReturnToTrainingAutomation.IsBusy | Hp={snapshot.Hp}/{snapshot.MaxHp}");
				} else {
					accountLog($"LOW_HP_RETURN_TO_TRAINING_REQUESTED | Hp={snapshot.Hp}/{snapshot.MaxHp} | MapId={game.LastObservedMapId}");
					game.ReturnToTrainingAutomation.Prepare(game, snapshot, accountLog);
				}
			}

			// Monitor độ bền tắt theo ô Đánh: luật là "bật Đánh -> đồ hỏng -> đi sửa", nên tắt Đánh thì không có gì để
			// theo dõi. Không tắt thì nó vẫn xếp yêu cầu sửa, mà yêu cầu đó không ai thực hiện được (repairBusy đòi
			// attackConfigured), rồi hai chốt HasPendingRepairRequest/RequiresExclusiveControl bên dưới khoá luôn Nhặt.
			// Cả hai chốt đó đều đã AND sẵn cờ enabled của monitor nên tắt ở đây là vô hiệu hoá được cả hai.
			// Bật Đánh trở lại thì SetEnabled(true) tự đặt lịch kiểm tra ngay, không phải chờ chu kỳ.
			game.WeaponRepairMonitor.SetEnabled(masterEnabled && repairConfigured && attackConfigured);
			// Tham số thứ ba là competingEnginesEnabled ("có engine nào đang tranh chấp cần tạm giữ để đọc độ bền"),
			// KHÔNG phải công tắc bật/tắt monitor — công tắc là SetEnabled ngay trên. Giữ nguyên nghĩa gốc.
			if (!game.WeaponRepairAutomation.IsBusy) game.WeaponRepairMonitor.Tick(game.ProcessId, game.BasicSettings.WeaponDurabilityThreshold, masterEnabled && (attackEnabled || lootEnabled), accountLog);
			if (repairConfigured && !layout.RepairReady) game.WeaponRepairMonitor.ReportUnavailableTransport(layout.DescribeUnavailable(RuntimeSubsystem.MovementTransport, RuntimeSubsystem.Map, RuntimeSubsystem.Shop, RuntimeSubsystem.RepairTransport), accountLog);

			// questEnabled phải nằm trong cổng này: nó vừa tắt attackEnabled/repairEnabled ở trên, nên nếu account
			// chỉ bật mỗi nhiệm vụ thì cổng sẽ thoát sớm và luồng nhiệm vụ không bao giờ được tick.
			if (!masterEnabled || (!attackEnabled && !lootEnabled && !repairEnabled && !saleEnabled && !returnToTownEnabled && !returnTalismanEnabled && !autoAdvertiseEnabled && !questEnabled)) {
				LogAutoGate(game, masterEnabled, attackEnabled, lootEnabled, repairEnabled, saleEnabled);
				// Quên trạng thái Đánh của lượt trước khi thoát ở cổng này, để lần bật lại được tính là chuyển tắt->bật.
				//
				// Không quên thì: đang train (nhớ true) -> tắt Auto tổng (thoát ở đây, KHÔNG ghi nhớ gì) -> dùng phù về
				// thành -> bật lại Auto tổng. Lúc đó attackEnabled=true và giá trị nhớ cũng vẫn là true, nên chốt
				// "bật Đánh khi đang ở ngoài bãi thì đi lên bãi" bên dưới không kích hoạt và nhân vật đứng im giữa thành.
				// Người dùng phải tắt/bật lại riêng ô Đánh mới chạy được — đúng hiện tượng báo ngày 2026-09-09.
				attackEnabledByWindow.TryRemove(game.Handle, out _);
				game.ReturnToTrainingAutomation.Cancel();
				game.ConfiguredTrainingMovementAutomation.Cancel();
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				// Chuyến bán chạy tay (nút "Bán ngay" tab Debug) cũng vậy. Nút đó BẮT BUỘC Auto tổng phải tắt, nên
				// đây là chỗ DUY NHẤT bơm được Tick cho nó — luồng bán tự động nằm trong WeaponRepairAutomation và
				// luồng đó không chạy khi Auto tổng tắt.
				//
				// Dùng AddDebugForProcess chứ KHÔNG dùng accountLog: accountLog đi qua DebugLog.AddForProcess, mà hàm
				// đó đòi cả phiên log đang bật LẪN log của tiến trình này đang bật (DebugLog.CanLogProcess) — cả hai
				// đều tắt theo masterEnabled ở dòng trên. Dùng accountLog thì chuyến bán chẩn đoán không ghi nổi một
				// dòng nào, tức mất sạch bằng chứng đúng lúc cần nhất.
				if (game.InventorySaleEngine.IsDebugRun) {
					Action<string> saleDebugLog = text => DebugLog.AddDebugForProcess(game.ProcessId, text);
					if (game.InventorySaleEngine.Tick(game.ProcessId, game.Handle, saleDebugLog, out string saleDebugFailure) == Auto.Sale.InventorySaleTickResult.Failed) {
						saleDebugLog($"SALE_DEBUG_FAILED | {saleDebugFailure}");
					}
					return;
				}
				// Chuyến sửa đồ chạy tay vẫn được đi tiếp: nó là lệnh trực tiếp của chủ dự án, không phải luồng tự động.
				// Đánh và Nhặt đã dừng ở trên nên không ai tranh quyền điều khiển — đúng điều kiện sạch để chẩn đoán.
				if (debugRepairRun) {
					game.WeaponRepairAutomation.Tick(game, snapshot, accountLog);
					return;
				}
				game.WeaponRepairAutomation.Cancel(game, accountLog, "Tắt Auto tổng hoặc tắt ô Sửa đồ");
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

			// Bật Auto tổng = RÀ LẠI TOÀN BỘ, không phải chạy tiếp từ trạng thái cũ (chủ dự án chốt 2026-09-10).
			// Cổng Auto tổng ở trên đã xoá attackEnabledByWindow nên lần bật lại luôn được tính là chuyển tắt->bật.
			//
			// Vì sao phải ghi log cả khi không làm gì: trước đây nhánh này im lặng hoàn toàn khi IsOutsideTrainingArea
			// trả false, nên lúc nhân vật đứng im sau khi bật lại Auto tổng thì không có một dòng nào cho biết vì sao.
			// Ngày 2026-09-10 PID=34032 kết thúc luồng sửa đồ ở 59334/93306 (cạnh Đại Phu, cách tâm bãi hơn 5000 raw)
			// và không có dòng log nào giải thích. Bốn giá trị dưới đây đủ để chỉ ra ngay ô cấu hình nào đang chặn.
			if (attackEnabled && !attackWasEnabled) {
				Settings rearmSettings = game.AttackSettings;
				bool outsideArea = IsOutsideTrainingArea(game, snapshot);
				accountLog($"MASTER_REARM | PID={game.ProcessId} | NgoàiBãi={outsideArea} | TựLênBãi={rearmSettings.EnableReturnToTraining} | QuanhĐiểm={rearmSettings.UseCenterPosition} | Tâm={rearmSettings.CenterX}/{rearmSettings.CenterY} | MapTâm={rearmSettings.CenterMapId} | BãiĐãLưu={game.SavedTrainingMapId} | ViTríHiệnTại=Map{game.LastObservedMapId}/{snapshot.X}/{snapshot.Y} | SửaĐồ={repairConfigured} | ĐangChờSửa={game.WeaponRepairMonitor.HasPendingRepairRequest}");
				// Luồng sửa đồ: xoá cache độ bền để nó đọc lại từ client thay vì tin số đã nhớ từ trước lúc tắt.
				if (repairConfigured) {
					game.WeaponRepairMonitor.ResetCache();
					game.WeaponRepairMonitor.ScheduleImmediateCheck("Bật lại Auto tổng");
				}
				// Luồng đánh: ở ngoài bãi thì đi lên bãi trước. RequestReturnToTrainingCenter tự ghi lý do khi không
				// khởi động được, nên nhánh này không còn đường nào im lặng.
				if (outsideArea) RequestReturnToTrainingCenter(game, snapshot, accountLog, "Bật Auto tổng khi đang ở ngoài bãi");
			}

			// AutoFS gốc không theo dõi chuột, nên Auto cũng không tạm dừng tự động đánh theo click trái.
			bool manualInputActive = false;

			LogHeartbeat(game, snapshot, masterEnabled, attackEnabled, lootEnabled, repairConfigured, repairEnabled, saleEnabled, questEnabled, accountLog);
			if (HandleDeathPopup(game, snapshot, accountLog)) {
				game.WeaponRepairAutomation.Cancel(game, accountLog, "Nhân vật chết, popup hồi sinh đang mở");
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}

			// Nhiệm vụ chạy sau khi đã xử lý xong chết/hồi sinh, và trước Buff. Đánh/Sửa đồ đã bị khoá ở đầu tick nên
			// không tranh quyền di chuyển. Nhặt vẫn chạy tiếp bên dưới theo quyết định của chủ dự án.
			if (game.QuestSettings.ScoutEnabled) game.ScoutQuestAutomation.Tick(game, snapshot, accountLog);
			else game.ScoutQuestAutomation.ResetSession();

			// Skill hỗ trợ bị động chỉ gửi gói bật lại, không chiếm quyền điều khiển nên chạy trước và không chặn luồng khác.
			game.BuffEngine.Tick(game.ProcessId, game.Handle, attackEnabled, AreSkillsAllowedHere(game), accountLog);

			// Tránh quái thủ lĩnh/boss là ƯU TIÊN SỐ 1, đứng trên cả Buff: chạy ra xa rồi mới heal (chủ dự án chốt
			// 2026-09-10). Boss mạnh hơn hẳn, đứng yên cạnh nó mà heal thì heal không kịp máu tụt.
			//
			// Bằng chứng vì sao phải có nhánh này (movement.log + buff.log + death.log, PID=34032):
			//   18:24:01.932  ELITE_RETREAT | Distance=146   <- dòng trốn CUỐI CÙNG
			//   18:24:04.407  BUFF Target=Owner | Exclusive=True
			//   18:24:05.699  Nhân vật chết
			// Khối tránh boss nằm TRONG worker của luồng đánh, mà nhánh Buff bên dưới vừa gọi AttackEngine.Stop()
			// vừa bật SetLootSuspended — chặn cả hai lớp, nên luồng trốn tắt theo đúng lúc cần nó nhất.
			// Hai nguồn: cờ do worker cập nhật (tươi nhất khi worker sống), và đường quét độc lập cho lúc worker đã bị
			// dừng — thiếu đường thứ hai thì cờ đóng băng ở false và boss tới sau đó không ai thấy.
			bool eliteRetreatActive = attackEnabled && !manualInputActive
				&& (game.AttackEngine.IsRetreatingFromElite || game.AttackEngine.IsInsideEliteZoneNow(game.ProcessId));
			// Chặn Buff SUỐT đợt trốn, không có trần thời gian (chủ dự án chốt lại 2026-09-24: tránh boss là ưu tiên số 1,
			// mọi chức năng khác phải dừng). Trần 5 giây cũ trả quyền cho Buff giữa lúc boss còn sát người. Bằng chứng
			// (movement.log + buff.log, PID=2228 ZALO0988777666): 23:14:57.459 ELITE_RETREAT_BUFF_BLOCK_EXPIRED khi
			// Distance=121; 23:15:16.747 ELITE_DETECTED ToPlayer=376 mà cùng lúc BUFF_CAST_CONFIRMED Target=Owner
			// Exclusive=True, heal đứng im tới 23:15:20. Cả ngày 2026-09-24 có 58 lần Buff giành quyền kiểu này.
			// Ngoại lệ DUY NHẤT: trốn bị kẹt (xem EliteRetreatStuckSeconds) thì cho Buff heal tại chỗ, vì đứng im cạnh boss
			// mà không heal thì chắc chết. Đường heal này đã chạy được lúc trốn: buff.log PID=2228 23:15:16-23:15:20 HP
			// 101 -> 418 với Target=Owner Exclusive=True.
			bool eliteRetreatStuck = IsEliteRetreatStuck(game, snapshot, eliteRetreatActive, accountLog);
			// Đệ không được làm nhân vật đứng im (chủ dự án chốt 2026-09-25): đang trốn boss, sửa đồ (đang chạy hoặc đang chờ),
			// tự lên bãi hay di chuyển bãi thì KHÔNG heal Đệ. Heal Đệ giữ quyền điều khiển độc quyền nên nhánh bên dưới sẽ huỷ
			// đúng các luồng đó và dừng Đánh. Heal Chủ không bị ảnh hưởng.
			bool petHealAllowed = !eliteRetreatActive
				&& !game.WeaponRepairAutomation.IsBusy && !(game.WeaponRepairMonitor.HasPendingRepairRequest && layout.RepairReady)
				&& !game.ReturnToTrainingAutomation.IsBusy && !game.ConfiguredTrainingMovementAutomation.IsBusy;
			if (eliteRetreatActive && !eliteRetreatStuck) {
				game.SupportEngine.ReleaseForHigherPriority();
			} else if (! game.SupportEngine.Tick(game.ProcessId, game.Handle, snapshot, attackEnabled, AreSkillsAllowedHere(game), petHealAllowed, accountLog)) {
				// Buff không cần giữ quyền (heal xong hoặc máu còn đủ): trả luồng trốn thêm một lượt 3 giây để thử đi
				// tiếp, thay vì nhịp sau lại coi là kẹt ngay vì mốc vị trí đã cũ.
				if (eliteRetreatStuck) eliteRetreatAnchorByWindow[game.Handle] = new AttackPositionState(snapshot.X, snapshot.Y, DateTime.UtcNow);
			} else {
				game.ReturnToTrainingAutomation.Cancel(accountLog, "Buff hỗ trợ giữ quyền điều khiển");
				game.ConfiguredTrainingMovementAutomation.Cancel(accountLog, "Buff hỗ trợ giữ quyền điều khiển");
				game.WeaponRepairAutomation.Cancel(game, accountLog, "Buff hỗ trợ giữ quyền điều khiển");
				game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.ReturnMovementOwner, true);
				game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.TrainingMovementOwner, true);
				game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.SaleRepairOwner, true);
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}
			game.ChatEngine.Tick(game.ProcessId, game.Handle, accountLog);

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
			// ĐANG TRỐN BOSS THÌ KHÔNG ĐƯỢC ĐẬP WORKER ĐÁNH.
			//
			// Nguyên nhân gốc của vụ PID=24236 MaiAnhNhe kẹt ngày 2026-09-22, đã truy ra trọn vòng lặp bằng log:
			//   1. Trốn boss thì nhánh trên KHÔNG gửi lệnh đánh (eliteRetreatActive -> ReleaseForHigherPriority),
			//      nên recentAttack = false và cổng "(movementRecoveryPending || !recentAttack)" luôn mở.
			//   2. Nhân vật chưa đi hết chặng trốn (~520 raw) trong StationaryRestartSeconds -> bị coi là đứng im.
			//   3. Nhánh else gọi AttackEngine.Stop(), mà Stop() xoá eliteWalkDestinationX/Y + loggedEliteNames +
			//      nextOutsideAreaWalkUtc (Attack/Engine.cs:249-256).
			//   4. Worker dựng lại: mất đích đang giữ nên tính đích MỚI từ chỗ vừa đứng, và in lại ELITE_DETECTED.
			// Số đo: anti-afk.log 09:20:10 -> 09:22:31 có 19 dòng ANTI_AFK_POSITION_UNCHANGED_10_SECONDS cách nhau
			// đúng 10 giây, tất cả Flow=AUTO_ATTACK | RecentAttack=False; cùng khoảng đó movement.log có MỌI dòng
			// ELITE_RETREAT mang LýDoBắn=ĐÍCH_MỚI | CònCáchĐíchCũ="chưa có đích", và nhân vật đứng nguyên quanh
			// 62265/91411. Tức luật "chốt đích, đi cho hết chỗ đó" bị chính ANTI_AFK vô hiệu hoá mỗi 10 giây.
			//
			bool eliteRetreatHoldingControl = IsEliteRetreatWithinAntiAfkGrace(game.Handle, eliteRetreatActive);
			if (attackEnabled && !manualInputActive && !repairPriority && !eliteRetreatHoldingControl && (movementRecoveryPending || !recentAttack) && ShouldRestartStationaryAttack(game.Handle, snapshot, out int stationaryMilliseconds)) {
				// CHẨN ĐOÁN 2026-09-22: chủ dự án khẳng định tận mắt thấy "mất Đệ -> Chủ đứng im" ở PID=19860, nhưng
				// buff.log/movement.log thật không trùng thời điểm — không đủ bằng chứng để sửa mù theo giả thuyết
				// đó (Mục 4 CLAUDE.md). Ghi thêm trạng thái Đệ ngay tại đúng thời điểm coordinator xác nhận đứng im,
				// để lần sau đối chiếu trực tiếp thay vì suy luận gián tiếp qua buff.log như lần này.
				Auto.Support.PetHealthReading petReading = Auto.Support.PetHealthReader.Read(game.ProcessId);
				string reason = $"ANTI_AFK_POSITION_UNCHANGED_{StationaryRestartSeconds}_SECONDS | StationaryMilliseconds={stationaryMilliseconds} | Position={snapshot.X}/{snapshot.Y} | RecentAttack={recentAttack} | MovementBusy={movementRecoveryPending} | Đệ={(petReading.Success ? (petReading.Present ? "CÓ" : "KHÔNG_CÓ") : "KHÔNG_ĐỌC_ĐƯỢC")}";
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

			// Ngoài bãi liên tục quá OutsideAreaReturnSeconds thì kéo về bãi, không phụ thuộc đứng im — xem chú thích ở hằng số.
			// Bỏ qua khi có luồng nào đang giữ quyền di chuyển (đồng hồ cũng về 0 để lần sau đếm lại từ đầu).
			if (attackEnabled && !manualInputActive && !repairPriority && !eliteRetreatHoldingControl && !game.ReturnToTrainingAutomation.IsBusy && !game.ConfiguredTrainingMovementAutomation.IsBusy && IsOutsideTrainingArea(game, snapshot)) {
				DateTime outsideSinceUtc = outsideAreaSinceByWindow.GetOrAdd(game.Handle, DateTime.UtcNow);
				double outsideSeconds = (DateTime.UtcNow - outsideSinceUtc).TotalSeconds;
				if (outsideSeconds >= OutsideAreaReturnSeconds) {
					// Đặt lại mốc TRƯỚC khi gọi: nếu chuyến về không khởi động được thì thử lại sau đúng một chu kỳ, không bắn mỗi nhịp.
					outsideAreaSinceByWindow[game.Handle] = DateTime.UtcNow;
					accountLog($"OUTSIDE_AREA_RETURN | PID={game.ProcessId} | NgoàiBãi={outsideSeconds:F0}s | ViTri=Map{game.LastObservedMapId}/{snapshot.X}/{snapshot.Y} | Tâm={game.AttackSettings.CenterX}/{game.AttackSettings.CenterY} | Range={game.AttackSettings.Range} | Action=Tự lên bãi");
					RequestReturnToTrainingCenter(game, snapshot, accountLog, "Ngoài bãi quá lâu");
					game.AttackEngine.Stop();
				}
			} else {
				outsideAreaSinceByWindow.TryRemove(game.Handle, out _);
			}

			// Công tắc Tự động đánh khóa thực thi các luồng Tân thủ, Thành thị và Mê cung nhưng không sửa cấu hình của chúng.
			if (!attackEnabled) {
				game.ReturnToTrainingAutomation.Cancel();
				game.ConfiguredTrainingMovementAutomation.Cancel();
			}

			// Sửa đồ dừng theo ĐÚNG ô checkbox Đánh, không theo attackEnabled: state Returning của nó đi thẳng về
			// tâm bãi nên tắt Đánh mà để chạy tiếp thì nhân vật vẫn tự di chuyển. Huỷ giữa chuyến có thể để nhân vật
			// đứng lại ở NPC. Dùng attackConfigured để một nhịp rớt layout.AttackReady không giết luôn luồng Sửa đồ.
			// Ngoại lệ IsDebugRun: chuyến do người dùng bấm nút "Đi sửa đồ" là lệnh trực tiếp, không phải luồng tự động.
			bool repairAllowed = attackConfigured || game.WeaponRepairAutomation.IsDebugRun;
			if (!repairAllowed) game.WeaponRepairAutomation.Cancel(game, accountLog, "Tắt ô Đánh nên dừng luồng Sửa đồ");

			bool returnToTrainingBusy = attackEnabled && !repairPriority && game.ReturnToTrainingAutomation.Tick(game, snapshot, manualInputActive, accountLog);
			game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.ReturnMovementOwner, returnToTrainingBusy);
			if (returnToTrainingBusy) {
				game.ConfiguredTrainingMovementAutomation.Cancel(accountLog, "Tự lên bãi giữ quyền điều khiển");
				game.WeaponRepairAutomation.Cancel(game, accountLog, "Tự lên bãi giữ quyền điều khiển");
				game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.SaleRepairOwner, false);
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}

			bool configuredTrainingMovementBusy = attackEnabled && !repairPriority && game.ConfiguredTrainingMovementAutomation.Tick(game, snapshot, manualInputActive, accountLog);
			game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.TrainingMovementOwner, configuredTrainingMovementBusy);
			if (configuredTrainingMovementBusy) {
				game.WeaponRepairAutomation.Cancel(game, accountLog, "Di chuyển tới bãi cấu hình giữ quyền điều khiển");
				game.AutoFsActionGate.SetLootSuspended(AutoFsActionGate.SaleRepairOwner, false);
				game.AttackEngine.Stop();
				game.LootEngine.Stop();
				return;
			}

			bool repairBusy = repairAllowed && game.WeaponRepairAutomation.Tick(game, snapshot, accountLog);
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
		} finally {
			HotPathProfiler.End(HotPathProfiler.AccountTick, profilerStart);
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
	// Phát hiện account đứng nguyên một chỗ quá lâu rồi nhả sạch quyền điều khiển, bất kể engine nào đang giữ.
	//
	// Nguyên tắc: KHÔNG tin engine nào. Không đọc IsBusy của ai để quyết định có chạy hay không — chỉ nhìn toạ độ.
	// Mọi phép kiểm "luồng X đang bận nên bỏ qua" chính là thứ đã làm watchdog 10 giây câm suốt 3 giờ 39 phút.
	private static void SuperviseStuckAccount(GameWindow game, GameSnapshot snapshot, bool masterEnabled, Action<string> accountLog) {
		// Auto tổng tắt thì nhân vật đứng im là đúng, không theo dõi.
		if (!masterEnabled) {
			stuckAnchorByWindow.TryRemove(game.Handle, out _);
			return;
		}
		// Toạ độ 0/0 là lỗi đọc bộ nhớ, không phải đứng im. Trong 8 quãng dài đo được có tới 5 quãng dạng này; tính
		// chúng vào là báo động giả rồi đi reset engine của một account đang chạy bình thường.
		if (snapshot.X <= 0 && snapshot.Y <= 0) {
			stuckAnchorByWindow.TryRemove(game.Handle, out _);
			return;
		}
		// Chỉ bỏ qua khi máu ĐANG LÊN, không phải khi máu chưa đầy.
		//
		// Ý định ban đầu (chủ dự án chốt 2026-09-12) vẫn giữ nguyên: đứng yên hồi máu trước khi lên bãi là hành vi
		// đúng, không được coi là kẹt. Nhưng điều kiện cũ là "Hp < MaxHp -> xoá mốc", mà coordinator chạy mỗi 100ms
		// và nhân vật kẹt giữa bãi thì bị quái đánh liên tục — chỉ một nhịp máu hụt là đồng hồ 5 phút về 0, nên lớp
		// giám sát gần như không bao giờ chạy được ở bãi.
		//
		// Bằng chứng (Release/Diagnostics 2026-09-15, PID=28500): đứng nguyên 52572/104835 suốt 38 nhịp tim liên
		// tiếp (~38 phút), anti-afk.log ghi StationaryMilliseconds tới 516812 (8,6 phút), mà ANTI_AFK_STUCK_SUPERVISOR
		// nổ ĐÚNG 0 lần trong toàn bộ log. Heartbeat cùng quãng đó cho thấy máu dao động 470/470 -> 459/470 -> 470/470.
		// Chính comment cũ ở đây cũng đã tự khai: ca 22824 chỉ lọt qua được vì máu tình cờ đầy suốt.
		//
		// Máu đi lên nghĩa là đang hồi thật -> nhả. Máu đứng yên hoặc tụt (bị đánh) thì để đồng hồ chạy tiếp.
		int previousStuckHp = lastStuckHpByWindow.TryGetValue(game.Handle, out int stored) ? stored : snapshot.Hp;
		lastStuckHpByWindow[game.Handle] = snapshot.Hp;
		if (snapshot.MaxHp > 0 && snapshot.Hp < snapshot.MaxHp && snapshot.Hp > previousStuckHp) {
			stuckAnchorByWindow.TryRemove(game.Handle, out _);
			return;
		}

		DateTime now = DateTime.UtcNow;
		if (!stuckAnchorByWindow.TryGetValue(game.Handle, out StuckAnchor anchor) || GetRawDistance(anchor.RawX, anchor.RawY, snapshot.X, snapshot.Y) > StuckSupervisorRadiusRaw) {
			stuckAnchorByWindow[game.Handle] = new StuckAnchor(snapshot.X, snapshot.Y, now);
			return;
		}
		double stuckMinutes = (now - anchor.SinceUtc).TotalMinutes;
		if (stuckMinutes < StuckSupervisorMinutes) return;

		// Đặt lại mốc TRƯỚC khi cứu, để nếu vẫn kẹt thì lần cứu sau cách đúng một chu kỳ nữa chứ không bắn mỗi nhịp.
		stuckAnchorByWindow[game.Handle] = new StuckAnchor(snapshot.X, snapshot.Y, now);
		string state = $"RepairBusy={game.WeaponRepairAutomation.IsBusy} | RepairPending={game.WeaponRepairMonitor.HasPendingRepairRequest} | ReturnBusy={game.ReturnToTrainingAutomation.IsBusy} | TrainingBusy={game.ConfiguredTrainingMovementAutomation.IsBusy} | QuestBusy={game.ScoutQuestAutomation.IsBusy} | AttackState={game.AttackEngine.GetDiagnosticState()} | LootState={game.LootEngine.GetDiagnosticState()}";
		accountLog($"ANTI_AFK_STUCK_SUPERVISOR | Đứng nguyên {stuckMinutes:F1} phút trong bán kính {StuckSupervisorRadiusRaw} raw | ViTri={snapshot.X}/{snapshot.Y} | Map={game.LastObservedMapId} | HP={snapshot.Hp}/{snapshot.MaxHp} | {state}");

		// Nhả theo đúng thứ tự đã biết là cần thiết, không bỏ bước nào:
		// 1. ESC đóng mọi popup/shop đang treo — kẹt ở NPC thì giao diện thường còn mở, không đóng thì mọi lệnh sau vô nghĩa.
		// 2. Huỷ ba luồng giữ quyền di chuyển.
		// 3. DeferRepairRequest là bắt buộc: chỉ Cancel thì IsBusy về false nhưng HasPendingRepairRequest vẫn true,
		//    repairPriority vẫn bật và nhân vật lại đi sửa ngay. Đây đúng là vòng lặp đã xảy ra lúc 09:43:54.
		// 4. Dừng hai worker; chúng được dựng lại ở nhịp sau.
		// ESC CHỈ khi thật sự có giao diện đang mở.
		//
		// Không có popup thì ESC KHÔNG vô hại: client mở menu hệ thống (trong đó có mục thoát game), tức lớp cứu hộ
		// tự tay làm hỏng account nó định cứu. Đêm 2026-09-15 -> 16 lớp này nổ 29 lần và toàn bộ đều nổ oan.
		//
		// Mọi chỗ gửi ESC khác trong Auto đã có chốt sẵn (WeaponRepairAutomation dòng 368/371/374/377 gác bằng
		// shopOpened, dòng 683 gác bằng interactionLocked); riêng chỗ này trước đây gửi vô điều kiện.
		// modalState != 0 là đang có popup, shopState != 0 là đang mở cửa hàng — đối chiếu cùng cách đọc mà luồng
		// Sửa đồ dùng để nhận biết shop (modalState == 0 && shopState == 2).
		WeaponRepairAutomation.ReadShopState(game.ProcessId, out uint modalState, out uint shopState);
		bool anyUiOpen = modalState != 0 || shopState != 0;
		if (anyUiOpen) {
			BackgroundEscapeCommand.Run(game.ProcessId, game.Handle);
		}
		accountLog($"ANTI_AFK_STUCK_SUPERVISOR_ESC | ModalState=0x{modalState:X8} | ShopState={shopState} | {(anyUiOpen ? "có giao diện đang mở, đã gửi ESC" : "không có giao diện nào mở, BỎ QUA ESC để khỏi bật menu hệ thống")}");
		game.WeaponRepairAutomation.Cancel(game, accountLog, "Lớp giám sát đứng im thu hồi quyền điều khiển");
		game.WeaponRepairMonitor.DeferRepairRequest(StuckSupervisorRepairCooldownSeconds, "sau khi lớp giám sát đứng im thu hồi quyền điều khiển");
		game.ReturnToTrainingAutomation.Cancel(accountLog, "Lớp giám sát đứng im thu hồi quyền điều khiển");
		game.ConfiguredTrainingMovementAutomation.Cancel(accountLog, "Lớp giám sát đứng im thu hồi quyền điều khiển");
		game.SupportEngine.ReleaseForHigherPriority();
		game.ScoutQuestAutomation.Cancel(accountLog, "Lớp giám sát đứng im thu hồi quyền điều khiển");
		attackPositionByWindow.TryRemove(game.Handle, out _);
		attackEnabledByWindow.TryRemove(game.Handle, out _);
		game.AttackEngine.Stop();
		game.LootEngine.Stop();
		accountLog($"ANTI_AFK_STUCK_SUPERVISOR_RECOVERY | Đã ESC, huỷ Sửa đồ/Lên bãi/Di chuyển bãi/Nhiệm vụ, hoãn yêu cầu sửa {StuckSupervisorRepairCooldownSeconds}s, dựng lại worker Đánh/Nhặt ở nhịp sau.");
	}

	private static double GetRawDistance(int firstRawX, int firstRawY, int secondRawX, int secondRawY) {
		double deltaX = (double)firstRawX - secondRawX;
		double deltaY = (double)firstRawY - secondRawY;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	private readonly record struct StuckAnchor(int RawX, int RawY, DateTime SinceUtc);

	private static void LogHeartbeat(GameWindow game, GameSnapshot snapshot, bool masterEnabled, bool attackEnabled, bool lootEnabled, bool repairConfigured, bool repairReady, bool saleEnabled, bool questEnabled, Action<string> log) {
		DateTime now = DateTime.UtcNow;
		if (nextHeartbeatByWindow.TryGetValue(game.Handle, out DateTime nextUtc) && now < nextUtc) return;
		nextHeartbeatByWindow[game.Handle] = now.AddMinutes(1);
		EntitySnapshot target = snapshot.Success ? EntitySnapshot.ReadCurrentTarget(game.ProcessId) : new EntitySnapshot();
		log(
			$"HEARTBEAT_AUTO | Master={masterEnabled} | Attack={attackEnabled} | Loot={lootEnabled} | RepairConfigured={repairConfigured} | RepairReady={repairReady} | Sale={saleEnabled} | Quest={questEnabled} | " +
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

	// Trốn boss có đang bị kẹt không. Mốc vị trí dời theo nhân vật mỗi khi nó đi được quá EliteRetreatStuckRadius, nên
	// "kẹt" nghĩa là đứng trong vòng đó liên tục EliteRetreatStuckSeconds. Ghi log một lần lúc bắt đầu kẹt.
	private static bool IsEliteRetreatStuck(GameWindow game, GameSnapshot snapshot, bool retreatActive, Action<string> accountLog) {
		if (! retreatActive) {
			eliteRetreatAnchorByWindow.TryRemove(game.Handle, out _);
			return false;
		}
		DateTime now = DateTime.UtcNow;
		AttackPositionState anchor = eliteRetreatAnchorByWindow.GetOrAdd(game.Handle, new AttackPositionState(snapshot.X, snapshot.Y, now));
		long dx = snapshot.X - anchor.X;
		long dy = snapshot.Y - anchor.Y;
		if (dx * dx + dy * dy > (long)EliteRetreatStuckRadius * EliteRetreatStuckRadius) {
			eliteRetreatAnchorByWindow[game.Handle] = new AttackPositionState(snapshot.X, snapshot.Y, now);
			return false;
		}
		double stuckSeconds = (now - anchor.SinceUtc).TotalSeconds;
		if (stuckSeconds < EliteRetreatStuckSeconds) return false;
		// Mốc mới (nhân vật đi được hoặc hết đợt trốn) thì SinceUtc đổi, nên so với mốc đã log là biết đợt kẹt mới.
		if (eliteRetreatStuckLoggedByWindow.TryGetValue(game.Handle, out DateTime loggedSince) && loggedSince == anchor.SinceUtc) return true;
		eliteRetreatStuckLoggedByWindow[game.Handle] = anchor.SinceUtc;
		accountLog($"ELITE_RETREAT_STUCK_HEAL_ALLOWED | PID={game.ProcessId} | Player={snapshot.X}/{snapshot.Y} | Seconds={stuckSeconds:F1} | Radius={EliteRetreatStuckRadius} | Action=Trốn boss bị kẹt, cho Buff heal tại chỗ");
		return true;
	}

	// Đợt trốn boss này còn trong thời gian được miễn ANTI_AFK hay không.
	//
	// Hết đợt trốn thì xoá mốc ngay, nên đợt sau đo lại từ đầu thay vì thừa hưởng thời gian của đợt trước.
	private static bool IsEliteRetreatWithinAntiAfkGrace(IntPtr handle, bool retreatActive) {
		if (! retreatActive) {
			eliteRetreatAntiAfkSinceByWindow.TryRemove(handle, out _);
			return false;
		}
		DateTime since = eliteRetreatAntiAfkSinceByWindow.GetOrAdd(handle, DateTime.UtcNow);
		return (DateTime.UtcNow - since).TotalSeconds < EliteRetreatAntiAfkGraceSeconds;
	}

	// CẢNH BÁO ACCOUNT RƠI KHỎI GAME.
	//
	// Snapshot thất bại nghĩa là bản ghi nhân vật không đọc được — thường là NameReadFailed, tức trường tên rỗng,
	// tức nhân vật không còn trong thế giới (màn hình đăng nhập / chọn nhân vật). Auto đã treo cổng hành động và
	// dừng worker ở nhánh gọi hàm này, nên nó KHÔNG bơm lệnh vào client nữa — phần đó vốn đã đúng.
	//
	// Cái thiếu là: Auto chờ VÔ HẠN mà không nói gì. Đêm 2026-09-15 -> 16 server reset lúc 09:05:46 làm cả 6 account
	// rơi cùng lúc; 5 account vào lại sau ~5 phút, riêng PID=32364 (XinLỗiEm) nằm ngoài game từ 09:05:46 tới
	// 10:33:51 — 88 phút — mà không một dòng log nào ở mức cảnh báo. Muốn biết phải tự mở heartbeat ra đếm.
	//
	// Cảnh báo giữ nguyên như cũ. Phần MỚI là nhánh gọi ReloginSupervisor khi đã rớt quá ngưỡng — hiện đang ở chế
	// độ dry-run, chỉ ghi log chứ chưa giết client (xem ghi chú đầu ReloginSupervisor.cs).
	private static void ReportAccountOutOfWorld(GameWindow game, GameSnapshot snapshot) {
		DateTime now = DateTime.UtcNow;
		OutOfWorldState state = outOfWorldByWindow.GetOrAdd(game.Handle, _ => new OutOfWorldState { SinceUtc = now });
		double minutes = (now - state.SinceUtc).TotalMinutes;

		// CHỈ nhận NameReadFailed — đó mới đúng nghĩa "nhân vật không còn trong thế giới" (trường tên rỗng, tức
		// đang ở màn đăng nhập/chọn nhân vật). StatsPointerFailed là lỗi đọc layout bộ nhớ, client vẫn đang trong
		// game: giết nó là giết oan, đúng ca hàng loạt ngày 2026-09-18.
		if (! state.ReloginRequested && minutes >= ReloginSupervisor.ThresholdMinutes && snapshot.Status == SnapshotStatus.NameReadFailed) {
			state.ReloginRequested = true;
			ReloginSupervisor.Request(game, minutes, snapshot.Status);
		}

		if (minutes < OutOfWorldWarnAfterMinutes) return;
		if (now < state.NextReportUtc) return;
		state.NextReportUtc = now.AddMinutes(OutOfWorldRepeatMinutes);
		DebugLog.AddClientEvent($"ACCOUNT_OUT_OF_WORLD | PID={game.ProcessId} | {game.CharacterName} | RơiKhỏiGame={minutes:F1} phút | Lý do={snapshot.Status} | {snapshot.FailReason} | Auto đã treo cổng hành động và dừng worker.");
	}

	private static void ReportAccountBackInWorld(GameWindow game) {
		// Quên bộ đếm số lần thử: vào lại được game nghĩa là đợt rớt đó đã khép lại.
		ReloginSupervisor.NoteBackInWorld(game.CharacterName);
		if (! outOfWorldByWindow.TryRemove(game.Handle, out OutOfWorldState? state)) return;
		double minutes = (DateTime.UtcNow - state.SinceUtc).TotalMinutes;
		// Chỉ báo khi đã từng cảnh báo, để những nhịp trượt một hai giây không đẻ rác log.
		if (state.NextReportUtc == DateTime.MinValue) return;
		DebugLog.AddClientEvent($"ACCOUNT_BACK_IN_WORLD | PID={game.ProcessId} | {game.CharacterName} | Đã ở ngoài game {minutes:F1} phút rồi vào lại");
	}

	private static bool ShouldRestartStationaryAttack(IntPtr gameWindow, GameSnapshot snapshot, out int stationaryMilliseconds) {
		stationaryMilliseconds = 0;
		DateTime now = DateTime.UtcNow;
		// So theo BÁN KÍNH chứ không so khớp toạ độ tuyệt đối.
		//
		// Bản trước dùng "state.X != snapshot.X || state.Y != snapshot.Y", nên nhân vật nhích đúng 1 raw là bộ đếm về
		// 0 và mốc 10 giây không bao giờ tới. Bằng chứng lỗi này có thật (2026-09-12, PID=22824): suốt quãng kẹt cạnh
		// Đại Phu nhân vật trôi từ 58958/96143 sang 58972/96144 — 14 raw, tức 0,05 ô, đủ để phép so tuyệt đối cắt
		// quãng 219 phút thành hai mảnh 135 và 89 khi tính trên heartbeat.
		// Dùng chung StuckSupervisorRadiusRaw với lớp giám sát để hai nơi không lệch định nghĩa "đứng nguyên chỗ".
		if (!attackPositionByWindow.TryGetValue(gameWindow, out AttackPositionState state) || GetRawDistance(state.X, state.Y, snapshot.X, snapshot.Y) > StuckSupervisorRadiusRaw) {
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
		string line = $"Tự lên bãi không khởi động được | PID={game.ProcessId} | YêuCầu={reason} | TựLênBãi={settings.EnableReturnToTraining} | QuanhĐiểm={settings.UseCenterPosition} | Tâm={settings.CenterX}/{settings.CenterY} | MapTâm={settings.CenterMapId} | MapHiệnTại={game.LastObservedMapId}";
		if (lastReturnRequestFailureByWindow.TryGetValue(game.Handle, out string? previous) && string.Equals(previous, line, StringComparison.Ordinal)) return;
		lastReturnRequestFailureByWindow[game.Handle] = line;
		log(line);
	}

	// Nhân vật có đang đứng ở chỗ dùng được kỹ năng không.
	//
	// Quy tắc chủ dự án chốt 2026-09-11: "Mọi map trong Train quái đều là ngoài thành, còn lại đều là phạm vi trong
	// thành." Trong thành game CẤM dùng kỹ năng, nên cast ở đó là ném lệnh đi vô ích.
	//
	// Không giải được đích bãi thì trả true (KHÔNG chặn). Chặn nhầm ở đây nghĩa là Buff không bao giờ chạy — hỏng
	// nặng hơn hẳn so với việc thỉnh thoảng cast thừa, và trần cast theo tiến độ máu vẫn còn đó làm lưới an toàn.
	private static bool AreSkillsAllowedHere(GameWindow game) {
		if (!ConfiguredTrainingMovementAutomation.TryResolveTrainingPoint(game, out int trainingMapId, out _, out _)) return true;
		if (trainingMapId <= 0 || game.LastObservedMapId <= 0) return true;
		return game.LastObservedMapId == trainingMapId;
	}

	private static bool IsOutsideTrainingArea(GameWindow game, GameSnapshot snapshot) {
		Settings settings = game.AttackSettings;
		if (snapshot.X <= 0 || snapshot.Y <= 0) return false;
		// Mê cung / Thành thị / Tân thủ thôn ưu tiên cao hơn "Quanh điểm" (chủ dự án chốt 2026-09-10).
		// TryResolveTrainingPoint đã xếp đúng thứ tự đó rồi (ConfiguredTrainingMovementAutomation.TryResolveDestination),
		// và nó cũng chính là đích mà ReturnToTrainingAutomation.Prepare sẽ dùng — hỏi "có đang ở ngoài chỗ sắp bị kéo
		// về không" thì phải hỏi đúng cái đích đó.
		//
		// Bản cũ mở đầu bằng "if (!settings.UseCenterPosition) return false", nên cấu hình bãi kiểu Mê cung mà không
		// bật Quanh điểm thì hàm này luôn trả false: nhân vật đứng ngoài bãi vẫn bị coi là trong bãi, không ai kéo về.
		int areaMapId;
		int areaRawX;
		int areaRawY;
		if (ConfiguredTrainingMovementAutomation.TryResolveTrainingPoint(game, out int resolvedMapId, out int resolvedRawX, out int resolvedRawY)) {
			areaMapId = resolvedMapId;
			areaRawX = resolvedRawX;
			areaRawY = resolvedRawY;
		} else if (settings.UseCenterPosition && settings.CenterX > 0 && settings.CenterY > 0) {
			areaMapId = settings.CenterMapId;
			areaRawX = settings.CenterX;
			areaRawY = settings.CenterY;
		} else {
			return false;
		}
		// Khác map bãi là chắc chắn ngoài bãi. Không xét map thì toạ độ của hai map khác nhau bị đem trừ nhau, cho kết quả vô nghĩa.
		if (areaMapId > 0 && game.LastObservedMapId > 0 && game.LastObservedMapId != areaMapId) return true;
		long deltaX = (long)snapshot.X - areaRawX;
		long deltaY = (long)snapshot.Y - areaRawY;
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
				if (deathReadErrorLoggedByWindow.TryAdd(game.Handle, true)) log($"Về thành đọc trạng thái FAIL | PID={game.ProcessId} | {readError}");
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
			if (deathReturnLoggedByWindow.TryAdd(game.Handle, true)) log($"Nhân vật chết | PID={game.ProcessId} | Status={deathStatus} | State={deathState} | Modal=0x{deathModal:X8} | Về thành đã tắt");
			return true;
		}

		int returnDelayMilliseconds = game.BasicSettings.ReturnToTownDelayMilliseconds;
		DateTime detectedUtc = deathDetectedUtcByWindow.GetOrAdd(game.Handle, DateTime.UtcNow);
		if ((DateTime.UtcNow - detectedUtc).TotalMilliseconds < returnDelayMilliseconds) {
			if (deathReturnLoggedByWindow.TryAdd(game.Handle, true)) log($"Nhân vật chết | PID={game.ProcessId} | Status={deathStatus} | State={deathState} | Modal=0x{deathModal:X8} | Chờ={returnDelayMilliseconds}ms");
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
			string failure = $"Xử lý khi chết FAIL | PID={game.ProcessId} | Command=38 | Payload={deathActionPayload} | {sendError}";
			if (!deathSendFailureByWindow.TryGetValue(game.Handle, out string? previousFailure) || !string.Equals(previousFailure, failure, StringComparison.Ordinal)) {
				deathSendFailureByWindow[game.Handle] = failure;
				log(failure);
			}
			return true;
		}
		deathSendFailureByWindow.TryRemove(game.Handle, out _);
		log($"Xử lý khi chết | PID={game.ProcessId} | Command=38 | Payload={deathActionPayload} | Action={game.BasicSettings.DeathAction} | Delay={returnDelayMilliseconds}ms");
		return true;
	}

	// Chỉ ghi khi HP đã bằng 0 mà vẫn không nhận diện được là chết, và chỉ ghi lại khi bộ giá trị đổi.
	// Cụm "Về thành" giữ cho dòng này vào đúng death.log theo bộ định tuyến hiện hành.
	private static void LogDeathStateUndetected(GameWindow game, GameSnapshot snapshot, bool deathStateRead, int status, int state, uint modal, uint expectedModal, Action<string> log) {
		if (! deathStateRead || ! snapshot.Success || snapshot.MaxHp <= 0 || snapshot.Hp > 0) {
			deathUndetectedLoggedByWindow.TryRemove(game.Handle, out _);
			return;
		}
		string line = $"Về thành chưa nhận diện được trạng thái chết | PID={game.ProcessId} | Hp={snapshot.Hp}/{snapshot.MaxHp} | " +
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
