namespace Auto.Runtime;

using Auto.Attack;
using Auto.DebugTools;
using Auto.Loot;
using Auto.Movement;
using Auto.Repair;
using Auto.Utils;

// Tự chạy tới Đại Phu của map hiện tại, mở cửa hàng, mua cho ĐỦ TargetPotionCount mỗi loại HP + MP đã chọn ở
// "Hồi phục" (tab Cơ bản), rồi quay lại bãi.
//
// Chuỗi di chuyển + click NPC + mở cửa hàng PORT Y HỆT Repair/WeaponRepairAutomation (đã verify chạy thật trên 6
// client, xem file đó để biết bằng chứng từng bước) — cố tình KHÔNG refactor dùng chung để không động vào luồng Sửa
// đồ đang ổn định.
//
// Hai lối vào TÁCH BIỆT (RequestDebugRun / RequestAutomaticRun), cùng gọi Tick() nhưng khác flag debugMode/automaticMode:
//   - Debug: người dùng bấm nút tab Debug, bỏ qua thời gian nghỉ sau lần hỏng, bỏ qua công tắc Auto tổng khi gửi mua.
//   - Automatic: AccountEngineCoordinator gọi khi LowHpEngine về thành mà hết loại thuốc HP đã chọn (chủ dự án chốt
//     2026-09-28, xem site "LOW_HP_DOCTOR_PURCHASE_REQUESTED"). Tôn trọng công tắc Auto tổng khi gửi mua, và bị chặn
//     bởi FailureCooldownMinutes sau một lần hỏng — không có cửa chặn này thì về thành vẫn hết thuốc -> Hồi thành phù
//     bắn lại -> về thành -> thử mua lại -> hỏng lại, lặp vô hạn nếu nguyên nhân hỏng không tự hết.
//
// Hỏng ở bất kỳ bước nào đều quay lại bãi (BeginReturn) thay vì đứng tại NPC — chủ dự án chốt 2026-09-27.
internal sealed class DoctorPurchaseAutomation {
	// Chủ dự án chốt 2026-09-27: mặc định 10 bình mỗi loại, chưa có ô cấu hình riêng.
	private const int TargetPotionCount = 10;
	private const int DoctorEntityLoadTimeoutMilliseconds = 15000;
	private const int MaximumDoctorClickAttempts = 6;
	private const int MaximumMovementFailures = 3;
	private const int MaximumStalledRouteResends = 10;
	private const int StuckDetectionMilliseconds = 8000;
	private const int AutoFsDialogTransitionTimeoutMilliseconds = 1500;
	private const int AutoFsShopReadyTimeoutMilliseconds = 5000;
	private const int AutoFsDialogPollMilliseconds = 300;
	private const int AutoFsShopReadyPollMilliseconds = 500;
	private const int BuyConfirmPollMilliseconds = 400;
	private const int BuyConfirmTimeoutMilliseconds = 4000;
	// Chủ dự án chốt 2026-09-27: mua thất bại thì quay lại bãi, 5 phút sau mới thử lại. Chỉ áp cho lối tự động — lối
	// debug (bấm nút tay) không bị chặn để chủ dự án test lại ngay.
	private const int FailureCooldownMinutes = 5;
	private const double DoctorRouteArrivalDistance = 1.5;
	private const double MinimumObservedMovement = 0.03;
	private const double MinimumNavigationProgress = 0.08;

	private DoctorPurchaseState state;
	private DateTime deadlineUtc;
	private DateTime nextActionUtc;
	private DateTime nextMoveRefreshUtc;
	private DateTime nextProgressLogUtc;
	private DateTime nextDoctorEntityLogUtc;
	private DateTime nextReturnLogUtc;
	private int mapId;
	private int doctorRawX;
	private int doctorRawY;
	private int returnMapId;
	private int returnRawX;
	private int returnRawY;
	private readonly AutoFsTrainingOrderQueue returnOrderQueue = new();
	private int doctorClickAttempts;
	private bool doctorCoordinateFallbackUsed;
	private uint handledDoctorModalState;
	private bool interactionLocked;
	private int navigationTargetRawX;
	private int navigationTargetRawY;
	private int lastObservedRawX;
	private int lastObservedRawY;
	private double bestNavigationDistance;
	private DateTime lastMovementUtc;
	private int movementFailures;
	private int stalledRouteResends;
	private bool debugRunRequested;
	private bool debugMode;
	private bool automaticRunRequested;
	private bool automaticMode;
	// KHÔNG bị Reset() xoá — phải sống qua nhiều lượt chạy để chặn được vòng lặp hỏng-liên-tục.
	private DateTime purchaseCooldownUntilUtc = DateTime.MinValue;
	private List<(string Kind, string Name, int Target)> buyQueue = [];
	private int buyIndex;
	private string pendingBuyName = "";
	private string pendingBuyKind = "";
	private int pendingBuyCountBefore;
	private int pendingBuyExpectedAfter;
	private DateTime pendingBuyDeadlineUtc;
	private readonly InventoryPotionCounter potionCounter = new();

	public bool IsBusy => state != DoctorPurchaseState.Idle;

	// Cùng khuôn WeaponRepairAutomation.IsDebugRun: dùng ở cổng "Auto tổng tắt" của coordinator, PHẢI đúng ngay ở
	// nhịp vừa RequestDebugRun (lúc đó state còn Idle, IsBusy chưa kịp true) — thiếu OR debugRunRequested thì nhịp
	// đầu tiên bị bỏ lỡ, chuyến chạy tay không bao giờ khởi động khi Auto tổng đang tắt.
	public bool IsDebugRun => debugRunRequested || debugMode;

	// Cùng công thức với IsDebugRun, cho lối vào tự động (AccountEngineCoordinator gọi từ luồng Hồi thành phù).
	public bool IsAutomaticRun => automaticRunRequested || automaticMode;

	public bool IsOnFailureCooldown => DateTime.UtcNow < purchaseCooldownUntilUtc;

	// Cùng khuôn WeaponRepairAutomation.RequestDebugRun: chạy được bất kể Auto tổng đang bật hay tắt, vì đây là lệnh
	// trực tiếp của người dùng bấm nút, không phải luồng tự động. KHÔNG bị chặn bởi FailureCooldown — chủ dự án cần
	// test lại ngay sau khi sửa lỗi, không phải chờ 5 phút.
	public string RequestDebugRun() {
		if (IsBusy) return "DEBUG Mua ở Đại Phu chưa thể chạy vì flow hiện tại đang bận.";
		debugRunRequested = true;
		return "DEBUG Mua ở Đại Phu đã xếp lịch cho account đang chọn.";
	}

	// Lối vào tự động: AccountEngineCoordinator gọi khi LowHpEngine về thành mà hết loại thuốc HP đã chọn.
	public string RequestAutomaticRun() {
		if (IsBusy) return "Mua ở Đại Phu (tự động) chưa thể chạy vì flow hiện tại đang bận.";
		if (IsOnFailureCooldown) return $"Mua ở Đại Phu (tự động) đang nghỉ sau lần hỏng gần nhất, còn {(purchaseCooldownUntilUtc - DateTime.UtcNow).TotalSeconds:F0}s.";
		automaticRunRequested = true;
		return "Mua ở Đại Phu (tự động) đã xếp lịch cho account đang chọn.";
	}

	public bool Tick(GameWindow game, GameSnapshot snapshot, Action<string>? log) {
		if (state == DoctorPurchaseState.Idle) {
			if (!debugRunRequested && !automaticRunRequested) return false;
			// Debug ưu tiên nếu cả hai cờ cùng được đặt (không nên xảy ra, nhưng an toàn hơn là bỏ cả hai).
			bool startingAutomatic = automaticRunRequested && !debugRunRequested;
			debugRunRequested = false;
			automaticRunRequested = false;
			if (!game.RuntimeLayout.RepairReady) {
				log?.Invoke((startingAutomatic ? "" : "DEBUG ") + "Mua ở Đại Phu bị chặn an toàn | " + game.RuntimeLayout.DescribeUnavailable(RuntimeSubsystem.Map, RuntimeSubsystem.Shop, RuntimeSubsystem.RepairTransport));
				return false;
			}
			if (startingAutomatic) automaticMode = true; else debugMode = true;
			return Start(game, snapshot, log);
		}

		if (!snapshot.Success) return true;
		GameMapInfo currentMap = GameMapReader.Read(game.ProcessId);
		if (state != DoctorPurchaseState.Returning && (!currentMap.Success || currentMap.MapId != mapId)) {
			return Fail(game, $"Map thay đổi trong lúc mua ở Đại Phu | Map={mapId}->{(currentMap.Success ? currentMap.MapId.ToString() : "UNKNOWN:" + currentMap.FailureReason)}", log);
		}

		switch (state) {
			case DoctorPurchaseState.MovingToDoctor:
				TickMovingToDoctor(game, snapshot, log);
				break;
			case DoctorPurchaseState.ClickingDoctor:
				TickClickingDoctor(game, snapshot, log);
				break;
			case DoctorPurchaseState.WaitingDialog:
				TickWaitingDialog(game, log);
				break;
			case DoctorPurchaseState.SelectingShopAction:
				if (DateTime.UtcNow >= nextActionUtc) ClickShopAction(game, log);
				break;
			case DoctorPurchaseState.WaitingShop:
				TickWaitingShop(game, log);
				break;
			case DoctorPurchaseState.Buying:
				if (DateTime.UtcNow >= nextActionUtc) TickBuying(game, log);
				break;
			case DoctorPurchaseState.WaitingBuyConfirm:
				TickWaitingBuyConfirm(game, log);
				break;
			case DoctorPurchaseState.ClosingUi:
				BackgroundEscapeCommand.Run(game.ProcessId, game.Handle);
				log?.Invoke("Mua ở Đại Phu | đã mua xong, gửi một ESC để đóng shop.");
				BeginReturn(game, log);
				break;
			case DoctorPurchaseState.Returning:
				bool arrived = returnOrderQueue.Tick(game, snapshot, returnMapId, returnRawX, returnRawY, out string returnDetail);
				if (arrived) return Complete(game, log);
				if (DateTime.UtcNow >= nextReturnLogUtc) {
					nextReturnLogUtc = DateTime.UtcNow.AddSeconds(10);
					log?.Invoke($"Mua ở Đại Phu | quay lại bãi | {returnDetail}");
				}
				break;
		}
		return true;
	}

	private bool Start(GameWindow game, GameSnapshot snapshot, Action<string>? log) {
		GameMapInfo map = GameMapReader.Read(game.ProcessId);
		if (!map.Success || !map.HasDoctor) {
			log?.Invoke("Mua ở Đại Phu FAIL | " + (map.Success ? "Map hiện tại không có tọa độ Đại Phu." : map.FailureReason));
			Reset();
			return false;
		}
		mapId = map.MapId;
		doctorRawX = map.DoctorRawX;
		doctorRawY = map.DoctorRawY;
		if (ConfiguredTrainingMovementAutomation.TryResolveTrainingPoint(game, out int configuredMapId, out int configuredRawX, out int configuredRawY)) {
			returnMapId = configuredMapId;
			returnRawX = configuredRawX;
			returnRawY = configuredRawY;
		} else {
			returnMapId = map.MapId;
			returnRawX = snapshot.X;
			returnRawY = snapshot.Y;
		}
		BasicSettings settings = game.BasicSettings;
		buyQueue = [
			("HP", ResolvePotionName(QuickBuyPotions.Hp, settings.QuickBuyHpPotionCode), TargetPotionCount),
			("MP", ResolvePotionName(QuickBuyPotions.Mp, settings.QuickBuyMpPotionCode), TargetPotionCount)
		];
		buyIndex = 0;
		if (!AutoFsMovementCommand.TryMoveTo(game, doctorRawX, doctorRawY, out string routeResult)) {
			log?.Invoke("Mua ở Đại Phu FAIL | Không khởi tạo được tuyến nội bộ tới Đại Phu | " + routeResult);
			Reset();
			return false;
		}
		InitializeNavigation(snapshot.X, snapshot.Y, doctorRawX, doctorRawY);
		doctorClickAttempts = 0;
		state = DoctorPurchaseState.MovingToDoctor;
		nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(StuckDetectionMilliseconds);
		nextProgressLogUtc = DateTime.UtcNow.AddSeconds(10);
		log?.Invoke($"Mua ở Đại Phu bắt đầu | Map={map.MapId} | ĐạiPhu={map.DoctorRawX}/{map.DoctorRawY} | Muốn mua = {string.Join(", ", buyQueue.Select(item => $"{item.Target} {item.Name}"))} | QuayLại=Map{returnMapId}/{returnRawX}/{returnRawY}");
		log?.Invoke("Mua ở Đại Phu | đã gửi lệnh di chuyển AutoFS tới Đại Phu | " + routeResult);
		return true;
	}

	private static string ResolvePotionName(IReadOnlyList<(string Name, int Code)> list, int code) {
		return QuickBuyPotions.TryGetName(list, code, out string name) ? name : "";
	}

	// Dùng ở AccountEngineCoordinator để quyết định "về thành mà còn thuốc thì lên bãi thẳng, hết thuốc thì ghé Đại
	// Phu trước" — CÙNG phép đếm mà TickBuying dùng (chỉ tính đúng loại đã chọn ở Hồi phục, KHÔNG phải "còn bất kỳ
	// thuốc HP nào", đúng chủ dự án chốt 2026-09-27). Đọc lỗi hoặc chưa chọn loại nào thì trả true (coi như còn
	// thuốc) để không chặn nhầm luồng lên bãi bình thường khi bản thân phép kiểm này hỏng.
	internal static bool BagHasPotion(int processId, string potionName) {
		if (potionName.Length == 0) return true;
		InventoryPotionCounter counter = new();
		if (!counter.TrySnapshot(processId, out Dictionary<string, int> bag)) return true;
		return QuickBuyEngine.CountPotion(bag, potionName) > 0;
	}

	internal static string ResolveHpPotionName(BasicSettings settings) => ResolvePotionName(QuickBuyPotions.Hp, settings.QuickBuyHpPotionCode);

	private void TickMovingToDoctor(GameWindow game, GameSnapshot snapshot, Action<string>? log) {
		double doctorDistance = GetDistance(snapshot.X, snapshot.Y, doctorRawX, doctorRawY);
		ObserveNavigationProgress(snapshot.X, snapshot.Y, doctorRawX, doctorRawY);
		if (doctorDistance <= DoctorRouteArrivalDistance) {
			state = DoctorPurchaseState.ClickingDoctor;
			nextActionUtc = DateTime.UtcNow.AddMilliseconds(1500);
			deadlineUtc = DateTime.UtcNow.AddMilliseconds(DoctorEntityLoadTimeoutMilliseconds);
			nextDoctorEntityLogUtc = DateTime.MinValue;
			log?.Invoke("Mua ở Đại Phu | đã tới Đại Phu, chuẩn bị mở hội thoại");
			return;
		}
		if (DateTime.UtcNow >= nextMoveRefreshUtc && HasNavigationStalled()) {
			nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(StuckDetectionMilliseconds);
			if (!AutoFsMovementCommand.TryMoveTo(game, doctorRawX, doctorRawY, out string moveResult)) {
				movementFailures++;
				if (movementFailures >= MaximumMovementFailures) { Fail(game, $"Không thể tiếp tục đi tới Đại Phu sau {movementFailures} lần thử | " + moveResult, log); return; }
				nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(500);
				log?.Invoke($"Mua ở Đại Phu | lệnh di chuyển lỗi thoáng qua, sẽ thử lại | Lần={movementFailures}/{MaximumMovementFailures} | {moveResult}");
			} else {
				movementFailures = 0;
				lastMovementUtc = DateTime.UtcNow;
				stalledRouteResends++;
				if (stalledRouteResends >= MaximumStalledRouteResends) {
					Fail(game, $"Gửi lại tuyến tới Đại Phu {stalledRouteResends} lần mà không tiến thêm được ô nào | HiệnTại={snapshot.X}/{snapshot.Y} | Đích={doctorRawX}/{doctorRawY} | Còn={doctorDistance:F2}", log);
					return;
				}
				log?.Invoke($"Mua ở Đại Phu | đứng yên, đã gửi lại tuyến tới Đại Phu | Lần={stalledRouteResends}/{MaximumStalledRouteResends} | {moveResult}");
			}
		}
		if (DateTime.UtcNow >= nextProgressLogUtc) {
			nextProgressLogUtc = DateTime.UtcNow.AddSeconds(10);
			log?.Invoke($"Mua ở Đại Phu | đang đi tới Đại Phu | HiệnTại={snapshot.X}/{snapshot.Y} | Còn={doctorDistance:F2}");
		}
	}

	private void TickClickingDoctor(GameWindow game, GameSnapshot snapshot, Action<string>? log) {
		if (DateTime.UtcNow < nextActionUtc) return;
		double alignmentDistance = GetDistance(snapshot.X, snapshot.Y, doctorRawX, doctorRawY);
		if (alignmentDistance > DoctorRouteArrivalDistance) {
			if (!AutoFsMovementCommand.TryMoveTo(game, doctorRawX, doctorRawY, out string moveResult)) { Fail(game, "Bị lệch khỏi Đại Phu và không thể quay lại | " + moveResult, log); return; }
			InitializeNavigation(snapshot.X, snapshot.Y, doctorRawX, doctorRawY);
			state = DoctorPurchaseState.MovingToDoctor;
			nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(StuckDetectionMilliseconds);
			log?.Invoke($"Mua ở Đại Phu | vị trí đã lệch, đang quay lại Đại Phu | HiệnTại={snapshot.X}/{snapshot.Y}");
			return;
		}
		if (!RuntimeEntityLocator.TryFindNamedEntity(game.ProcessId, "Đại phu", doctorRawX, doctorRawY, out RuntimeEntityLocation doctor, out string findReason)) {
			if (HasTimedOut()) {
				if (!doctorCoordinateFallbackUsed) {
					doctorCoordinateFallbackUsed = true;
					log?.Invoke($"Mua ở Đại Phu | không thấy entity Đại Phu, chuyển sang click theo toạ độ Data/Maps | Đích={doctorRawX}/{doctorRawY} | {findReason}");
					ClickDoctor(game, snapshot, new RuntimeEntityLocation(-1, 0, doctorRawX, doctorRawY, 0, 0, "Đại phu (toạ độ Data/Maps)", 0), log);
					return;
				}
				Fail(game, $"Không thấy entity Đại Phu sau {DoctorEntityLoadTimeoutMilliseconds}ms, click theo toạ độ cũng không mở được hội thoại | {findReason}", log);
				return;
			}
			nextActionUtc = DateTime.UtcNow.AddSeconds(1);
			if (DateTime.UtcNow >= nextDoctorEntityLogUtc) {
				nextDoctorEntityLogUtc = DateTime.UtcNow.AddSeconds(5);
				log?.Invoke("Mua ở Đại Phu | đã tới nơi, đang chờ entity Đại Phu tải | " + findReason);
			}
			return;
		}
		ClickDoctor(game, snapshot, doctor, log);
	}

	private void ClickDoctor(GameWindow game, GameSnapshot snapshot, RuntimeEntityLocation doctor, Action<string>? log) {
		doctorClickAttempts++;
		bool byIndex = doctor.Index >= 0 && (doctor.RawX <= 0 || doctor.RawY <= 0);
		bool clicked = byIndex
			? game.AutoFsTransport.TrySelectEntity(game.Handle, doctor.Index, out string result)
			: FullMouseHandlerPickupCommand.TryClickRawPosition(game.ProcessId, game.Handle, snapshot.X, snapshot.Y, doctor.RawX, doctor.RawY, out _, out result);
		if (!clicked) {
			if (doctorClickAttempts < MaximumDoctorClickAttempts) {
				bool moveSent = AutoFsMovementCommand.TryMoveTo(game, doctorRawX, doctorRawY, out string moveResult);
				if (moveSent) {
					InitializeNavigation(snapshot.X, snapshot.Y, doctorRawX, doctorRawY);
					state = DoctorPurchaseState.MovingToDoctor;
					nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(900);
					nextProgressLogUtc = DateTime.UtcNow.AddSeconds(10);
				} else {
					nextActionUtc = DateTime.UtcNow.AddSeconds(1);
				}
				log?.Invoke($"Mua ở Đại Phu | click Đại Phu chưa hợp lệ, sẽ thử lại | Lần={doctorClickAttempts}/{MaximumDoctorClickAttempts} | Click={result} | Move={moveResult}");
				return;
			}
			Fail(game, "Click Đại Phu thất bại | " + result, log);
			return;
		}
		state = DoctorPurchaseState.WaitingDialog;
		deadlineUtc = DateTime.UtcNow.AddSeconds(4);
	}

	private void TickWaitingDialog(GameWindow game, Action<string>? log) {
		if (DateTime.UtcNow < nextActionUtc) return;
		WeaponRepairAutomation.ReadShopState(game.ProcessId, out uint openingModalState, out uint openingShopState);
		if (openingModalState == 0 && openingShopState == 2) {
			handledDoctorModalState = 0;
			interactionLocked = true;
			state = DoctorPurchaseState.Buying;
			nextActionUtc = DateTime.UtcNow.AddMilliseconds(500);
			log?.Invoke($"Mua ở Đại Phu | Đại Phu đã mở trực tiếp cửa hàng, bỏ qua menu hội thoại | ShopState={openingShopState}");
		} else if (handledDoctorModalState != 0 && openingModalState == handledDoctorModalState) {
			if (HasTimedOut()) { Fail(game, $"Popup Đại Phu không chuyển trạng thái theo chu kỳ AutoFS | PreviousModal=0x{handledDoctorModalState:X8} | CurrentModal=0x{openingModalState:X8} | ShopState={openingShopState}.", log); return; }
			if (!game.AutoFsTransport.TrySendCommand(game.Handle, 7, -1, out string retryResult)) { Fail(game, "Không gửi lại được command 7/-1 trong chu kỳ popup Đại Phu | " + retryResult, log); return; }
			nextActionUtc = DateTime.UtcNow.AddMilliseconds(AutoFsDialogPollMilliseconds);
		} else if (handledDoctorModalState != 0 && openingModalState == 0) {
			handledDoctorModalState = 0;
			state = DoctorPurchaseState.WaitingShop;
			nextActionUtc = DateTime.UtcNow.AddMilliseconds(AutoFsShopReadyPollMilliseconds);
			deadlineUtc = DateTime.UtcNow.AddMilliseconds(AutoFsShopReadyTimeoutMilliseconds);
		} else if (openingModalState != 0) {
			handledDoctorModalState = 0;
			interactionLocked = true;
			state = DoctorPurchaseState.SelectingShopAction;
			nextActionUtc = DateTime.UtcNow.AddMilliseconds(1200);
		} else if (HasTimedOut()) {
			if (doctorClickAttempts < MaximumDoctorClickAttempts) {
				state = DoctorPurchaseState.ClickingDoctor;
				nextActionUtc = DateTime.UtcNow.AddMilliseconds(1000);
				deadlineUtc = DateTime.UtcNow.AddSeconds(30);
				log?.Invoke($"Mua ở Đại Phu | chưa thấy hội thoại, chờ NPC tải và thử lại | Lần={doctorClickAttempts + 1}/{MaximumDoctorClickAttempts}");
			} else {
				Fail(game, "Không mở được hội thoại Đại Phu.", log);
			}
		}
	}

	private void ClickShopAction(GameWindow game, Action<string>? log) {
		uint invokedModalState = 0;
		WeaponRepairAutomation.ReadShopState(game.ProcessId, out invokedModalState, out _);
		if (!DoctorShopSemanticCommand.TryInvoke(game, out string semanticResult)) {
			Fail(game, "Không gọi được lệnh nội bộ mở shop | " + semanticResult, log);
			return;
		}
		log?.Invoke("Mua ở Đại Phu | đã gọi lệnh nội bộ mở shop | " + semanticResult);
		if (semanticResult.StartsWith("Doctor confirmation modal", StringComparison.Ordinal)) {
			handledDoctorModalState = invokedModalState;
			state = DoctorPurchaseState.WaitingDialog;
			nextActionUtc = DateTime.UtcNow.AddMilliseconds(AutoFsDialogPollMilliseconds);
			deadlineUtc = DateTime.UtcNow.AddMilliseconds(AutoFsDialogTransitionTimeoutMilliseconds);
			return;
		}
		state = DoctorPurchaseState.WaitingShop;
		nextActionUtc = DateTime.UtcNow.AddMilliseconds(1000);
		deadlineUtc = DateTime.UtcNow.AddSeconds(4);
	}

	private void TickWaitingShop(GameWindow game, Action<string>? log) {
		if (DateTime.UtcNow < nextActionUtc) return;
		WeaponRepairAutomation.ReadShopState(game.ProcessId, out uint modalState, out uint shopState);
		if (modalState == 0 && shopState == 2) {
			state = DoctorPurchaseState.Buying;
			nextActionUtc = DateTime.UtcNow.AddMilliseconds(500);
			log?.Invoke($"Mua ở Đại Phu | đã xác nhận cửa hàng nội bộ sẵn sàng | ShopState={shopState}");
		} else if (HasTimedOut()) {
			Fail(game, $"Hội thoại đã đóng nhưng cửa hàng nội bộ chưa sẵn sàng | ShopState={shopState}.", log);
		} else {
			nextActionUtc = DateTime.UtcNow.AddMilliseconds(AutoFsShopReadyPollMilliseconds);
		}
	}

	// Mua cho đủ Target của từng loại trong buyQueue, chỉ mua phần CÒN THIẾU so với số đang có trong túi/ô nhanh/rương 2.
	private void TickBuying(GameWindow game, Action<string>? log) {
		if (buyIndex >= buyQueue.Count) {
			state = DoctorPurchaseState.ClosingUi;
			return;
		}
		(string kind, string name, int target) = buyQueue[buyIndex];
		if (name.Length == 0) {
			log?.Invoke($"Mua ở Đại Phu | {kind} | Bỏ qua vì chưa chọn loại thuốc ở Hồi phục.");
			buyIndex++;
			return;
		}
		if (!potionCounter.TrySnapshot(game.ProcessId, out Dictionary<string, int> bag)) {
			Fail(game, $"Mua ở Đại Phu | {kind} | Không đọc được túi đồ để đếm {name}.", log);
			return;
		}
		int current = QuickBuyEngine.CountPotion(bag, name);
		int needed = target - current;
		if (needed <= 0) {
			log?.Invoke($"Mua ở Đại Phu | {kind} | Đã đủ {current}/{target} {name}, không cần mua.");
			buyIndex++;
			return;
		}
		if (!NpcShopReader.TryFindItem(game.ProcessId, name, out int position, out int unitWeight, out string shopDetail)) {
			log?.Invoke($"Mua ở Đại Phu | {kind} | Không mua được: {shopDetail}");
			buyIndex++;
			return;
		}
		int buyCount = needed;
		InventoryStrengthReading strength = InventoryStrengthReader.Read(game.ProcessId);
		if (strength.Success && unitWeight > 0) buyCount = Math.Min(needed, Math.Max(0, strength.Free) / unitWeight);
		if (buyCount <= 0) {
			log?.Invoke($"Mua ở Đại Phu | {kind} | Không đủ sức lực để mua thêm {name} | Sức lực còn={strength.Free} | Nặng mỗi bình={unitWeight}");
			buyIndex++;
			return;
		}
		if (!game.AutoFsTransport.TryBuyFromShop(game.Handle, position, buyCount, bypassMasterSwitch: debugMode, out string sendError)) {
			log?.Invoke($"Mua ở Đại Phu | {kind} | Gửi lệnh mua thất bại | {sendError}");
			buyIndex++;
			return;
		}
		log?.Invoke($"Mua ở Đại Phu | {kind} | Đã gửi lệnh mua {buyCount} {name} | Đang có {current}/{target} | {shopDetail}");
		pendingBuyName = name;
		pendingBuyKind = kind;
		pendingBuyCountBefore = current;
		pendingBuyExpectedAfter = current + buyCount;
		pendingBuyDeadlineUtc = DateTime.UtcNow.AddMilliseconds(BuyConfirmTimeoutMilliseconds);
		state = DoctorPurchaseState.WaitingBuyConfirm;
		nextActionUtc = DateTime.UtcNow.AddMilliseconds(BuyConfirmPollMilliseconds);
	}

	private void TickWaitingBuyConfirm(GameWindow game, Action<string>? log) {
		if (DateTime.UtcNow < nextActionUtc) return;
		potionCounter.Invalidate();
		if (potionCounter.TrySnapshot(game.ProcessId, out Dictionary<string, int> bag)) {
			int now = QuickBuyEngine.CountPotion(bag, pendingBuyName);
			if (now > pendingBuyCountBefore) {
				log?.Invoke($"Mua ở Đại Phu | {pendingBuyKind} | Mua THÀNH CÔNG | {pendingBuyName} {pendingBuyCountBefore} -> {now} (mong đợi {pendingBuyExpectedAfter})");
				buyIndex++;
				state = DoctorPurchaseState.Buying;
				nextActionUtc = DateTime.UtcNow;
				return;
			}
		}
		if (DateTime.UtcNow >= pendingBuyDeadlineUtc) {
			log?.Invoke($"Mua ở Đại Phu | {pendingBuyKind} | Mua KHÔNG THÀNH CÔNG | Túi {pendingBuyName} không tăng sau {BuyConfirmTimeoutMilliseconds}ms");
			buyIndex++;
			state = DoctorPurchaseState.Buying;
			nextActionUtc = DateTime.UtcNow;
			return;
		}
		nextActionUtc = DateTime.UtcNow.AddMilliseconds(BuyConfirmPollMilliseconds);
	}

	private void BeginReturn(GameWindow game, Action<string>? log) {
		interactionLocked = false;
		if (returnRawX <= 0 || returnRawY <= 0 || returnMapId <= 0) {
			log?.Invoke("Mua ở Đại Phu hoàn tất nhưng không thể quay lại bãi | Tâm bãi không hợp lệ.");
			Complete(game, log);
			return;
		}
		returnOrderQueue.Reset();
		nextReturnLogUtc = DateTime.MinValue;
		state = DoctorPurchaseState.Returning;
		log?.Invoke($"Mua ở Đại Phu | bắt đầu quay lại bãi | Map={returnMapId} | Đích={returnRawX}/{returnRawY}");
	}

	private bool Complete(GameWindow game, Action<string>? log) {
		log?.Invoke(debugMode ? $"DEBUG Mua ở Đại Phu hoàn tất | Đã quay lại {returnRawX}/{returnRawY}." : $"Mua ở Đại Phu hoàn tất | Đã quay lại {returnRawX}/{returnRawY}.");
		Reset();
		return true;
	}

	// Hỏng thì KHÔNG đứng im tại NPC — đóng giao diện (nếu có) rồi quay lại bãi, cùng đường BeginReturn dùng khi mua
	// xong. Lối tự động còn bị chặn lại FailureCooldownMinutes trước khi được yêu cầu chạy lần nữa (chủ dự án chốt
	// 2026-09-27): không có cửa chặn này, HP thấp -> về thành -> mua hỏng -> về bãi vẫn hết thuốc -> Hồi thành phù bắn
	// lại ngay -> lặp vô hạn nếu nguyên nhân hỏng không tự hết (ví dụ không tìm thấy entity Đại Phu).
	private bool Fail(GameWindow game, string reason, Action<string>? log) {
		log?.Invoke("Mua ở Đại Phu dừng an toàn | " + reason + (interactionLocked ? " | Đang giữ giao diện NPC đang mở, gửi ESC rồi quay lại bãi." : " | Quay lại bãi."));
		if (interactionLocked) BackgroundEscapeCommand.Run(game.ProcessId, game.Handle);
		if (automaticMode) {
			purchaseCooldownUntilUtc = DateTime.UtcNow.AddMinutes(FailureCooldownMinutes);
			log?.Invoke($"Mua ở Đại Phu | (tự động) nghỉ {FailureCooldownMinutes} phút trước khi thử lại.");
		}
		BeginReturn(game, log);
		return false;
	}

	private void InitializeNavigation(int rawX, int rawY, int targetRawX, int targetRawY) {
		navigationTargetRawX = targetRawX;
		navigationTargetRawY = targetRawY;
		lastObservedRawX = rawX;
		lastObservedRawY = rawY;
		bestNavigationDistance = GetDistance(rawX, rawY, targetRawX, targetRawY);
		lastMovementUtc = DateTime.UtcNow;
		movementFailures = 0;
		stalledRouteResends = 0;
	}

	private void ObserveNavigationProgress(int rawX, int rawY, int targetRawX, int targetRawY) {
		double distance = GetDistance(rawX, rawY, targetRawX, targetRawY);
		if (navigationTargetRawX != targetRawX || navigationTargetRawY != targetRawY) {
			navigationTargetRawX = targetRawX;
			navigationTargetRawY = targetRawY;
			lastObservedRawX = rawX;
			lastObservedRawY = rawY;
			bestNavigationDistance = distance;
			lastMovementUtc = DateTime.UtcNow;
			return;
		}
		if (GetDistance(lastObservedRawX, lastObservedRawY, rawX, rawY) >= MinimumObservedMovement) {
			lastObservedRawX = rawX;
			lastObservedRawY = rawY;
		}
		if (distance <= bestNavigationDistance - MinimumNavigationProgress) {
			bestNavigationDistance = distance;
			lastMovementUtc = DateTime.UtcNow;
			stalledRouteResends = 0;
		}
	}

	private bool HasNavigationStalled() => (DateTime.UtcNow - lastMovementUtc).TotalMilliseconds >= StuckDetectionMilliseconds;
	private bool HasTimedOut() => DateTime.UtcNow >= deadlineUtc;

	private static double GetDistance(int rawX1, int rawY1, int rawX2, int rawY2) {
		double dx = (rawX2 - rawX1) / 256.0;
		double dy = (rawY2 - rawY1) / 512.0;
		return Math.Sqrt(dx * dx + dy * dy);
	}

	private void Reset() {
		state = DoctorPurchaseState.Idle;
		nextActionUtc = DateTime.MinValue;
		nextMoveRefreshUtc = DateTime.MinValue;
		nextProgressLogUtc = DateTime.MinValue;
		nextDoctorEntityLogUtc = DateTime.MinValue;
		nextReturnLogUtc = DateTime.MinValue;
		returnOrderQueue.Reset();
		doctorClickAttempts = 0;
		doctorCoordinateFallbackUsed = false;
		handledDoctorModalState = 0;
		interactionLocked = false;
		movementFailures = 0;
		stalledRouteResends = 0;
		buyQueue = [];
		buyIndex = 0;
		debugMode = false;
		debugRunRequested = false;
		automaticMode = false;
		automaticRunRequested = false;
		// purchaseCooldownUntilUtc CỐ Ý không xoá ở đây — phải sống qua Reset() mới chặn được vòng lặp hỏng liên tục.
	}

	private enum DoctorPurchaseState {
		Idle,
		MovingToDoctor,
		ClickingDoctor,
		WaitingDialog,
		SelectingShopAction,
		WaitingShop,
		Buying,
		WaitingBuyConfirm,
		ClosingUi,
		Returning
	}
}
